package com.ygorogue.modloader;

import org.json.JSONArray;
import org.json.JSONObject;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;

/**
 * Pure-Java zero-copy overlay compositor for Android:
 * Composes all enabled imported MODs (Android Overlays, Mod Spec v1, and YPK expansions)
 * into filesDir/game/overlay/ so GameActivity can serve untouched vanilla assets directly
 * from vanilla-web.zip with zero duplication!
 */
public final class ModComposerLite {
    private ModComposerLite() {}

    public static int composeAll(ModStore store, ModStore.ProgressCallback cb) throws Exception {
        File vanillaZipFile = store.getVanillaZip();
        if (!vanillaZipFile.isFile()) {
            throw new IOException("No vanilla web-*.zip installed yet. Download or import web-*.zip first.");
        }

        JSONObject state = store.loadState();
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

        if (enabled.isEmpty()) {
            state.put("applied", false);
            state.put("appliedCount", 0);
            store.saveState(state);
            if (cb != null) cb.onProgress("Vanilla mode (0 MODs enabled)", 100);
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
            String requestedDuelRules = "";

            for (int idx = 0; idx < enabled.size(); idx++) {
                JSONObject mod = enabled.get(idx);
                String modId = mod.optString("id");
                String kind = mod.optString("kind", "modspec");
                File modDir = new File(store.getModsDir(), modId);
                if (!modDir.isDirectory()) continue;

                if (cb != null) {
                    int pct = 20 + (idx * 60 / Math.max(1, enabled.size()));
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
                                if ((typeMask & (0x2000 | 0x800000 | 0x1000000 | 0x4000000)) != 0) {
                                    hasModernExtraDeck = true;
                                }
                                if (cCards != null) upsertById(cCards, cid, card);
                                if (data != null) engine.put(String.valueOf(cid), data);
                                if (zhCards != null) {
                                    JSONObject z = new JSONObject();
                                    z.put("name", card.optString("zhName", card.optString("name", "")));
                                    z.put("desc", card.optString("zhDesc", card.optString("desc", "")));
                                    zhCards.put(String.valueOf(cid), z);
                                }
                                // Ensure assets/cards/<id>.jpg exists (copy from mod art or fallback to card-back)
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

                // 4. Lua card scripts (lua/cards/*.lua)
                File luaCardsDir = new File(modDir, "lua/cards");
                if (luaCardsDir.isDirectory()) {
                    File[] luas = luaCardsDir.listFiles();
                    if (luas != null && luas.length > 0) {
                        if (scripts == null) scripts = readZipJson(vanilla, "scripts-0.json");
                        for (File lf : luas) {
                            if (lf.isFile() && lf.getName().endsWith(".lua")) {
                                scripts.put(lf.getName(), ModStore.readUtf8(lf));
                            }
                        }
                    }
                }

                // 5. Campaign file overrides (campaign/**)
                File campDir = new File(modDir, "campaign");
                if (campDir.isDirectory()) {
                    if (campaign == null) campaign = readZipJson(vanilla, "campaign-files.json");
                    mergeCampaignFiles(campDir, campDir, campaign);
                }

                // 6. Auto-discovered JS plugins & CSS styles
                File jsDir = new File(modDir, "js");
                if (jsDir.isDirectory()) {
                    collectAssets(jsDir, jsDir, new File(overlayDir, "mods/" + modId + "/js"), "mods/" + modId + "/js", ".js", extraJsPaths);
                }
                File cssDir = new File(modDir, "styles");
                if (cssDir.isDirectory()) {
                    collectAssets(cssDir, cssDir, new File(overlayDir, "mods/" + modId + "/styles"), "mods/" + modId + "/styles", ".css", extraCssPaths);
                }

                // 7. Arbitrary static overrides (overrides/**)
                File ovDir = new File(modDir, "overrides");
                if (ovDir.isDirectory()) {
                    ModStore.copyDir(ovDir, overlayDir);
                }
            }

            // Write modified JSON payloads
            if (content != null) ModStore.writeUtf8(new File(overlayDir, "content.json"), content.toString());
            if (engine != null) ModStore.writeUtf8(new File(overlayDir, "engine-data.json"), engine.toString());
            if (scripts != null) ModStore.writeUtf8(new File(overlayDir, "scripts-0.json"), scripts.toString());
            if (campaign != null) ModStore.writeUtf8(new File(overlayDir, "campaign-files.json"), campaign.toString());
            if (zh != null) ModStore.writeUtf8(new File(overlayDir, "i18n/zh.json"), zh.toString());

            // Modern Era MR5 auto-unlock if modern Extra Deck cards or duelRules present
            if (hasModernExtraDeck || !requestedDuelRules.isEmpty()) {
                String engJs = readOverlayOrZip(overlayDir, vanilla, "engine.js");
                if (engJs != null && engJs.contains("MODE_MR1")) {
                    String mode = "mr2".equals(requestedDuelRules) ? "MODE_MR2"
                        : "mr3".equals(requestedDuelRules) ? "MODE_MR3"
                        : "mr4".equals(requestedDuelRules) ? "MODE_MR4" : "MODE_MR5";
                    engJs = engJs.replace("MODE_MR1", mode);
                    ModStore.writeUtf8(new File(overlayDir, "engine.js"), engJs);
                }
            }

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
                    if (html.contains("</head>")) {
                        html = html.replace("</head>", inject + "\n</head>");
                    } else {
                        html += inject;
                    }
                    ModStore.writeUtf8(new File(overlayDir, "index.html"), html);
                }
            }

            // Apply -mod identity tag & official Cloudflare Worker isolation (consistent with Desktop Studio!)
            String baseVer = state.optString("vanillaVersion", "0.0.0");
            applyModTagAndIsolation(overlayDir, vanilla, baseVer, enabled.size());
        }

