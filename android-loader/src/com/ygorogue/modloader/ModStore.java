package com.ygorogue.modloader;

import android.content.Context;
import android.database.Cursor;
import android.database.sqlite.SQLiteDatabase;
import android.net.Uri;
import android.provider.OpenableColumns;
import org.json.JSONArray;
import org.json.JSONObject;
import java.io.*;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.zip.ZipEntry;
import java.util.zip.ZipInputStream;

/**
 * Lightweight on-device MOD & web-*.zip storage for the Android Import-Only MOD Loader APK.
 * Zero external dependencies — pure Android SDK + built-in SQLite for .ypk (.cdb) imports!
 */
public final class ModStore {
    public static final String DEFAULT_REPO = "Thronior/ygo-rogue-duelist";

    private final Context ctx;
    private final File gameDir;
    private final File modsDir;
    private final File overlayDir;
    private final File vanillaZip;
    private final File stateFile;

    public interface ProgressCallback {
        void onProgress(String message, int percent);
    }

    public ModStore(Context ctx) {
        this.ctx = ctx.getApplicationContext();
        File root = this.ctx.getFilesDir();
        this.gameDir = new File(root, "game");
        this.modsDir = new File(root, "mods");
        this.overlayDir = new File(this.gameDir, "overlay");
        this.vanillaZip = new File(this.gameDir, "vanilla-web.zip");
        this.stateFile = new File(root, "loader-state.json");
        this.gameDir.mkdirs();
        this.modsDir.mkdirs();
    }

    public File getVanillaZip() { return vanillaZip; }
    public File getOverlayDir() { return overlayDir; }
    public File getModsDir() { return modsDir; }

    public synchronized JSONObject loadState() {
        try {
            if (stateFile.isFile()) {
                return new JSONObject(readUtf8(stateFile));
            }
        } catch (Exception ignored) {}
        JSONObject def = new JSONObject();
        try {
            def.put("lang", "zh");
            def.put("githubProxy", "");
            def.put("vanillaVersion", "");
            def.put("applied", false);
            def.put("appliedCount", 0);
            def.put("mods", new JSONArray());
        } catch (Exception ignored) {}
        return def;
    }

    public synchronized void saveState(JSONObject state) throws IOException {
        try {
            writeUtf8(stateFile, state.toString(2));
        } catch (org.json.JSONException e) {
            throw new IOException("Failed to serialize state JSON", e);
        }
    }

    public synchronized JSONObject getStatusJson() {
        JSONObject st = loadState();
        try {
            st.put("hasVanilla", vanillaZip.isFile() && vanillaZip.length() > 1024);
            st.put("vanillaSizeMb", vanillaZip.isFile() ? Math.round(vanillaZip.length() / 104857.6) / 10.0 : 0.0);
            st.put("overlayReady", overlayDir.isDirectory() && new File(overlayDir, "index.html").isFile());
        } catch (Exception ignored) {}
        return st;
    }

    public String resolveDisplayName(Uri uri) {
        try (Cursor c = ctx.getContentResolver().query(uri, null, null, null, null)) {
            if (c != null && c.moveToFirst()) {
                int idx = c.getColumnIndex(OpenableColumns.DISPLAY_NAME);
                if (idx >= 0) {
                    String n = c.getString(idx);
                    if (n != null && !n.trim().isEmpty()) return n.trim();
                }
            }
        } catch (Exception ignored) {}
        String seg = uri.getLastPathSegment();
        return seg != null ? seg : "imported.zip";
    }

    /** Import an offline local web-X.Y.Z.zip into app-private storage. */
    public String importLocalWebZip(Uri uri, ProgressCallback cb) throws Exception {
        String name = resolveDisplayName(uri);
        if (cb != null) cb.onProgress("Copying " + name + "...", 15);
        File tmp = new File(gameDir, "vanilla-web.zip.tmp");
        try (InputStream in = ctx.getContentResolver().openInputStream(uri);
             OutputStream out = new FileOutputStream(tmp)) {
            if (in == null) throw new IOException("Cannot read selected file.");
            byte[] buf = new byte[65536];
            int n;
            while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
        }
        String ver = detectVersionFromWebZip(tmp, name);
        if (vanillaZip.exists()) vanillaZip.delete();
        if (!tmp.renameTo(vanillaZip)) throw new IOException("Failed to finalize vanilla-web.zip");

        synchronized (this) {
            JSONObject st = loadState();
            st.put("vanillaVersion", ver);
            saveState(st);
        }
        if (cb != null) cb.onProgress("Imported web-" + ver + ".zip", 100);
        return ver;
    }

