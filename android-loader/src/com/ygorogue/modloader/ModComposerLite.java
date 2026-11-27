package com.ygorogue.modloader;

import org.json.JSONArray;
import org.json.JSONObject;
import java.io.*;
import java.util.*;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;

/**
 * Pure-Java zero-copy overlay compositor for Android:
 *   - Composes enabled imported MODs (.yrdpk / Mod Spec v1 / Android Overlays / YPK expansions)
 *     into filesDir/game/overlay/ so GameActivity serves untouched vanilla assets directly from vanilla-web.zip.
 *   - Supports Modern Era MR2-MR5 Extra Deck rules, Lua helper libraries, Built-in Touch Trainer (修改器),
 *     Optional Developer Debug HUD, and "Hide Online Multiplayer when Modded" protection switch.
 */
public final class ModComposerLite {
    private static final String GET_ID_OLD = "function GetID()\n\treturn self_table,self_code\nend";
    private static final String GET_ID_NEW = "function GetID()\n\tlocal offset=self_code<100000000 and 1 or 100\n\treturn self_table,self_code,offset\nend";

    private ModComposerLite() {}

    public static int composeAll(ModStore store, ModStore.ProgressCallback cb) throws Exception {
        File vanillaZipFile = store.getVanillaZip();
        if (!vanillaZipFile.isFile()) {
            throw new IOException("No vanilla web-*.zip installed yet. Download or import web-*.zip first.");
        }

        JSONObject state = store.loadState();
        boolean enableTrainer = state.optBoolean("enableTrainer", true);
        boolean developerMode = state.optBoolean("developerMode", false);
        boolean hideOnlineWhenModded = state.optBoolean("hideOnlineWhenModded", true);
        JSONObject trainerConfig = state.optJSONObject("trainerConfig");

        JSONArray mods = state.optJSONArray("mods");
        List<JSONObject> enabled = new ArrayList<>();
        if (mods != null) {
            for (int i = 0; i < mods.length(); i++) {
                JSONObject m = mods.getJSONObject(i);
                if (m.optBoolean("enabled", true)) enabled.add(m);
            }
        }

        File overlayDir = store.getOverlayDir();
        ModStore.deleteRecursive(overlayDir);

        boolean hasActiveSession = !enabled.isEmpty() || enableTrainer || developerMode;
        if (!hasActiveSession) {
            state.put("applied", false);
            state.put("appliedCount", 0);
            store.saveState(state);
            if (cb != null) cb.onProgress("Pure vanilla mode (0 MODs enabled)", 100);
            return 0;
        }

        overlayDir.mkdirs();
        if (cb != null) cb.onProgress("Composing " + enabled.size() + " MOD(s)...", 15);

        try (ZipFile vanilla = new ZipFile(vanillaZipFile)) {
            JSONObject content = null;
            JSONObject engine = null;
            JSONObject scripts = null;
            JSONObject campaign = null;
            JSONObject zh = null;
            List<String> extraCssPaths = new ArrayList<>();
            List<String> extraJsPaths = new ArrayList<>();
            boolean hasModernExtraDeck = false;
            boolean hasPendulumOrLink = false;
            boolean ensureGetId3 = false;
            boolean unlockAllVariantsJs = false;
            String requestedDuelRules = "";

            for (int idx = 0; idx < enabled.size(); idx++) {
                JSONObject mod = enabled.get(idx);
                String modId = mod.optString("id");
                String kind = mod.optString("kind", "modspec");
                File modDir = new File(store.getModsDir(), modId);
                if (!modDir.isDirectory()) continue;

                if (cb != null) {
                    int pct = 20 + (idx * 55 / Math.max(1, enabled.size()));
                    cb.onProgress("Applying " + mod.optString("name", modId) + "...", pct);
                }

                // 1. Pre-composed Android Overlay (from Desktop Studio) — direct copy!
                File preOverlay = new File(modDir, "overlay");
                if ("overlay".equals(kind) && preOverlay.isDirectory()) {
                    ModStore.copyDir(preOverlay, overlayDir);
                    continue;
                }

                // Read manifest duelRules if present
                File manifestFile = new File(modDir, "mod-manifest.json");
                if (manifestFile.isFile()) {
                    try {
                        JSONObject mf = new JSONObject(ModStore.readUtf8(manifestFile));
                        String dr = mf.optString("duelRules", "").trim().toLowerCase(Locale.ROOT);
                        if (!dr.isEmpty()) requestedDuelRules = dr;
                    } catch (Exception ignored) {}
                }

                // 2. Declarative cards/*.json
                File cardsDir = new File(modDir, "cards");
                if (cardsDir.isDirectory()) {
                    File[] cardFiles = cardsDir.listFiles();
                    if (cardFiles != null) {
                        for (File cf : cardFiles) {
                            if (!cf.isFile() || !cf.getName().endsWith(".json")) continue;
                            if (content == null) content = readZipJson(vanilla, "content.json");
                            if (engine == null) engine = readZipJson(vanilla, "engine-data.json");
                            if (zh == null) zh = readZipJson(vanilla, "i18n/zh.json");

                            List<JSONObject> list = parseJsonObjects(ModStore.readUtf8(cf));
                            JSONArray cCards = content.optJSONArray("cards");
                            JSONObject zhCards = zh.optJSONObject("cards");
                            for (JSONObject card : list) {
                                int cid = card.optInt("id", 0);
                                if (cid <= 0) continue;
                                JSONObject data = card.optJSONObject("data");
                                int typeMask = data != null ? data.optInt("type", 0) : 0;
                                if ((typeMask & (0x2000 | 0x800000 | 0x4000000)) != 0) hasModernExtraDeck = true;
                                if ((typeMask & (0x1000000 | 0x4000000)) != 0) hasPendulumOrLink = true;
                                if (cCards != null) upsertById(cCards, cid, card);
                                if (data != null) engine.put(String.valueOf(cid), data);
                                if (zhCards != null) {
                                    // i18n/zh.json stores cards[id] as a 2-element array [zhName, zhDesc]!
                                    JSONArray zArr = new JSONArray();
                                    zArr.put(card.optString("zhName", card.optString("name", "")));
                                    zArr.put(card.optString("zhDesc", card.optString("desc", "")));
                                    zhCards.put(String.valueOf(cid), zArr);
                                }
                                emitCardArt(modDir, overlayDir, vanilla, cid);
                            }
                        }
                    }
                }

                // 3. Declarative packs/*.json
                File packsDir = new File(modDir, "packs");
                if (packsDir.isDirectory()) {
                    File[] packFiles = packsDir.listFiles();
                    if (packFiles != null) {
                        for (File pf : packFiles) {
                            if (!pf.isFile() || !pf.getName().endsWith(".json")) continue;
                            if (content == null) content = readZipJson(vanilla, "content.json");
                            if (campaign == null) campaign = readZipJson(vanilla, "campaign-files.json");
                            List<JSONObject> list = parseJsonObjects(ModStore.readUtf8(pf));
                            JSONArray cPacks = content.optJSONArray("packs");
                            JSONArray campPacks = new JSONArray(campaign.optString("data/packs.json", "[]"));
                            for (JSONObject pack : list) {
                                String pid = pack.optString("id", "").trim();
                                if (pid.isEmpty()) continue;
                                if (cPacks != null) upsertByStringId(cPacks, pid, pack);
                                upsertByStringId(campPacks, pid, pack);
                            }
                            campaign.put("data/packs.json", campPacks.toString());
                        }
                    }
                }

                // 3b. Preconstructed decks & decks/*.ydk
                File decksDir = new File(modDir, "decks");
                if (decksDir.isDirectory()) {
                    File[] ydks = decksDir.listFiles();
                    if (ydks != null) {
                        for (File yf : ydks) {
                            if (yf.isFile() && yf.getName().toLowerCase(Locale.ROOT).endsWith(".ydk")) {
                                if (campaign == null) campaign = readZipJson(vanilla, "campaign-files.json");
                                campaign.put("data/decks/" + yf.getName(), ModStore.readUtf8(yf));
                            }
                        }
                    }
                }

                // 4. Lua card scripts (lua/cards/*.lua & scripts/*.lua) + lua/lib/*.lua
                for (String subPath : new String[]{"lua/cards", "scripts"}) {
                    File luaDir = new File(modDir, subPath);
                    if (luaDir.isDirectory()) {
                        File[] luas = luaDir.listFiles();
                        if (luas != null && luas.length > 0) {
                            if (scripts == null) scripts = readZipJson(vanilla, "scripts-0.json");
                            for (File lf : luas) {
                                if (lf.isFile() && lf.getName().toLowerCase(Locale.ROOT).endsWith(".lua")) {
                                    scripts.put(lf.getName(), ModStore.readUtf8(lf));
                                    ensureGetId3 = true;
                                }
                            }
                        }
                    }
                }
                File luaLibDir = new File(modDir, "lua/lib");
                if (luaLibDir.isDirectory()) {
                    File[] libs = luaLibDir.listFiles();
                    if (libs != null && libs.length > 0) {
                        Arrays.sort(libs, Comparator.comparing(File::getName));
                        if (scripts == null) scripts = readZipJson(vanilla, "scripts-0.json");
                        StringBuilder util = new StringBuilder(scripts.optString("utility.lua", ""));
                        for (File lf : libs) {
                            if (lf.isFile() && lf.getName().toLowerCase(Locale.ROOT).endsWith(".lua")) {
                                util.append("\n-- [MOD LIB: ").append(modId).append("/").append(lf.getName()).append("]\n");
                                util.append(ModStore.readUtf8(lf)).append("\n");
                                ensureGetId3 = true;
                            }
                        }
                        scripts.put("utility.lua", util.toString());
                    }
                }

                // 5. Campaign file overrides (campaign/**)
                File campDir = new File(modDir, "campaign");
                if (campDir.isDirectory()) {
                    if (campaign == null) campaign = readZipJson(vanilla, "campaign-files.json");
                    mergeCampaignFiles(campDir, campDir, campaign);
                }

                // 6. Auto-discovered JS plugins & CSS styles (styles/ and css/)
                File jsDir = new File(modDir, "js");
                if (jsDir.isDirectory()) {
                    collectAssets(jsDir, jsDir, new File(overlayDir, "mods/" + modId + "/js"), "mods/" + modId + "/js", ".js", extraJsPaths);
                }
                File cssDir = new File(modDir, "styles");
                if (cssDir.isDirectory()) {
                    collectAssets(cssDir, cssDir, new File(overlayDir, "mods/" + modId + "/styles"), "mods/" + modId + "/styles", ".css", extraCssPaths);
                }
                File cssDir2 = new File(modDir, "css");
                if (cssDir2.isDirectory()) {
                    collectAssets(cssDir2, cssDir2, new File(overlayDir, "mods/" + modId + "/css"), "mods/" + modId + "/css", ".css", extraCssPaths);
                }

                // 7. Arbitrary static overrides (overrides/**)
                File ovDir = new File(modDir, "overrides");
                if (ovDir.isDirectory()) {
                    ModStore.copyDir(ovDir, overlayDir);
                }
            }

            // Upgrade utility.lua GetID() to 3 return values for modern ocgcore scripts
            if (ensureGetId3 && scripts != null) {
                String util = scripts.optString("utility.lua", "");
                if (util.contains(GET_ID_OLD)) {
                    scripts.put("utility.lua", util.replace(GET_ID_OLD, GET_ID_NEW));
                }
            }

            // Built-in Player Trainer (修改器) Python backend hook + optional startup presets
            if (enableTrainer || developerMode) {
                if (campaign == null) campaign = readZipJson(vanilla, "campaign-files.json");
                String mbCode = campaign.optString("mobile_backend.py", "");
                if (!mbCode.isEmpty() && !mbCode.contains("# mod-trainer-backend:v2")) {
                    campaign.put("mobile_backend.py", mbCode + "\n\n" + buildTrainerBackendPythonHook());
                }
                if (trainerConfig != null && (trainerConfig.optBoolean("unlockAllChars", false) || trainerConfig.optBoolean("unlockAllVariants", false))) {
                    unlockAllVariantsJs = true;
                    String storagePy = campaign.optString("storage.py", "");
                    if (!storagePy.isEmpty() && !storagePy.contains("# android-trainer-unlock")) {
                        StringBuilder inj = new StringBuilder();
                        if (trainerConfig.optBoolean("unlockAllChars", false)) {
                            inj.append("\n p['unlock_all']=True;p['unlocked']=list(content.PLAYABLE_IDS) # android-trainer-unlock");
                        }
                        if (trainerConfig.optBoolean("unlockAllVariants", false)) {
                            inj.append("\n p['variants_unlocked']=True # android-trainer-unlock");
                        }
                        storagePy = storagePy.replace("p.setdefault(k,v)", "p.setdefault(k,v)" + inj);
                        campaign.put("storage.py", storagePy);
                    }
                }
                if (trainerConfig != null) {
                    int goldBonus = trainerConfig.optInt("startingGoldBonus", 0);
                    int startLp = trainerConfig.optInt("startingLp", 0);
                    boolean freeShop = trainerConfig.optBoolean("freeShop", false);
                    if (goldBonus > 0 || startLp > 0 || freeShop) {
                        String campPy = campaign.optString("campaign.py", "");
                        if (!campPy.isEmpty() && !campPy.contains("# android-trainer-run")) {
                            StringBuilder hook = new StringBuilder("\n# android-trainer-run\n");
                            hook.append("_tr_orig_new_run = new_run\n");
                            hook.append("def new_run(*a, **kw):\n");
                            hook.append(" r = _tr_orig_new_run(*a, **kw)\n");
                            if (goldBonus > 0) hook.append(" r['gold'] = int(r.get('gold', 0)) + ").append(goldBonus).append("\n");
                            if (startLp > 0) hook.append(" r['lp'] = ").append(startLp).append("\n");
                            if (freeShop) {
                                hook.append(" for _it in r.get('shop', []):\n");
                                hook.append("  if isinstance(_it, dict) and 'price' in _it: _it['price'] = 0\n");
                            }
                            hook.append(" return r\n");
                            campaign.put("campaign.py", campPy + hook);
                        }
                    }
                }
                ModStore.writeUtf8(new File(overlayDir, "mod-android-trainer.js"),
                    buildTouchTrainerScript(developerMode, state.optString("lang", "zh")));
                extraJsPaths.add("mod-android-trainer.js");
            }

            if (unlockAllVariantsJs) {
                String diffJs = readOverlayOrZip(overlayDir, vanilla, "difficulty-preference.js");
                if (diffJs != null && diffJs.contains("return !!profile.variants_unlocked||")) {
                    diffJs = diffJs.replace("return !!profile.variants_unlocked||", "return true||!!profile.variants_unlocked||");
                    ModStore.writeUtf8(new File(overlayDir, "difficulty-preference.js"), diffJs);
                }
            }

            // Modern Era MR2-MR5 auto-unlock (safe targeted patch matching Desktop ModCompositor.cs!)
            String effectiveRules = !requestedDuelRules.isEmpty() ? requestedDuelRules
                : (hasPendulumOrLink ? "mr5" : (hasModernExtraDeck ? "mr2" : ""));
            if (hasModernExtraDeck || !effectiveRules.isEmpty()) {
                if (campaign == null) campaign = readZipJson(vanilla, "campaign-files.json");
                String campPy = campaign.optString("campaign.py", "");
                if (campPy.contains("['type'] & 64")) {
                    campaign.put("campaign.py", campPy.replace("['type'] & 64", "['type'] & (64 | 0x2000 | 0x800000 | 0x4000000)"));
                }

                String fullExtraMask = "(64|0x2000|0x800000|0x4000000)";
                for (String jsName : new String[]{"mobile.js", "draft-ui.js", "tag-ui.js", "run-card.js"}) {
                    String jsText = readOverlayOrZip(overlayDir, vanilla, jsName);
                    if (jsText != null) {
                        String patched = jsText
                            .replace(".data.type&64", ".data.type&" + fullExtraMask)
                            .replace(".data.type&0x40", ".data.type&" + fullExtraMask);
                        if (!patched.equals(jsText)) {
                            ModStore.writeUtf8(new File(overlayDir, jsName), patched);
                        }
                    }
                }

                if (!effectiveRules.isEmpty()) {
                    String engJs = readOverlayOrZip(overlayDir, vanilla, "engine.js");
                    if (engJs != null) {
                        String modeExpr = "mr5".equals(effectiveRules) ? "(D.MODE_MR5??D.MODE_MR2)"
                            : "mr4".equals(effectiveRules) ? "(D.MODE_MR4??D.MODE_MR2)"
                            : "mr3".equals(effectiveRules) ? "(D.MODE_MR3??D.MODE_MR2)"
                            : "mr2".equals(effectiveRules) ? "(D.MODE_MR2??D.MODE_MR1)"
                            : "(D.MODE_MR1??D.MODE_GOAT)";
                        String patchedEng = engJs
                            .replace("flags:rules===2?D.MODE_MR2:(D.MODE_MR1??D.MODE_GOAT)", "flags:rules===2?D.MODE_MR2:" + modeExpr)
                            .replace("[[L.MZONE,0,5],[L.SZONE,8,8]]", "[[L.MZONE,0,7],[L.SZONE,8,8]]")
                            .replace("monsters:this.query(p,L.MZONE).slice(0,5)", "monsters:this.query(p,L.MZONE).slice(0,7)");
                        if (!patchedEng.equals(engJs)) {
                            ModStore.writeUtf8(new File(overlayDir, "engine.js"), patchedEng);
                        }
                    }
                }
            }

            // Write modified JSON payloads
            if (content != null) ModStore.writeUtf8(new File(overlayDir, "content.json"), content.toString());
            if (engine != null) ModStore.writeUtf8(new File(overlayDir, "engine-data.json"), engine.toString());
            if (scripts != null) ModStore.writeUtf8(new File(overlayDir, "scripts-0.json"), scripts.toString());
            if (campaign != null) ModStore.writeUtf8(new File(overlayDir, "campaign-files.json"), campaign.toString());
            if (zh != null) ModStore.writeUtf8(new File(overlayDir, "i18n/zh.json"), zh.toString());

            // Inject extra CSS/JS into index.html if any
            if (!extraCssPaths.isEmpty() || !extraJsPaths.isEmpty()) {
                String html = readOverlayOrZip(overlayDir, vanilla, "index.html");
                if (html != null) {
                    StringBuilder inject = new StringBuilder();
                    for (String css : extraCssPaths) {
                        inject.append("\n<link rel=\"stylesheet\" href=\"./").append(css).append("\">");
                    }
                    for (String js : extraJsPaths) {
                        inject.append("\n<script type=\"module\" src=\"./").append(js).append("\"></script>");
                    }
                    if (html.contains("</body>")) {
                        html = html.replace("</body>", inject + "\n</body>");
                    } else if (html.contains("</head>")) {
                        html = html.replace("</head>", inject + "\n</head>");
                    } else {
                        html += inject;
                    }
                    ModStore.writeUtf8(new File(overlayDir, "index.html"), html);
                }
            }

            // Apply -mod identity tag, safe official Worker isolation, and Hide Online Multiplayer when modded!
            String baseVer = state.optString("vanillaVersion", "0.0.0");
            applyModTagAndIsolation(overlayDir, vanilla, baseVer, enabled.size(), hideOnlineWhenModded);
        }

        state.put("applied", true);
        state.put("appliedCount", enabled.size());
        store.saveState(state);
        if (cb != null) cb.onProgress("Applied " + enabled.size() + " MOD(s)!", 100);
        return enabled.size();
    }

