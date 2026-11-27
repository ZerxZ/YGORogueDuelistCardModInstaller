package com.ygorogue.modloader;

import android.app.Activity;
import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;
import android.webkit.*;
import org.json.JSONObject;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Import-Only Android MOD Loader Activity.
 * Clean, lightweight mobile UI focused exclusively on:
 *   1) Importing MODs (.zip / .ypk / Desktop Studio Android Overlay .zip / Workspace Modpacks)
 *   2) Downloading or importing base game web-*.zip
 *   3) Enabling, reordering, applying/restoring, and launching the full-screen modded game!
 */
public final class LoaderActivity extends Activity {
    private static final int REQ_PICK_MOD = 5101;
    private static final int REQ_PICK_WEB_ZIP = 5102;

    private WebView web;
    private ModStore store;
    private final ExecutorService ioPool = Executors.newSingleThreadExecutor();

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        store = new ModStore(this);

        web = new WebView(this);
        setContentView(web);

        WebSettings s = web.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        s.setAllowFileAccess(false);
        s.setAllowContentAccess(false);
        s.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);

        web.addJavascriptInterface(new Bridge(), "AndroidModLoader");
        web.setWebViewClient(new WebViewClient());
        web.loadUrl("file:///android_asset/loader/index.html");
    }

    @Override
    protected void onResume() {
        super.onResume();
        if (web != null) {
            emitToUi("onRefresh", store.getStatusJson());
        }
    }

    @Override
    protected void onDestroy() {
        ioPool.shutdownNow();
        if (web != null) {
            web.removeJavascriptInterface("AndroidModLoader");
            web.stopLoading();
            web.destroy();
            web = null;
        }
        super.onDestroy();
    }

    @Override
    protected void onActivityResult(int req, int resultCode, Intent data) {
        super.onActivityResult(req, resultCode, data);
        if (resultCode != RESULT_OK || data == null || data.getData() == null) return;
        final Uri uri = data.getData();

        if (req == REQ_PICK_MOD) {
            ioPool.execute(() -> {
                try {
                    JSONObject meta = store.importModArchive(uri, this::emitProgress);
                    emitToast("ok", "Imported: " + meta.optString("name", meta.optString("id")));
                    emitToUi("onRefresh", store.getStatusJson());
                } catch (Exception e) {
                    emitToast("err", "Import failed: " + e.getMessage());
                }
            });
        } else if (req == REQ_PICK_WEB_ZIP) {
            ioPool.execute(() -> {
                try {
                    String ver = store.importLocalWebZip(uri, this::emitProgress);
                    emitToast("ok", "Game base ready: v" + ver);
                    emitToUi("onRefresh", store.getStatusJson());
                } catch (Exception e) {
                    emitToast("err", "Import web-.zip failed: " + e.getMessage());
                }
            });
        }
    }

    private void emitProgress(String msg, int pct) {
        try {
            JSONObject o = new JSONObject();
            o.put("message", msg);
            o.put("percent", pct);
            emitToUi("onProgress", o);
        } catch (Exception ignored) {}
    }

    private void emitToast(String level, String msg) {
        try {
            JSONObject o = new JSONObject();
            o.put("level", level);
            o.put("message", msg);
            emitToUi("onToast", o);
        } catch (Exception ignored) {}
    }

    private void emitToUi(String fn, JSONObject payload) {
        final String js = "window.__loaderEvents && window.__loaderEvents." + fn + "(" + payload.toString() + ")";
        runOnUiThread(() -> {
            if (web != null) web.evaluateJavascript(js, null);
        });
    }

    public final class Bridge {
        @JavascriptInterface
        public String getStatus() {
            return store.getStatusJson().toString();
        }

        @JavascriptInterface
        public void setSetting(String key, String value) {
            ioPool.execute(() -> {
                try {
                    JSONObject st = store.loadState();
                    st.put(key, value);
                    store.saveState(st);
                    emitToUi("onRefresh", store.getStatusJson());
                } catch (Exception ignored) {}
            });
        }

        @JavascriptInterface
        public void pickImportMod() {
            runOnUiThread(() -> {
                Intent i = new Intent(Intent.ACTION_OPEN_DOCUMENT);
                i.addCategory(Intent.CATEGORY_OPENABLE);
                i.setType("*/*");
                try {
                    startActivityForResult(Intent.createChooser(i, "Select MOD (.zip / .ypk)"), REQ_PICK_MOD);
                } catch (Exception e) {
                    emitToast("err", "No file picker available.");
                }
            });
        }

        @JavascriptInterface
        public void pickImportWebZip() {
            runOnUiThread(() -> {
                Intent i = new Intent(Intent.ACTION_OPEN_DOCUMENT);
                i.addCategory(Intent.CATEGORY_OPENABLE);
                i.setType("*/*");
                try {
                    startActivityForResult(Intent.createChooser(i, "Select web-X.Y.Z.zip"), REQ_PICK_WEB_ZIP);
                } catch (Exception e) {
                    emitToast("err", "No file picker available.");
                }
            });
        }

        @JavascriptInterface
        public void downloadWebZip(String proxyPrefix) {
            ioPool.execute(() -> {
                try {
                    JSONObject st = store.loadState();
                    st.put("githubProxy", proxyPrefix != null ? proxyPrefix : "");
                    store.saveState(st);
                    String ver = store.downloadLatestWebZip(proxyPrefix, LoaderActivity.this::emitProgress);
                    emitToast("ok", "Downloaded web-" + ver + ".zip!");
                    emitToUi("onRefresh", store.getStatusJson());
                } catch (Exception e) {
                    emitToast("err", "Download failed: " + e.getMessage());
                }
            });
        }

        @JavascriptInterface
        public void toggleMod(String id, boolean enabled) {
            ioPool.execute(() -> {
                try {
                    store.toggleMod(id, enabled);
                    emitToUi("onRefresh", store.getStatusJson());
                } catch (Exception e) {
                    emitToast("err", e.getMessage());
                }
            });
        }

        @JavascriptInterface
        public void moveMod(String id, int delta) {
            ioPool.execute(() -> {
                try {
                    store.moveMod(id, delta);
                    emitToUi("onRefresh", store.getStatusJson());
                } catch (Exception e) {
                    emitToast("err", e.getMessage());
                }
            });
        }

        @JavascriptInterface
        public void deleteMod(String id) {
            ioPool.execute(() -> {
                try {
                    store.deleteMod(id);
                    emitToast("info", "Removed MOD: " + id);
                    emitToUi("onRefresh", store.getStatusJson());
                } catch (Exception e) {
                    emitToast("err", e.getMessage());
                }
            });
        }

        @JavascriptInterface
        public void applyMods(boolean launchAfter) {
            ioPool.execute(() -> {
                try {
                    int n = ModComposerLite.composeAll(store, LoaderActivity.this::emitProgress);
                    emitToast("ok", n > 0 ? ("Applied " + n + " MOD(s)!") : "Running clean vanilla.");
                    emitToUi("onRefresh", store.getStatusJson());
                    if (launchAfter) {
                        runOnUiThread(() -> startActivity(new Intent(LoaderActivity.this, GameActivity.class)));
                    }
                } catch (Exception e) {
                    emitToast("err", "Apply failed: " + e.getMessage());
                }
            });
        }

        @JavascriptInterface
        public void restoreVanilla() {
            ioPool.execute(() -> {
                try {
                    store.clearOverlay();
                    emitToast("ok", "Restored clean vanilla!");
                    emitToUi("onRefresh", store.getStatusJson());
                } catch (Exception e) {
                    emitToast("err", e.getMessage());
                }
            });
        }

        @JavascriptInterface
        public void launchGame() {
            if (!store.getVanillaZip().isFile()) {
                emitToast("err", "Please download or import web-*.zip first.");
                return;
            }
            runOnUiThread(() -> startActivity(new Intent(LoaderActivity.this, GameActivity.class)));
        }
    }
}