    /** Download latest official web-X.Y.Z.zip from GitHub (or optional mirror prefix like https://ghfast.top/). */
    public String downloadLatestWebZip(String proxyPrefix, ProgressCallback cb) throws Exception {
        String apiUrl = "https://api.github.com/repos/" + DEFAULT_REPO + "/releases/latest";
        if (cb != null) cb.onProgress("Checking latest GitHub release...", 5);
        HttpURLConnection conn = openConn(apiUrl);
        conn.setRequestProperty("Accept", "application/vnd.github+json");
        int code = conn.getResponseCode();
        if (code < 200 || code >= 300) throw new IOException("GitHub API HTTP " + code);
        String body = readStreamUtf8(conn.getInputStream());
        conn.disconnect();

        JSONObject rel = new JSONObject(body);
        String tag = rel.optString("tag_name", "latest").replaceFirst("^v", "");
        JSONArray assets = rel.optJSONArray("assets");
        String dlUrl = null;
        String assetName = "web-" + tag + ".zip";
        long totalSize = -1;
        if (assets != null) {
            for (int i = 0; i < assets.length(); i++) {
                JSONObject a = assets.getJSONObject(i);
                String n = a.optString("name", "");
                if (n.startsWith("web-") && n.endsWith(".zip")) {
                    dlUrl = a.optString("browser_download_url", "");
                    assetName = n;
                    totalSize = a.optLong("size", -1);
                    break;
                }
            }
        }
        if (dlUrl == null || dlUrl.isEmpty()) throw new IOException("No web-*.zip asset found in release " + tag);

        String finalUrl = applyProxy(dlUrl, proxyPrefix);
        if (cb != null) cb.onProgress("Downloading " + assetName + "...", 10);
        File tmp = new File(gameDir, "vanilla-web.zip.part");
        HttpURLConnection dl = openConn(finalUrl);
        dl.setInstanceFollowRedirects(true);
        int dlCode = dl.getResponseCode();
        if (dlCode < 200 || dlCode >= 300) throw new IOException("Download failed: HTTP " + dlCode);
        long contentLen = dl.getContentLengthLong();
        if (contentLen <= 0) contentLen = totalSize;

        long readTotal = 0;
        long lastReport = 0;
        try (InputStream in = dl.getInputStream();
             OutputStream out = new FileOutputStream(tmp)) {
            byte[] buf = new byte[65536];
            int n;
            while ((n = in.read(buf)) > 0) {
                out.write(buf, 0, n);
                readTotal += n;
                long now = System.currentTimeMillis();
                if (cb != null && now - lastReport > 300) {
                    lastReport = now;
                    int pct = contentLen > 0 ? (int) Math.min(98, 10 + (readTotal * 88 / contentLen)) : 50;
                    double mb = readTotal / (1024.0 * 1024.0);
                    double totMb = contentLen > 0 ? contentLen / (1024.0 * 1024.0) : 0;
                    String msg = contentLen > 0
                        ? String.format(Locale.US, "Downloading %s (%.1f / %.1f MB)", assetName, mb, totMb)
                        : String.format(Locale.US, "Downloading %s (%.1f MB)", assetName, mb);
                    cb.onProgress(msg, pct);
                }
            }
        } finally {
            dl.disconnect();
        }

        String ver = detectVersionFromWebZip(tmp, assetName);
        if (vanillaZip.exists()) vanillaZip.delete();
        if (!tmp.renameTo(vanillaZip)) throw new IOException("Failed to move downloaded web zip.");

        synchronized (this) {
            JSONObject st = loadState();
            st.put("vanillaVersion", ver);
            saveState(st);
        }
        if (cb != null) cb.onProgress("Ready: v" + ver, 100);
        return ver;
    }

