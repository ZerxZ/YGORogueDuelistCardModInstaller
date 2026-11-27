namespace YGORogueDuelistCardModInstaller.Core.Mcp;

public static class ModKnowledgeBase
{
    public static string GetGuide(string? topic) => (topic?.Trim().ToLowerInvariant()) switch
    {
        "card" or "cards" => CardGuide,
        "character" or "characters" => CharacterGuide,
        "relic" or "relics" => RelicGuide,
        "trainer" or "modifier" => TrainerGuide,
        "lua" => LuaGuide,
        "js" or "javascript" => JsGuide,
        "settings" or "config" => SettingsGuide,
        "dsl" or "xmake" => DslGuide,
        "pack" or "packs" or "deck" or "decks" => PackDeckGuide,
        "bitmasks" or "constants" => BitmaskGuide,
        "modern" or "modern_era" or "synchro" or "xyz" or "pendulum" or "link" or "runtime_script" => ModernEraGuide,
        "platforms" or "linux" or "android" or "delta" or "packaging" => CrossPlatformGuide,
        "relic_deep" or "relic_system" or "fusions" => RelicDeepDiveGuide,
        "campaign" or "removals" or "curse" or "curses" or "seal" => CampaignRemovalsGuide,
        _ => OverviewGuide
    };

    public const string CampaignRemovalsGuide = """
        # Campaign Rules, Vanilla Removals & System Toggles (`campaign-config.json` / `removals.json`)

        Drop a `campaign-config.json` (or `removals.json`) at the root of your MOD folder — or edit it visually inside **Visual MOD Studio -> Campaign & Removals (`campaign`)** — to cleanly remove vanilla content or disable whole subsystems without hand-editing upstream Python!

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

        ## What Each Toggle Patches Automatically
        - `removeCardIds`: Strips cards from `content.json -> cards`, booster packs (`content.json` & `data/packs.json`), preconstructed decks, and character signature decks.
        - `removeCharacterNames` / `removeCharacterIndices`: Hides characters from `playable`, `progression.json`, and `character-selection` without breaking numeric index alignment!
        - `removePackIds` / `removeDeckIds`: Removes packs/decks from `content.json`, `data/packs.json`, `data/preconstructed-decks.json`, and character starting packs.
        - `removeRelicKeys`: Removes relics from `content.json -> artifacts`, `approved_relics.py -> REMOVED`, `artifact_expansion.py`, `cursed-relics.json`, and `relic_fusions.py`.
        - `disableRelicSeal`: Removes `'relic_seal'` from `CURSES` and strips `relic_seal` / `sealed_relics` inside `approved_relics.duel_run(run)`.
        - `disableBossCurses`: Patches `feedback_update.draw_curses` and route curse generators to return `[]`.
        - `disableShadowRelics`: Patches `cursed_relics.create_offer` / `require_choice` so post-boss shadow relic offers never trigger.
        - `disableRelicBanish`: Disables shop relic banish (`banish_relic`) and sets `BANISH_PRICE = 0`.
        - `disableRelicFusions`: Clears fusion recipe tables (`FUSED`, `NEEDED`, `REQUIRED`) in `relic_fusions.py`.
        """;

