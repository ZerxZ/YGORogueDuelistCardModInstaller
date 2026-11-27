---
name: ygo-rogue-modding
description: Deep, verified technical skill & authoring reference for Yu-Gi-Oh! Rogue Duelist MODs — covering EDOPro ocgcore-wasm 0.1.2 runtime/script conventions (constant.lua, utility.lua, proc_synchro/xyz/pendulum/link, rogue-rng.lua), Post-Synchro modern era modding (MR2–MR5), Mod Spec v1 declarative formats, F9 in-game settings, JS/CSS runtime plugins, and cross-platform Windows/Linux/Android delivery.
---

# Yu-Gi-Oh! Rogue Duelist — Complete Modding & `runtime/script` Skill

Use this skill whenever authoring, porting, or debugging MODs (especially custom Lua card scripts, relics, modern-era Synchro/Xyz/Pendulum/Link expansions, F9 settings, or JS/CSS plugins) for **Yu-Gi-Oh! Rogue Duelist**.

---

## 1. Engine & `runtime/script` Ground Truth (Verified from Upstream Source)

- **Duel Engine**: EDOPro **`ocgcore-wasm 0.1.2`** (`android/web/vendor/package/dist/index.js` + `engine.js` + `duel.js`).
- **Bundled Helper Scripts in `scripts.json` / `scripts-0.json` (27 core files already shipped!)**:
  - Core: `constant.lua`, `utility.lua` (87.9 KB), `archetype_setcode_constants.lua`, `card_counter_constants.lua`, `cards_specific_functions.lua`, `chain.lua`, `debug_utility.lua`, `deprecated_functions.lua`, `rogue-rng.lua`, `randomizer.lua`.
  - **ALL Summoning Procedures ALREADY SHIPPED**:
    - `proc_fusion.lua` + `proc_fusion_spell.lua`
    - `proc_ritual.lua`
    - **`proc_synchro.lua`** (`Synchro.AddProcedure`, `Synchro.NonTuner`, `Synchro.NonTunerEx`, `Synchro.NonTunerCode`)
    - **`proc_xyz.lua`** (`Xyz.AddProcedure`, `Xyz.InfiniteMats`)
    - **`proc_pendulum.lua`** (`Pendulum.AddProcedure`)
    - **`proc_link.lua`** (`Link.AddProcedure`)
    - `proc_spirit.lua`, `proc_union.lua`, `proc_gemini.lua`, `proc_equip.lua`, `proc_persistent.lua`, `proc_maximum.lua`, `proc_rush.lua`, `proc_skill.lua`, `proc_workaround.lua`.
- **Script Directory Structure in Upstream (`runtime/script/`)**:
  - `official/` (12,722 EDOPro official scripts)
  - `unofficial/` (5,532 anime/manga/custom scripts)
  - `rush/` (2,284), `goat/` (187), `skill/` (154), `pre-errata/` (68), `custom/` (7 roguelike-exclusive cards `c190000050–c190000056`).
- **Auto-Upgraded `GetID()`**:
  - Vanilla `utility.lua` defines `function GetID() return self_table, self_code end` (2 return values).
  - Our MOD Manager **automatically upgrades `GetID()` to 3 return values** (`local s, id, o = GetID()`) whenever any MOD includes modern Lua scripts!
- **Rogue Anti-Streak RNG (`rogue-rng.lua`)**:
  - Automatically loaded by `utility.lua`. Wraps `Duel.GetRandomNumber`, `Duel.TossCoin`, `Duel.TossDice` and exposes `Duel.SetRogueWeight(player, "coin"|"dice", face, share)` for weighted-probability relics.
- **`ocgcore-wasm 0.1.2` Hard Constraints**:
  1. `ocgcore-wasm 0.1.2` predates `CHAININFO_TRIGGERING_LINK (31)` and `CHAININFO_TRIGGERING_LSCALE (32)`. Do **not** call `Chain.GetTriggeringLink` or `Chain.GetTriggeringScale` inside custom card scripts.
  2. Always use standard EDOPro `Duel.*`, `Card.*`, `Group.*`, `Effect.*`, `aux.*`, `Synchro.*`, `Xyz.*`, `Pendulum.*`, `Link.*`, `Fusion.*`, `Ritual.*` helpers.
  3. Never rely on `io.*` or `os.*` (sandboxed WASM Lua).

---

## 2. Post-Synchro / Modern Era Modding (Synchro / Xyz / Pendulum / Link)

### Why Vanilla Is Stuck in DM/GX & How Our MOD Manager Unlocks Modern Eras
Vanilla `engine.js` defaults to `OcgDuelMode.MODE_MR1` and vanilla `campaign.py` / `mobile.js` check `data.type & 64` (Fusion-only).
**Our MOD Manager (`ModCompositor.cs`) automatically unlocks modern eras** whenever:
- A MOD sets `"duelRules": "mr2" | "mr3" | "mr4" | "mr5"` in `mod-manifest.json`, **OR**
- Any enabled MOD registers a card with Synchro (`0x2000`), Xyz (`0x800000`), Pendulum (`0x1000000`), or Link (`0x4000000`) in `data.type`!

