# Vanilla Relic System Deep Dive (`ygo-rogue-duelist v0.2.47`)

> **Verified directly against upstream source** (`campaign/*.py`, `android/web/duel.js`, `android/web/engine.js`, `android/web/mobile.js`, `runtime/script/rogue-rng.lua` at `v0.2.47`).
>
> **Note**: This document & project were authored end-to-end with **Gemini AI** as an open-source technical reference.

---

## 1. Architecture Overview (12 Python Modules · 324+ Relics)

In *Yu-Gi-Oh! Rogue Duelist*, relics (internally named `artifacts` / `relics`) are NOT merely JSON entries — they span a **12-module Python campaign architecture** that dynamically compiles active relics and boss curses into an ephemeral **`shadow-passives.lua`** script injected into EDOPro `ocgcore-wasm 0.1.2` before every single duel.

| # | Module (`campaign/`) | Responsibility | Key Symbols |
|---|---|---|---|
| 1 | `content.py` | Base 51 relics + 23 Boss Curses + relic metadata | `ARTIFACTS`, `ART_INFO`, `RETIRED_ARTIFACTS = {'feather'}`, `CURSES` |
| 2 | `artifact_expansion.py` | Expanded shop relics (utility, economy, deck manipulation) | `EXPANDED_ARTIFACTS`, `broken_limiter`, `deck_spyglass`, `magic_mirror`, `golden_sleeve`, `crystal_prism` |
| 3 | `artifact_effects.py` | Lua duel hooks for expanded shop relics | `effect_lines(relic, run)` |
| 4 | `approved_relics.py` | **48+ Regular Approved Relics** + **48 Shadow/Cursed Relics** (`cursed_*`), `REMOVED` frozenset, and **Relic Seal (`duel_run`)** | `REGULAR`, `CURSED`, `REMOVED`, `duel_run(run)` |
| 5 | `approved_relic_effects.py` | Lua duel effects for all `REGULAR` and `CURSED` approved relics | `CUSTOM_EFFECTS` / Lua code generator |
| 6 | `archetype_relics.py` | Archetype/race/attribute-focused relics | `ARCHETYPE_ARTIFACTS` |
| 7 | `relic_affinity.py` | Deck-aware shop offer weighting | `offer_weights()`, `shop_sample()`, `WEIGHTED_SHOP_SLOTS = 2` |
| 8 | `relic_fusions.py` | **46 Secret Relic Fusion Recipes** (auto-combine on acquisition) | `FUSED`, `NEEDED`, `REQUIRED`, `INCLUDES`, `MOTH_TOKEN=190000057`, `DANDELION_TOKEN=190000058` |
| 9 | `variant_relics.py` | Alternate character variant starting relics | `VARIANT_RELICS` |
| 10 | `choice_relics.py` | Interactive/Modal choice relics + Very Rare pool | `relic_picker`, `card_picker`, `name_changer`, `weighted_coins`, `weighted_dice`, `VERY_RARE = ('almighty', 'the_creator', 'dimensional_rift')` |
| 11 | `cursed_relics.py` | Post-boss mandatory **Shadow Relic** selection | `create_offer(run, rng)`, `reroll`, `choose`, `skip`, `require_choice`, reads `data/cursed-relics.json` |
| 12 | `draft_relics.py` | Online Draft mode relic pool | `DRAFT_RELICS` |
| ★ | `passives.py` | **Master Compiler**: combines relics + curses + AI hints into `shadow-passives.lua` | `build_passives(run, opp, ...)` |

---

## 2. The 6 Relic Tiers & Lifecycle

1. **Base Starter / Core Relics (`content.py -> ARTIFACTS`)**: 51 classic relics (`mage`, `dragon`, `fiend`, `phoenix`, `merchant_urn`, etc.). `RETIRED_ARTIFACTS = frozenset({'feather'})` marks retired keys excluded from rolls.
2. **Expanded Shop Relics (`artifact_expansion.py`)**: Meta-campaign & duel utility relics (`broken_limiter`, `deck_spyglass`, `magic_mirror`, `golden_sleeve`).
3. **Approved Regular Relics (`approved_relics.py -> REGULAR`)**: 48+ community-approved regular shop relics.
4. **Shadow / Cursed Relics (`approved_relics.py -> CURSED` + `cursed_relics.py`)**: 48 high-risk / high-reward relics prefixed `cursed_*` (stored in `data/cursed-relics.json`). They **never** appear in regular shops — only offered via `cursed_relics.create_offer(run, rng)` after defeating a Boss.
5. **Very Rare / Interactive Choice Relics (`choice_relics.py`)**:
   - `VERY_RARE = ('almighty', 'the_creator', 'dimensional_rift')`
   - Choice relics trigger in-shop/post-acquisition UI modals (`relic_picker`, `card_picker`, `name_changer`) or configure `Duel.SetRogueWeight(tp, 'coin'|'dice', face, share)` via `rogue-rng.lua` (`weighted_coins`, `weighted_dice`).
6. **Secret Fused Relics (`relic_fusions.py -> FUSED`) — 46 Recipes**:
   - Whenever the player acquires all ingredients of a recipe (or partial required set in `NEEDED`/`REQUIRED`), the ingredients automatically fuse into a single super-relic!
   - Supports **multi-tier chaining** (e.g. `genetic_override` fuses `attribute_mutation` + `unstable_dna`).
   - Supports **`INCLUDES` inheritance**: fused relics like `jar_of_greed` or `grand_bazaar` inherit the passive effects of component relics without re-implementing them.
   - Spawns custom token cards directly in Lua: `MOTH_TOKEN = 190000057`, `DANDELION_TOKEN = 190000058`.