    private static void applyModTagAndIsolation(
        File overlayDir,
        ZipFile vanilla,
        String baseVer,
        int modCount,
        boolean hideOnlineWhenModded
    ) throws IOException {
        String cleanVer = (baseVer == null || baseVer.trim().isEmpty()) ? "0.0.0" : baseVer.trim();
        String modTag = cleanVer.endsWith("-mod") ? cleanVer : (cleanVer + "-mod");
        ModStore.writeUtf8(new File(overlayDir, "replay-version.js"),
            "export const replayVersion=" + JSONObject.quote(modTag) + ";\n");

        // Keep local patch-notes lookup working with "-mod" stripped
        String pn = readOverlayOrZip(overlayDir, vanilla, "patch-notes.js");
        if (pn != null && pn.contains("versions.find(v=>v.version===replayVersion)")) {
            pn = pn.replace(
                "versions.find(v=>v.version===replayVersion)",
                "versions.find(v=>v.version===String(replayVersion).replace(/-mod.*$/,''))");
            ModStore.writeUtf8(new File(overlayDir, "patch-notes.js"), pn);
        }

        // Safely isolate official global-stats & replays Cloudflare Workers (without breaking syntax!)
        if (!new File(overlayDir, "multiplayer-config.json").isFile()) {
            String gs = readOverlayOrZip(overlayDir, vanilla, "global-stats.js");
            if (gs != null && gs.contains("async function syncBatch(){") && !gs.contains("mod-isolated")) {
                gs = gs.replace(
                    "async function syncBatch(){",
                    "async function syncBatch(){if(/hadow--un--esktop\\.workers\\.dev/i.test(String(endpoint||'')))return;/* mod-isolated */");
                ModStore.writeUtf8(new File(overlayDir, "global-stats.js"), gs);
            }
            String rp = readOverlayOrZip(overlayDir, vanilla, "replays.js");
            if (rp != null && rp.contains("export async function sendReplayBatch(rows,request=fetch){") && !rp.contains("Modded build")) {
                rp = rp.replace(
                    "export async function sendReplayBatch(rows,request=fetch){",
                    "export async function sendReplayBatch(rows,request=fetch){if(/hadow--un--esktop\\.workers\\.dev/i.test(String(REPLAY_ENDPOINT||'')))throw Error('Modded build: uploads disabled.');");
                ModStore.writeUtf8(new File(overlayDir, "replays.js"), rp);
            }
        }

        // Patch mobile.js SAFELY:
        // 1) Visible MOD badge ONLY on the exact <small class="title-version"> tag (never touching `import {replayVersion}`!)
        // 2) Hide Online multiplayer menu entry when hideOnlineWhenModded is enabled
        String mobileJs = readOverlayOrZip(overlayDir, vanilla, "mobile.js");
        if (mobileJs != null) {
            boolean modified = false;
            String verMarker = "<small class=\"title-version\">v${esc(replayVersion)}</small>";
            String modBadge = "<small class=\"title-version\">v${esc(replayVersion)} · MOD (" + modCount + ")</small>";
            if (mobileJs.contains(verMarker)) {
                mobileJs = mobileJs.replace(verMarker, modBadge);
                modified = true;
            }
            if (hideOnlineWhenModded) {
                if (mobileJs.contains("baseMenu.filter(m=>") && !mobileJs.contains("m[4]!=='online'")) {
                    mobileJs = mobileJs.replace("baseMenu.filter(m=>", "baseMenu.filter(m=>m[4]!=='online'&&");
                    modified = true;
                }
                if (mobileJs.contains("if(a==='online'){") && !mobileJs.contains("mod-online-hidden")) {
                    mobileJs = mobileJs.replace(
                        "if(a==='online'){",
                        "if(a==='online'){/* mod-online-hidden */toast('MOD 模式下已隐藏在线联机（可在加载器设置中开关，原版启动正常显示）');return;");
                    modified = true;
                }
            }
            if (modified) {
                ModStore.writeUtf8(new File(overlayDir, "mobile.js"), mobileJs);
            }
        }
    }