Automatic patches applied cleanly inside the isolated Virtual Bundle `Game\900.x.y`:
1. **`campaign.py` + `mobile.js` + `draft-ui.js` + `tag-ui.js` + `run-card.js`**: Upgrades Extra Deck detection from `& 64` to `& (64 | 0x2000 | 0x800000 | 0x4000000)`.
2. **`engine.js`**: Switches `createDuel({ flags })` to `OcgDuelMode.MODE_MR2` (Synchro/Xyz), `MODE_MR3` (Pendulum), `MODE_MR4` (Link), or `MODE_MR5` (MR2020: `PZONE | EMZONE | FSX_MMZONE | TRAP_MONSTERS_NOT_USE_ZONE | TRIGGER_ONLY_IN_LOCATION`), and expands `MZONE` queries & `SELECT_PLACE` from `0..4` to `0..6` (including EMZ 5 & 6).

### Exact `data` Fields & Procedure Recipes

| Era / Type | `data.type` Bitmask | Key `data` Fields | Lua `initial_effect(c)` Boilerplate |
|---|---|---|---|
| **Tuner Effect** | `0x1021` (`4129`) | `level`, `attack`, `defense` | Standard monster effect |
| **Synchro Effect** | `0x2021` (`8225`) | `level` = Synchro Level | `Synchro.AddProcedure(c,nil,1,1,Synchro.NonTuner(nil),1,99)` + `c:EnableReviveLimit()` |
| **Xyz Effect** | `0x800021` (`8388641`) | `level` = Rank (1–13) | `Xyz.AddProcedure(c,nil,4,2)` + `c:EnableReviveLimit()` |
| **Pendulum Effect** | `0x1000021` (`16777249`) | `lscale`, `rscale` (0–13) | `Pendulum.AddProcedure(c)` |
| **Link Effect** | `0x4000021` (`67108897`) | `level` = Link Rating, `defense` = 0, `link_marker` bitmask | `Link.AddProcedure(c,aux.FilterBoolFunctionEx(Card.IsType,TYPE_EFFECT),2,2)` + `c:EnableReviveLimit()` |

**Link Marker Bits (`data.link_marker`)**:
- `0x001` Bottom-Left, `0x002` Bottom, `0x004` Bottom-Right
- `0x008` Left, `0x020` Right
- `0x040` Top-Left, `0x080` Top, `0x100` Top-Right

**Xyz Material Detach Cost**:
```lua
e1:SetCost(Cost.DetachFromSelf(1))
-- or classic fallback:
function s.cost(e,tp,eg,ep,ev,re,r,rp,chk)
    if chk==0 then return e:GetHandler():CheckRemoveOverlayCard(tp,1,REASON_COST) end
    e:GetHandler():RemoveOverlayCard(tp,1,1,REASON_COST)
end
```

---

## 3. Core `constant.lua` & `utility.lua` Cheat Sheet

### Locations & Zones
- `LOCATION_DECK` (`0x1`), `LOCATION_HAND` (`0x2`), `LOCATION_MZONE` (`0x4`), `LOCATION_SZONE` (`0x8`), `LOCATION_GRAVE` (`0x10`), `LOCATION_REMOVED` (`0x20`), `LOCATION_EXTRA` (`0x40`), `LOCATION_OVERLAY` (`0x80`)
- Symbolic: `LOCATION_FZONE` (`0x100`), `LOCATION_PZONE` (`0x200`), `LOCATION_STZONE` (`0x400`), `LOCATION_MMZONE` (`0x800`), `LOCATION_EMZONE` (`0x1000`)
- `LOCATION_ONFIELD = 0xC`, `LOCATION_PUBLIC = 0x3C`, `ZONES_MMZ = 0x1F`, `ZONES_EMZ = 0x60`

### Attributes & Races (Types)
- Attributes: `EARTH=0x1`, `WATER=0x2`, `FIRE=0x4`, `WIND=0x8`, `LIGHT=0x10`, `DARK=0x20`, `DIVINE=0x40`
- Races (including modern): `WARRIOR=0x1`, `SPELLCASTER=0x2`, `FAIRY=0x4`, `FIEND=0x8`, `ZOMBIE=0x10`, `MACHINE=0x20`, `AQUA=0x40`, `PYRO=0x80`, `ROCK=0x100`, `WINGEDBEAST=0x200`, `PLANT=0x400`, `INSECT=0x800`, `THUNDER=0x1000`, `DRAGON=0x2000`, `BEAST=0x4000`, `BEASTWARRIOR=0x8000`, `DINOSAUR=0x10000`, `FISH=0x20000`, `SEASERPENT=0x40000`, `REPTILE=0x80000`, `PSYCHIC=0x100000`, `DIVINE=0x200000`, `WYRM=0x800000`, `CYBERSE=0x1000000`, `ILLUSION=0x2000000`