        state.put("applied", true);
        state.put("appliedCount", enabled.size());
        store.saveState(state);
        if (cb != null) cb.onProgress("Applied " + enabled.size() + " MOD(s)!", 100);
        return enabled.size();
    }

    private static void applyModTagAndIsolation(File overlayDir, ZipFile vanilla, String baseVer, int modCount) throws IOException {
        String modTag = baseVer.endsWith("-mod") ? baseVer : (baseVer + "-mod");
        ModStore.writeUtf8(new File(overlayDir, "replay-version.js"),
            "export const replayVersion=" + JSONObject.quote(modTag) + ";\n");

        // Block official global-stats & replays worker uploads unless a MOD supplied custom multiplayer-config.json
        if (!new File(overlayDir, "multiplayer-config.json").isFile()) {
            String gs = readOverlayOrZip(overlayDir, vanilla, "global-stats.js");
            if (gs != null && gs.contains("workers.dev")) {
                gs = gs.replaceAll("https://[^'\"\\s]*workers\\.dev[^'\"\\s]*", "");
                ModStore.writeUtf8(new File(overlayDir, "global-stats.js"), gs);
            }
            String rp = readOverlayOrZip(overlayDir, vanilla, "replays.js");
            if (rp != null && rp.contains("workers.dev")) {
                rp = rp.replaceAll("https://[^'\"\\s]*workers\\.dev[^'\"\\s]*", "");
                ModStore.writeUtf8(new File(overlayDir, "replays.js"), rp);
            }
        }

        String mobileJs = readOverlayOrZip(overlayDir, vanilla, "mobile.js");
        if (mobileJs != null && !mobileJs.contains("MOD (")) {
            String badge = " · MOD (" + modCount + ")";
            mobileJs = mobileJs.replace("replayVersion", "(replayVersion + " + JSONObject.quote(badge) + ")");
            ModStore.writeUtf8(new File(overlayDir, "mobile.js"), mobileJs);
        }
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
        // Fallback to vanilla card-back so startup image preload never fails!
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
