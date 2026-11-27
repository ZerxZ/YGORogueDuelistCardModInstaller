package com.ygorogue.modloader;

import android.app.Activity;
import android.content.Intent;
import android.content.pm.ActivityInfo;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.util.AtomicFile;
import android.view.Display;
import android.view.View;
import android.view.WindowManager;
import android.webkit.*;
import org.json.JSONObject;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;

/**
 * Full-screen offline native Android game container with Zero-Copy Overlay Serving:
 *   1. Serves modded files from filesDir/game/overlay/<path>
 *   2. Falls back directly to entries inside filesDir/game/vanilla-web.zip (zero duplicate disk space!)
 *   3. Neutralizes web-only PWA chrome (pwa.js / pwa.css / web-boot.js) so web-*.zip runs as a
 *      100% genuine native Android app with full landscape / orientation & ShadowNative support.
 */
public final class GameActivity extends Activity {
    private static final String HOST = "appassets.androidplatform.net";
    private static final int PICK_FILE = 4103;
    private static final int SAVE_DECK_IMAGE = 4102;

    private static final String NATIVE_WEB_BOOT_JS = "import './mobile.js';\n";
    private static final String NATIVE_PWA_JS_STUB = "export {};\n";
    private static final String NATIVE_PWA_CSS_STUB =
        "html,body{overscroll-behavior:none}" +
        ".web-install-menu,#web-app-options,.web-update-ready,#web-app-settings,#web-offline-offer,#web-save-recovery,#web-brave-warning{display:none!important;}\n";

    private WebView web;
    private ModStore store;
    private ZipFile vanillaZip;
    private File overlayDir;
    private boolean forceVanilla;
    private ValueCallback<Uri[]> pendingFiles;
    private byte[] pendingDeckImage;

    private static boolean bundled(Uri uri) {
        return "https".equals(uri.getScheme()) && HOST.equals(uri.getHost())
            && uri.getUserInfo() == null && (uri.getPort() == -1 || uri.getPort() == 443);
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        store = new ModStore(this);
        overlayDir = store.getOverlayDir();
        forceVanilla = getIntent() != null && getIntent().getBooleanExtra("forceVanilla", false);

        // Apply saved orientation preference immediately (supports landscape, portrait, or auto)
        applyRequestedOrientation(store.loadState().optString("orientation", "auto"));

        if (Build.VERSION.SDK_INT >= 28) {
            WindowManager.LayoutParams lp = getWindow().getAttributes();
            lp.layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES;
            getWindow().setAttributes(lp);
        }

        try {
            vanillaZip = new ZipFile(store.getVanillaZip());
        } catch (IOException e) {
            android.widget.Toast.makeText(this, "Missing vanilla web-*.zip", android.widget.Toast.LENGTH_LONG).show();
            finish();
            return;
        }

        applyImmersiveUi();

        web = new WebView(this);
        setContentView(web);
        web.setDefaultFocusHighlightEnabled(false);

        WebSettings s = web.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        s.setAllowFileAccess(false);
        s.setAllowContentAccess(false);
        s.setAllowFileAccessFromFileURLs(false);
        s.setAllowUniversalAccessFromFileURLs(false);
        s.setJavaScriptCanOpenWindowsAutomatically(false);
        s.setSupportMultipleWindows(false);
        s.setGeolocationEnabled(false);
        s.setMediaPlaybackRequiresUserGesture(false);
        s.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);

        web.addJavascriptInterface(new SaveStore(), "ShadowNative");
        web.setWebChromeClient(new WebChromeClient() {
            @Override
            public boolean onShowFileChooser(WebView view, ValueCallback<Uri[]> callback, FileChooserParams params) {
                synchronized (GameActivity.this) {
                    if (pendingFiles != null) pendingFiles.onReceiveValue(null);
                    pendingFiles = callback;
                }
                Intent intent = new Intent(Intent.ACTION_GET_CONTENT)
                    .addCategory(Intent.CATEGORY_OPENABLE).setType("*/*");
                try {
                    startActivityForResult(Intent.createChooser(intent, null), PICK_FILE);
                } catch (Exception e) {
                    synchronized (GameActivity.this) { pendingFiles = null; }
                    callback.onReceiveValue(null);
                }
                return true;
            }
        });

