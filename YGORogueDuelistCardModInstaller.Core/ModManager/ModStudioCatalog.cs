using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace YGORogueDuelistCardModInstaller.Core.ModManager;

public enum StudioCategory
{
    Cards,
    Characters,
    Relics,
    Packs,
    Decks,
    Trainer,
    Code
}

public sealed record StudioImageRef(
    string? LocalFilePath,
    string? DataUri,
    string? VanillaZipEntry);

public sealed record StudioCardRecord(
    int Id,
    string Name,
    string ZhName,
    string Desc,
    string ZhDesc,
    string DisplayType,
    string Race,
    string Attribute,
    int Level,
    int Atk,
    int Def,
    int TypeMask,
    string RaceMask,
    int AttributeMask,
    string PackId,
    string LuaScript,
    string SourceTag,
    string? SourceFilePath,
    StudioImageRef Image);

public sealed record StudioCharacterRecord(
    string Key,
    int? Index,
    string Name,
    string ZhName,
    string Sprite,
    string Race,
    string Attribute,
    string Color,
    string StartingRelic,
    IReadOnlyList<string> StartingPacks,
    IReadOnlyList<string> DeckCardNames,
    bool UnlockedByDefault,
    string SourceTag,
    string? SourceFilePath,
    StudioImageRef Image);

public sealed record StudioRelicRecord(
    string Key,
    string Name,
    string ZhName,
    int Cost,
    string Tag,
    string Description,
    string ZhDescription,
    string Art,
    string LuaEffect,
    string SourceTag,
    string? SourceFilePath,
    StudioImageRef Image);

public sealed record StudioPackRecord(
    string Id,
    string Name,
    string ZhName,
    string Code,
    int Size,
    IReadOnlyList<int> CommonIds,
    IReadOnlyList<int> RareIds,
    IReadOnlyList<int> SuperIds,
    IReadOnlyList<int> UltraIds,
    string SourceTag,
    string? SourceFilePath,
    StudioImageRef Image)
{
    public int TotalCardCount => CommonIds.Count + RareIds.Count + SuperIds.Count + UltraIds.Count;
}

public sealed record StudioDeckRecord(
    string Id,
    string Name,
    string ZhName,
    string Art,
    IReadOnlyList<int> CardIds,
    bool IsYdk,
    string SourceTag,
    string? SourceFilePath,
    StudioImageRef Image);

public sealed record StudioTrainerRecord(
    bool Exists,
    bool UnlockAllCharacters,
    bool UnlockAllVariants,
    int? StartingGold,
    int? StartingGoldBonus,
    int? StartingLp,
    bool FreeShop,
    double? ShopPriceMultiplier,
    double? RewardGoldMultiplier,
    IReadOnlyList<string> StartingRelics,
    bool InGameOverlay);

public sealed class ModStudioSnapshot
{
    public required string ModId { get; init; }
    public required string ModDir { get; init; }
    public required IReadOnlyList<StudioCardRecord> Cards { get; init; }
    public required IReadOnlyList<StudioCharacterRecord> Characters { get; init; }
    public required IReadOnlyList<StudioRelicRecord> Relics { get; init; }
    public required IReadOnlyList<StudioPackRecord> Packs { get; init; }
    public required IReadOnlyList<StudioDeckRecord> Decks { get; init; }
    public required StudioTrainerRecord Trainer { get; init; }
    public required IReadOnlyList<string> TextFiles { get; init; }
}

