# Yu-Gi-Oh! Rogue Duelist MOD Development Guide (Mod Spec v1 + Xmake-Style DSL)

> [!IMPORTANT]
> **AI Authorship & Maintenance Notice**: This project and documentation were written end-to-end with **Gemini AI**. It is not maintained long-term and is provided as an open-source technical reference — forks and community improvements are warmly welcomed!

This guide covers the complete **Mod Spec v1** format, **Xmake-style Dual-Scope `mod.lua` / `mod.js` DSL**, **All-Entries Auto-Import**, **In-Game `F9` MOD Settings**, **Campaign Rules & Vanilla Removals (`campaign-config.json`)**, **Post-Synchro Modern Era (`MR2–MR5`)**, **Cards / Characters / Relics / Booster Packs / Preconstructed Decks / `.ydk` / `.ypk` / Trainer**, **Developer Mode (`F10`)**, **MOD Identity Tagging (`-mod`)**, and **23-Tool MCP Server** workflows.

See also:
- **[Vanilla Relic System Deep Dive](RELIC-SYSTEM-DEEP-DIVE.md)** (12 modules, 46 fusion recipes, `passives.py` Lua/Hint protocol, Relic Seal & Shadow Relics)
- **[Cross-Platform Packaging Plan (Windows / Linux / Android)](CROSS-PLATFORM-PACKAGING.md)**

---

## 1. Complete MOD Directory Layout

```text
mods/<mod-id>/
├── mod-manifest.json          # Metadata, duelRules ("mr1".."mr5"), settingsSchema, autoDiscoverEntries (default true)
├── settings.json              # Optional standalone in-game F9 MOD Settings schema
├── campaign-config.json       # Optional declarative campaign rules, vanilla removals & curse/seal/fusion disables
├── removals.json              # Optional alias for declarative vanilla removals
├── mod.lua                    # Optional Xmake-style Dual-Scope Lua DSL (can replace or complement JSON)
├── mod.js                     # Optional Xmake-style Dual-Scope JS DSL (auto-compiled into ES Module)
├── cards/*.json               # Declarative custom cards (auto-synced to content.json / engine / era / zh / assets/cards/)
├── characters/*.json          # New playable characters or patches to existing characters
├── relics/*.json              # Custom relics (shop, art, i18n, and Python/Lua duel effects)
├── packs/*.json               # Custom booster packs (common/rare/super/ultra + art/packs/<id>.jpg)
├── preconstructed/*.json      # Custom preconstructed / structure decks (+ art/decks/<art>.jpg)
├── decks/*.ydk                # Standard YGOPro .ydk decklists (auto-converted to preconstructed decks)
├── trainer.json               # Declarative trainer/modifier (unlock all, starting gold/LP, free shop, F8 overlay)
├── lua/
│   ├── lib/*.lua              # Shared Lua helper libraries (auto-appended to ocgcore-wasm utility.lua)
│   └── cards/c<id>.lua        # ocgcore-wasm Lua card scripts
├── js/**/*.js                 # Auto-imported ES Module JS plugins exporting init(ctx) using window.YgoModAPI
├── styles/**/*.css            # Auto-imported stylesheets injected into index.html
├── art/                       # Card art (<id>.jpg|png|webp), art/packs/, art/decks/, art/characters/, relic_*.png
├── campaign/<relPath>         # Direct overrides into campaign-files.json (Python/JSON/data)
├── overrides/<relPath>        # Arbitrary static web bundle overrides or new files (VirtualBundle mode)
└── data/mod.json              # Legacy single-pack format (still 100% supported)
```

---

## 2. Version Constraints (`mod-manifest.json` or DSL)

Protect players from incompatible game or manager versions:

```json
{
  "schemaVersion": 1,
  "id": "my-mod",
  "name": "My Expansion MOD",
  "version": "1.0.0",
  "minGameVersion": "0.2.46",
  "maxGameVersion": "0.9.99",
  "minManagerVersion": "1.0.0",
  "tags": ["cards", "characters", "relics", "packs", "decks"],
  "config": { "showBootToast": true }
}
```

