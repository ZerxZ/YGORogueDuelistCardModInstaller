# Yu-Gi-Oh! Rogue Duelist MOD Studio & Modding SDK

[中文文档 (Chinese)](README.zh-CN.md) | **English**

> [!IMPORTANT]
> **AI Authorship & Maintenance Notice**
> - **Built with Gemini AI**: This entire project — including the Native AOT C# engine, React 19 + HeroUI v3 Desktop Studio, embedded MCP Server, delta patcher, Lua/JS DSL, and technical documentation — was authored end-to-end with **Gemini AI**.
> - **Reference Project (No Long-Term Maintenance)**: This repository is **not planned for long-term maintenance**. It is published as an open-source technical reference and working foundation. You are warmly welcome to **fork, adapt, extend, or build upon it** for your own needs!

A **Zero-Touch Multi-MOD Manager**, **Standalone `web-*.zip` Player & Delta Updater**, **Visual MOD Studio (Modal Editors + Campaign/Removals Editor + CodeMirror 6)**, **Xmake-Style Lua/JS Modding SDK**, **Cross-Platform Packaging Suite (Windows / Linux / Android)**, **CLI**, and **23-Tool AI MCP Server** for [**Yu-Gi-Oh! Rogue Duelist**](https://github.com/Thronior/ygo-rogue-duelist).

---

## Core Architecture & Highlights

### 1. Zero-Touch Virtual Bundle + Official Server Isolation (`-mod`)
- Compiles all enabled MODs into an isolated virtual version (`Game\900.x.y`) that reuses the official launcher's built-in SHA-256 verification — **never touches `Yu-Gi-Oh! Rogue Duelist.exe/.dll` or original `web-*.zip`**.
- **Automatic MOD Identity Tagging**: Automatically tags modded builds as `v<ver>-mod` (displayed on the title screen as `v0.2.47-mod · MOD (N)` and annotated in `build.json`) and isolates official Cloudflare Workers (`global-stats`, `replays`, `shadow-run-rooms`) unless a MOD intentionally configures its own `multiplayer-config.json`, preventing modded builds from polluting official vanilla leaderboards or matchmaking.
- **1-Click Vanilla Restore**: Deletes `Game\900+.*` and clears WebView2 cache to return to 100% pristine vanilla instantly.

### 2. Standalone `web-*.zip` Mode (Play & Update Without Original `.exe`!)
- **No original `.exe` installer required**: Download the lightweight `web-X.Y.Z.zip` directly into `Game/` and immediately play in the **Embedded Live Preview** or your browser (`http://127.0.0.1:17845/`) with full MOD composition & hot-reload.
- **Official `.delta.gz` Incremental Patching (`YGODLT01`)**: Built-in pure-C# Native AOT delta patch applier (`DeltaPatchService.cs`) reconstructs byte-for-byte identical `web-<new>.zip` using **~5.5 MB** official `.delta.gz` patches instead of re-downloading ~228 MB, with HTTP Range resumable downloads and optional GitHub proxy acceleration (`ghfast.top`, etc.).

### 3. Complete Visual MOD Studio & Content Authoring
| Capability | Details |
| :--- | :--- |
| **Modal Visual Editors & Cover Upload** | Full-width 7-column gallery for Cards, Characters, Relics, Booster Packs, and Preconstructed Decks; click any item to open a spacious modal editor with browse, file upload, and drag-and-drop cover art (`assets/cards/*.jpg`, `assets/packs/*.jpg`, `assets/decks/*.jpg`, `art/characters/`). |
| **Visual Campaign & Vanilla Removals (`campaign-config.json`)** | Remove/hide vanilla **cards**, **playable characters**, **booster packs**, **preconstructed decks**, **relics**, or specific **boss curses** (checkboxes for all 23 vanilla curses). One-click switches to disable **Boss/Route Curses (诅咒系统)**, **Shadow Relics (暗影遗物)**, **Relic Seal (封印系统)**, **Shop Relic Banish (放逐)**, or **Secret Relic Fusions (46 条融合配方)**, plus numeric overrides (`startingGold`, `startingLp`, `bossEvery`, `shopPackCount`, `rerollBasePrice`, `banishPrice`). |
| **Post-Synchro / Modern Era Unlock (`MR2–MR5`)** | Automatically unlocks EDOPro `ocgcore-wasm 0.1.2` **Synchro, Xyz, Pendulum & Link** summoning (`OcgDuelMode.MODE_MR2..MR5`, EMZ slots, Extra Deck filtering, 3-return `GetID()`) whenever `"duelRules": "mr2..mr5"` is set or any modern Extra Deck card is added. |
| **Native `.ypk` Expansion Import** | Drag-and-drop or browse any YGOPro / EDOPro / MDPro3 `.ypk` archive — pure-C# SQLite `.cdb` reader extracts cards, Lua scripts, artwork (with automatic `assets/cards/*.jpg` emission & card-back fallback), and `.ydk` decks straight into Mod Spec v1. |
| **All-Entries Auto-Import & In-Game `F9` Settings** | Automatically loads all `js/**`, `styles/**`, `css/**`, `lua/lib/**` without manual manifest listing; define `settingsSchema` / `settings.json` so players can adjust difficulty, game speed, multipliers, or theme colors live via **`F9 -> MOD Settings`**. |
| **Xmake-Style Dual-Scope DSL** | Write MODs declaratively in `mod.lua` and/or `mod.js` with Description Scope (`card`, `relic`, `character`, `pack`, `preconstructed`, `trainer`) + Script Scope (`on_duel_script`, `on_load`, `on_fetch`, `on_ui_change`, `on_hotkey`). |
| **Live Hot-Reload, `F8` Trainer & `F10` Dev Mode** | Loopback + LAN HTTP/SSE Hot-Reload server (`http://127.0.0.1:17845/`), built-in `F8` Live Trainer overlay (with SHA-256 save integrity), and `F10` Developer Mode inspector. |
| **23-Tool Built-in MCP Server + AI Skill** | Embedded Streamable HTTP (`/mcp`), Legacy SSE (`/mcp/sse`), and Stdio MCP server with 23 tools + 1-click AI prompt presets + curated `ygo-rogue-modding` skill. |
| **Cross-Platform Packaging (Windows / Linux / Android)** | Native AOT `win-x64` & `linux-x64` builds + **lightweight Import-Only Android MOD Loader APK** (`android-loader/`, < 100 KB, pure Java + zero-copy overlay serving) built automatically via GitHub Actions; 1-click **Linux / Steam Deck** bundle export; **`.yrdpk`** (ZIP-based dedicated MOD package, like `.ypk`) & `.zip` pack/import across Desktop, CLI, and Android. |

---

## Documentation Index

- **[MOD Development Guide (Mod Spec v1 & DSL)](docs/MOD_DEVELOPMENT_GUIDE.md)** — Full declarative schema, Lua/JS API, F9 Settings, Modern Era (`MR2–MR5`), and `campaign-config.json` / `removals.json`.
- **[Vanilla Relic System Deep Dive](docs/RELIC-SYSTEM-DEEP-DIVE.md)** — Verified analysis of all 12 upstream relic Python modules, 6 relic tiers, 46 fusion recipes, `passives.py` -> `shadow-passives.lua` compilation, Hint protocol, Relic Seal, and Shadow Relics.
- **[Cross-Platform Packaging Plan (Windows / Linux / Android)](docs/CROSS-PLATFORM-PACKAGING.md)** — Both modded-game export (Linux/Steam Deck + Android APK/LAN PWA) and packaging this MOD Manager for Linux & Android (Termux / WebView wrapper).
- **[AI Authoring Skill (`ygo-rogue-modding`)](.agents/skills/ygo-rogue-modding/SKILL.md)** — Verified EDOPro `ocgcore-wasm 0.1.2` `runtime/script` conventions for AI coding assistants.

---

## Quick Start

### 1. Desktop MOD Studio (Windows `win-x64` & Linux `linux-x64`)
Launch `YGORogueDuelistCardModInstaller.Desktop`:
- **First-Run Onboarding**: Choose **简体中文 / English**, detect your game directory, or click **No EXE Needed · Download `web-.zip` & Play** to start playing immediately!
- **Headless Web Studio Mode**: Run `YGORogueDuelistCardModInstaller.Desktop --serve` (great for Linux servers or Android Termux) and open `http://127.0.0.1:17850/` in any browser.

### 2. Command-Line Interface (CLI)
```powershell
# Inspect managed MODs, load order, version checks & conflicts
ygo-mod.exe list

# Create a new MOD or import a .zip / .ypk / folder / workspace modpack
ygo-mod.exe create my-pack --name "My Expansion" --author "Me" --min-game-ver 0.2.47
ygo-mod.exe import .\examples\sample-xmake-dsl-mod

# Apply enabled MODs (Zero-Touch Virtual Bundle) or restore clean vanilla
ygo-mod.exe apply --mode virtual --dev --yes --no-pause
ygo-mod.exe restore --yes --no-pause

# Pack a single MOD or an entire Workspace Modpack
ygo-mod.exe pack-mod my-pack
ygo-mod.exe pack-workspace --name "My-Modpack" --version "1.0.0"
```

### 3. Built-in AI MCP Server (23 Tools)
Connect any MCP client (Cursor, Claude Desktop, Codex, VS Code, Windsurf) either via the **live Desktop Studio HTTP endpoint** (`http://127.0.0.1:17850/mcp`) or standalone **Stdio executable** (`YGORogueDuelistCardModInstaller.Mcp.exe`):
```json
{
  "mcpServers": {
    "ygo-rogue-mod": {
      "url": "http://127.0.0.1:17850/mcp"
    }
  }
}
```

---

## Building from Source (Native AOT · Windows & Linux)

Requires **.NET 10 SDK**, **Node.js 22+ (`pnpm`)**, and C++ Native AOT toolchain (MSVC x64 on Windows; `clang` + `zlib1g-dev` on Linux):

```bash
# 1. Build React 19 + HeroUI v3 + CodeMirror 6 frontend (outputs to Desktop/wwwroot/)
cd frontend && pnpm install --frozen-lockfile && pnpm build && cd ..

# 2. Build full solution
dotnet build YGORogueDuelistCardModInstaller.slnx -c Release

# 3. Publish Native AOT executables (replace win-x64 with linux-x64 on Linux)
dotnet publish YGORogueDuelistCardModInstaller         -c Release -r win-x64 -o dist/aot/cli
dotnet publish YGORogueDuelistCardModInstaller.Desktop -c Release -r win-x64 -o dist/aot/gui
dotnet publish YGORogueDuelistCardModInstaller.Mcp     -c Release -r win-x64 -o dist/aot/mcp
```

---

## Credits & License
- Built for [**Yu-Gi-Oh! Rogue Duelist**](https://github.com/Thronior/ygo-rogue-duelist) by Thronior.
- Licensed under [LICENSE](LICENSE).