    private static String buildTrainerBackendPythonHook() {
        return "# mod-trainer-backend:v2\n"
            + "_mod_trainer_orig_dispatch = dispatch\n"
            + "def _mod_trainer_handle(val):\n"
            + " global run\n"
            + " op = str((val or {}).get('op') or '')\n"
            + " res = {'op': op}\n"
            + " if op == 'unlock-everything':\n"
            + "  import relic_fusions\n"
            + "  p = storage.profile()\n"
            + "  p.update(unlock_all=True,unlocked=list(content.PLAYABLE_IDS),variants_unlocked=True,relicless_unlocked=True,gallery_unlocked=True,cpu_viewer_unlocked=True,sleeves_all=True,testing_all_clears=True,wins=max(int(p.get('wins') or 0),1),character_levels={str(i):6 for i in content.PLAYABLE_IDS},fused_relics=sorted(set(p.get('fused_relics',[]))|set(relic_fusions.KEYS)))\n"
            + "  storage.write(storage.ROOT/'profile.json', p)\n"
            + "  all_ids = sorted(c['id'] for c in g.CARDS if isinstance(c.get('id'), int) and 0 < c['id'] < 191000000)\n"
            + "  storage.write(storage.ROOT/'registered-cards.json', {'cards': all_ids})\n"
            + "  res['unlocked'] = len(p['unlocked'])\n"
            + "  return res\n"
            + " if not run:\n"
            + "  raise ValueError('No active run. Start a run first.')\n"
            + " if op == 'add-gold':\n"
            + "  run['gold'] = max(0, int(run.get('gold', 0)) + int(val.get('delta', 0)))\n"
            + " elif op == 'set-gold':\n"
            + "  run['gold'] = max(0, int(val.get('amount', 0)))\n"
            + " elif op == 'set-lp':\n"
            + "  run['lp'] = max(1, int(val.get('amount', 8000)))\n"
            + " elif op == 'add-card':\n"
            + "  cid = int(val.get('id', 0))\n"
            + "  if cid not in g.BY_ID:\n"
            + "   raise ValueError('Unknown card ID: ' + str(cid))\n"
            + "  count = max(1, min(9, int(val.get('count', 1))))\n"
            + "  pool = run.setdefault('pool', [])\n"
            + "  sel = run.setdefault('selected', [])\n"
            + "  for _ in range(count):\n"
            + "   idx = len(pool); pool.append(cid)\n"
            + "   if bool(val.get('toDeck', False)) and idx not in sel: sel.append(idx)\n"
            + "  storage.register_collection(run)\n"
            + " elif op == 'free-shop':\n"
            + "  for it in run.get('shop', []):\n"
            + "   if isinstance(it, dict) and 'price' in it:\n"
            + "    it['price'] = 0; it['sold'] = False\n"
            + " elif op == 'restock-shop':\n"
            + "  g.restock(run, general_stock=True)\n"
            + " elif op == 'clear-curses':\n"
            + "  run['curses'] = []; run['boss_curse'] = None; run['cursed_artifacts'] = []\n"
            + " else:\n"
            + "  raise ValueError('Unknown op: ' + op)\n"
            + " g.save(run)\n"
            + " return res\n"
            + "def dispatch(raw):\n"
            + " try:\n"
            + "  req = json.loads(raw)\n"
            + " except Exception:\n"
            + "  return _mod_trainer_orig_dispatch(raw)\n"
            + " if isinstance(req, dict) and req.get('action') == 'mod-trainer':\n"
            + "  res = _mod_trainer_handle(req.get('value') or {})\n"
            + "  return json.dumps(dict(**state(), result=res), ensure_ascii=False)\n"
            + " return _mod_trainer_orig_dispatch(raw)\n";
    }