---

## 3. Execution Pipeline: Python -> `shadow-passives.lua` -> `ocgcore-wasm` -> `duel.js`

Before every duel starts:
1. `approved_relics.duel_run(run)` prepares the effective duel view of `run['artifacts']`:
   - If the player holds the Boss Curse **`relic_seal`** (封印系统), `duel_run(run)` deterministically seals **half of the player's active relics** into `sealed_relics` for that duel!
2. `passives.py` compiles the unsealed active relics + active boss curses into `shadow-passives.lua`:
   - Prepends header directives read by JS:
     - `-- SHADOW_RUN_RNG 2` -> enables `Duel.EnableRogueAntiStreak()` in `rogue-rng.lua`
     - `-- SHADOW_RUN_AI <json>` -> parsed by `duel.js` & `smart_policy.js` to tell the AI how to play around active relics/curses!
   - Emits per-relic Lua blocks using `Effect.GlobalEffect()` registered via `Duel.RegisterEffect(e, 0)` (player `0` = human, `1` = AI).
3. **Lua -> UI Hint Protocol (`Duel.Hint(HINT_MESSAGE, 0, code)`)**:
   `duel.js` intercepts special numeric `HINT_MESSAGE` codes emitted by `shadow-passives.lua`:
   - `1800000000 + idx`: Flash relic artwork banner in the duel HUD
   - `1850000000 + idx` (with `--[[relic-ask]]` comment): Show relic art confirmation prompt (`Duel.SelectEffectYesNo`)
   - `1860000000 + idx`: Set the prompt relic name label
   - `1100000000` .. `1700000000`: Combat/effect telemetry counters (damage, chain, destroy, banish/remove, tribute)
   - `900000001`: Phoenix revive animation & banner

---

## 4. Related Systems: Boss Curses (诅咒系统), Relic Seal (封印系统) & Shop Banish (放逐)

- **Boss / Route Curses (`content.py -> CURSES`, 23 curses)**:
  Drawn via `feedback_update.draw_curses(...)` and attached to elite/boss routes in `campaign_expansion.routes()`. Includes `relic_seal`, `less_lp`, `tiny_hand`, `no_mulligan`, `boss_draw`, `boss_lp`, `weak_atk`, `weak_def`, `taxed_shop`, `no_heal`, `no_reroll`, `burn_turn`, `spell_tax`, `trap_tax`, `less_gold`, `locked_extra`, `enemy_atk`, `enemy_def`, `no_tribute`, `no_special`, `mill_start`, `blind_hand`, `boss_relic`.
- **Relic Seal (封印系统)**:
  Triggered by the `relic_seal` boss curse and executed inside `approved_relics.duel_run(run)`.
- **Shop Relic Banish (商店放逐)**:
  Defined in `campaign_expansion.py` (`BANISH_PRICE = 20`, `BANISH_STEP = 10`, `BANISH_MAX = 60`, `banish_relic(run, index)`), allowing players to pay gold to banish unwanted shop relics from appearing again in the run.

---

## 5. How MOD Authors Add, Fuse, or Remove Relics Safely

### A. Adding a Custom Relic (Declarative `relics/*.json`)
Place a JSON object (or array) in `mods/<id>/relics/custom-relics.json`:
```json
{
  "key": "astral_compass",
  "name": "Astral Compass",
  "zhName": "星界罗盘",
  "cost": 90,
  "tag": "utility",
  "description": "At the start of each duel, draw 1 extra card and gain 500 LP.",
  "zhDescription": "每次决斗开始时，额外抽 1 张卡并回复 500 LP。",
  "art": "art/relic_astral_compass.png",
  "luaEffect": "do\n  local e=Effect.GlobalEffect()\n  e:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)\n  e:SetCode(EVENT_TURN_COUNT)\n  e:SetCountLimit(1)\n  e:SetOperation(function(e,tp) Duel.Draw(0,1,REASON_RULE); Duel.Recover(0,500,REASON_RULE) end)\n  Duel.RegisterEffect(e,0)\nend"
}
```
`ModCompositor.cs` automatically:
1. Registers the relic in `content.json -> artifacts` + `ui-art` + `i18n/zh.json`.
2. Appends it into `approved_relics.py` (`REGULAR` + `ART_INFO`) and `approved_relic_effects.py` (with your `luaEffect` wrapped safely).
3. Embeds your custom relic art (or falls back cleanly to a stock relic icon).

### B. Removing Vanilla Relics or Disabling Seal / Curses / Shadow Relics / Fusions (`campaign-config.json`)
Use **Visual MOD Studio -> Campaign & Rules (`campaign`)** or write `mods/<id>/campaign-config.json`:
```json
{
  "disableBossCurses": false,
  "disableShadowRelics": false,
  "disableRelicSeal": true,
  "disableRelicBanish": false,
  "disableRelicFusions": false,
  "removals": {
    "removeRelicKeys": ["cursed_greed", "feather"],
    "removeCurseKeys": ["relic_seal", "tiny_hand"]
  }
}
```
`ModCompositor.cs` cleanly strips the targeted keys across `content.json`, `approved_relics.py`, `artifact_expansion.py`, `cursed-relics.json`, `relic_fusions.py`, `feedback_update.py`, and `campaign_expansion.py` without breaking save compatibility or Python imports!
