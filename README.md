# Yu-Gi-Oh! Rogue Duelist MOD Manager & Modding SDK

[中文文档 (Chinese)](README.zh-CN.md) | **English**

A **Zero-Touch Multi-MOD Manager**, **Xmake-Style Lua/JS Modding SDK**, **Visual WebView2 MOD Studio (HeroUI v3 + React + CodeMirror 6 + Open REST/SSE API)**, **CLI (CommandLineParser)**, and **AI MCP Server** for [**Yu-Gi-Oh! Rogue Duelist**](https://github.com/Thronior/ygo-rogue-duelist) — compiled to standalone **Native AOT** Windows x64 executables.

---

## Why Zero-Touch Virtual Bundle Injection?

Official builds of *Yu-Gi-Oh! Rogue Duelist* ship with a .NET desktop launcher (`Yu-Gi-Oh! Rogue Duelist.dll`) that checks an embedded `StartupHashes.json` for its bundled `web-<ver>.zip`, AND automatically picks the highest verified `<ver>` folder inside `%LOCALAPPDATA%\Yu-Gi-Oh! Rogue Duelist\Game\` that has matching `<ver>.zip` + `<ver>.sha256` and `live-version.json`.

This MOD Manager uses that exact built-in update mechanism to inject all enabled MODs into an isolated virtual version (`Game\900.x.y`):

- **Never touches `Yu-Gi-Oh! Rogue Duelist.exe` / `.dll` or original `web-*.zip`**
- **Stack unlimited MODs** with explicit load order, automatic conflict detection, and minimum version validation
- **Survives official game updates** — update the vanilla game anytime and click **Apply MODs** to re-compose on top of the new version
- **1-click Clean Restore** — deletes `Game\900+.*` and returns to 100% untouched vanilla instantly
- **Full ES Module JS, CSS, Lua, Card Art & Static Asset Support** — add brand-new files without hitting launcher hash limits

---

## Key Features

| Area | Capabilities |
| :--- | :--- |
| **Zero-Touch Multi-MOD Stacking** | Import `.zip` archives, folders, or legacy `data/mod.json` packs; reorder load priority; automatic resource conflict analyzer; 1-click vanilla restore & GitHub official update check. |
| **Xmake-Style Dual-Scope DSL** | Write MODs declaratively in **`mod.lua`** and/or **`mod.js`** with clean **Description Scope** (metadata, `set_min_gamever`, wildcard globs, `card`/`relic`/`character`/`pack`/`preconstructed`/`trainer` blocks) + **Script Scope** (inline Lua duel scripts/effects and auto-compiled JS runtime hooks `on_load`, `on_fetch`, `on_ui_change`, `on_hotkey`). |
| **Full Game Content Authoring** | Create new **Cards** (`cards/*.json`), **Lua Card Scripts & Shared Libs** (`lua/cards/`, `lua/lib/`), **Playable Characters** (`characters/*.json`), **Relics** (`relics/*.json`), **Booster Packs** (`packs/*.json`), **Preconstructed Decks & YGOPro `.ydk`** (`preconstructed/*.json`, `decks/*.ydk`), **JS ES Module Plugins & CSS** (`js/`, `styles/`), and **Trainer / Modifier** (`trainer.json` + in-game `F8` overlay). |
| **Developer Mode (`F10`)** | Toggle Developer Mode to inject an in-game **`F10` Dev Inspector**, expose `window.YgoModDev` console helpers, add a quick floating DEV bar, and unlock Sandbox Free Duel & character variants. |
| **Visual MOD Studio & Vanilla Gallery** | Gallery-first **HeroUI v3** (OKLCH Dark/Light themes) + React 19 + TypeScript + Tailwind CSS v4 UI hosted in a lightweight Native AOT WebView2 shell (`Photino.NET`). Features **true infinite scroll** (`IntersectionObserver`) for both your MOD assets and the offline Vanilla Reference Catalog (2,143 cards, 116 characters, 324 relics, 86 packs, 22 decks with 100% artwork & 1-click clone), **CodeMirror 6 IDE** with intelligent autocomplete for Xmake DSL (`mod.lua`/`mod.js`), ocgcore Lua (`Duel.*`/`Effect.*`), and `window.YgoModAPI` JS, plus Multi-Workspace switching. |
| **Live Hot-Reload & Embedded Preview** | Built-in loopback HTTP/SSE Hot-Reload server + `FileSystemWatcher` (`http://127.0.0.1:17845/`): live CSS hot-swap, instant in-memory re-composition, an **embedded playable preview tab** right inside the MOD Studio, and desktop hot-reload bridge injection. |
| **Open Local REST & SSE API** | Exposes `http://127.0.0.1:17850/api/*` (JSON REST + `/api/events` SSE + `/api/docs`) usable by both the embedded UI and external tools/scripts, plus headless `--api-only` mode. |
| **Version Constraints & Packaging** | Enforce `minGameVersion`, `maxGameVersion`, and `minManagerVersion`; pack individual MODs (`.zip`) or entire **MOD Workspaces** (preserving load order, enabled flags, and computed `minGameVersion`) for 1-click sharing & import. |
| **Three Native AOT Apps** | **WebView2 Desktop Studio** (Native AOT + Photino.NET + embedded React/CodeMirror 6, EN / 中文), **CommandLineParser CLI**, and **MCP Server** (stdio JSON-RPC 2.0 so AI assistants can guide you and directly build/validate/package MODs). |

---

## Project Structure

```text
YGORogueDuelistCardModInstaller.slnx
├── YGORogueDuelistCardModInstaller.Core/      # AOT-safe core engine (DSL parser, compositor, packager, GDI+ WebP encoder, virtual bundle injector, offline vanilla catalog)
├── YGORogueDuelistCardModInstaller/           # Native AOT CLI (CommandLineParser)
├── YGORogueDuelistCardModInstaller.Desktop/   # Native AOT WebView2 Desktop Shell (Photino.NET + Loopback REST/SSE API + embedded React SPA)
├── YGORogueDuelistCardModInstaller.Mcp/       # Native AOT MCP Server (stdio JSON-RPC 2.0)
├── frontend/                                  # Vite + React 19 + TypeScript + Tailwind CSS v4 + CodeMirror 6 IDE
├── examples/
│   ├── sample-lua-js-mod/                     # Standard JSON + Lua + JS + CSS example MOD
│   └── sample-xmake-dsl-mod/                  # Xmake-style mod.lua + mod.js + .ydk full-featured example MOD
└── docs/
    └── MOD_DEVELOPMENT_GUIDE.md               # Complete Mod Spec v1 & DSL reference
```

---

## Quick Start

### 1. Desktop MOD Studio (Native AOT WebView2 — Bilingual EN / 中文)

Launch `YGORogueDuelistCardModInstaller.Desktop.exe`:
- Click **`EN` / `中文`** in the top-right bar to switch UI language at any time.
- Browse the **Vanilla Catalog** with infinite scroll and 1-click clone cards (with artwork + Lua script), characters, relics, booster packs, or decks straight into your MOD.
- Author & edit in the **Visual MOD Studio** gallery with live artwork previews and **CodeMirror 6** autocomplete (`Ctrl+Space` for Xmake DSL / ocgcore Lua / `YgoModAPI`, `Ctrl+S` to save & hot-reload).
- Test immediately in the **Live Hot-Reload Preview** tab or inject into the desktop game with **Apply MODs** / **Apply & Launch**.

### 2. Command-Line Interface (CLI)

```powershell
# Inspect managed MODs, load order, version warnings, and conflicts
ygo-mod.exe list

# Create a new Mod Spec v1 template with a minimum game version constraint
ygo-mod.exe create my-pack --name "My Expansion" --author "Me" --min-game-ver 0.2.46

# Import a single MOD (.zip or folder) OR a Workspace Modpack (.zip)
ygo-mod.exe import .\examples\sample-xmake-dsl-mod

# Manage enable/disable & priority order
ygo-mod.exe enable my-pack
ygo-mod.exe disable my-pack
ygo-mod.exe order my-pack 1

# Toggle Developer Mode (F10 Dev Inspector + Sandbox Free Duel)
ygo-mod.exe dev on

# Apply all enabled MODs via Zero-Touch Virtual Bundle (never touches DLL / vanilla ZIP)
ygo-mod.exe apply --mode virtual --dev --yes --no-pause

# Package a single MOD or the entire MOD workspace
ygo-mod.exe pack-mod my-pack
ygo-mod.exe pack-workspace --name "My-Modpack" --version "1.0.0" --min-game-ver "0.2.46"

# Import a workspace modpack in one command
ygo-mod.exe import-workspace .\dist\My-Modpack-v1.0.0.zip

# Restore 100% clean vanilla game
ygo-mod.exe restore --yes --no-pause
```

### 3. AI-Assisted Modding via MCP Server

Configure `YGORogueDuelistCardModInstaller.Mcp.exe` in any MCP-compatible AI client (Claude Desktop, Cursor, Windsurf, VS Code, etc.):

```json
{
  "mcpServers": {
    "ygo-rogue-mod-manager": {
      "command": "C:/path/to/YGORogueDuelistCardModInstaller.Mcp.exe",
      "args": []
    }
  }
}
```

The MCP server exposes **15 tools** (`get_mod_guide`, `get_manager_status`, `search_game_data`, `create_mod`, `add_mod_card`, `add_mod_character`, `add_mod_relic`, `add_mod_pack`, `add_mod_preconstructed_deck`, `configure_mod_trainer`, `write_mod_file`, `read_mod_file`, `validate_mod`, `pack_mod_or_workspace`, `apply_or_restore_mods`), built-in documentation **resources** (`ygo-mod://guide/*`), and workflow **prompts** so your AI assistant can inspect vanilla game data, scaffold complete MODs, write Lua/JS scripts, validate syntax/bitmasks, package `.zip` bundles, and apply them directly.

---

## Xmake-Style Dual-Scope DSL Preview

You can define an entire MOD using **`mod.lua`** and/or **`mod.js`** without hand-editing raw game JSON files:

```lua
-- mod.lua (Description Scope + Script Scope)
set_mod("astral-dsl-demo")
set_name("Astral DSL Demo")
set_version("1.0.0")
set_min_gamever("0.2.46")
set_min_managerver("1.0.0")
add_css_files("styles/*.css")
add_ydk("decks/*.ydk")

card(190099002)
    set_name("Astral Insight")
    set_zh_name("星界洞察")
    set_kind("Spell Card", "Normal")
    set_data_masks(2, "0", 0)
    set_pack("EN-DSL")
    on_duel_script([[
local s,id=GetID()
function s.initial_effect(c)
    local e1=Effect.CreateEffect(c)
    e1:SetCategory(CATEGORY_DRAW)
    e1:SetType(EFFECT_TYPE_ACTIVATE)
    e1:SetCode(EVENT_FREE_CHAIN)
    e1:SetOperation(function(e,tp) Duel.Draw(tp,1,REASON_EFFECT) end)
    c:RegisterEffect(e1)
end
]])
target_end()

pack("EN-DSL")
    set_name("Astral DSL Collection")
    add_ultra(190099002, 46986414)
target_end()

character("Astral Architect")
    set_starting_relic("mage")
    set_unlocked(true)
    add_deck_cards("Astral Insight", "Dark Magician", "Pot of Greed")
    add_starting_packs("EN-DSL")
target_end()
```

```javascript
// mod.js (Script Scope auto-compiled into an isolated ES Module)
add_css_files("styles/*.css");

on_load(async (ctx) => {
  ctx.showToast(`Loaded ${ctx.mod.name}!`);
});

on_hotkey("Alt+J", (ctx) => {
  ctx.showToast("Custom MOD hotkey triggered!");
});
```

See [examples/sample-xmake-dsl-mod](examples/sample-xmake-dsl-mod) and [docs/MOD_DEVELOPMENT_GUIDE.md](docs/MOD_DEVELOPMENT_GUIDE.md) for the complete reference.

---

## Building & Publishing with Native AOT

Requires **.NET 10 SDK**, **Node.js 22+ (pnpm)**, and **Visual Studio C++ x64 Build Tools** on Windows:

```powershell
# 1. Build React + CodeMirror 6 frontend into Desktop/wwwroot/
cd frontend
pnpm install
pnpm build
cd ..

# 2. Build entire solution
dotnet build YGORogueDuelistCardModInstaller.slnx -c Release

# 3. Publish standalone Native AOT executables (win-x64)
dotnet publish YGORogueDuelistCardModInstaller -c Release -r win-x64 -o dist\aot\cli
dotnet publish YGORogueDuelistCardModInstaller.Desktop -c Release -r win-x64 -o dist\aot\gui
dotnet publish YGORogueDuelistCardModInstaller.Mcp -c Release -r win-x64 -o dist\aot\mcp
```

---

## License

Released under the [MIT License](LICENSE). Copyright (c) 2026 ZerxZ.

This tool is an independent community MOD manager and modding SDK for *Yu-Gi-Oh! Rogue Duelist*. It does not distribute game assets, card artwork, or copyrighted data files.