    private static String buildTouchTrainerScript(boolean devMode, String lang) {
        boolean isEn = "en".equalsIgnoreCase(lang);
        return "// Built-in Android Touch Trainer & Quick Panel\n"
            + "(function(){\n"
            + " if(window.__YGO_ANDROID_TRAINER__)return;window.__YGO_ANDROID_TRAINER__=true;\n"
            + " const EN=" + (isEn ? "true" : "false") + ",DEV=" + (devMode ? "true" : "false") + ";\n"
            + " let speed=1;const seen=new WeakSet(),origAnim=Element.prototype.animate;\n"
            + " Element.prototype.animate=function(...a){const an=origAnim.apply(this,a);if(an&&!seen.has(an)){seen.add(an);if(speed!==1)try{an.playbackRate*=speed}catch{}}return an};\n"
            + " function setSpeed(m){const prev=speed;speed=m;try{document.getAnimations().forEach(a=>{try{a.playbackRate*=speed/prev}catch{}})}catch{}}\n"
            + " const br=()=>window.shadowTest&&typeof window.shadowTest.command==='function'?window.shadowTest:null;\n"
            + " async function op(name,payload={}){const b=br();if(!b)throw Error(EN?'Engine still loading...':'游戏引擎加载中，请稍候...');const r=await b.command('mod-trainer',{op:name,...payload});try{const s=b.screen?.();if(s&&s!=='duel')b.go(s);}catch{}return r;}\n"
            + " async function winDuel(){const b=br();if(!b||b.screen?.()!=='duel')throw Error(EN?'Not in duel':'当前不在决斗中');const st=b.state?.(),eng=b.engine?.();await b.command('finish',{winner:0,lp:Number(st?.run?.lp||8000),reason:0,events:eng?.summary?.()||[]});try{eng?.destroy?.()}catch{}const ns=b.state?.();b.go(ns?.run?.stage==='shop'?'shop':ns?.run?.stage==='routes'?'routes':'ending');}\n"
            + " function mount(){\n"
            + "  if(document.getElementById('ygo-tr-fab'))return;\n"
            + "  const fab=document.createElement('button');fab.id='ygo-tr-fab';fab.textContent=EN?'⚡ Trainer':'⚡ 修改器';\n"
            + "  fab.style.cssText='position:fixed;right:10px;bottom:14px;z-index:2147483640;padding:7px 12px;border-radius:999px;border:1px solid rgba(56,189,248,.65);background:rgba(11,15,24,.88);color:#e0f2fe;font:700 12px/1 system-ui,sans-serif;box-shadow:0 4px 16px rgba(0,0,0,.5);backdrop-filter:blur(6px)';\n"
            + "  const dlg=document.createElement('dialog');dlg.id='ygo-tr-dlg';\n"
            + "  dlg.style.cssText='width:min(460px,92vw);max-height:84vh;overflow:auto;padding:14px;border-radius:14px;border:1px solid #334155;background:#0f172a;color:#f1f5f9;font:13px/1.4 system-ui,sans-serif';\n"
            + "  const css='display:grid;grid-template-columns:repeat(2,1fr);gap:8px;margin-top:10px';\n"
            + "  const bsty='padding:9px 10px;border-radius:9px;border:1px solid #334155;background:#1e293b;color:#f8fafc;font-weight:600;font-size:12px';\n"
            + "  dlg.innerHTML=`<div style=\"display:flex;justify-content:space-between;align-items:center\"><b>${EN?'In-Game Trainer':'游戏内实时修改器'}</b><button id=\"tr-close\" style=\"background:#334155;color:#fff;border:0;border-radius:8px;padding:4px 10px\">✕</button></div>`\n"
            + "   +`<div id=\"tr-msg\" style=\"margin-top:8px;font-size:12px;color:#38bdf8;min-height:16px\"></div>`\n"
            + "   +`<div style=\"${css}\">`\n"
            + "   +`<button data-op=\"gold500\" style=\"${bsty}\">+500 ${EN?'Gold':'金币'}</button>`\n"
            + "   +`<button data-op=\"gold9999\" style=\"${bsty}\">9999 ${EN?'Gold':'金币'}</button>`\n"
            + "   +`<button data-op=\"lp16000\" style=\"${bsty}\">16000 LP</button>`\n"
            + "   +`<button data-op=\"free-shop\" style=\"${bsty}\">${EN?'Free Shop':'商店0元购'}</button>`\n"
            + "   +`<button data-op=\"restock-shop\" style=\"${bsty}\">${EN?'Restock Shop':'刷新商店'}</button>`\n"
            + "   +`<button data-op=\"clear-curses\" style=\"${bsty}\">${EN?'Clear Curses':'清除诅咒'}</button>`\n"
            + "   +`<button data-op=\"win-duel\" style=\"${bsty};border-color:#22c55e;color:#86efac\">${EN?'Instant Win Duel':'直接赢得本场决斗'}</button>`\n"
            + "   +`<button data-op=\"unlock-all\" style=\"${bsty};border-color:#f59e0b;color:#fcd34d\">${EN?'Unlock Everything':'解锁全角色/形态/图鉴'}</button>`\n"
            + "   +`</div>`\n"
            + "   +`<div style=\"display:flex;gap:6px;margin-top:10px;align-items:center\"><span style=\"font-size:12px;color:#94a3b8\">${EN?'Speed':'倍速'}:</span>`\n"
            + "   +`<button data-spd=\"1\" style=\"${bsty};flex:1\">1x</button><button data-spd=\"2\" style=\"${bsty};flex:1\">2x</button><button data-spd=\"3\" style=\"${bsty};flex:1\">3x</button></div>`\n"
            + "   +`<div style=\"display:flex;gap:6px;margin-top:10px\"><input id=\"tr-cid\" type=\"number\" placeholder=\"${EN?'Card ID (e.g. 55144522)':'输入卡牌 ID (如 55144522)'}\" style=\"flex:1;padding:8px;border-radius:8px;border:1px solid #334155;background:#020617;color:#fff\"><button data-op=\"add-card\" style=\"${bsty};background:#0284c7;border-color:#38bdf8\">${EN?'Add Card':'加入卡池'}</button></div>`\n"
            + "   +`<div style=\"display:flex;gap:8px;margin-top:12px\"><button data-op=\"back-loader\" style=\"${bsty};flex:1;background:#334155\">${EN?'Back to MOD Loader':'返回 MOD 管理器'}</button></div>`;\n"
            + "  document.body.append(fab,dlg);\n"
            + "  const msg=dlg.querySelector('#tr-msg');\n"
            + "  const say=(t,err)=>{msg.textContent=t;msg.style.color=err?'#f87171':'#4ade80';};\n"
            + "  fab.onclick=()=>{if(!dlg.open)dlg.showModal();};\n"
            + "  dlg.querySelector('#tr-close').onclick=()=>dlg.close();\n"
            + "  dlg.addEventListener('click',async e=>{\n"
            + "   const spd=e.target.closest('[data-spd]');if(spd){setSpeed(Number(spd.dataset.spd)||1);say((EN?'Animation speed: ':'动画倍速：')+speed+'x');return;}\n"
            + "   const btn=e.target.closest('[data-op]');if(!btn)return;\n"
            + "   const k=btn.dataset.op;\n"
            + "   try{\n"
            + "    if(k==='gold500'){await op('add-gold',{delta:500});say(EN?'+500 Gold':'已增加 500 金币');}\n"
            + "    else if(k==='gold9999'){await op('set-gold',{amount:9999});say(EN?'Gold = 9999':'金币已设为 9999');}\n"
            + "    else if(k==='lp16000'){await op('set-lp',{amount:16000});say(EN?'LP = 16000':'LP 已设为 16000');}\n"
            + "    else if(k==='free-shop'){await op('free-shop',{unsold:true});say(EN?'Shop items set to 0G':'商店物品已免费');}\n"
            + "    else if(k==='restock-shop'){await op('restock-shop');say(EN?'Shop restocked':'商店已刷新');}\n"
            + "    else if(k==='clear-curses'){await op('clear-curses');say(EN?'Curses cleared':'已清除所有诅咒');}\n"
            + "    else if(k==='win-duel'){await winDuel();dlg.close();}\n"
            + "    else if(k==='unlock-all'){await op('unlock-everything');say(EN?'Unlocked all!':'已解锁全部内容！');}\n"
            + "    else if(k==='add-card'){const cid=Number(dlg.querySelector('#tr-cid').value);if(!cid)throw Error(EN?'Enter card ID':'请输入有效卡牌 ID');await op('add-card',{id:cid,count:1,toDeck:true});say((EN?'Added card #':'已添加卡牌 #')+cid);}\n"
            + "    else if(k==='back-loader'){window.ShadowNative?.returnToLoader?.();}\n"
            + "   }catch(err){say(err.message||String(err),true);}\n"
            + "  });\n"
            + "  if(DEV){\n"
            + "   const fps=document.createElement('div');fps.style.cssText='position:fixed;top:6px;left:6px;z-index:2147483639;padding:3px 7px;border-radius:6px;background:rgba(0,0,0,.7);color:#4ade80;font:600 11px monospace;pointer-events:none';\n"
            + "   document.body.append(fps);let f=0,lt=performance.now();\n"
            + "   const tick=t=>{f++;if(t-lt>=1000){fps.textContent=f+' FPS · DEV';f=0;lt=t;}requestAnimationFrame(tick);};requestAnimationFrame(tick);\n"
            + "  }\n"
            + " }\n"
            + " if(document.body)mount();else addEventListener('DOMContentLoaded',mount,{once:true});\n"
            + "})();\n";
    }

