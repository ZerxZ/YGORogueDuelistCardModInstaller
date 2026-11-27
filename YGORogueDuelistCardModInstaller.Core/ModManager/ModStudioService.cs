using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YGORogueDuelistCardModInstaller.Core.ModManager;

public sealed record CardPresetInfo(string Label, string DisplayType, int TypeMask, bool IsMonster, string DefaultRace);

public sealed record ModValidationReport(bool IsValid, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

/// <summary>
/// Beginner-friendly visual MOD authoring & validation service shared by the Avalonia MOD Studio.
/// 100% Native AOT compatible.
/// </summary>
public static class ModStudioService
{
    public static readonly IReadOnlyList<CardPresetInfo> CardTypePresets =
    [
        new("Effect Monster / 效果怪兽", "Effect Monster", 33, true, "Spellcaster"),
        new("Normal Monster / 通常怪兽", "Normal Monster", 17, true, "Dragon"),
        new("Tuner Effect Monster / 调整效果怪兽", "Tuner Monster", 4129, true, "Warrior"),
        new("Fusion Effect Monster / 融合效果怪兽", "Fusion Effect Monster", 97, true, "Spellcaster"),
        new("Ritual Effect Monster / 仪式效果怪兽", "Ritual Effect Monster", 161, true, "Warrior"),
        new("Synchro Effect Monster / 同调效果怪兽 (5D's)", "Synchro Monster", 8225, true, "Dragon"),
        new("Xyz Effect Monster / 超量效果怪兽 (ZEXAL)", "Xyz Monster", 8388641, true, "Warrior"),
        new("Pendulum Effect Monster / 灵摆效果怪兽 (ARC-V)", "Pendulum Effect Monster", 16777249, true, "Spellcaster"),
        new("Link Effect Monster / 连接效果怪兽 (VRAINS)", "Link Monster", 67108897, true, "Cyberse"),
        new("Normal Spell / 通常魔法", "Spell Card", 2, false, "Normal"),
        new("Quick-Play Spell / 速攻魔法", "Spell Card", 65538, false, "Quick-Play"),
        new("Continuous Spell / 永续魔法", "Spell Card", 131074, false, "Continuous"),
        new("Equip Spell / 装备魔法", "Spell Card", 262146, false, "Equip"),
        new("Field Spell / 场地魔法", "Spell Card", 524290, false, "Field"),
        new("Normal Trap / 通常陷阱", "Trap Card", 4, false, "Normal"),
        new("Continuous Trap / 永续陷阱", "Trap Card", 131076, false, "Continuous"),
        new("Counter Trap / 反击陷阱", "Trap Card", 1048580, false, "Counter")
    ];

    public static readonly IReadOnlyDictionary<string, int> AttributeMasks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        [""] = 0,
        ["EARTH"] = 1,
        ["WATER"] = 2,
        ["FIRE"] = 4,
        ["WIND"] = 8,
        ["LIGHT"] = 16,
        ["DARK"] = 32,
        ["DIVINE"] = 64
    };

    public static readonly IReadOnlyDictionary<string, string> MonsterRaceMasks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Warrior"] = "1",
        ["Spellcaster"] = "2",
        ["Fairy"] = "4",
        ["Fiend"] = "8",
        ["Zombie"] = "16",
        ["Machine"] = "32",
        ["Aqua"] = "64",
        ["Pyro"] = "128",
        ["Rock"] = "256",
        ["Winged Beast"] = "512",
        ["Plant"] = "1024",
        ["Insect"] = "2048",
        ["Thunder"] = "4096",
        ["Dragon"] = "8192",
        ["Beast"] = "16384",
        ["Beast-Warrior"] = "32768",
        ["Dinosaur"] = "65536",
        ["Fish"] = "131072",
        ["Sea Serpent"] = "262144",
        ["Reptile"] = "524288",
        ["Psychic"] = "1048576",
        ["Divine-Beast"] = "2097152",
        ["Wyrm"] = "8388608",
        ["Cyberse"] = "16777216",
        ["Illusion"] = "33554432"
    };

    public static int SuggestNextCardId(string modDir)
    {
        var maxId = 190010000;
        foreach (var file in ModRepository.EnumerateDeclarativeCardFiles(modDir))
        {
            try
            {
                var node = ModPatcher.ParseJson(File.ReadAllText(file, Encoding.UTF8));
                IEnumerable<JsonObject> objs = node is JsonArray arr
                    ? arr.OfType<JsonObject>()
                    : node is JsonObject o ? [o] : [];
                foreach (var obj in objs)
                {
                    if (obj["id"] is JsonValue v && v.TryGetValue<int>(out var id) && id > maxId)
                    {
                        maxId = id;
                    }
                }
            }
            catch
            {
                // Ignore malformed json while suggesting next id
            }
        }

        var dsl = ModDslParser.TryParseModDirectory(modDir);
        if (dsl is not null)
        {
            foreach (var obj in dsl.InlineCards)
            {
                if (obj["id"] is JsonValue v && v.TryGetValue<int>(out var id) && id > maxId)
                {
                    maxId = id;
                }
            }
        }

        return maxId + 1;
    }

    public static string BuildLuaCardTemplate(string templateKey, int cardId, string cardName)
    {
        return templateKey.Trim().ToLowerInvariant() switch
        {
            "draw1" => $$"""
                -- {{cardName}} (c{{cardId}}.lua)
                local s,id=GetID()
                function s.initial_effect(c)
                    local e1=Effect.CreateEffect(c)
                    e1:SetCategory(CATEGORY_DRAW)
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
                    Duel.Draw(tp,1,REASON_EFFECT)
                end
                """,
            "draw2" => $$"""
                -- {{cardName}} (c{{cardId}}.lua)
                local s,id=GetID()
                function s.initial_effect(c)
                    local e1=Effect.CreateEffect(c)
                    e1:SetCategory(CATEGORY_DRAW)
                    e1:SetType(EFFECT_TYPE_ACTIVATE)
                    e1:SetCode(EVENT_FREE_CHAIN)
                    e1:SetTarget(s.target)
                    e1:SetOperation(s.activate)
                    c:RegisterEffect(e1)
                end
                function s.target(e,tp,eg,ep,ev,re,r,rp,chk)
                    if chk==0 then return Duel.IsPlayerCanDraw(tp,2) end
                    Duel.SetOperationInfo(0,CATEGORY_DRAW,nil,0,tp,2)
                end
                function s.activate(e,tp,eg,ep,ev,re,r,rp)
                    Duel.Draw(tp,2,REASON_EFFECT)
                end
                """,
            "gain_lp" => $$"""
                -- {{cardName}} (c{{cardId}}.lua)
                local s,id=GetID()
                function s.initial_effect(c)
                    local e1=Effect.CreateEffect(c)
                    e1:SetCategory(CATEGORY_RECOVER)
                    e1:SetType(EFFECT_TYPE_ACTIVATE)
                    e1:SetCode(EVENT_FREE_CHAIN)
                    e1:SetOperation(function(e,tp) Duel.Recover(tp,1000,REASON_EFFECT) end)
                    c:RegisterEffect(e1)
                end
                """,
            "destroy_monster" => $$"""
                -- {{cardName}} (c{{cardId}}.lua)
                local s,id=GetID()
                function s.initial_effect(c)
                    local e1=Effect.CreateEffect(c)
                    e1:SetCategory(CATEGORY_DESTROY)
                    e1:SetType(EFFECT_TYPE_ACTIVATE)
                    e1:SetCode(EVENT_FREE_CHAIN)
                    e1:SetTarget(s.target)
                    e1:SetOperation(s.activate)
                    c:RegisterEffect(e1)
                end
                function s.target(e,tp,eg,ep,ev,re,r,rp,chk)
                    if chk==0 then return Duel.IsExistingMatchingCard(aux.TRUE,tp,0,LOCATION_MZONE,1,nil) end
                    local g=Duel.GetMatchingGroup(aux.TRUE,tp,0,LOCATION_MZONE,nil)
                    Duel.SetOperationInfo(0,CATEGORY_DESTROY,g,1,0,0)
                end
                function s.activate(e,tp,eg,ep,ev,re,r,rp)
                    Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_DESTROY)
                    local g=Duel.SelectMatchingCard(tp,aux.TRUE,tp,0,LOCATION_MZONE,1,1,nil)
                    if #g>0 then Duel.Destroy(g,REASON_EFFECT) end
                end
                """,
            "monster_summon_draw" => $$"""
                -- {{cardName}} (c{{cardId}}.lua)
                local s,id=GetID()
                function s.initial_effect(c)
                    local e1=Effect.CreateEffect(c)
                    e1:SetDescription(aux.Stringid(id,0))
                    e1:SetCategory(CATEGORY_DRAW)
                    e1:SetType(EFFECT_TYPE_SINGLE+EFFECT_TYPE_TRIGGER_O)
                    e1:SetCode(EVENT_SUMMON_SUCCESS)
                    e1:SetProperty(EFFECT_FLAG_DELAY)
                    e1:SetTarget(s.drtg)
                    e1:SetOperation(s.drop)
                    c:RegisterEffect(e1)
                    local e2=e1:Clone()
                    e2:SetCode(EVENT_SPSUMMON_SUCCESS)
                    c:RegisterEffect(e2)
                end
                function s.drtg(e,tp,eg,ep,ev,re,r,rp,chk)
                    if chk==0 then return Duel.IsPlayerCanDraw(tp,1) end
                    Duel.SetOperationInfo(0,CATEGORY_DRAW,nil,0,tp,1)
                end
                function s.drop(e,tp,eg,ep,ev,re,r,rp)
                    Duel.Draw(tp,1,REASON_EFFECT)
                end
                """,
            "synchro_monster" => $$"""
                -- {{cardName}} (c{{cardId}}.lua) - Synchro Monster (proc_synchro.lua)
                local s,id,o=GetID()
                function s.initial_effect(c)
                    -- 1 Tuner + 1+ non-Tuner monsters
                    Synchro.AddProcedure(c,nil,1,1,Synchro.NonTuner(nil),1,99)
                    c:EnableReviveLimit()
                    -- On Synchro Summon: draw 1 card
                    local e1=Effect.CreateEffect(c)
                    e1:SetDescription(aux.Stringid(id,0))
                    e1:SetCategory(CATEGORY_DRAW)
                    e1:SetType(EFFECT_TYPE_SINGLE+EFFECT_TYPE_TRIGGER_O)
                    e1:SetProperty(EFFECT_FLAG_DELAY)
                    e1:SetCode(EVENT_SPSUMMON_SUCCESS)
                    e1:SetCondition(function(e) return e:GetHandler():IsSummonType(SUMMON_TYPE_SYNCHRO) end)
                    e1:SetTarget(function(e,tp,eg,ep,ev,re,r,rp,chk)
                        if chk==0 then return Duel.IsPlayerCanDraw(tp,1) end
                        Duel.SetOperationInfo(0,CATEGORY_DRAW,nil,0,tp,1)
                    end)
                    e1:SetOperation(function(e,tp) Duel.Draw(tp,1,REASON_EFFECT) end)
                    c:RegisterEffect(e1)
                end
                """,
            "xyz_monster" => $$"""
                -- {{cardName}} (c{{cardId}}.lua) - Xyz Monster (proc_xyz.lua)
                local s,id,o=GetID()
                function s.initial_effect(c)
                    -- 2 Level 4 monsters
                    Xyz.AddProcedure(c,nil,4,2)
                    c:EnableReviveLimit()
                    -- Once per turn: detach 1 material; destroy 1 monster opponent controls
                    local e1=Effect.CreateEffect(c)
                    e1:SetDescription(aux.Stringid(id,0))
                    e1:SetCategory(CATEGORY_DESTROY)
                    e1:SetType(EFFECT_TYPE_IGNITION)
                    e1:SetRange(LOCATION_MZONE)
                    e1:SetCountLimit(1)
                    e1:SetCost(Cost.DetachFromSelf(1))
                    e1:SetTarget(function(e,tp,eg,ep,ev,re,r,rp,chk)
                        if chk==0 then return Duel.IsExistingMatchingCard(aux.TRUE,tp,0,LOCATION_MZONE,1,nil) end
                        Duel.SetOperationInfo(0,CATEGORY_DESTROY,nil,1,1-tp,LOCATION_MZONE)
                    end)
                    e1:SetOperation(function(e,tp)
                        Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_DESTROY)
                        local g=Duel.SelectMatchingCard(tp,aux.TRUE,tp,0,LOCATION_MZONE,1,1,nil)
                        if #g>0 then Duel.Destroy(g,REASON_EFFECT) end
                    end)
                    c:RegisterEffect(e1)
                end
                """,
            "pendulum_monster" => $$"""
                -- {{cardName}} (c{{cardId}}.lua) - Pendulum Monster (proc_pendulum.lua, requires MR3/MR5)
                local s,id,o=GetID()
                function s.initial_effect(c)
                    Pendulum.AddProcedure(c)
                    -- Pendulum Effect: monsters you control gain 300 ATK
                    local e1=Effect.CreateEffect(c)
                    e1:SetType(EFFECT_TYPE_FIELD)
                    e1:SetCode(EFFECT_UPDATE_ATTACK)
                    e1:SetRange(LOCATION_PZONE)
                    e1:SetTargetRange(LOCATION_MZONE,0)
                    e1:SetValue(300)
                    c:RegisterEffect(e1)
                end
                """,
            "link_monster" => $$"""
                -- {{cardName}} (c{{cardId}}.lua) - Link Monster (proc_link.lua, requires MR4/MR5)
                local s,id,o=GetID()
                function s.initial_effect(c)
                    -- 2 Effect Monsters (set data.link_marker e.g. 0x5 = Bottom-Left + Bottom-Right, level = Link Rating 2)
                    Link.AddProcedure(c,aux.FilterBoolFunctionEx(Card.IsType,TYPE_EFFECT),2,2)
                    c:EnableReviveLimit()
                    local e1=Effect.CreateEffect(c)
                    e1:SetDescription(aux.Stringid(id,0))
                    e1:SetCategory(CATEGORY_DRAW)
                    e1:SetType(EFFECT_TYPE_SINGLE+EFFECT_TYPE_TRIGGER_O)
                    e1:SetProperty(EFFECT_FLAG_DELAY)
                    e1:SetCode(EVENT_SPSUMMON_SUCCESS)
                    e1:SetCondition(function(e) return e:GetHandler():IsSummonType(SUMMON_TYPE_LINK) end)
                    e1:SetTarget(function(e,tp,eg,ep,ev,re,r,rp,chk)
                        if chk==0 then return Duel.IsPlayerCanDraw(tp,1) end
                        Duel.SetOperationInfo(0,CATEGORY_DRAW,nil,0,tp,1)
                    end)
                    e1:SetOperation(function(e,tp) Duel.Draw(tp,1,REASON_EFFECT) end)
                    c:RegisterEffect(e1)
                end
                """,
            _ => $$"""
                -- {{cardName}} (c{{cardId}}.lua)
                local s,id=GetID()
                function s.initial_effect(c)
                    -- Register card effects here
                end
                """
        };
    }

    public static string BuildLuaRelicTemplate(string templateKey) => templateKey.Trim().ToLowerInvariant() switch
    {
        "gain_lp" => "Duel.Recover(0, 1000, REASON_EFFECT)",
        "draw_extra" => "Duel.Draw(0, 1, REASON_EFFECT)",
        "damage_opp" => "Duel.Damage(1, 800, REASON_EFFECT)",
        _ => "-- Custom Lua executed at duel start\nDuel.Recover(0, 500, REASON_EFFECT)"
    };

    public static string SaveVisualCard(
        string modDir,
        int id,
        string name,
        string? zhName,
        string desc,
        string? zhDesc,
        CardPresetInfo preset,
        string race,
        string attribute,
        int level,
        int atk,
        int defense,
        string? packId,
        string? artSourcePath,
        string? luaScript)
    {
        if (id <= 0) throw new ModOperationException("卡牌 ID 必须为正整数（自定义卡推荐 190010001+）。");
        if (string.IsNullOrWhiteSpace(name)) throw new ModOperationException("请输入卡牌英文名称。");

        var attrMask = preset.IsMonster && AttributeMasks.TryGetValue(attribute, out var am) ? am : 0;
        var raceMask = preset.IsMonster && MonsterRaceMasks.TryGetValue(race, out var rm) ? rm : "0";
        var effectiveRace = string.IsNullOrWhiteSpace(race) ? preset.DefaultRace : race.Trim();

        var cardObj = new JsonObject
        {
            ["id"] = id,
            ["name"] = name.Trim(),
            ["desc"] = desc.Trim(),
            ["type"] = preset.DisplayType,
            ["race"] = effectiveRace,
            ["attribute"] = preset.IsMonster ? attribute.Trim().ToUpperInvariant() : string.Empty,
            ["level"] = preset.IsMonster ? level : 0,
            ["atk"] = preset.IsMonster ? atk : 0,
            ["defense"] = preset.IsMonster ? defense : 0,
            ["addToEraPool"] = true,
            ["data"] = new JsonObject
            {
                ["code"] = id,
                ["alias"] = 0,
                ["setcodes"] = new JsonArray(),
                ["type"] = preset.TypeMask,
                ["level"] = preset.IsMonster ? level : 0,
                ["attribute"] = attrMask,
                ["race"] = raceMask,
                ["attack"] = preset.IsMonster ? atk : 0,
                ["defense"] = preset.IsMonster ? defense : 0,
                ["lscale"] = 0,
                ["rscale"] = 0,
                ["link_marker"] = 0
            }
        };

        if (!string.IsNullOrWhiteSpace(zhName)) cardObj["zhName"] = zhName.Trim();
        if (!string.IsNullOrWhiteSpace(zhDesc)) cardObj["zhDesc"] = zhDesc.Trim();
        if (!string.IsNullOrWhiteSpace(packId)) cardObj["packId"] = packId.Trim();

        var cardsFile = Path.Combine(modDir, "cards", "custom-cards.json");
        UpsertIntoJsonArrayFile(cardsFile, "id", id.ToString(CultureInfo.InvariantCulture), cardObj);

        if (!string.IsNullOrWhiteSpace(artSourcePath) && File.Exists(artSourcePath))
        {
            var ext = Path.GetExtension(artSourcePath).ToLowerInvariant();
            if (ext is not (".webp" or ".png" or ".jpg" or ".jpeg")) ext = ".png";
            var artDir = Path.Combine(modDir, "art");
            Directory.CreateDirectory(artDir);
            File.Copy(artSourcePath, Path.Combine(artDir, $"{id}{ext}"), overwrite: true);
        }

        if (!string.IsNullOrWhiteSpace(luaScript))
        {
            var luaDir = Path.Combine(modDir, "lua", "cards");
            Directory.CreateDirectory(luaDir);
            File.WriteAllText(Path.Combine(luaDir, $"c{id}.lua"), luaScript.Replace("\r\n", "\n"), ModPatcher.Utf8NoBom);
        }

        return cardsFile;
    }

    public static string SaveVisualCharacter(
        string modDir,
        string name,
        string? zhName,
        string sprite,
        string race,
        string attribute,
        string color,
        string startingRelic,
        string startingPacksCsv,
        string deckCardNamesText,
        bool unlockedByDefault)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ModOperationException("请输入角色名称。");
        var slug = ModRepository.SanitizeId(name);
        if (string.IsNullOrWhiteSpace(slug)) slug = "custom-character";

        var deckCards = SplitNames(deckCardNamesText);
        var packs = SplitNames(startingPacksCsv);

        var spriteVal = string.IsNullOrWhiteSpace(sprite) ? "dm01.png" : sprite.Trim();
        if (File.Exists(spriteVal))
        {
            var ext = Path.GetExtension(spriteVal).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp")) ext = ".png";
            var charArtDir = Path.Combine(modDir, "art", "characters");
            Directory.CreateDirectory(charArtDir);
            var destName = $"{slug}{ext}";
            var destPath = Path.Combine(charArtDir, destName);
            if (!string.Equals(Path.GetFullPath(spriteVal), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(spriteVal, destPath, overwrite: true);
            }
            spriteVal = $"characters/{destName}";
        }
        else if (spriteVal.StartsWith("art/", StringComparison.OrdinalIgnoreCase))
        {
            spriteVal = spriteVal[4..];
        }

        var obj = new JsonObject
        {
            ["name"] = name.Trim(),
            ["sprite"] = spriteVal,
            ["race"] = string.IsNullOrWhiteSpace(race) ? "Spellcaster" : race.Trim(),
            ["attribute"] = string.IsNullOrWhiteSpace(attribute) ? "DARK" : attribute.Trim().ToUpperInvariant(),
            ["color"] = string.IsNullOrWhiteSpace(color) ? "#f4cf57" : color.Trim(),
            ["starting_relic"] = string.IsNullOrWhiteSpace(startingRelic) ? "mage" : startingRelic.Trim(),
            ["unlockedByDefault"] = unlockedByDefault
        };

        if (!string.IsNullOrWhiteSpace(zhName)) obj["zhName"] = zhName.Trim();
        if (deckCards.Count > 0)
            obj["cards"] = new JsonArray(deckCards.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());
        if (packs.Count > 0)
            obj["starting_packs"] = new JsonArray(packs.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());

        var dir = Path.Combine(modDir, "characters");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{slug}.json");
        File.WriteAllText(path, obj.ToJsonString(ModPatcher.JsonOptions), ModPatcher.Utf8NoBom);
        return path;
    }

    public static string SaveVisualRelic(
        string modDir,
        string key,
        string name,
        string? zhName,
        int cost,
        string tag,
        string description,
        string? zhDescription,
        string? artPathOrName,
        string? luaEffect)
    {
        var cleanKey = ModRepository.SanitizeId(key).Replace('-', '_');
        if (string.IsNullOrWhiteSpace(cleanKey)) throw new ModOperationException("请输入遗物 Key（英文/下划线，如 starlight_urn）。");
        if (string.IsNullOrWhiteSpace(name)) name = cleanKey;

        var artField = "Merchant Urn.png";
        if (!string.IsNullOrWhiteSpace(artPathOrName))
        {
            if (File.Exists(artPathOrName))
            {
                var fileName = $"relic_{cleanKey}{Path.GetExtension(artPathOrName).ToLowerInvariant()}";
                var artDir = Path.Combine(modDir, "art");
                Directory.CreateDirectory(artDir);
                File.Copy(artPathOrName, Path.Combine(artDir, fileName), overwrite: true);
                artField = fileName;
            }
            else
            {
                artField = artPathOrName.Trim();
            }
        }

        var obj = new JsonObject
        {
            ["key"] = cleanKey,
            ["name"] = name.Trim(),
            ["cost"] = Math.Max(0, cost),
            ["tag"] = string.IsNullOrWhiteSpace(tag) ? "utility" : tag.Trim(),
            ["description"] = description.Trim(),
            ["art"] = artField,
            ["effect"] = $"approved_{cleanKey}",
            ["amount"] = 0,
            ["filter"] = string.Empty
        };

        if (!string.IsNullOrWhiteSpace(zhName)) obj["zhName"] = zhName.Trim();
        if (!string.IsNullOrWhiteSpace(zhDescription)) obj["zhDescription"] = zhDescription.Trim();
        if (!string.IsNullOrWhiteSpace(luaEffect)) obj["luaEffect"] = luaEffect.Trim();

        var dir = Path.Combine(modDir, "relics");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{cleanKey}.json");
        File.WriteAllText(path, obj.ToJsonString(ModPatcher.JsonOptions), ModPatcher.Utf8NoBom);
        return path;
    }

    public static string SaveVisualPack(
        string modDir,
        string packId,
        string name,
        string? zhName,
        string code,
        int size,
        string commonIdsText,
        string rareIdsText,
        string superIdsText,
        string ultraIdsText,
        string? coverSourcePath = null)
    {
        if (string.IsNullOrWhiteSpace(packId)) throw new ModOperationException("请输入卡包 ID（如 EN-MYPACK）。");
        var id = packId.Trim();
        var coverRel = string.Empty;
        if (!string.IsNullOrWhiteSpace(coverSourcePath))
        {
            var trimmed = coverSourcePath.Trim();
            if (File.Exists(trimmed))
            {
                var ext = Path.GetExtension(trimmed).ToLowerInvariant();
                if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp")) ext = ".jpg";
                var packArtDir = Path.Combine(modDir, "art", "packs");
                Directory.CreateDirectory(packArtDir);
                var destName = $"{id}{ext}";
                var destPath = Path.Combine(packArtDir, destName);
                if (!string.Equals(Path.GetFullPath(trimmed), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(trimmed, destPath, overwrite: true);
                }
                coverRel = $"art/packs/{destName}";
            }
            else
            {
                coverRel = trimmed.Replace('\\', '/');
            }
        }

        var obj = new JsonObject
        {
            ["id"] = id,
            ["name"] = string.IsNullOrWhiteSpace(name) ? id : name.Trim(),
            ["region"] = "EN",
            ["code"] = string.IsNullOrWhiteSpace(code) ? (id.Contains('-') ? id.Split('-').Last() : id) : code.Trim(),
            ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["image"] = string.Empty,
            ["size"] = Math.Clamp(size, 1, 30),
            ["common"] = ParseIdJsonArray(commonIdsText),
            ["rare"] = ParseIdJsonArray(rareIdsText),
            ["super"] = ParseIdJsonArray(superIdsText),
            ["ultra"] = ParseIdJsonArray(ultraIdsText)
        };
        if (!string.IsNullOrWhiteSpace(zhName)) obj["zhName"] = zhName.Trim();
        if (!string.IsNullOrWhiteSpace(coverRel)) obj["cover"] = coverRel;

        var dir = Path.Combine(modDir, "packs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{ModRepository.SanitizeId(id)}.json");
        File.WriteAllText(path, obj.ToJsonString(ModPatcher.JsonOptions), ModPatcher.Utf8NoBom);
        return path;
    }

    public static string SaveVisualPreconstructedDeck(
        string modDir,
        string deckId,
        string name,
        string? zhName,
        string artCode,
        string cardIdsText,
        string? coverSourcePath = null)
    {
        if (string.IsNullOrWhiteSpace(deckId)) throw new ModOperationException("请输入预组卡组 ID（如 MY-STARTER）。");
        var cards = ParseIdJsonArray(cardIdsText);
        if (cards.Count == 0) throw new ModOperationException("请至少填入 1 个有效卡牌数字 ID。");

        var id = deckId.Trim();
        var effectiveArt = string.IsNullOrWhiteSpace(artCode) ? id : artCode.Trim();
        var coverRel = string.Empty;
        if (!string.IsNullOrWhiteSpace(coverSourcePath))
        {
            var trimmed = coverSourcePath.Trim();
            if (File.Exists(trimmed))
            {
                var ext = Path.GetExtension(trimmed).ToLowerInvariant();
                if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp")) ext = ".jpg";
                var deckArtDir = Path.Combine(modDir, "art", "decks");
                Directory.CreateDirectory(deckArtDir);
                var destName = $"{effectiveArt}{ext}";
                var destPath = Path.Combine(deckArtDir, destName);
                if (!string.Equals(Path.GetFullPath(trimmed), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(trimmed, destPath, overwrite: true);
                }
                coverRel = $"art/decks/{destName}";
            }
            else
            {
                coverRel = trimmed.Replace('\\', '/');
            }
        }

        var obj = new JsonObject
        {
            ["id"] = id,
            ["name"] = string.IsNullOrWhiteSpace(name) ? id : name.Trim(),
            ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["art"] = effectiveArt,
            ["cards"] = cards
        };
        if (!string.IsNullOrWhiteSpace(zhName)) obj["zhName"] = zhName.Trim();
        if (!string.IsNullOrWhiteSpace(coverRel)) obj["cover"] = coverRel;

        var dir = Path.Combine(modDir, "preconstructed");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{ModRepository.SanitizeId(id)}.json");
        File.WriteAllText(path, obj.ToJsonString(ModPatcher.JsonOptions), ModPatcher.Utf8NoBom);
        return path;
    }

    public static string ImportYdkFile(string modDir, string sourceYdkPath)
    {
        if (!File.Exists(sourceYdkPath)) throw new ModOperationException($"找不到 YDK 文件：{sourceYdkPath}");
        var decksDir = Path.Combine(modDir, "decks");
        Directory.CreateDirectory(decksDir);
        var dest = Path.Combine(decksDir, Path.GetFileName(sourceYdkPath));
        File.Copy(sourceYdkPath, dest, overwrite: true);
        return dest;
    }

    public static string SaveVisualTrainer(
        string modDir,
        bool unlockAllCharacters,
        bool unlockAllVariants,
        int? startingGold,
        int? startingGoldBonus,
        int? startingLp,
        bool freeShop,
        double? shopPriceMultiplier,
        double? rewardGoldMultiplier,
        string startingRelicsCsv,
        bool inGameOverlay)
    {
        var obj = new JsonObject
        {
            ["unlockAllCharacters"] = unlockAllCharacters,
            ["unlockAllVariants"] = unlockAllVariants,
            ["freeShop"] = freeShop,
            ["inGameOverlay"] = inGameOverlay
        };
        if (startingGold is > 0) obj["startingGold"] = startingGold.Value;
        if (startingGoldBonus is > 0) obj["startingGoldBonus"] = startingGoldBonus.Value;
        if (startingLp is > 0) obj["startingLp"] = startingLp.Value;
        if (shopPriceMultiplier is > 0) obj["shopPriceMultiplier"] = shopPriceMultiplier.Value;
        if (rewardGoldMultiplier is > 0) obj["rewardGoldMultiplier"] = rewardGoldMultiplier.Value;

        var relics = SplitNames(startingRelicsCsv);
        if (relics.Count > 0)
            obj["startingRelics"] = new JsonArray(relics.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());

        var path = Path.Combine(modDir, "trainer.json");
        File.WriteAllText(path, obj.ToJsonString(ModPatcher.JsonOptions), ModPatcher.Utf8NoBom);
        return path;
    }

    public static (string LuaPath, string JsPath) ScaffoldXmakeDsl(ManagedMod mod)
    {
        var luaPath = Path.Combine(mod.DirectoryPath, ModDslParser.LuaDslFileName);
        var jsPath = Path.Combine(mod.DirectoryPath, ModDslParser.JsDslFileName);

        if (!File.Exists(luaPath))
        {
            var luaContent = $$"""
                -- Xmake-style Dual-Scope MOD Definition (mod.lua)
                set_mod("{{mod.Id}}")
                set_name("{{mod.Manifest.Name.Replace("\"", "\\\"")}}")
                set_version("{{mod.Manifest.Version}}")
                set_author("{{mod.Manifest.Author.Replace("\"", "\\\"")}}")
                set_min_gamever("{{mod.Manifest.MinGameVersion ?? "0.2.46"}}")
                set_min_managerver("1.0.0")

                -- Wildcard asset discovery
                add_cards("cards/*.json")
                add_characters("characters/*.json")
                add_relics("relics/*.json")
                add_packs("packs/*.json")
                add_preconstructed("preconstructed/*.json")
                add_ydk("decks/*.ydk")
                add_css_files("styles/*.css")

                -- Example inline card block (Description Scope + Script Scope):
                -- card(190010099)
                --     set_name("My DSL Spell")
                --     set_zh_name("我的 DSL 魔法")
                --     set_kind("Spell Card", "Normal")
                --     set_data_masks(2, "0", 0)
                --     on_duel_script([[
                -- local s,id=GetID()
                -- function s.initial_effect(c)
                --     local e1=Effect.CreateEffect(c)
                --     e1:SetCategory(CATEGORY_DRAW)
                --     e1:SetType(EFFECT_TYPE_ACTIVATE)
                --     e1:SetCode(EVENT_FREE_CHAIN)
                --     e1:SetOperation(function(e,tp) Duel.Draw(tp,1,REASON_EFFECT) end)
                --     c:RegisterEffect(e1)
                -- end
                --     ]])
                -- target_end()
                """;
            File.WriteAllText(luaPath, luaContent, ModPatcher.Utf8NoBom);
        }

        if (!File.Exists(jsPath))
        {
            var jsContent = """
                // Xmake-style Dual-Scope JS DSL (mod.js)
                add_css_files("styles/*.css");

                on_load(async (ctx) => {
                  ctx.log("MOD JS DSL loaded", ctx.mod);
                  ctx.showToast(`MOD Ready: ${ctx.mod.name}`, 2500);
                });

                on_hotkey("Alt+M", (ctx) => {
                  ctx.showToast(`[${ctx.mod.id}] Hotkey Alt+M triggered!`);
                });
                """;
            File.WriteAllText(jsPath, jsContent, ModPatcher.Utf8NoBom);
        }

        return (luaPath, jsPath);
    }

    public static List<string> ListModTextFiles(string modDir)
    {
        if (!Directory.Exists(modDir)) return [];
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".lua", ".js", ".mjs", ".json", ".css", ".ydk", ".txt", ".md", ".py", ".html"
        };

        return Directory.EnumerateFiles(modDir, "*", SearchOption.AllDirectories)
            .Where(f => allowed.Contains(Path.GetExtension(f)))
            .Select(f => Path.GetRelativePath(modDir, f).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string ReadModTextFile(string modDir, string relativePath)
    {
        var full = ResolveSafeModPath(modDir, relativePath);
        return File.Exists(full) ? File.ReadAllText(full, Encoding.UTF8) : string.Empty;
    }

    public static string WriteModTextFile(string modDir, string relativePath, string content)
    {
        var full = ResolveSafeModPath(modDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, ModPatcher.Utf8NoBom);
        return full;
    }

    public static ModValidationReport ValidateMod(ModRepository repository, string modId, string? activeGameVersion = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var mods = repository.LoadMods();
        var mod = mods.FirstOrDefault(m => string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase));
        if (mod is null)
        {
            errors.Add($"找不到 MOD：{modId}");
            return new ModValidationReport(false, errors, warnings);
        }

        // 1. Validate all JSON files parse cleanly
        foreach (var file in Directory.EnumerateFiles(mod.DirectoryPath, "*.json", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(mod.DirectoryPath, file).Replace('\\', '/');
            try
            {
                JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                errors.Add($"JSON 语法错误 ({rel}): {ex.Message}");
            }
        }

        // 2. Validate card IDs & check Lua script presence
        var luaScripts = ModRepository.EnumerateLuaCardScriptFiles(mod.DirectoryPath)
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dsl = ModDslParser.TryParseModDirectory(mod.DirectoryPath);
        if (dsl is not null)
        {
            foreach (var k in dsl.InlineLuaCardScripts.Keys) luaScripts.Add(k);
        }

        foreach (var cardFile in ModRepository.EnumerateDeclarativeCardFiles(mod.DirectoryPath))
        {
            try
            {
                var node = ModPatcher.ParseJson(File.ReadAllText(cardFile, Encoding.UTF8));
                IEnumerable<JsonObject> objs = node is JsonArray arr
                    ? arr.OfType<JsonObject>()
                    : node is JsonObject o ? [o] : [];
                foreach (var obj in objs)
                {
                    if (obj["id"] is not JsonValue v || !v.TryGetValue<int>(out var cid) || cid <= 0)
                    {
                        errors.Add($"{Path.GetFileName(cardFile)}: 卡片缺少有效的数字 id");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(obj["name"]?.GetValue<string>()))
                        errors.Add($"卡片 {cid}: 缺少 name 字段");

                    var typeMask = obj["data"]?["type"] is JsonValue tv && tv.TryGetValue<int>(out var tm) ? tm : 0;
                    var isNormalMonster = (typeMask & 16) != 0 && (typeMask & 32) == 0;
                    if (!isNormalMonster && !luaScripts.Contains($"c{cid}.lua"))
                        warnings.Add($"卡片 {cid} ({obj["name"]}) 未找到对应的 lua/cards/c{cid}.lua 脚本（若非原版已有脚本，决斗中可能无效果）");
                }
            }
            catch
            {
                // Already captured in JSON syntax pass
            }
        }

        // 3. Version constraint check
        if (!string.IsNullOrWhiteSpace(activeGameVersion))
        {
            foreach (var issue in ModRepository.EvaluateVersionIssues([mod], activeGameVersion))
            {
                warnings.Add(issue.Message);
            }
        }

        return new ModValidationReport(errors.Count == 0, errors, warnings);
    }

    private static string ResolveSafeModPath(string modDir, string relativePath)
    {
        var rel = relativePath.Trim().Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var fullMod = Path.GetFullPath(modDir);
        var full = Path.GetFullPath(Path.Combine(fullMod, rel));
        if (!full.StartsWith(fullMod, StringComparison.OrdinalIgnoreCase))
        {
            throw new ModOperationException($"非法路径越界：{relativePath}");
        }

        return full;
    }

    private static void UpsertIntoJsonArrayFile(string filePath, string idProp, string idVal, JsonObject newObj)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        JsonArray arr;
        if (File.Exists(filePath))
        {
            var parsed = ModPatcher.ParseJson(File.ReadAllText(filePath, Encoding.UTF8));
            arr = parsed as JsonArray ?? new JsonArray(parsed);
        }
        else
        {
            arr = new JsonArray();
        }

        var replaced = false;
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is JsonObject existing &&
                string.Equals(existing[idProp]?.ToString(), idVal, StringComparison.OrdinalIgnoreCase))
            {
                arr[i] = newObj;
                replaced = true;
                break;
            }
        }

        if (!replaced) arr.Add((JsonNode?)newObj);
        File.WriteAllText(filePath, arr.ToJsonString(ModPatcher.JsonOptions), ModPatcher.Utf8NoBom);
    }

    private static List<string> SplitNames(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return text
            .Split([',', '，', '\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static JsonArray ParseIdJsonArray(string? text)
    {
        var arr = new JsonArray();
        if (string.IsNullOrWhiteSpace(text)) return arr;
        foreach (var token in text.Split([',', '，', ' ', '\t', '\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
            {
                arr.Add((JsonNode?)JsonValue.Create(id));
            }
        }

        return arr;
    }
}