### Common Effect Patterns
- **Spell/Trap Activate**: `EFFECT_TYPE_ACTIVATE`, `EVENT_FREE_CHAIN`
- **Ignition**: `EFFECT_TYPE_IGNITION`, `SetRange(LOCATION_MZONE)`, `SetCountLimit(1, id)`
- **Quick Effect**: `EFFECT_TYPE_QUICK_O`, `EVENT_FREE_CHAIN`, `SetHintTiming(0, TIMINGS_CHECK_MONSTER_E)`
- **On-Summon Trigger**: `EFFECT_TYPE_SINGLE + EFFECT_TYPE_TRIGGER_O`, `SetProperty(EFFECT_FLAG_DELAY)`, `EVENT_SUMMON_SUCCESS` / `EVENT_SPSUMMON_SUCCESS`
- **Relic Passive Lua** (executed at duel start in `approved_relic_effects.py` via `Effect.GlobalEffect()`):
  ```lua
  do
      local e = Effect.GlobalEffect()
      e:SetType(EFFECT_TYPE_FIELD)
      e:SetCode(EFFECT_UPDATE_ATTACK)
      e:SetTargetRange(LOCATION_MZONE, 0)
      e:SetValue(300)
      Duel.RegisterEffect(e, 0)
  end
  ```

---

## 4. Mod Spec v1 Golden Rules & Pitfalls to Avoid

1. **Zero-Touch Virtual Bundle**: Never edit vanilla `Game/<ver>` or `.dll` directly. Put everything under `mods/<mod-id>/`.
2. **All-Entries Auto-Import**: `autoDiscoverEntries: true` (default) auto-imports ALL `js/**/*.js|mjs`, `styles/**/*.css`, `css/**/*.css`, `lua/lib/**/*.lua` (use `"!path"` to exclude).
3. **Custom Characters (`characters/*.json`)**:
   - `cards` MUST be exact **card names** (strings), NOT numeric IDs.
   - Never write custom character indices `>= 39` into `data/character-progression.json` or `data/character-signatures.json` directly (`content.py` loads those when `len(CHARACTERS) == 39`!). Use declarative `characters/*.json` — `ModCompositor` registers them safely via `data/mod-characters.json` at the end of `content.py`!
4. **Custom Packs (`packs/*.json`)**: Game `campaign_expansion.py` requires **>= 9 distinct cards** per booster pack (`ModCompositor` auto-pads smaller packs safely, or supply >= 9 IDs).
5. **In-Game F9 MOD Settings**: Define `settingsSchema` in `mod-manifest.json` or `settings.json`, and read live values in JS via `ctx.settings.get(key, fallback)` / `ctx.settings.onChange(...)`.
6. **CSP Rule**: No external CDN scripts/images in JS/CSS plugins; bundle assets locally or inline as `data:`.
7. **MOD Identity & Server Isolation**: `ModCompositor` automatically appends `-mod` to the version string (`v0.2.47-mod`), marks `build.json`, and isolates official Cloudflare Workers (`global-stats`, `replays`, `shadow-run-rooms`) unless the MOD explicitly supplies its own `multiplayer-config.json`.
8. **Vanilla Removals & System Toggles (`campaign-config.json` / `removals.json`)**:
   - Use `campaign-config.json` (or Visual Studio -> `Campaign & Rules`) to remove vanilla cards/characters/packs/decks/relics/curses OR cleanly disable entire systems: `disableBossCurses`, `disableShadowRelics`, `disableRelicSeal` (封印系统), `disableRelicBanish`, `disableRelicFusions`, plus `duelRules` (`mr1..mr5`) and campaign numeric parameters.
9. **Vanilla Relic Deep Dive**: Read [references/relic-system.md](references/relic-system.md) (or `docs/RELIC-SYSTEM-DEEP-DIVE.md`) for all 12 upstream relic Python modules, 46 fusion recipes, `passives.py` -> `shadow-passives.lua` compilation, and Hint protocol (`1800000000+idx`, `1850000000+idx`, `1860000000+idx`).
10. **Standalone `web-*.zip` Mode**: Even without the original `.exe` installer, `web-X.Y.Z.zip` (plus ~5.5 MB `.delta.gz` incremental updates) can be played and tested directly via `ModHotReloadServer` (`http://127.0.0.1:17845/`).
11. **Built-in AI MCP Workflow**:
   - `get_studio_context` → `get_vanilla_card_script` / `get_mod_guide` → `patch_mod_file` / `write_mod_file` / `add_mod_card` → `validate_mod` → `trigger_hot_reload`.