    /**
     * Import any MOD archive (.zip Mod Spec v1, Desktop Studio Android Overlay .zip, Workspace Modpack .zip, or .ypk).
     */
    public JSONObject importModArchive(Uri uri, ProgressCallback cb) throws Exception {
        String displayName = resolveDisplayName(uri);
        String lower = displayName.toLowerCase(Locale.ROOT);
        if (cb != null) cb.onProgress("Extracting " + displayName + "...", 15);

        File stage = new File(ctx.getCacheDir(), "import-" + UUID.randomUUID().toString().substring(0, 8));
        stage.mkdirs();
        try {
            try (InputStream in = ctx.getContentResolver().openInputStream(uri)) {
                if (in == null) throw new IOException("Cannot read file.");
                unzipSafely(in, stage);
            }

            // Unwrap single top-level folder if present
            File root = unwrapSingleRootDir(stage);

            // Case 1: Desktop Studio Android Overlay ZIP (contains assets/web/)
            File overlayWeb = new File(root, "assets/web");
            if (overlayWeb.isDirectory()) {
                String id = sanitizeId(stripExt(displayName));
                File dest = new File(modsDir, id);
                deleteRecursive(dest);
                copyDir(overlayWeb, new File(dest, "overlay"));
                JSONObject meta = buildModRecord(id, stripExt(displayName), "1.0.0", "overlay",
                    "Android Pre-Composed Overlay (" + countFiles(new File(dest, "overlay")) + " files)");
                registerOrUpdateMod(meta);
                if (cb != null) cb.onProgress("Imported Android Overlay: " + id, 100);
                return meta;
            }

            // Case 2: Workspace Modpack ZIP (contains workspace-pack.json + mods/)
            File wsMods = new File(root, "mods");
            if (new File(root, "workspace-pack.json").isFile() && wsMods.isDirectory()) {
                File[] subDirs = wsMods.listFiles();
                JSONObject last = null;
                int count = 0;
                if (subDirs != null) {
                    Arrays.sort(subDirs, Comparator.comparing(File::getName));
                    for (File sub : subDirs) {
                        if (!sub.isDirectory()) continue;
                        last = importSingleModFolder(sub, sub.getName());
                        count++;
                    }
                }
                if (cb != null) cb.onProgress("Imported Workspace Modpack (" + count + " MODs)", 100);
                JSONObject res = new JSONObject();
                res.put("name", displayName);
                res.put("count", count);
                res.put("id", last != null ? last.optString("id") : "pack");
                return res;
            }

            // Case 3: YGOPro / MDPro3 .ypk archive (contains *.cdb / script / pics)
            File cdb = findFirstByExt(root, ".cdb");
            if (lower.endsWith(".ypk") || cdb != null) {
                if (cb != null) cb.onProgress("Parsing .ypk SQLite .cdb & scripts...", 50);
                JSONObject meta = importYpkFolder(root, stripExt(displayName), cdb);
                if (cb != null) cb.onProgress("Imported YPK: " + meta.optString("name"), 100);
                return meta;
            }

            // Case 4: Standard Mod Spec v1 folder (mod-manifest.json / cards / js / styles / overrides / etc.)
            JSONObject meta = importSingleModFolder(root, stripExt(displayName));
            if (cb != null) cb.onProgress("Imported MOD: " + meta.optString("name"), 100);
            return meta;
        } finally {
            deleteRecursive(stage);
        }
    }

    private JSONObject importSingleModFolder(File src, String fallbackName) throws Exception {
        String id = sanitizeId(fallbackName);
        String name = fallbackName;
        String version = "1.0.0";
        File manifest = new File(src, "mod-manifest.json");
        if (manifest.isFile()) {
            try {
                JSONObject m = new JSONObject(readUtf8(manifest));
                if (!m.optString("id", "").trim().isEmpty()) id = sanitizeId(m.getString("id"));
                if (!m.optString("name", "").trim().isEmpty()) name = m.getString("name");
                if (!m.optString("version", "").trim().isEmpty()) version = m.getString("version");
            } catch (Exception ignored) {}
        }
        File dest = new File(modsDir, id);
        deleteRecursive(dest);
        copyDir(src, dest);
        String summary = "Mod Spec v1 (" + countFiles(dest) + " files)";
        JSONObject rec = buildModRecord(id, name, version, "modspec", summary);
        registerOrUpdateMod(rec);
        return rec;
    }