        web.setWebViewClient(new WebViewClient() {
            @Override
            public boolean shouldOverrideUrlLoading(WebView w, WebResourceRequest r) {
                if (r.isForMainFrame() && bundled(r.getUrl())) return false;
                if (r.isForMainFrame() && "https".equals(r.getUrl().getScheme()) && "discord.gg".equals(r.getUrl().getHost())) {
                    try { startActivity(new Intent(Intent.ACTION_VIEW, r.getUrl())); } catch (Exception ignored) {}
                }
                return true;
            }

            @Override
            public WebResourceResponse shouldInterceptRequest(WebView w, WebResourceRequest r) {
                Uri uri = r.getUrl();
                String path = uri.getPath();
                try {
                    if (!HOST.equals(uri.getHost()) && "https".equals(uri.getScheme())) return null;
                    if (!bundled(uri) || path == null || path.contains("..") || path.contains("\\")) {
                        throw new IOException("Blocked");
                    }
                    path = path.substring(1);
                    if (path.isEmpty()) path = "index.html";

                    // Keep APK strictly native-app mode (never load Web PWA install banners or SW update loops)
                    if ("web-boot.js".equals(path)) {
                        return makeResponse("text/javascript", NATIVE_WEB_BOOT_JS.getBytes(StandardCharsets.UTF_8));
                    }
                    if ("pwa.js".equals(path)) {
                        return makeResponse("text/javascript", NATIVE_PWA_JS_STUB.getBytes(StandardCharsets.UTF_8));
                    }
                    if ("pwa.css".equals(path)) {
                        return makeResponse("text/css", NATIVE_PWA_CSS_STUB.getBytes(StandardCharsets.UTF_8));
                    }

                    InputStream stream = openOverlayOrVanilla(path);
                    if (stream == null) throw new IOException("Not found: " + path);

                    String ext = "";
                    int dot = path.lastIndexOf('.');
                    if (dot >= 0) ext = path.substring(dot + 1).toLowerCase(Locale.ROOT);
                    String mime = MimeTypeMap.getSingleton().getMimeTypeFromExtension(ext);
                    if ("js".equals(ext) || "mjs".equals(ext)) mime = "text/javascript";
                    else if ("wasm".equals(ext)) mime = "application/wasm";
                    else if ("m4a".equals(ext)) mime = "audio/mp4";
                    else if ("json".equals(ext)) mime = "application/json";
                    else if ("css".equals(ext)) mime = "text/css";
                    else if ("svg".equals(ext)) mime = "image/svg+xml";
                    if (mime == null) mime = "application/octet-stream";

                    Map<String, String> headers = new HashMap<>();
                    headers.put("X-Content-Type-Options", "nosniff");
                    headers.put("Referrer-Policy", "no-referrer");
                    headers.put("Cache-Control", "no-store");
                    return new WebResourceResponse(mime, "UTF-8", 200, "OK", headers, stream);
                } catch (IOException e) {
                    return new WebResourceResponse("text/plain", "UTF-8", 404, "Not Found",
                        Collections.emptyMap(), new ByteArrayInputStream(new byte[0]));
                }
            }
        });