    public const string RelicDeepDiveGuide = """
        # Vanilla Relic System Deep Dive (Verified from `ygo-rogue-duelist v0.2.47`)

        The vanilla relic engine spans **12 Python campaign modules** (~324+ relics across 6 tiers) compiled dynamically into `shadow-passives.lua` before every duel:

        1. `content.py`: Core 51 relics (`ARTIFACTS`, `ART_INFO`, `RETIRED_ARTIFACTS = {'feather'}`), plus 23 boss `CURSES` (including `relic_seal`).
        2. `artifact_expansion.py` + `artifact_effects.py`: Expanded shop relics (`broken_limiter`, `deck_spyglass`, `magic_mirror`, `golden_sleeve`, `crystal_prism`, etc.).
        3. `approved_relics.py` + `approved_relic_effects.py`: 48+ approved regular relics (`REGULAR`), 48 Shadow/Cursed relics (`CURSED`, `cursed_*` prefix, excluded from normal shop), `REMOVED` frozenset, and **`duel_run(run)`** which executes **`relic_seal`** (sealing half the player's active relics during a duel!).
        4. `archetype_relics.py` + `relic_affinity.py`: Deck-based shop weighting (`offer_weights()`, `shop_sample()`, `WEIGHTED_SHOP_SLOTS = 2`).
        5. `relic_fusions.py`: **46 secret fusion recipes (`FUSED`)** including multi-tier fusion (`genetic_override = attribute_mutation + unstable_dna`), `INCLUDES` inheritance (`jar_of_greed`, `grand_bazaar`), and custom tokens (`MOTH_TOKEN = 190000057`, `DANDELION_TOKEN = 190000058`).
        6. `variant_relics.py`: Alternate character variant starting relics.
        7. `choice_relics.py`: Interactive choice relics (`relic_picker`, `card_picker`, `name_changer`, `weighted_coins`, `weighted_dice` -> `Duel.SetRogueWeight`), plus `VERY_RARE = ('almighty', 'the_creator', 'dimensional_rift')`.
        8. `cursed_relics.py`: Mandatory post-boss Shadow Relic offer (`create_offer`, `reroll`, `choose`, `skip`, `require_choice`).
        9. `draft_relics.py`: Online Draft duel relics.
        10. `passives.py`: Compiles active relics + curses into `shadow-passives.lua`:
            - Injects `-- SHADOW_RUN_RNG 2`, `Duel.EnableRogueAntiStreak()`, and `-- SHADOW_RUN_AI <json>` (read by `duel.js` & `smart_policy.js`).
            - Hint protocol (`Duel.Hint(HINT_MESSAGE, 0, code)`): `1800000000+idx` (relic art banner), `1850000000+idx` (`--[[relic-ask]]` confirmation prompt), `1860000000+idx` (prompt label), `1100000000..1700000000` (damage/chain/destroy/remove/tribute telemetry), `900000001` (phoenix revive).

        See `docs/RELIC-SYSTEM-DEEP-DIVE.md` or `.agents/skills/ygo-rogue-modding/references/relic-system.md` for full recipes & code walkthroughs.
        """;