    /** Pure Android SQLite .ypk -> Mod Spec v1 importer (zero native deps!). */
    private JSONObject importYpkFolder(File src, String fallbackName, File cdbFile) throws Exception {
        String id = sanitizeId(fallbackName);
        File dest = new File(modsDir, id);
        deleteRecursive(dest);
        dest.mkdirs();

        int cardCount = 0, luaCount = 0, artCount = 0;
        JSONArray cardsArr = new JSONArray();
        JSONArray packCommon = new JSONArray();

        if (cdbFile != null && cdbFile.isFile()) {
            SQLiteDatabase db = SQLiteDatabase.openDatabase(cdbFile.getAbsolutePath(), null, SQLiteDatabase.OPEN_READONLY);
            try (Cursor cur = db.rawQuery(
                "SELECT d.id, d.type, d.atk, d.def, d.level, d.race, d.attribute, t.name, t.desc " +
                "FROM datas d JOIN texts t ON d.id = t.id", null)) {
                while (cur.moveToNext()) {
                    int cid = cur.getInt(0);
                    int typeMask = cur.getInt(1);
                    int atk = cur.getInt(2);
                    int def = cur.getInt(3);
                    int rawLevel = cur.getInt(4);
                    long raceMask = cur.getLong(5);
                    int attrMask = cur.getInt(6);
                    String cname = cur.getString(7) != null ? cur.getString(7) : ("Card #" + cid);
                    String cdesc = cur.getString(8) != null ? cur.getString(8) : "";

                    JSONObject data = new JSONObject();
                    data.put("type", typeMask);
                    data.put("level", rawLevel & 0xFF);
                    data.put("attack", atk);
                    data.put("defense", def);
                    data.put("race", String.valueOf(raceMask));
                    data.put("attribute", attrMask);
                    int lscale = (rawLevel >> 24) & 0xFF;
                    int rscale = (rawLevel >> 16) & 0xFF;
                    if (lscale > 0 || rscale > 0) {
                        data.put("lscale", lscale);
                        data.put("rscale", rscale);
                    }

                    JSONObject card = new JSONObject();
                    card.put("id", cid);
                    card.put("name", cname);
                    card.put("zhName", cname);
                    card.put("desc", cdesc);
                    card.put("zhDesc", cdesc);
                    card.put("type", (typeMask & 1) != 0 ? "Effect Monster" : ((typeMask & 2) != 0 ? "Spell Card" : "Trap Card"));
                    card.put("level", rawLevel & 0xFF);
                    card.put("atk", atk);
                    card.put("defense", def);
                    card.put("race", "Monster");
                    card.put("attribute", "");
                    card.put("data", data);
                    cardsArr.put(card);
                    packCommon.put(cid);
                    cardCount++;
                }
            } finally {
                db.close();
            }
        }

        if (cardCount > 0) {
            File cardsDir = new File(dest, "cards");
            cardsDir.mkdirs();
            writeUtf8(new File(cardsDir, "ypk-cards.json"), cardsArr.toString(2));

            File packsDir = new File(dest, "packs");
            packsDir.mkdirs();
            JSONObject pack = new JSONObject();
            pack.put("id", ("YPK-" + id).toUpperCase(Locale.ROOT));
            pack.put("name", fallbackName);
            pack.put("zhName", fallbackName);
            pack.put("code", "YPK");
            pack.put("size", 9);
            pack.put("common", packCommon);
            writeUtf8(new File(packsDir, "ypk-pack.json"), pack.toString(2));
        }

        // Copy Lua scripts (script/c*.lua) & card art (pics/*)
        File scriptDir = new File(src, "script");
        if (scriptDir.isDirectory()) {
            File luaCards = new File(dest, "lua/cards");
            luaCards.mkdirs();
            File[] files = scriptDir.listFiles();
            if (files != null) {
                for (File f : files) {
                    if (f.isFile() && f.getName().toLowerCase(Locale.ROOT).endsWith(".lua")) {
                        copyFile(f, new File(luaCards, f.getName()));
                        luaCount++;
                    }
                }
            }
        }
        File picsDir = new File(src, "pics");
        if (picsDir.isDirectory()) {
            File artDest = new File(dest, "art");
            artDest.mkdirs();
            File[] files = picsDir.listFiles();
            if (files != null) {
                for (File f : files) {
                    if (f.isFile()) {
                        String ln = f.getName().toLowerCase(Locale.ROOT);
                        if (ln.endsWith(".jpg") || ln.endsWith(".jpeg") || ln.endsWith(".png") || ln.endsWith(".webp")) {
                            copyFile(f, new File(artDest, f.getName()));
                            artCount++;
                        }
                    }
                }
            }
        }

        JSONObject manifest = new JSONObject();
        manifest.put("schemaVersion", 1);
        manifest.put("id", id);
        manifest.put("name", fallbackName);
        manifest.put("version", "1.0.0");
        manifest.put("author", "YPK Import");
        writeUtf8(new File(dest, "mod-manifest.json"), manifest.toString(2));

        String summary = String.format(Locale.US, "YPK (%d cards · %d Lua · %d art)", cardCount, luaCount, artCount);
        JSONObject rec = buildModRecord(id, fallbackName, "1.0.0", "ypk", summary);
        registerOrUpdateMod(rec);
        return rec;
    }