/// <summary>
/// Scans any managed MOD (modular v1 JSON, Xmake mod.lua/mod.js DSL, AND legacy data/mod.json)
/// into rich visual studio records with resolved local/data-uri/vanilla-zip artwork references.
/// 100% Native AOT compatible.
/// </summary>
public static class ModStudioCatalog
{
    public static ModStudioSnapshot Load(string modId, string modDir, string? vanillaZipPath)
    {
        var vanillaEntries = BuildVanillaEntryLookup(vanillaZipPath);
        var artDir = Path.Combine(modDir, "art");
        var dsl = ModDslParser.TryParseModDirectory(modDir);

        string? FindLocalArt(string stem)
        {
            if (!Directory.Exists(artDir) || string.IsNullOrWhiteSpace(stem)) return null;
            var exact = Path.Combine(artDir, stem);
            if (File.Exists(exact)) return exact;
            foreach (var ext in new[] { ".webp", ".png", ".jpg", ".jpeg" })
            {
                var cand = Path.Combine(artDir, stem + ext);
                if (File.Exists(cand)) return cand;
            }

            return null;
        }

        string? ResolveVanilla(params string?[] candidates)
        {
            foreach (var c in candidates)
            {
                if (!string.IsNullOrWhiteSpace(c) && vanillaEntries.TryGetValue(c, out var exact)) return exact;
            }

            return null;
        }

        var cardBack = ResolveVanilla("assets/card-back.jpg");

        StudioImageRef ResolveCardImage(int cardId)
        {
            var idStr = cardId.ToString(CultureInfo.InvariantCulture);
            var local = FindLocalArt(idStr);
            if (local is not null) return new StudioImageRef(local, null, null);
            var v = ResolveVanilla($"assets/cards-thumb/{idStr}.jpg", $"assets/cards/{idStr}.jpg", cardBack);
            return new StudioImageRef(null, null, v);
        }

        StudioImageRef ResolveCharacterImage(string sprite, int? index)
        {
            var local = FindLocalArt(sprite) ?? FindLocalArt($"characters/{sprite}");
            if (local is not null) return new StudioImageRef(local, null, null);
            var v = ResolveVanilla(
                string.IsNullOrWhiteSpace(sprite) ? null : $"assets/{sprite}",
                string.IsNullOrWhiteSpace(sprite) ? null : $"assets/opponents/{sprite}",
                index is >= 0 ? $"assets/character-backgrounds/{index.Value}.jpg" : null,
                "assets/dm01.png");
            return new StudioImageRef(null, null, v);
        }

        StudioImageRef ResolveRelicImage(string key, string art)
        {
            if (art.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                return new StudioImageRef(null, art, null);
            var local = FindLocalArt(art) ?? FindLocalArt($"relic_{key}") ?? FindLocalArt(key);
            if (local is not null) return new StudioImageRef(local, null, null);
            var v = ResolveVanilla(
                string.IsNullOrWhiteSpace(art) ? null : $"assets/relics/{art}",
                string.IsNullOrWhiteSpace(art) ? null : $"assets/relics/{art}.jpg",
                string.IsNullOrWhiteSpace(art) ? null : $"assets/relics/{art}.png",
                string.IsNullOrWhiteSpace(art) ? null : $"assets/icons/{art}",
                "assets/relics/Merchant Urn.png",
                cardBack);
            return new StudioImageRef(null, null, v);
        }

        string? FindExplicitModFile(string? relOrAbs)
        {
            if (string.IsNullOrWhiteSpace(relOrAbs) || relOrAbs.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return null;
            var norm = relOrAbs.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(norm) && File.Exists(norm)) return norm;
            var p1 = Path.Combine(modDir, norm);
            if (File.Exists(p1)) return p1;
            var p2 = Path.Combine(modDir, "art", norm);
            if (File.Exists(p2)) return p2;
            return null;
        }

        StudioImageRef ResolvePackImage(string packId, string code, IEnumerable<int> cardIds, string? explicitCover = null)
        {
            var local = FindExplicitModFile(explicitCover)
                ?? FindLocalArt($"packs/{packId}")
                ?? FindLocalArt($"packs/{ModRepository.SanitizeId(packId)}")
                ?? FindLocalArt(packId)
                ?? FindLocalArt(code);
            if (local is not null) return new StudioImageRef(local, null, null);
            foreach (var cid in cardIds)
            {
                if (cid <= 0) continue;
                var localCard = FindLocalArt(cid.ToString(CultureInfo.InvariantCulture));
                if (localCard is not null) return new StudioImageRef(localCard, null, null);
            }
            var firstId = cardIds.FirstOrDefault();
            var v = ResolveVanilla(
                $"assets/packs/{packId}.jpg",
                $"assets/packs/{packId}.png",
                string.IsNullOrWhiteSpace(code) ? null : $"assets/packs/EN-{code}.jpg",
                firstId > 0 ? $"assets/cards/{firstId}.jpg" : null,
                cardBack);
            return new StudioImageRef(null, null, v);
        }

        StudioImageRef ResolveDeckImage(string deckId, string art, IEnumerable<int> cardIds, string? explicitCover = null)
        {
            var local = FindExplicitModFile(explicitCover)
                ?? FindLocalArt($"decks/{art}")
                ?? FindLocalArt($"decks/{deckId}")
                ?? FindLocalArt($"decks/{ModRepository.SanitizeId(deckId)}")
                ?? FindLocalArt(deckId)
                ?? FindLocalArt(art);
            if (local is not null) return new StudioImageRef(local, null, null);
            var firstId = cardIds.FirstOrDefault();
            var localCard = firstId > 0 ? FindLocalArt(firstId.ToString(CultureInfo.InvariantCulture)) : null;
            if (localCard is not null) return new StudioImageRef(localCard, null, null);
            var v = ResolveVanilla(
                string.IsNullOrWhiteSpace(art) ? null : $"assets/decks/{art}.jpg",
                string.IsNullOrWhiteSpace(art) ? null : $"assets/decks/EN-{art}.jpg",
                $"assets/decks/{deckId}.jpg",
                firstId > 0 ? $"assets/cards/{firstId}.jpg" : null,
                cardBack);
            return new StudioImageRef(null, null, v);
        }

        // Load Lua card script lookup (modular lua/cards + scripts + DSL + legacy data/mod.json)
        var luaByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in ModRepository.EnumerateLuaCardScriptFiles(modDir))
        {
            try { luaByFile[Path.GetFileName(f)] = File.ReadAllText(f, Encoding.UTF8); } catch { }
        }
        if (dsl is not null)
        {
            foreach (var (k, v) in dsl.InlineLuaCardScripts) luaByFile[k] = v;
        }

        // Load optional legacy data/mod.json
        JsonObject? legacyMod = null;
        var legacyPath = Path.Combine(modDir, "data", "mod.json");
        if (File.Exists(legacyPath))
        {
            try
            {
                legacyMod = ModPatcher.ParseJson(File.ReadAllText(legacyPath, Encoding.UTF8)).AsObject();
                if (legacyMod["scripts"] is JsonObject legScripts)
                {
                    foreach (var (k, v) in legScripts)
                    {
                        var s = TryGetStr(v);
                        if (!string.IsNullOrEmpty(s)) luaByFile.TryAdd(k, s);
                    }
                }
            }
            catch
            {
                legacyMod = null;
            }
        }

        // 1. Cards
        var cards = new List<StudioCardRecord>();
        var seenCardIds = new HashSet<int>();

        void AddCardObject(JsonObject obj, JsonObject? legacyZh, string sourceTag, string? sourcePath)
        {
            var id = TryGetInt(obj["id"]);
            if (id <= 0 || !seenCardIds.Add(id)) return;
            var idStr = id.ToString(CultureInfo.InvariantCulture);
            var zhName = TryGetStr(obj["zhName"]);
            var zhDesc = TryGetStr(obj["zhDesc"]);
            if (string.IsNullOrEmpty(zhName) && legacyZh?[idStr] is JsonArray zArr)
            {
                if (zArr.Count > 0) zhName = TryGetStr(zArr[0]);
                if (zArr.Count > 1) zhDesc = TryGetStr(zArr[1]);
            }

            var data = obj["data"] as JsonObject;
            luaByFile.TryGetValue($"c{id}.lua", out var luaCode);

            cards.Add(new StudioCardRecord(
                id,
                TryGetStr(obj["name"], $"Card {id}"),
                zhName,
                TryGetStr(obj["desc"]),
                zhDesc,
                TryGetStr(obj["type"], "Spell Card"),
                TryGetStr(obj["race"], "Normal"),
                TryGetStr(obj["attribute"]),
                TryGetInt(obj["level"]),
                TryGetInt(obj["atk"]),
                TryGetInt(obj["defense"]),
                TryGetInt(data?["type"], 2),
                TryGetStr(data?["race"], "0"),
                TryGetInt(data?["attribute"]),
                TryGetStr(obj["packId"]),
                luaCode ?? string.Empty,
                sourceTag,
                sourcePath,
                ResolveCardImage(id)));
        }

        foreach (var file in ModRepository.EnumerateDeclarativeCardFiles(modDir))
        {
            foreach (var obj in ReadJsonObjects(file)) AddCardObject(obj, null, Path.GetFileName(file), file);
        }
        if (dsl is not null)
        {
            foreach (var obj in dsl.InlineCards) AddCardObject(obj, null, "mod.lua", Path.Combine(modDir, "mod.lua"));
        }
        if (legacyMod?["cards"] is JsonArray legCards)
        {
            var legZh = legacyMod["zh"] as JsonObject;
            foreach (var obj in legCards.OfType<JsonObject>()) AddCardObject(obj, legZh, "data/mod.json", legacyPath);
        }

        // 2. Characters
        var characters = new List<StudioCharacterRecord>();
        void AddCharacterObject(JsonObject obj, string sourceTag, string? sourcePath)
        {
            var name = TryGetStr(obj["name"]);
            if (string.IsNullOrWhiteSpace(name)) return;
            int? idx = obj["index"] is JsonValue iv && iv.TryGetValue<int>(out var n) ? n : null;
            var sprite = TryGetStr(obj["sprite"], "dm01.png");
            var deckCards = (obj["cards"] as JsonArray ?? []).Select(x => TryGetStr(x)).Where(s => s.Length > 0).ToList();
            var startPacks = (obj["starting_packs"] as JsonArray ?? []).Select(x => TryGetStr(x)).Where(s => s.Length > 0).ToList();
            var unlocked = obj["unlockedByDefault"] is JsonValue bv && bv.TryGetValue<bool>(out var b) ? b : true;

            characters.Add(new StudioCharacterRecord(
                ModRepository.SanitizeId(name),
                idx,
                name,
                TryGetStr(obj["zhName"]),
                sprite,
                TryGetStr(obj["race"], "Spellcaster"),
                TryGetStr(obj["attribute"], "DARK"),
                TryGetStr(obj["color"], "#38bdf8"),
                TryGetStr(obj["starting_relic"], "mage"),
                startPacks,
                deckCards,
                unlocked,
                sourceTag,
                sourcePath,
                ResolveCharacterImage(sprite, idx)));
        }

        foreach (var file in ModRepository.EnumerateDeclarativeCharacterFiles(modDir))
        {
            foreach (var obj in ReadJsonObjects(file)) AddCharacterObject(obj, Path.GetFileName(file), file);
        }
        if (dsl is not null)
        {
            foreach (var obj in dsl.InlineCharacters) AddCharacterObject(obj, "mod.lua", Path.Combine(modDir, "mod.lua"));
        }
        if (legacyMod?["decks"] is JsonObject legDecks)
        {
            foreach (var (k, v) in legDecks)
            {
                if (v is not JsonArray arr) continue;
                var idxParsed = int.TryParse(k, out var ci) ? ci : -1;
                var cardNames = arr.Select(x => TryGetStr(x)).Where(s => s.Length > 0).ToList();
                var sprite = idxParsed >= 0 ? $"dm{(idxParsed + 1):D2}.png" : "dm01.png";
                characters.Add(new StudioCharacterRecord(
                    $"char-{k}",
                    idxParsed >= 0 ? idxParsed : null,
                    $"Character #{k} Deck",
                    $"角色 #{k} 起始卡组",
                    sprite,
                    "Spellcaster",
                    "DARK",
                    "#e5c58a",
                    "mage",
                    [],
                    cardNames,
                    true,
                    "data/mod.json",
                    legacyPath,
                    ResolveCharacterImage(sprite, idxParsed >= 0 ? idxParsed : null)));
            }
        }

        // 3. Relics
        var relics = new List<StudioRelicRecord>();
        void AddRelicObject(JsonObject obj, string sourceTag, string? sourcePath)
        {
            var key = TryGetStr(obj["key"]);
            if (string.IsNullOrWhiteSpace(key)) return;
            var art = TryGetStr(obj["art"], "Merchant Urn.png");
            relics.Add(new StudioRelicRecord(
                key,
                TryGetStr(obj["name"], key),
                TryGetStr(obj["zhName"]),
                TryGetInt(obj["cost"], 80),
                TryGetStr(obj["tag"], "utility"),
                TryGetStr(obj["description"]),
                TryGetStr(obj["zhDescription"]),
                art,
                TryGetStr(obj["luaEffect"]),
                sourceTag,
                sourcePath,
                ResolveRelicImage(key, art)));
        }

        foreach (var file in ModRepository.EnumerateDeclarativeRelicFiles(modDir))
        {
            foreach (var obj in ReadJsonObjects(file)) AddRelicObject(obj, Path.GetFileName(file), file);
        }
        if (dsl is not null)
        {
            foreach (var obj in dsl.InlineRelics) AddRelicObject(obj, "mod.lua", Path.Combine(modDir, "mod.lua"));
        }
        if (legacyMod?["relic"] is JsonObject legRelic)
        {
            var key = TryGetStr(legRelic["key"]);
            if (!string.IsNullOrWhiteSpace(key))
            {
                var entry = legRelic["entry"] as JsonArray;
                var info = legRelic["info"] as JsonObject;
                var art = TryGetStr(info?["art"], "Merchant Urn.png");
                var name = entry is { Count: > 0 } ? TryGetStr(entry[0], key) : key;
                var cost = entry is { Count: > 1 } ? TryGetInt(entry[1], 80) : 80;
                var desc = entry is { Count: > 2 } ? TryGetStr(entry[2]) : string.Empty;
                relics.Add(new StudioRelicRecord(
                    key, name, string.Empty, cost, TryGetStr(info?["tag"], "utility"),
                    desc, string.Empty, art, string.Empty, "data/mod.json", legacyPath, ResolveRelicImage(key, art)));
            }
        }

        // 4. Packs
        var packs = new List<StudioPackRecord>();
        void AddPackObject(JsonObject obj, string sourceTag, string? sourcePath)
        {
            var id = TryGetStr(obj["id"]);
            if (string.IsNullOrWhiteSpace(id)) return;
            var code = TryGetStr(obj["code"], id);
            var common = ReadIntList(obj["common"]);
            var rare = ReadIntList(obj["rare"]);
            var super = ReadIntList(obj["super"]);
            var ultra = ReadIntList(obj["ultra"]);
            var allIds = ultra.Concat(super).Concat(rare).Concat(common);

            packs.Add(new StudioPackRecord(
                id,
                TryGetStr(obj["name"], id),
                TryGetStr(obj["zhName"]),
                code,
                TryGetInt(obj["size"], 9),
                common,
                rare,
                super,
                ultra,
                sourceTag,
                sourcePath,
                ResolvePackImage(id, code, allIds, TryGetStr(obj["cover"], TryGetStr(obj["art"])))));
        }

        foreach (var file in ModRepository.EnumerateDeclarativePackFiles(modDir))
        {
            foreach (var obj in ReadJsonObjects(file)) AddPackObject(obj, Path.GetFileName(file), file);
        }
        if (dsl is not null)
        {
            foreach (var obj in dsl.InlinePacks) AddPackObject(obj, "mod.lua", Path.Combine(modDir, "mod.lua"));
        }

        // 5. Preconstructed Decks & .ydk
        var decks = new List<StudioDeckRecord>();
        void AddDeckObject(JsonObject obj, string sourceTag, string? sourcePath)
        {
            var id = TryGetStr(obj["id"]);
            if (string.IsNullOrWhiteSpace(id)) return;
            var art = TryGetStr(obj["art"], "SDK");
            var cardIds = ReadIntList(obj["cards"]);
            decks.Add(new StudioDeckRecord(
                id,
                TryGetStr(obj["name"], id),
                TryGetStr(obj["zhName"]),
                art,
                cardIds,
                false,
                sourceTag,
                sourcePath,
                ResolveDeckImage(id, art, cardIds, TryGetStr(obj["cover"], TryGetStr(obj["artPath"])))));
        }

        foreach (var file in ModRepository.EnumerateDeclarativePreconstructedFiles(modDir))
        {
            foreach (var obj in ReadJsonObjects(file)) AddDeckObject(obj, Path.GetFileName(file), file);
        }
        if (dsl is not null)
        {
            foreach (var obj in dsl.InlinePreconstructedDecks) AddDeckObject(obj, "mod.lua", Path.Combine(modDir, "mod.lua"));
        }
        foreach (var ydkFile in ModRepository.EnumerateYdkFiles(modDir))
        {
            var ids = ParseYdkCardIds(ydkFile);
            var id = Path.GetFileNameWithoutExtension(ydkFile);
            decks.Add(new StudioDeckRecord(
                id, id, string.Empty, "SDK", ids, true, Path.GetFileName(ydkFile), ydkFile, ResolveDeckImage(id, "SDK", ids)));
        }

        // 6. Trainer
        var trainer = LoadTrainer(modDir, dsl);

        return new ModStudioSnapshot
        {
            ModId = modId,
            ModDir = modDir,
            Cards = cards,
            Characters = characters,
            Relics = relics,
            Packs = packs,
            Decks = decks,
            Trainer = trainer,
            TextFiles = ModStudioService.ListModTextFiles(modDir)
        };
    }