    public const string ModernEraGuide = """
        # Post-Synchro / Modern Era Modding (`runtime/script` + Synchro / Xyz / Pendulum / Link)

        ## 1. Engine Capabilities (Verified in `ygo-rogue-duelist` source!)
        - The game runs **EDOPro `ocgcore-wasm 0.1.2`** and ALREADY bundles ALL 27 core EDOPro helper scripts inside `scripts.json`:
          - `constant.lua`, `utility.lua` (87 KB), `archetype_setcode_constants.lua`, `card_counter_constants.lua`, `cards_specific_functions.lua`, `rogue-rng.lua`
          - **`proc_synchro.lua`** (`Synchro.AddProcedure`), **`proc_xyz.lua`** (`Xyz.AddProcedure`), **`proc_pendulum.lua`** (`Pendulum.AddProcedure`), **`proc_link.lua`** (`Link.AddProcedure`), `proc_fusion.lua`, `proc_ritual.lua`, `proc_maximum.lua`, `proc_rush.lua`, `proc_skill.lua`.
        - `OcgDuelMode` inside `vendor/package/dist/index.js` ALREADY supports:
          - `MODE_MR1` (default DM/GX), `MODE_MR2` (5D's/ZEXAL: Synchro & Xyz), `MODE_MR3` (Pendulum Zones), `MODE_MR4` (EMZ + Link), **`MODE_MR5`** (MR2020: `PZONE | EMZONE | FSX_MMZONE | TRAP_MONSTERS_NOT_USE_ZONE | TRIGGER_ONLY_IN_LOCATION`).

        ## 2. How Our MOD Manager Automatically Unlocks Modern Eras!
        Set `"duelRules": "mr2" | "mr3" | "mr4" | "mr5"` in `mod-manifest.json` (OR simply add any card with Synchro `0x2000`, Xyz `0x800000`, Pendulum `0x1000000`, or Link `0x4000000` type mask — **auto-detected!**):
        - Automatically upgrades `campaign.py` (`is_extra_deck`), `mobile.js`, `draft-ui.js`, `tag-ui.js`, `run-card.js` from Fusion-only (`& 64`) to full Extra Deck (`& (64 | 0x2000 | 0x800000 | 0x4000000)`).
        - Automatically patches `engine.js` to start duels with the required `OcgDuelMode.MODE_MR2/MR3/MR4/MR5`, expands `SELECT_PLACE` MZONE to include EMZ slots (`0..6`), queries `OVERLAY_CARD` / `LSCALE` / `RSCALE` / `LINK`, and preserves EMZ in `snapshot()`.
        - Automatically upgrades `utility.lua`'s `GetID()` to 3-return `(s, id, o)` for modern EDOPro scripts.

        ## 3. Card Bitmasks & `data` Fields for Modern Mechanics
        | Card Kind | `data.type` Bitmask | Extra `data` Fields | Procedure Call |
        |---|---|---|---|
        | Tuner Effect Monster | `0x1021` (`4129`) | standard `level/attack/defense` | normal effect |
        | Synchro Effect Monster | `0x2021` (`8225`) | `level` = Synchro Level | `Synchro.AddProcedure(c,nil,1,1,Synchro.NonTuner(nil),1,99)` + `c:EnableReviveLimit()` |
        | Xyz Effect Monster | `0x800021` (`8388641`) | `level` = Rank (1–13) | `Xyz.AddProcedure(c,nil,4,2)` + `c:EnableReviveLimit()` |
        | Pendulum Effect Monster | `0x1000021` (`16777249`) | `lscale`, `rscale` (0–13) | `Pendulum.AddProcedure(c)` |
        | Link Effect Monster | `0x4000021` (`67108897`) | `level` = Link Rating, `defense` = `0`, `link_marker` bitmask | `Link.AddProcedure(c,filter,min,max)` + `c:EnableReviveLimit()` |

        ### Link Marker Bitmasks (`data.link_marker`)
        - `0x001` Bottom-Left, `0x002` Bottom, `0x004` Bottom-Right
        - `0x008` Left, `0x020` Right
        - `0x040` Top-Left, `0x080` Top, `0x100` Top-Right

        ## 4. `ocgcore-wasm 0.1.2` Important Caveats
        - `engine.js` stubs `Chain.GetTriggeringScale` / `Chain.GetTriggeringLink` only if NO cards have `0x1000000` / `0x4000000`, so adding Pendulum/Link cards disables the stub — avoid calling `CHAININFO_TRIGGERING_LINK (31)` or `CHAININFO_TRIGGERING_LSCALE (32)` in scripts (not in wasm 0.1.2).
        - Official `rogue-rng.lua` hooks `Duel.GetRandomNumber` / `TossCoin` / `TossDice` for anti-streak & weighted relics (`Duel.SetRogueWeight`).
        - Prefer `Cost.DetachFromSelf(1)` or `e:GetHandler():RemoveOverlayCard(tp,1,1,REASON_COST)` for Xyz costs.
        - Remember to also provide UI styling via `styles/*.css` / `js/*.js` if you want custom EMZ / Pendulum Zone visual slots beyond the core duel prompt choices.
        """;