        web.loadUrl("https://" + HOST + "/index.html");
    }

    private static WebResourceResponse makeResponse(String mime, byte[] bytes) {
        Map<String, String> headers = new HashMap<>();
        headers.put("X-Content-Type-Options", "nosniff");
        headers.put("Cache-Control", "no-store");
        return new WebResourceResponse(mime, "UTF-8", 200, "OK", headers, new ByteArrayInputStream(bytes));
    }

    private void applyImmersiveUi() {
        getWindow().getDecorView().setSystemUiVisibility(
            View.SYSTEM_UI_FLAG_FULLSCREEN
                | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                | View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN);
    }

    private void applyRequestedOrientation(String mode) {
        final int o = "portrait".equals(mode)
            ? ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT
            : "landscape".equals(mode)
                ? ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
                : ActivityInfo.SCREEN_ORIENTATION_FULL_USER;
        if (getRequestedOrientation() != o) {
            setRequestedOrientation(o);
        }
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) {
            applyImmersiveUi();
        }
    }

    private InputStream openOverlayOrVanilla(String relPath) throws IOException {
        if (!forceVanilla) {
            File ov = new File(overlayDir, relPath);
            if (ov.isFile()) return new FileInputStream(ov);
        }
        synchronized (this) {
            if (vanillaZip == null) return null;
            ZipEntry entry = vanillaZip.getEntry(relPath);
            if (entry == null) return null;
            // Buffer zip stream into byte[] so concurrent WebView requests never race on shared ZipFile streams
            try (InputStream zin = vanillaZip.getInputStream(entry)) {
                ByteArrayOutputStream bos = new ByteArrayOutputStream(Math.max(4096, (int) Math.min(entry.getSize(), 4_000_000)));
                byte[] buf = new byte[32768];
                int n;
                while ((n = zin.read(buf)) > 0) bos.write(buf, 0, n);
                return new ByteArrayInputStream(bos.toByteArray());
            }
        }
    }

    @Override
    protected void onActivityResult(int req, int res, Intent data) {
        super.onActivityResult(req, res, data);
        if (req == PICK_FILE) {
            ValueCallback<Uri[]> cb;
            synchronized (this) { cb = pendingFiles; pendingFiles = null; }
            if (cb != null) cb.onReceiveValue(WebChromeClient.FileChooserParams.parseResult(res, data));
            return;
        }
        if (req == SAVE_DECK_IMAGE && res == RESULT_OK && data != null && data.getData() != null) {
            final byte[] bytes;
            synchronized (this) { bytes = pendingDeckImage; pendingDeckImage = null; }
            if (bytes != null) {
                final Uri uri = data.getData();
                new Thread(() -> {
                    try (OutputStream out = getContentResolver().openOutputStream(uri, "wt")) {
                        if (out != null) { out.write(bytes); out.flush(); }
                    } catch (Exception ignored) {}
                }).start();
            }
        }
    }

    @Override
    protected void onPause() {
        if (web != null) {
            web.evaluateJavascript("window.suspendAudio && window.suspendAudio()", null);
            web.onPause();
        }
        super.onPause();
    }

    @Override
    protected void onResume() {
        super.onResume();
        applyImmersiveUi();
        if (web != null) {
            web.onResume();
            web.evaluateJavascript("window.resumeAudio && window.resumeAudio()", null);
        }
    }

    @Override
    public void onBackPressed() {
        if (web != null) {
            web.evaluateJavascript("window.androidBack ? window.androidBack() : false", val -> {
                if ("false".equals(val) || "null".equals(val)) finish();
            });
        } else {
            super.onBackPressed();
        }
    }

    @Override
    protected void onDestroy() {
        if (web != null) {
            web.removeJavascriptInterface("ShadowNative");
            web.stopLoading();
            web.destroy();
            web = null;
        }
        synchronized (this) {
            if (vanillaZip != null) {
                try { vanillaZip.close(); } catch (IOException ignored) {}
                vanillaZip = null;
            }
        }
        super.onDestroy();
    }

    public final class SaveStore {
        private final AtomicFile saveFile = new AtomicFile(new File(getFilesDir(), "campaign.json"));

        @JavascriptInterface
        public synchronized String load() {
            try { return new String(saveFile.readFully(), StandardCharsets.UTF_8); }
            catch (IOException e) { return "{}"; }
        }

        @JavascriptInterface
        public synchronized boolean store(String data) {
            if (data == null || data.length() > 16_000_000) return false;
            FileOutputStream out = null;
            try {
                out = saveFile.startWrite();
                out.write(data.getBytes(StandardCharsets.UTF_8));
                saveFile.finishWrite(out);
                return true;
            } catch (IOException e) {
                if (out != null) saveFile.failWrite(out);
                return false;
            }
        }

        @JavascriptInterface
        public void vibrate(int ms) {
            android.os.Vibrator v = getSystemService(android.os.Vibrator.class);
            if (v != null) {
                v.vibrate(android.os.VibrationEffect.createOneShot(
                    Math.max(1, Math.min(ms, 200)), android.os.VibrationEffect.DEFAULT_AMPLITUDE));
            }
        }

        @JavascriptInterface
        public void setOrientation(String mode) {
            String normalized = "portrait".equals(mode) ? "portrait" : ("landscape".equals(mode) ? "landscape" : "auto");
            try {
                JSONObject st = store.loadState();
                // Only overwrite loader orientation if user explicitly changed it inside game Settings
                if (!normalized.equals(st.optString("orientation", "auto")) && !"auto".equals(normalized)) {
                    st.put("orientation", normalized);
                    store.saveState(st);
                }
            } catch (Exception ignored) {}
            // Respect loader's explicit lock if game passes "auto" on startup
            String effective = "auto".equals(normalized) ? store.loadState().optString("orientation", "auto") : normalized;
            runOnUiThread(() -> applyRequestedOrientation(effective));
        }

        @JavascriptInterface
        public void setFrameRateCap(int fps) {
            final int cap = Math.max(30, Math.min(120, fps));
            runOnUiThread(() -> {
                try {
                    Display display = getWindow().getDecorView().getDisplay();
                    if (display == null) return;
                    Display.Mode current = display.getMode(), best = null, lowest = null;
                    float fastest = 0;
                    for (Display.Mode m : display.getSupportedModes()) {
                        if (m.getPhysicalWidth() != current.getPhysicalWidth() || m.getPhysicalHeight() != current.getPhysicalHeight()) continue;
                        fastest = Math.max(fastest, m.getRefreshRate());
                        if (lowest == null || m.getRefreshRate() < lowest.getRefreshRate()) lowest = m;
                        if (m.getRefreshRate() <= cap + 0.5f && (best == null || m.getRefreshRate() > best.getRefreshRate())) best = m;
                    }
                    if (best == null) best = lowest;
                    WindowManager.LayoutParams params = getWindow().getAttributes();
                    final int modeId = best == null || fastest <= cap + 0.5f ? 0 : best.getModeId();
                    if (params.preferredDisplayModeId == modeId) return;
                    params.preferredDisplayModeId = modeId;
                    getWindow().setAttributes(params);
                } catch (RuntimeException ignored) {}
            });
        }

        @JavascriptInterface
        public boolean saveDeckImage(String name, String base64) {
            try {
                byte[] bytes = android.util.Base64.decode(base64, android.util.Base64.DEFAULT);
                synchronized (GameActivity.this) { pendingDeckImage = bytes; }
                runOnUiThread(() -> {
                    Intent i = new Intent(Intent.ACTION_CREATE_DOCUMENT)
                        .addCategory(Intent.CATEGORY_OPENABLE).setType("image/png")
                        .putExtra(Intent.EXTRA_TITLE, name != null ? name : "Deck.png");
                    startActivityForResult(i, SAVE_DECK_IMAGE);
                });
                return true;
            } catch (Exception e) { return false; }
        }

        @JavascriptInterface public boolean copyImage(String name, String base64) { return false; }
        @JavascriptInterface public boolean shareImage(String name, String base64) { return false; }
        @JavascriptInterface public void openUpdate() { runOnUiThread(GameActivity.this::finish); }
        @JavascriptInterface public void openUpdateIn(String lang) { runOnUiThread(GameActivity.this::finish); }
        @JavascriptInterface public void returnToLoader() { runOnUiThread(GameActivity.this::finish); }
        @JavascriptInterface public void quit() { runOnUiThread(GameActivity.this::finish); }
    }
}