Or in `mod.lua` / `mod.js`:
```lua
set_min_gamever("0.2.46")
set_max_gamever("0.9.99")
set_min_managerver("1.0.0")
```

When injecting (`apply`), incompatible MODs are blocked unless `--ignore-version` (CLI) or Developer Mode override is used.

---

## 3. Xmake-Style Dual-Scope DSL (`mod.lua` & `mod.js`)

Inspired by Xmake's separation between **Description Scope** (fast static declarations) and **Script Scope** (executable logic):

### Description Scope APIs
- **Metadata & Version**: `set_mod(id)`, `set_name(name)`, `set_version(ver)`, `set_author(author)`, `set_description(desc)`, `set_gamever(ver)`, `set_min_gamever(ver)`, `set_max_gamever(ver)`, `set_min_managerver(ver)`, `add_tags(...)`
- **Wildcard Asset Globs**: `add_cards("cards/*.json")`, `add_characters(...)`, `add_relics(...)`, `add_packs(...)`, `add_preconstructed(...)`, `add_ydk("decks/*.ydk")`, `add_lua_cards("lua/cards/*.lua")`, `add_lua_libs("lua/lib/*.lua")`, `add_js_files("js/*.js")`, `add_css_files("styles/*.css")`, `add_art("art/*")`, `add_campaign(...)`, `add_overrides(...)`, `add_trainer("trainer.json")`
- **Inline Target Blocks** (close with `target_end()`):
  - `card(id)`: `set_name`, `set_zh_name`, `set_desc`, `set_zh_desc`, `set_kind(type, race)`, `set_attribute`, `set_stats(lv, atk, def)`, `set_data_masks(typeMask, raceMaskStr, attrMask)`, `set_pack(packId)`, **`on_duel_script([[...]])`**
  - `relic(key)`: `set_name`, `set_zh_name`, `set_desc`, `set_zh_desc`, `set_cost`, `set_art`, `set_tag`, `set_amount`, `set_filter`, `add_starting_for(charIdx...)`, **`on_duel_effect([[...]])`**
  - `character(name[, index])`: `set_zh_name`, `set_sprite`, `set_race`, `set_attribute`, `set_theme`, `set_color`, `set_starting_relic`, `set_unlocked(bool)`, `add_deck_cards(...)`, `add_starting_packs(...)`
  - `pack(id)`: `set_name`, `set_zh_name`, `set_region`, `set_code`, `set_size`, `add_common(...)`, `add_rare(...)`, `add_super(...)`, `add_ultra(...)`
  - `preconstructed(id)` / `deck(id)`: `set_name`, `set_zh_name`, `set_art`, `set_date`, `add_cards(...)`
  - `trainer()`: `set_unlock_all_characters`, `set_unlock_all_variants`, `set_starting_gold`, `set_starting_gold_bonus`, `set_starting_lp`, `set_free_shop`, `set_shop_price_multiplier`, `set_reward_gold_multiplier`, `set_ingame_overlay`, `add_starting_relics(...)`

### Script Scope APIs
- **Lua (`mod.lua`)**:
  - `on_duel_script([[ ... ]])` inside `card(id)` -> emits `c<id>.lua` into ocgcore-wasm
  - `on_duel_effect([[ ... ]])` inside `relic(key)` -> emits Lua duel hook in `approved_relic_effects.py`
  - `on_utility_lua([[ ... ]])` -> appended to `utility.lua`
- **JavaScript (`mod.js` / `mod.mjs`)** — auto-compiled into an isolated ES Module (`__dsl_runtime.js`) loaded by `mod-runtime.js`:
  - `on_load(async (ctx) => { ... })`
  - `on_fetch(/pattern/, async (response, url, ctx, init) => { return response; })`
  - `on_ui_change((root, ctx) => { ... })`
  - `on_hotkey("Alt+J", (ctx, event) => { ... })`