    public const string CrossPlatformGuide = """
        # Cross-Platform Support (Windows / Linux / Android) & `.delta.gz` Patches

        ## 1. Official `.delta.gz` Patch Format (`YGODLT01`)
        - `web-<old>-to-<new>.delta.gz` (~5.5 MB vs 228 MB full zip): Used by Windows & Linux launchers AND our MOD Manager to reconstruct byte-for-byte identical `web-<new>.zip` via `DeltaPatchService.cs`!
        - `ygo-update-<baseApkSha256>.delta.gz`: Used by Android APK updater (`UpdateDelta.java`) to reconstruct the exact signed APK without re-signing.

        ## 2. Linux / Steam Deck Support
        - The official Linux launcher (`YGO-Rogue-Linux-x86_64.tar.gz`) uses the **exact same `Game/<version>` bundle format** under `~/.local/share/Yu-Gi-Oh! Rogue Duelist/Game/` (`$XDG_DATA_HOME`).
        - Use **Export Linux MOD Bundle** (`POST /api/export/linux-bundle`) to generate a portable `.zip` with `900.x.y.zip` + `.sha256` + `install-linux-mod.sh`.

        ## 3. Android Support (Two Modes)
        1. **LAN Hot-Reload PWA (Zero APK Re-sign!)**: Start Hot-Reload in Desktop Studio -> open the shown LAN URL (`http://<PC-IP>:17845/`) on Android Chrome/Edge -> Add to Home Screen!
        2. **Offline Modded APK (`POST /api/export/android-bundle`)**: Supply official `YGO-Rogue-Android.apk` to patch `assets/web/*` directly into `YGO-Rogue-Android-Modded-unsigned.apk`, then sign with `uber-apk-signer` / `apksigner` / Android MT Manager!
        """;

    public const string SettingsGuide = """
        # In-Game MOD Settings (`settingsSchema` / `settings.json` + `ctx.settings`)

        Let players adjust your MOD's difficulty, animation speed, multipliers, UI toggles, or theme colors right inside the game via **F9 -> MOD Settings** (or the quick bar `MOD设置` button)!

        ## 1. Declarative Schema (in `mod-manifest.json -> "settingsSchema"` OR standalone `settings.json`)
        Supported control types: `boolean` (switch), `range` (slider with `min`, `max`, `step`, `unit`), `number`, `select` (`options: [{value, label, zhLabel}]`), `text`, `color`.
        ```json
        [
          {
            "key": "difficultyPreset",
            "type": "select",
            "label": "Difficulty",
            "zhLabel": "游戏难度",
            "default": "standard",
            "options": [
              { "value": "casual", "label": "Casual", "zhLabel": "休闲" },
              { "value": "standard", "label": "Standard", "zhLabel": "标准" },
              { "value": "hard", "label": "Hard", "zhLabel": "困难" }
            ]
          },
          {
            "key": "speedMultiplier",
            "type": "range",
            "label": "Speed Multiplier",
            "zhLabel": "倍速调节",
            "default": 1.5,
            "min": 0.5,
            "max": 3.0,
            "step": 0.25,
            "unit": "x"
          },
          {
            "key": "showHud",
            "type": "boolean",
            "label": "Show HUD",
            "zhLabel": "显示状态栏",
            "default": true
          }
        ]
        ```

        ## 2. Runtime JS API (`ctx.settings`)
        - `ctx.settings.get(key, fallback)` — Read current persisted value (`localStorage['ygomod:settings:<modId>']`)
        - `ctx.settings.getAll()` — Get all key/value pairs for this MOD
        - `ctx.settings.set(key, value)` — Programmatically update & notify subscribers
        - `ctx.settings.reset()` — Reset all settings of this MOD back to schema defaults
        - `ctx.settings.onChange((key, newValue, allValues) => ...)` — React live with zero page reload
        - `ctx.settings.define(schemaArray)` — Optionally register additional settings dynamically from JS
        - `ctx.settings.open()` — Open the in-game `F9 -> MOD Settings` panel focused on your MOD
        """;