    public synchronized void toggleMod(String id, boolean enabled) throws Exception {
        JSONObject st = loadState();
        JSONArray arr = st.optJSONArray("mods");
        if (arr == null) return;
        for (int i = 0; i < arr.length(); i++) {
            JSONObject m = arr.getJSONObject(i);
            if (id.equals(m.optString("id"))) {
                m.put("enabled", enabled);
                break;
            }
        }
        saveState(st);
    }

    public synchronized void moveMod(String id, int delta) throws Exception {
        JSONObject st = loadState();
        JSONArray arr = st.optJSONArray("mods");
        if (arr == null || arr.length() < 2) return;
        List<JSONObject> list = new ArrayList<>();
        int idx = -1;
        for (int i = 0; i < arr.length(); i++) {
            JSONObject m = arr.getJSONObject(i);
            list.add(m);
            if (id.equals(m.optString("id"))) idx = i;
        }
        int next = idx + delta;
        if (idx < 0 || next < 0 || next >= list.size()) return;
        Collections.swap(list, idx, next);
        JSONArray out = new JSONArray();
        for (JSONObject m : list) out.put(m);
        st.put("mods", out);
        saveState(st);
    }

    public synchronized void deleteMod(String id) throws Exception {
        deleteRecursive(new File(modsDir, id));
        JSONObject st = loadState();
        JSONArray arr = st.optJSONArray("mods");
        JSONArray out = new JSONArray();
        if (arr != null) {
            for (int i = 0; i < arr.length(); i++) {
                JSONObject m = arr.getJSONObject(i);
                if (!id.equals(m.optString("id"))) out.put(m);
            }
        }
        st.put("mods", out);
        saveState(st);
    }

    public synchronized void clearOverlay() throws Exception {
        deleteRecursive(overlayDir);
        JSONObject st = loadState();
        st.put("applied", false);
        st.put("appliedCount", 0);
        saveState(st);
    }

    private synchronized void registerOrUpdateMod(JSONObject rec) throws Exception {
        JSONObject st = loadState();
        JSONArray arr = st.optJSONArray("mods");
        if (arr == null) arr = new JSONArray();
        boolean replaced = false;
        for (int i = 0; i < arr.length(); i++) {
            if (rec.optString("id").equals(arr.getJSONObject(i).optString("id"))) {
                arr.put(i, rec);
                replaced = true;
                break;
            }
        }
        if (!replaced) arr.put(rec);
        st.put("mods", arr);
        saveState(st);
    }

    private static JSONObject buildModRecord(String id, String name, String ver, String kind, String summary) throws Exception {
        JSONObject o = new JSONObject();
        o.put("id", id);
        o.put("name", name);
        o.put("version", ver);
        o.put("kind", kind);
        o.put("summary", summary);
        o.put("enabled", true);
        return o;
    }

    private static String detectVersionFromWebZip(File zipFile, String fallbackName) {
        try (java.util.zip.ZipFile zf = new java.util.zip.ZipFile(zipFile)) {
            ZipEntry e = zf.getEntry("live-version.json");
            if (e != null) {
                JSONObject o = new JSONObject(readStreamUtf8(zf.getInputStream(e)));
                String v = o.optString("version", "").trim();
                if (!v.isEmpty()) return v;
            }
            ZipEntry b = zf.getEntry("build.json");
            if (b != null) {
                JSONObject o = new JSONObject(readStreamUtf8(zf.getInputStream(b)));
                String v = o.optString("version", "").trim();
                if (!v.isEmpty()) return v;
            }
        } catch (Exception ignored) {}
        String cleaned = fallbackName.replaceFirst("^web-", "").replaceFirst("\\.zip$", "");
        return cleaned.isEmpty() ? "unknown" : cleaned;
    }

    private static HttpURLConnection openConn(String urlStr) throws IOException {
        HttpURLConnection conn = (HttpURLConnection) new URL(urlStr).openConnection();
        conn.setConnectTimeout(15000);
        conn.setReadTimeout(60000);
        conn.setRequestProperty("User-Agent", "YGORogueDuelistModLoader-Android/1.3");
        return conn;
    }