    private static void emitCardArt(File modDir, File overlayDir, ZipFile vanilla, int cid) throws IOException {
        File target = new File(overlayDir, "assets/cards/" + cid + ".jpg");
        if (target.isFile()) return;
        for (String ext : new String[]{".jpg", ".jpeg", ".png", ".webp"}) {
            File cand = new File(modDir, "art/" + cid + ext);
            if (cand.isFile()) {
                ModStore.copyFile(cand, target);
                return;
            }
        }
        ZipEntry back = vanilla.getEntry("assets/card-back.jpg");
        if (back != null) {
            File p = target.getParentFile();
            if (p != null) p.mkdirs();
            try (InputStream in = vanilla.getInputStream(back); OutputStream out = new FileOutputStream(target)) {
                byte[] buf = new byte[16384];
                int n;
                while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
            }
        }
    }

    private static void mergeCampaignFiles(File root, File cur, JSONObject campaign) throws Exception {
        File[] list = cur.listFiles();
        if (list == null) return;
        for (File f : list) {
            if (f.isDirectory()) {
                mergeCampaignFiles(root, f, campaign);
            } else {
                String rel = f.getAbsolutePath().substring(root.getAbsolutePath().length() + 1).replace('\\', '/');
                campaign.put(rel, ModStore.readUtf8(f));
            }
        }
    }