    public const string DslGuide = """
        # Xmake-Style Dual-Scope MOD DSL (`mod.lua` & `mod.js`)

        Write a single declarative + scriptable `mod.lua` or `mod.js` file without even needing `mod-manifest.json`:

        ## Description Scope (Global + Target Blocks)
        - Global: `set_mod("id")`, `set_name("...")`, `set_version("1.0.0")`, `set_author("...")`, `set_description("...")`, `set_category("cards")`, `set_cover("cover.png")`, `set_auto_entries(true)`
        - Version constraints: `set_min_gamever("0.2.46")`, `set_max_gamever("0.9.0")`, `set_min_managerver("1.0.0")`
        - Wildcard globs: `add_cards("cards/*.json")`, `add_characters(...)`, `add_relics(...)`, `add_packs(...)`, `add_preconstructed(...)`, `add_ydk("decks/*.ydk")`, `add_lua_cards("lua/cards/*.lua")`, `add_lua_libs("lua/lib/*.lua")`, `add_js_files("js/*.js")`, `add_css_files("styles/*.css")`, `add_overrides("overrides/**")`
        - Target blocks: `card(190099002)`, `relic("key")`, `character("Name")`, `pack("EN-MOD")`, `preconstructed("MOD-DECK")`, `trainer()`

        ## Script Scope
        - In `mod.lua`: `on_duel_script([[ ... ]])` inside `card(...)`, `on_duel_effect([[ ... ]])` inside `relic(...)`, `on_utility_lua([[ ... ]])`
        - In `mod.js`: `on_load(async (ctx) => { ... })`, `on_fetch(/content\.json$/, async (resp, url, ctx) => resp)`, `on_ui_change((root, ctx) => { ... })`, `on_hotkey("Alt+J", (ctx) => { ... })`
          (Automatically compiled into an ES Module `mods/<id>/__dsl_runtime.js` executed by `window.YgoModAPI`!)
        """;

    public const string PackDeckGuide = """
        # Booster Packs (`packs/*.json`) & Deck Building (`preconstructed/*.json` + `decks/*.ydk` + `decks.json`)

        1. Booster Pack (`packs/my-pack.json` or DSL `pack("EN-MYPACK")`):
           `{"id":"EN-MYPACK","name":"My Pack","zhName":"我的卡包","code":"MYP","size":9,"common":[190099001],"rare":[],"super":[],"ultra":[]}`
        2. Preconstructed Deck (`preconstructed/my-deck.json` or DSL `preconstructed("MY-DECK")`):
           `{"id":"MY-DECK","name":"My Starter Deck","zhName":"我的预组卡组","art":"SDK","cards":[89631139,190099001]}`
        3. Character Starting Deck (`decks.json` or `character(...)->add_deck_cards(...)`):
           `{"0":["Dark Magician","Dark Magician Girl","Modder's Golden Pot"]}`
        4. Standard `.ydk` files in `decks/*.ydk` -> automatically injected into `campaign-files.json -> data/decks/*.ydk`.
        """;

