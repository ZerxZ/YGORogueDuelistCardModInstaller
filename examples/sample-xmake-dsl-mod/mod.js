// =============================================================================
// Sample Xmake-style Dual-Scope JS DSL (mod.js)
// Combines cleanly with mod.lua in the same MOD folder.
// Script Scope hooks (on_load, on_fetch, on_ui_change, on_hotkey) are
// auto-compiled into an isolated ES Module loaded by mod-runtime.js!
// =============================================================================

// [Description Scope]
add_css_files("styles/*.css");

// [Script Scope] Runs once when the MOD runtime initializes
on_load(async (ctx) => {
  ctx.log("Xmake-style JS DSL initialized", ctx.mod);
  ctx.showToast(`DSL MOD Ready: ${ctx.mod.name} (Press Alt+J)`, 3200);
});

// [Script Scope] Intercept fetch responses cleanly
on_fetch(/content\.json/i, async (response, url, ctx) => {
  ctx.log("Observed content.json fetch:", url, "status=", response.status);
  return response;
});

// [Script Scope] Keyboard shortcut inside the game window
on_hotkey("Alt+J", (ctx) => {
  ctx.showToast(`[${ctx.mod.id}] Hotkey Alt+J triggered!`, 2400);
});
