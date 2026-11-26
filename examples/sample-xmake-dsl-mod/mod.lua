-- =============================================================================
-- Sample Xmake-style Dual-Scope MOD Definition (mod.lua)
-- Description Scope: metadata, minGameVersion, wildcard globs, inline targets
-- Script Scope     : on_duel_script, on_duel_effect, on_utility_lua
-- =============================================================================

-- [Description Scope] Global metadata & version constraints
set_mod("sample-xmake-dsl-mod")
set_name("Sample Xmake-Style Dual-Scope DSL MOD")
set_version("1.0.0")
set_author("YGO Mod Community")
set_description("Demonstrates Xmake-style Description/Script Scope DSL: custom card, relic, booster pack, preconstructed deck, .ydk deck, playable character, trainer & JS hooks.")
set_min_gamever("0.2.46")
set_min_managerver("1.0.0")
add_tags("dsl", "lua", "js", "cards", "characters", "relics", "packs", "decks", "trainer")

-- Wildcard asset discovery (Xmake add_files style)
add_css_files("styles/*.css")
add_ydk("decks/*.ydk")

-- [Script Scope] Shared Lua helper library appended to utility.lua
on_utility_lua([[
SampleDslHelper = SampleDslHelper or {}
function SampleDslHelper.GainBonusLp(tp, amount)
    Duel.Recover(tp, amount or 500, REASON_EFFECT)
end
]])

-- 1) Custom Card Target (Description Scope + Script Scope)
card(190099002)
    set_name("Astral Insight")
    set_zh_name("星界洞察")
    set_desc("Draw 1 card, then gain 500 LP.")
    set_zh_desc("抽1张卡，那之后自己回复500基本分。")
    set_kind("Spell Card", "Normal")
    set_data_masks(2, "0", 0)
    set_pack("EN-DSL")
    on_duel_script([[
local s,id=GetID()
function s.initial_effect(c)
    local e1=Effect.CreateEffect(c)
    e1:SetCategory(CATEGORY_DRAW+CATEGORY_RECOVER)
    e1:SetType(EFFECT_TYPE_ACTIVATE)
    e1:SetCode(EVENT_FREE_CHAIN)
    e1:SetTarget(s.target)
    e1:SetOperation(s.activate)
    c:RegisterEffect(e1)
end
function s.target(e,tp,eg,ep,ev,re,r,rp,chk)
    if chk==0 then return Duel.IsPlayerCanDraw(tp,1) end
    Duel.SetOperationInfo(0,CATEGORY_DRAW,nil,0,tp,1)
end
function s.activate(e,tp,eg,ep,ev,re,r,rp)
    if Duel.Draw(tp,1,REASON_EFFECT)>0 and SampleDslHelper then
        SampleDslHelper.GainBonusLp(tp,500)
    end
end
]])
target_end()

-- 2) Custom Relic Target (Description Scope + Script Scope)
relic("dsl_starlight_compass")
    set_name("Starlight Compass")
    set_zh_name("星光罗盘")
    set_cost(85)
    set_tag("utility")
    set_art("Merchant Urn.png")
    set_desc("At the start of the duel, gain 600 LP.")
    set_zh_desc("决斗开始时，自己回复600基本分。")
    on_duel_effect([[
Duel.Recover(0, 600, REASON_EFFECT)
]])
target_end()

-- 3) Custom Booster Pack Target
pack("EN-DSL")
    set_name("Astral DSL Collection")
    set_zh_name("星界 DSL 扩展包")
    set_region("EN")
    set_code("DSL")
    set_size(9)
    add_ultra(190099002, 46986414, 89631139)
    add_super(38033121, 55144522, 5318639)
    add_rare(79571449, 44095762)
    add_common(83764718, 12580477)
target_end()

-- 4) Custom Preconstructed Structure Deck Target
preconstructed("DSL-STARTER")
    set_name("Structure Deck: Astral DSL")
    set_zh_name("构筑卡组：星界 DSL")
    set_art("SDK")
    set_date("2026-01-01")
    add_cards(190099002, 190099002, 46986414, 38033121, 55144522, 83764718, 5318639, 44095762)
target_end()

-- 5) Custom Playable Character Target
character("Astral Architect")
    set_zh_name("星界构筑师")
    set_sprite("dm01.png")
    set_race("Spellcaster")
    set_attribute("LIGHT")
    set_theme("Astral Spellcaster")
    set_color("#38bdf8")
    set_starting_relic("dsl_starlight_compass")
    set_unlocked(true)
    add_deck_cards("Astral Insight", "Astral Insight", "Dark Magician", "Dark Magician Girl", "Pot of Greed", "Monster Reborn")
    add_starting_packs("EN-DSL")
target_end()

-- 6) Optional Trainer Target (F8 In-Game Overlay + Starting Gold Bonus)
trainer()
    set_ingame_overlay(true)
    set_starting_gold_bonus(100)
target_end()