    public const string OverviewGuide = """
        # Yu-Gi-Oh! Rogue Duelist Mod Spec v1 (Overview)

        ## Injection Architecture (Zero-Touch Virtual Bundle)
        - Default injection mode (`VirtualBundle`) NEVER modifies `Yu-Gi-Oh! Rogue Duelist.dll`, `.exe`, or the original `web-X.Y.Z.zip`.
        - Instead, all enabled mods under `mods/<mod-id>/` are composed in load order onto the latest official vanilla zip and emitted to `%LOCALAPPDATA%\Yu-Gi-Oh! Rogue Duelist\Game\900.x.y` (+ `.zip` + `.sha256`).
        - Supports unlimited stacked mods, brand-new assets, JS/CSS plugins, Lua libraries, declarative cards/characters/relics, live F8 Trainer, F10 Developer & Debug Mode, and built-in Hot-Reload.

        ## Standard Mod Layout (`mods/<mod-id>/`)
        - `mod-manifest.json`       : Metadata (`schemaVersion: 1`, `id`, `name`, `version`, `author`, `category`, `cover`, `autoDiscoverEntries: true`, `settingsSchema`, `config`)
        - `settings.json`           : Optional standalone in-game MOD settings schema (rendered in `F9 -> MOD Settings`)
        - `cover.png|webp|jpg|svg`  : MOD cover art (auto-detected if present in MOD root or `art/`)
        - `cards/*.json`            : Declarative custom cards (auto-synced to content.json, engine-data.json, era-cards.json, i18n/zh.json, art)
        - `characters/*.json`       : New playable characters or edits to existing characters
        - `relics/*.json`           : New/edited relics with art, i18n, and Lua duel effects
        - `trainer.json`            : Declarative trainer/modifier (unlock all, starting gold/LP, free shop, reward multiplier, F8 live overlay)
        - `lua/cards/c<code>.lua`   : ocgcore-wasm Lua card scripts
        - `lua/lib/**/*.lua`        : Auto-imported global Lua helper libraries appended idempotently to `utility.lua`
        - `js/**/*.js|mjs`          : Auto-imported ES Module frontend plugins loaded via `mod-runtime.js` (`window.YgoModAPI` & `ctx.settings`)
        - `styles/**/*.css` / `css/**/*.css`: Auto-imported CSS stylesheets (use `"!path"` in `jsEntries`/`cssEntries`/`luaLibs` to exclude a file)
        - `art/<code>.webp|jpg|png` : Card / relic artwork
        - `campaign/**`             : Overrides for `campaign-files.json` (.py / data/*.json / data/decks/*.ydk)
        - `decks.json` / `zh-cards.json` / `zh-ui.json` / `overrides/**`

        ## Fast AI Script Authoring Workflow (Built-in MCP)
        1. Call `get_studio_context` to see the exact MOD, card/relic, and script file open in the Desktop Studio.
        2. Call `get_vanilla_card_script` to study official ocgcore-wasm Lua scripts from the installed game.
        3. Call `patch_mod_file` or `write_mod_file` to edit the script — Desktop Studio refreshes automatically and triggers Hot-Reload!
        4. Call `validate_mod` and optionally `trigger_hot_reload` or `apply_or_restore_mods`.
        """;

    public const string CardGuide = """
        # Declarative Cards (`cards/*.json`)

        Recommended custom card ID range: `190010000` - `199999999` (avoids official custom cards `190000050+` and MDPro3 pack `190000901-190000907`).

        Example `cards/my-cards.json`:
        ```json
        [
          {
            "id": 190099001,
            "name": "Modder's Golden Pot",
            "desc": "Draw 2 cards, then gain 500 LP.",
            "zhName": "模组制作者的黄金之壶",
            "zhDesc": "①：自己从卡组抽2张，那之后回复500基本分。",
            "type": "Spell Card",
            "race": "Normal",
            "attribute": "",
            "level": 0,
            "atk": 0,
            "defense": 0,
            "addToEraPool": true,
            "data": {
              "code": 190099001,
              "alias": 0,
              "setcodes": [],
              "type": 2,
              "level": 0,
              "attribute": 0,
              "race": "0",
              "attack": 0,
              "defense": 0,
              "lscale": 0,
              "rscale": 0,
              "link_marker": 0
            },
            "strings": ["","","","","","","","","","","","","","","",""]
          }
        ]
        ```
        Pair with `lua/cards/c190099001.lua` and optional `art/190099001.webp`.
        Use MCP tool `add_mod_card` to generate both JSON and Lua script automatically!
        """;

    public const string CharacterGuide = """
        # Declarative Characters (`characters/*.json`)

        - Omit `index` (or set `-1`) to ADD a brand-new playable character (automatically registered in `content.json -> playable[]`, `character-progression.json`, `character-signatures.json`, and `storage.py`).
        - Provide `index` (e.g. `0` for Yami Yugi, `1` for Kaiba) to EDIT an existing character cleanly.
        - `cards` MUST be an array of exact card **names** (English/matching `content.json -> cards[].name`), NOT numeric IDs.

        Example `characters/my-duelist.json`:
        ```json
        [
          {
            "name": "Arcane Architect",
            "zhName": "奥术构筑师",
            "sprite": "dm01.png",
            "race": "Spellcaster",
            "attribute": "DARK",
            "theme": "Spellcaster / Custom",
            "color": "#f4cf57",
            "bonus": "Starts with custom Spellcaster & draw cards.",
            "pack": "DRAFT-YAMI-YUGI",
            "starting_packs": ["DRAFT-YAMI-YUGI", "EN-MFC"],
            "starting_relic": "mage",
            "unlockedByDefault": true,
            "cards": ["Dark Magician", "Dark Magician Girl", "Modder's Golden Pot"]
          }
        ]
        ```
        Use MCP tool `search_game_data(kind='characters')` to inspect existing characters and `add_mod_character` to create/edit one.
        """;