    private static String applyProxy(String url, String proxyPrefix) {
        if (proxyPrefix == null || proxyPrefix.trim().isEmpty()) return url;
        String p = proxyPrefix.trim();
        if (!p.endsWith("/")) p += "/";
        return p + url;
    }

    private static void unzipSafely(InputStream in, File destDir) throws IOException {
        String canonDest = destDir.getCanonicalPath() + File.separator;
        try (ZipInputStream zis = new ZipInputStream(new BufferedInputStream(in))) {
            ZipEntry entry;
            byte[] buf = new byte[32768];
            while ((entry = zis.getNextEntry()) != null) {
                String name = entry.getName().replace('\\', '/');
                if (name.contains("..")) continue;
                File outFile = new File(destDir, name);
                if (!outFile.getCanonicalPath().startsWith(canonDest)) continue;
                if (entry.isDirectory()) {
                    outFile.mkdirs();
                } else {
                    File parent = outFile.getParentFile();
                    if (parent != null) parent.mkdirs();
                    try (FileOutputStream fos = new FileOutputStream(outFile)) {
                        int n;
                        while ((n = zis.read(buf)) > 0) fos.write(buf, 0, n);
                    }
                }
            }
        }
    }

    private static File unwrapSingleRootDir(File dir) {
        File[] children = dir.listFiles();
        if (children != null && children.length == 1 && children[0].isDirectory()) {
            String n = children[0].getName();
            if (!n.equals("assets") && !n.equals("cards") && !n.equals("js") && !n.equals("styles")
                && !n.equals("lua") && !n.equals("script") && !n.equals("pics") && !n.equals("mods")) {
                return children[0];
            }
        }
        return dir;
    }

    private static File findFirstByExt(File dir, String ext) {
        File[] list = dir.listFiles();
        if (list == null) return null;
        for (File f : list) {
            if (f.isFile() && f.getName().toLowerCase(Locale.ROOT).endsWith(ext)) return f;
            if (f.isDirectory()) {
                File sub = findFirstByExt(f, ext);
                if (sub != null) return sub;
            }
        }
        return null;
    }

    public static void copyDir(File src, File dst) throws IOException {
        if (!src.exists()) return;
        if (src.isDirectory()) {
            dst.mkdirs();
            File[] kids = src.listFiles();
            if (kids != null) {
                for (File k : kids) copyDir(k, new File(dst, k.getName()));
            }
        } else {
            copyFile(src, dst);
        }
    }

    public static void copyFile(File src, File dst) throws IOException {
        File p = dst.getParentFile();
        if (p != null) p.mkdirs();
        try (InputStream in = new FileInputStream(src); OutputStream out = new FileOutputStream(dst)) {
            byte[] buf = new byte[32768];
            int n;
            while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
        }
    }

    public static void deleteRecursive(File f) {
        if (f == null || !f.exists()) return;
        if (f.isDirectory()) {
            File[] kids = f.listFiles();
            if (kids != null) for (File k : kids) deleteRecursive(k);
        }
        f.delete();
    }

    public static int countFiles(File dir) {
        if (dir == null || !dir.exists()) return 0;
        if (dir.isFile()) return 1;
        int c = 0;
        File[] kids = dir.listFiles();
        if (kids != null) for (File k : kids) c += countFiles(k);
        return c;
    }

    public static String sanitizeId(String raw) {
        String s = raw == null ? "" : raw.toLowerCase(Locale.ROOT).replaceAll("[^a-z0-9_-]+", "-").replaceAll("^-+|-+$", "");
        return s.isEmpty() ? ("mod-" + System.currentTimeMillis() % 100000) : s;
    }

    public static String stripExt(String name) {
        int i = name.lastIndexOf('.');
        return i > 0 ? name.substring(0, i) : name;
    }

    public static String readUtf8(File f) throws IOException {
        try (InputStream in = new FileInputStream(f)) {
            return readStreamUtf8(in);
        }
    }

    public static String readStreamUtf8(InputStream in) throws IOException {
        ByteArrayOutputStream bos = new ByteArrayOutputStream();
        byte[] buf = new byte[16384];
        int n;
        while ((n = in.read(buf)) > 0) bos.write(buf, 0, n);
        return new String(bos.toByteArray(), StandardCharsets.UTF_8);
    }

    public static void writeUtf8(File f, String text) throws IOException {
        File p = f.getParentFile();
        if (p != null) p.mkdirs();
        try (FileOutputStream fos = new FileOutputStream(f)) {
            fos.write(text.getBytes(StandardCharsets.UTF_8));
        }
    }
}