    public static byte[]? LoadImageBytes(StudioImageRef img, string? vanillaZipPath)
    {
        if (!string.IsNullOrWhiteSpace(img.LocalFilePath) && File.Exists(img.LocalFilePath))
        {
            try { return File.ReadAllBytes(img.LocalFilePath); } catch { }
        }

        if (!string.IsNullOrWhiteSpace(img.DataUri))
        {
            var comma = img.DataUri.IndexOf(',');
            if (comma > 0)
            {
                try { return Convert.FromBase64String(img.DataUri[(comma + 1)..]); } catch { }
            }
        }

        if (!string.IsNullOrWhiteSpace(img.VanillaZipEntry) && !string.IsNullOrWhiteSpace(vanillaZipPath))
        {
            return VanillaContentCatalog.ReadZipEntryBytes(vanillaZipPath, img.VanillaZipEntry);
        }

        return null;
    }

    public static bool DeleteCardFromModularJson(string modDir, int cardId)
    {
        var deleted = false;
        foreach (var file in ModRepository.EnumerateDeclarativeCardFiles(modDir))
        {
            try
            {
                var node = ModPatcher.ParseJson(File.ReadAllText(file, Encoding.UTF8));
                if (node is JsonArray arr)
                {
                    for (var i = arr.Count - 1; i >= 0; i--)
                    {
                        if (arr[i] is JsonObject o && TryGetInt(o["id"]) == cardId)
                        {
                            arr.RemoveAt(i);
                            deleted = true;
                        }
                    }

                    if (deleted) File.WriteAllText(file, arr.ToJsonString(ModPatcher.JsonOptions), ModPatcher.Utf8NoBom);
                }
                else if (node is JsonObject single && TryGetInt(single["id"]) == cardId)
                {
                    File.Delete(file);
                    deleted = true;
                }
            }
            catch { }
        }

        var luaPath = Path.Combine(modDir, "lua", "cards", $"c{cardId}.lua");
        if (File.Exists(luaPath)) { try { File.Delete(luaPath); } catch { } }
        return deleted;
    }