    public const string RelicGuide = """
        # Declarative Relics (`relics/*.json`)

        Automatically registers into `content.json` (`artifacts` & `artifactInfo`), `i18n/zh.json`, `approved_relics.py`, `approved_relic_effects.py`, and optionally assigns `startingForCharacters`.

        Example `relics/my-relic.json`:
        ```json
        [
          {
            "key": "arcane_lens",
            "name": "Arcane Lens",
            "zhName": "奥术透镜",
            "cost": 85,
            "description": "Spellcaster monsters you control gain 400 ATK.",
            "zhDescription": "自己场上的魔法师族怪兽攻击力上升400。",
            "art": "Merchant Urn.png",
            "tag": "offense",
            "effect": "approved_arcane_lens",
            "amount": 400,
            "filter": "Spellcaster",
            "luaEffect": "do local e=Effect.GlobalEffect() e:SetType(EFFECT_TYPE_FIELD) e:SetCode(EFFECT_UPDATE_ATTACK) e:SetTargetRange(LOCATION_MZONE,0) e:SetTarget(function(e,c) return c:IsRace(RACE_SPELLCASTER) end) e:SetValue(400) Duel.RegisterEffect(e,0) end",
            "startingForCharacters": []
          }
        ]
        ```
        Note: `art` can be a stock relic filename (`Merchant Urn.png`), a local file under `art/` (auto-inlined as `data:` URL with `ui-art.js` auto-patched), or an inline `data:` URL.
        """;

    public const string TrainerGuide = """
        # Declarative Trainer / Modifier (`trainer.json`)

        Drop `trainer.json` in a mod root to configure campaign modifiers + built-in **F8 In-Game Live Trainer**:
        ```json
        {
          "unlockAllCharacters": true,
          "unlockAllVariants": true,
          "startingGold": 2000,
          "startingGoldBonus": 0,
          "startingLp": 16000,
          "freeShop": false,
          "shopPriceMultiplier": 0.5,
          "rewardGoldMultiplier": 2.0,
          "startingRelics": ["urn"],
          "startingCards": [190099001],
          "inGameOverlay": true,
          "showTrainerHint": true
        }
        ```
        - `inGameOverlay: true` (and Developer Mode) injects `mod-trainer-overlay.js` + `mobile_backend.py` live bridge: press **F8** in-game to live-edit Gold/LP/Cards/Relics/Packs/Unlocks/Free Shop/Instant Win with zero reload & SHA-256 save persistence.
        """;

    public const string LuaGuide = """
        # Lua Duel Scripting (`ocgcore-wasm 0.1.2`)

        - Card scripts live in `lua/cards/c<code>.lua` (e.g. `c190099001.lua`).
        - Shared helper libraries live in `lua/lib/*.lua` (appended safely to `utility.lua`).
        - `GetID()` is automatically upgraded to return 3 values (`local s, id, o = GetID()`).
        - Tip: Call `get_vanilla_card_script(query='Pot of Greed')` to inspect real vanilla scripts from the installed game!
        - Standard template:
        ```lua
        local s, id, o = GetID()
        function s.initial_effect(c)
            local e1 = Effect.CreateEffect(c)
            e1:SetCategory(CATEGORY_DRAW)
            e1:SetType(EFFECT_TYPE_ACTIVATE)
            e1:SetCode(EVENT_FREE_CHAIN)
            e1:SetCountLimit(1, id, EFFECT_COUNT_CODE_OATH)
            e1:SetTarget(s.target)
            e1:SetOperation(s.activate)
            c:RegisterEffect(e1)
        end
        function s.target(e, tp, eg, ep, ev, re, r, rp, chk)
            if chk == 0 then return Duel.IsPlayerCanDraw(tp, 2) end
            Duel.SetTargetPlayer(tp)
            Duel.SetTargetParam(2)
            Duel.SetOperationInfo(0, CATEGORY_DRAW, nil, 0, tp, 2)
        end
        function s.activate(e, tp, eg, ep, ev, re, r, rp)
            local p, d = Duel.GetChainInfo(0, CHAININFO_TARGET_PLAYER, CHAININFO_TARGET_PARAM)
            Duel.Draw(p, d, REASON_EFFECT)
        end
        ```
        """;