See [examples/sample-xmake-dsl-mod](file:///C:/RiderProject/YGORogueDuelistCardModInstaller/examples/sample-xmake-dsl-mod) for a complete runnable reference!

---

## 4. Declarative JSON Schemas

### Card (`cards/<id>.json`)
- Custom IDs should use `190000000+` (or official YGOPro IDs when porting OCG/TCG cards).
- Bitmasks:
  - `data.type`: Monster `1`, Spell `2`, Trap `4`, Normal `16`, Effect `32`, Fusion `64`, Ritual `128`, Quick-Play `65536`, Continuous `131072`, Equip `262144`, Field `524288`, Counter `1048576`, Tuner `4096`, Synchro `8192`, Xyz `8388608`, Link `67108864`.
  - `data.attribute`: EARTH `1`, WATER `2`, FIRE `4`, WIND `8`, LIGHT `16`, DARK `32`, DIVINE `64`.
  - `data.race`: Stringified bitmask (`"1"` Warrior, `"2"` Spellcaster, `"4"` Fairy, `"8"` Fiend, `"16"` Zombie, `"32"` Machine, `"8192"` Dragon, etc.).
- Optional `packId` automatically injects the card into that booster pack's `ultra` pool!

### Character (`characters/<slug>.json`)
- **Important**: `cards` array in a character definition uses exact **card names** (e.g. `"Dark Magician"`), NOT numeric IDs.
- Omit `index` to append a brand-new playable character (automatically registered in `content.json`, `playable[]`, `character-progression.json`, `character-signatures.json`, and `storage.py`). Provide an existing `index` to patch a vanilla character.

### Relic (`relics/<key>.json`)
- Registered into `content.json -> artifacts[key] & artifactInfo[key]`, `approved_relics.py -> REGULAR`, and `approved_relic_effects.py -> effect(kind)` (where `kind == 'approved_' + key`).
- Provide `luaEffect` (Lua executed during duel start) or raw `pythonLines` for advanced Python logic.

### Booster Packs (`packs/<id>.json`) & Deck Building (`preconstructed/*.json` & `decks/*.ydk`)
- **Booster Packs** define `id` (e.g. `"EN-DSL"`), `name`, `region`, `code`, `size`, and numeric ID arrays `common`, `rare`, `super`, `ultra`. Merged into `content.json -> packs[]`, `campaign-files.json -> data/packs.json`, and `zh-card-names.json`.
- **Preconstructed Decks** define `id`, `name`, `art` (e.g. `"SDK"`), `date`, and `cards` (numeric card ID array). Merged into `content.json -> preconstructedDecks[]` and `data/preconstructed.json`.
- **YGOPro `.ydk` files** placed in `decks/*.ydk` (or matched by `add_ydk(...)`) automatically parse `#main` + `#extra` IDs into a playable preconstructed deck!

### Trainer / Modifier (`trainer.json`)
```json
{
  "unlockAllCharacters": true,
  "unlockAllVariants": true,
  "startingGold": 9999,
  "startingGoldBonus": 500,
  "startingLp": 12000,
  "freeShop": false,
  "shopPriceMultiplier": 0.5,
  "rewardGoldMultiplier": 2.0,
  "startingRelics": ["mage"],
  "inGameOverlay": true
}
```
When `inGameOverlay: true` is set, press **`F8`** inside the game to open the live Trainer Panel (modify active campaign gold/HP/relics, unlock characters, inspect save JSON).

---

## 5. JavaScript Runtime API (`window.YgoModAPI`) & Developer Mode (`F10`)

Every JS plugin (`js/main.js` or compiled `mod.js`) receives `ctx`:
- `ctx.mod`: `{ id, name, version, author, description, config }`
- `ctx.config`: JSON object from `mod-manifest.json`
- `ctx.onFetch(matcher, handler)` — intercept/transform `window.fetch` responses
- `ctx.onUiChange(callback)` — observe `#app` DOM mutations safely
- `ctx.addStyle(cssText)` — inject scoped `<style>`
- `ctx.showToast(message, durationMs)` — toast notification inside the game window
- `ctx.storage.get(key, fallback)` / `ctx.storage.set(key, value)` — persistent mod-scoped `localStorage`
- `ctx.api`: reference to `window.YgoModAPI`

### Developer Mode (`dev on` / GUI checkbox)
When enabled during `apply`:
- Injects **`mod-dev-tools.js`**: press **`F10`** (or click the bottom-right **`DEV`** floating pill) to inspect loaded MODs, search all cards/characters/packs/preconstructed decks in `content.json`, edit `localStorage` campaign saves live, add gold/heal/unlock all, and run quick JS diagnostics.
- Exposes **`window.YgoModDev`** in DevTools console (`info()`, `searchCards(q)`, `searchPacks(q)`, `searchDecks(q)`, `getSave()`, `setGold(n)`, `unlockAll()`, `reload()`).
- Automatically sets `p['wins'] >= 1` and `p['variants_unlocked'] = True` in `storage.py` so Sandbox Free Duel & character variants are immediately available for testing!

---

## 6. Packaging & Sharing

- **Pack a single MOD**:
  ```powershell
  ygo-mod.exe pack-mod sample-xmake-dsl-mod -o dist\sample-xmake-dsl-mod.zip
  ```
- **Pack an entire MOD Workspace** (all or `--enabled-only` MODs, load order, enabled flags, injection mode, and computed `minGameVersion` in `workspace-pack.json`):
  ```powershell
  ygo-mod.exe pack-workspace --name "My-Collection" --version "1.0.0" --min-game-ver "0.2.46"
  ```
- **Import a Workspace Modpack**:
  - CLI: `ygo-mod.exe import .\dist\My-Collection-v1.0.0.zip` (or `import-workspace`)
  - GUI: Click **`+ Import ZIP / Pack`** — automatically detects `workspace-pack.json` and restores all MODs + load order!

---

## 10. Campaign Rules, Vanilla Removals & System Toggles (`campaign-config.json`)

Place `campaign-config.json` (or `removals.json`) at your MOD root — or configure everything visually under **Visual MOD Studio -> Campaign & Rules**:

```json
{
  "duelRules": "mr5",
  "startingGold": 150,
  "startingLp": 8000,
  "bossEvery": 4,
  "shopPackCount": 4,
  "rerollBasePrice": 15,
  "banishPrice": 20,
  "disableBossCurses": false,
  "disableShadowRelics": false,
  "disableRelicSeal": true,
  "disableRelicBanish": false,
  "disableRelicFusions": false,
  "removals": {
    "removeCardIds": [55144522, 79571449],
    "removeCharacterNames": ["Weevil Underwood"],
    "removeCharacterIndices": [],
    "removePackIds": ["EN-LOB"],
    "removeDeckIds": ["SDK"],
    "removeRelicKeys": ["cursed_greed", "feather"],
    "removeCurseKeys": ["relic_seal", "tiny_hand"]
  }
}
```

## 11. Post-Synchro Modern Era (`MR2–MR5`) & MOD Identity Tagging (`-mod`)
- **Modern Era Unlock**: Set `"duelRules": "mr2" | "mr3" | "mr4" | "mr5"` in `mod-manifest.json` or `campaign-config.json` (or simply add any Synchro `0x2000`, Xyz `0x800000`, Pendulum `0x1000000`, or Link `0x4000000` card). `ModCompositor` automatically enables the matching `OcgDuelMode`, Extra Deck filtering, EMZ slots, and 3-return `GetID()`.
- **Automatic `-mod` Tag & Server Isolation**: Every composed virtual bundle automatically marks `replay-version.js` as `<ver>-mod`, renders `v<ver>-mod · MOD (N)` on the title screen, writes `modded: true` to `build.json`, and blocks uploads to official `*.hadow--un--esktop.workers.dev` stats/replays endpoints so vanilla and modded builds are never mixed.
- **Standalone `web-*.zip` Mode**: Works completely without the original `.exe` installer — download `web-X.Y.Z.zip` (and subsequent ~5.5 MB `.delta.gz` patches) and play directly via Hot-Reload Preview or your browser!