    public static bool DeleteModularSourceFile(string modDir, string? sourceFilePath)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath)) return false;
        var fullMod = Path.GetFullPath(modDir);
        var fullTarget = Path.GetFullPath(sourceFilePath);
        if (!fullTarget.StartsWith(fullMod, StringComparison.OrdinalIgnoreCase)) return false;
        var fileName = Path.GetFileName(fullTarget);
        if (fileName is "mod.lua" or "mod.js" or "mod.json" or "mod-manifest.json") return false;
        File.Delete(fullTarget);
        return true;
    }

    private static StudioTrainerRecord LoadTrainer(string modDir, ParsedModDsl? dsl)
    {
        JsonObject? obj = null;
        var path = Path.Combine(modDir, "trainer.json");
        if (File.Exists(path))
        {
            try { obj = ModPatcher.ParseJson(File.ReadAllText(path, Encoding.UTF8)).AsObject(); } catch { }
        }
        obj ??= dsl?.InlineTrainer;

        if (obj is null)
        {
            return new StudioTrainerRecord(false, true, true, 9999, null, 12000, false, 0.5, 2.0, ["mage"], true);
        }

        return new StudioTrainerRecord(
            true,
            TryGetBool(obj["unlockAllCharacters"]),
            TryGetBool(obj["unlockAllVariants"]),
            TryGetNullableInt(obj["startingGold"]),
            TryGetNullableInt(obj["startingGoldBonus"]),
            TryGetNullableInt(obj["startingLp"]),
            TryGetBool(obj["freeShop"]),
            TryGetNullableDouble(obj["shopPriceMultiplier"]),
            TryGetNullableDouble(obj["rewardGoldMultiplier"]),
            (obj["startingRelics"] as JsonArray ?? []).Select(x => TryGetStr(x)).Where(s => s.Length > 0).ToList(),
            TryGetBool(obj["inGameOverlay"], true));
    }

    private static Dictionary<string, string> BuildVanillaEntryLookup(string? vanillaZipPath)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(vanillaZipPath) || !File.Exists(vanillaZipPath)) return map;
        try
        {
            using var zip = ZipFile.OpenRead(vanillaZipPath);
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.EndsWith('/')) map.TryAdd(e.FullName, e.FullName);
            }
        }
        catch { }
        return map;
    }

    private static IEnumerable<JsonObject> ReadJsonObjects(string file)
    {
        try
        {
            var parsed = ModPatcher.ParseJson(File.ReadAllText(file, Encoding.UTF8));
            if (parsed is JsonArray arr) return arr.OfType<JsonObject>().ToList();
            if (parsed is JsonObject o) return [o];
        }
        catch { }
        return [];
    }

    private static List<int> ParseYdkCardIds(string file)
    {
        var list = new List<int>();
        try
        {
            foreach (var raw in File.ReadAllLines(file, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!')) continue;
                if (int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
                    list.Add(id);
            }
        }
        catch { }
        return list;
    }

    private static string TryGetStr(JsonNode? node, string fallback = "")
    {
        if (node is null) return fallback;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s ?? fallback;
            var raw = v.ToString();
            return string.IsNullOrWhiteSpace(raw) ? fallback : raw;
        }
        return fallback;
    }

    private static int TryGetInt(JsonNode? node, int fallback = 0)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<long>(out var l)) return (int)l;
            if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed)) return parsed;
        }
        return fallback;
    }

    private static int? TryGetNullableInt(JsonNode? node)
    {
        if (node is null) return null;
        var val = TryGetInt(node, -1);
        return val >= 0 ? val : null;
    }

    private static double? TryGetNullableDouble(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        }
        return null;
    }

    private static bool TryGetBool(JsonNode? node, bool fallback = false) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    private static List<int> ReadIntList(JsonNode? node)
    {
        var list = new List<int>();
        if (node is not JsonArray arr) return list;
        foreach (var x in arr)
        {
            var id = TryGetInt(x);
            if (id > 0) list.Add(id);
        }
        return list;
    }
}