    public const string JsGuide = """
        # JS Plugin Runtime (`window.YgoModAPI` v2.2)

        - ALL ES Modules under `js/**/*.js|mjs` and ALL stylesheets under `styles/**/*.css` + `css/**/*.css` are **auto-imported by default** (`autoDiscoverEntries: true`). Exclude a helper file with `"!js/helper.js"` in `jsEntries`.
        - Loaded BEFORE `web-boot.js` so `ctx.onFetch` intercepts `content.json`, `i18n/zh.json`, etc.
        - Hotkeys: **F8** Live Trainer, **F9** Loaded MODs & In-Game MOD Settings, **F10** Developer Settings & Debug Mode.
        - CSP allows `'self'` scripts and `data:`/`blob:` images (NO external CDN scripts/images).

        `ctx` API inside `export async function init(ctx)`:
        - `ctx.mod` / `ctx.config` / `ctx.api` / `ctx.i18n` / `ctx.saves`
        - `ctx.settings.get(key, fallback)` / `.getAll()` / `.set(key, val)` / `.reset()` / `.onChange(fn)` / `.define(schema)` / `.open()`
        - `ctx.showToast(msg, durationMs?)`
        - `ctx.addStyle(css, suffix?)`
        - `ctx.onDomReady(fn)` / `ctx.onUiChange(root => ...)`
        - `ctx.onFetch(regexOrStr, async (response, url) => response)`
        - `ctx.storage.get(key, fallback)` / `.set(key, val)` / `.remove(key)`
        - `ctx.events.on(evt, fn)` / `.emit(evt, data)`
        - `ctx.log(...)` / `ctx.warn(...)` / `ctx.error(...)`
        """;

    public const string BitmaskGuide = """
        # ocgcore-wasm Card Bitmasks Reference

        ## Card Type (`data.type`)
        - Monster = 1, Spell = 2, Trap = 4
        - Normal Monster = 17 (1+16), Effect Monster = 33 (1+32)
        - Fusion Effect = 97 (1+32+64), Ritual Effect = 161 (1+32+128)
        - Quick-Play Spell = 65538 (2+65536), Continuous Spell = 131074 (2+131072), Equip Spell = 262146 (2+262144), Field Spell = 524290 (2+524288), Ritual Spell = 130 (2+128)
        - Continuous Trap = 131076 (4+131072), Counter Trap = 1048580 (4+1048576)
        - Toon Effect Monster = 4194337 (1+32+4194304), Special Summon Only Effect = 33554465 (1+32+33554432)

        ## Attribute (`data.attribute`)
        - EARTH=1, WATER=2, FIRE=4, WIND=8, LIGHT=16, DARK=32, DIVINE=64

        ## Race (`data.race`, stored as string in `data.race`, e.g. `"2"`)
        - Warrior="1", Spellcaster="2", Fairy="4", Fiend="8", Zombie="16", Machine="32"
        - Aqua="64", Pyro="128", Rock="256", Winged Beast="512", Plant="1024", Insect="2048"
        - Thunder="4096", Dragon="8192", Beast="16384", Beast-Warrior="32768", Dinosaur="65536"
        - Fish="131072", Sea Serpent="262144", Reptile="524288", Psychic="1048576", Divine-Beast="2097152"
        """;
}