    private static void collectAssets(File root, File cur, File destBase, String urlBase, String ext, List<String> out) throws IOException {
        File[] list = cur.listFiles();
        if (list == null) return;
        Arrays.sort(list, Comparator.comparing(File::getName));
        for (File f : list) {
            if (f.isDirectory()) {
                collectAssets(root, f, destBase, urlBase, ext, out);
            } else if (f.getName().toLowerCase(Locale.ROOT).endsWith(ext)) {
                String rel = f.getAbsolutePath().substring(root.getAbsolutePath().length() + 1).replace('\\', '/');
                ModStore.copyFile(f, new File(destBase, rel));
                out.add(urlBase + "/" + rel);
            }
        }
    }

    private static String readOverlayOrZip(File overlayDir, ZipFile zip, String path) throws IOException {
        File ov = new File(overlayDir, path);
        if (ov.isFile()) return ModStore.readUtf8(ov);
        ZipEntry e = zip.getEntry(path);
        if (e == null) return null;
        try (InputStream in = zip.getInputStream(e)) {
            return ModStore.readStreamUtf8(in);
        }
    }

    private static JSONObject readZipJson(ZipFile zip, String path) throws Exception {
        ZipEntry e = zip.getEntry(path);
        if (e == null) return new JSONObject();
        try (InputStream in = zip.getInputStream(e)) {
            return new JSONObject(ModStore.readStreamUtf8(in));
        }
    }

    private static List<JSONObject> parseJsonObjects(String raw) throws Exception {
        String s = raw.trim();
        List<JSONObject> out = new ArrayList<>();
        if (s.startsWith("[")) {
            JSONArray a = new JSONArray(s);
            for (int i = 0; i < a.length(); i++) {
                JSONObject o = a.optJSONObject(i);
                if (o != null) out.add(o);
            }
        } else if (s.startsWith("{")) {
            out.add(new JSONObject(s));
        }
        return out;
    }

    private static void upsertById(JSONArray arr, int id, JSONObject item) throws Exception {
        for (int i = 0; i < arr.length(); i++) {
            JSONObject cur = arr.optJSONObject(i);
            if (cur != null && cur.optInt("id", -1) == id) {
                arr.put(i, item);
                return;
            }
        }
        arr.put(item);
    }

    private static void upsertByStringId(JSONArray arr, String id, JSONObject item) throws Exception {
        for (int i = 0; i < arr.length(); i++) {
            JSONObject cur = arr.optJSONObject(i);
            if (cur != null && id.equalsIgnoreCase(cur.optString("id", ""))) {
                arr.put(i, item);
                return;
            }
        }
        arr.put(item);
    }
}
