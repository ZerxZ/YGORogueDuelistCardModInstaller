# Vanilla Relic System Reference (`ygo-rogue-duelist v0.2.47`)

Quick AI/authoring reference distilled from `docs/RELIC-SYSTEM-DEEP-DIVE.md`.

## 12 Upstream Relic Modules (`campaign/`)
- `content.py`: 51 core `ARTIFACTS`, `ART_INFO`, `RETIRED_ARTIFACTS = {'feather'}`, 23 boss `CURSES` (`relic_seal`, `tiny_hand`, `less_lp`, etc.).
- `artifact_expansion.py` + `artifact_effects.py`: Expanded shop relics (`broken_limiter`, `deck_spyglass`, `magic_mirror`, `golden_sleeve`).
- `approved_relics.py` + `approved_relic_effects.py`: 48+ `REGULAR` relics + 48 `CURSED` (`cursed_*`) shadow relics + `REMOVED` frozenset + `duel_run(run)` (implements `relic_seal`).
- `archetype_relics.py` + `relic_affinity.py`: Deck-based shop weighting (`offer_weights()`, `shop_sample()`, `WEIGHTED_SHOP_SLOTS=2`).
- `relic_fusions.py`: 46 secret fusion recipes (`FUSED`, `NEEDED`, `REQUIRED`, `INCLUDES`, multi-tier `genetic_override`, tokens `MOTH_TOKEN=190000057`, `DANDELION_TOKEN=190000058`).
- `variant_relics.py`: Character alternate variant starting relics.
- `choice_relics.py`: Interactive choice relics (`relic_picker`, `card_picker`, `name_changer`, `weighted_coins`, `weighted_dice` -> `Duel.SetRogueWeight`), `VERY_RARE = ('almighty','the_creator','dimensional_rift')`.
- `cursed_relics.py`: Post-boss shadow relic offer (`create_offer`, `reroll`, `choose`, `skip`, `require_choice`, `data/cursed-relics.json`).
- `draft_relics.py`: Online Draft relics.
- `passives.py`: Compiles active relics + curses into `shadow-passives.lua` with `-- SHADOW_RUN_RNG 2`, `Duel.EnableRogueAntiStreak()`, `-- SHADOW_RUN_AI <json>`, and Hint protocol:
  - `1800000000+idx` (relic art banner), `1850000000+idx` (`--[[relic-ask]]` confirmation), `1860000000+idx` (prompt label), `1100000000..1700000000` (combat telemetry), `900000001` (phoenix revive).

## Safe MOD Extension & Removal
- **Add custom relic**: `mods/<id>/relics/*.json` (`key`, `name`, `zhName`, `cost`, `tag`, `description`, `zhDescription`, `art`, `luaEffect`).
- **Disable Relic Seal / Curses / Shadow Relics / Banish / Fusions or remove vanilla relics**: `mods/<id>/campaign-config.json` (or Visual Studio -> `campaign`).
