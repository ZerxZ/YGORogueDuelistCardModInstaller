using System.IO.Compression;
using System.Text.Json.Nodes;

namespace YGORogueDuelistCardModInstaller.Core.ModManager;

public enum VanillaCategory
{
    Cards,
    Characters,
    Relics,
    Packs,
    PreconstructedDecks
}

public sealed record VanillaCardItem(
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
    string? ImageZipPath,
    string? ThumbZipPath);

public sealed record VanillaCharacterItem(
    int Index,
    string Name,
    string ZhName,
    bool IsPlayable,
    string Sprite,
    string Race,
    string Attribute,
    string Color,
    string Theme,
    string StartingRelic,
    IReadOnlyList<string> StartingPacks,
    IReadOnlyList<string> Cards,
    string? ImageZipPath,
    string? BackgroundZipPath);

public sealed record VanillaRelicItem(
    string Key,
    string Name,
    string ZhName,
    int Cost,
    string Description,
    string ZhDescription,
    string Art,
    string Tag,
    string Effect,
    string? ImageZipPath);

public sealed record VanillaPackItem(
    string Id,
    string Name,
    string ZhName,
    string Region,
    string Code,
    string Date,
    int Size,
    IReadOnlyList<int> CommonIds,
    IReadOnlyList<int> RareIds,
    IReadOnlyList<int> SuperIds,
    IReadOnlyList<int> UltraIds,
    string? ImageZipPath)
{
    public int TotalCardCount => CommonIds.Count + RareIds.Count + SuperIds.Count + UltraIds.Count;
}

public sealed record VanillaPreconstructedDeckItem(
    string Id,
    string Name,
    string ZhName,
    string Date,
    string Art,
    IReadOnlyList<int> CardIds,
    string? ImageZipPath);

public sealed class VanillaCatalogSnapshot
{
    public required string ZipPath { get; init; }
    public required string Version { get; init; }
    public required IReadOnlyList<VanillaCardItem> Cards { get; init; }
    public required IReadOnlyList<VanillaCharacterItem> Characters { get; init; }
    public required IReadOnlyList<VanillaRelicItem> Relics { get; init; }
    public required IReadOnlyList<VanillaPackItem> Packs { get; init; }
    public required IReadOnlyList<VanillaPreconstructedDeckItem> PreconstructedDecks { get; init; }
    public required IReadOnlyDictionary<int, VanillaCardItem> CardById { get; init; }
    public required IReadOnlyDictionary<string, VanillaCardItem> CardByName { get; init; }
}

/// <summary>
/// Read-only offline catalog loader for vanilla Yu-Gi-Oh! Rogue Duelist web-X.Y.Z.zip assets
/// (cards, characters, relics, booster packs, preconstructed decks, Lua scripts, and artwork images).
/// 100% Native AOT compatible.
/// </summary>
public static class VanillaContentCatalog
{
    public static VanillaCatalogSnapshot Load(string vanillaZipPath, string version)
    {
        if (!File.Exists(vanillaZipPath))
        {
            throw new ModOperationException($"找不到原版数据包：{vanillaZipPath}");
        }

        using var zip = ZipFile.OpenRead(vanillaZipPath);
        var entryLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries)
        {
            if (!e.FullName.EndsWith('/'))
            {
                entryLookup.TryAdd(e.FullName, e.FullName);
            }
        }

        string? ResolveZipPath(params string?[] candidates)
        {
            foreach (var cand in candidates)
            {
                if (!string.IsNullOrWhiteSpace(cand) && entryLookup.TryGetValue(cand, out var exact))
                {
                    return exact;
                }
            }

            return null;
        }

        var content = ModPatcher.ParseJson(ModPatcher.ReadZipText(zip, "content.json")).AsObject();
        JsonObject? zhCards = null;
        JsonObject? zhUi = null;
        if (entryLookup.ContainsKey("i18n/zh.json"))
        {
            try
            {
                var zh = ModPatcher.ParseJson(ModPatcher.ReadZipText(zip, "i18n/zh.json")).AsObject();
                zhCards = zh["cards"] as JsonObject;
                zhUi = zh["ui"] as JsonObject;
            }
            catch
            {
                // Ignore malformed optional i18n
            }
        }

        string LookupZhUi(string key) =>
            zhUi is not null && zhUi[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;

        // 1. Cards
        var cards = new List<VanillaCardItem>();
        var cardById = new Dictionary<int, VanillaCardItem>();
        var cardByName = new Dictionary<string, VanillaCardItem>(StringComparer.OrdinalIgnoreCase);
        var cardBack = ResolveZipPath("assets/card-back.jpg");

        foreach (var c in content["cards"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var id = TryGetInt(c["id"]);
            if (id <= 0) continue;
            var idStr = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var name = TryGetStr(c["name"], idStr);
            var desc = TryGetStr(c["desc"]);

            var zhName = string.Empty;
            var zhDesc = string.Empty;
            if (zhCards?[idStr] is JsonArray zArr)
            {
                if (zArr.Count > 0) zhName = TryGetStr(zArr[0]);
                if (zArr.Count > 1) zhDesc = TryGetStr(zArr[1]);
            }

            var data = c["data"] as JsonObject;
            var typeMask = TryGetInt(data?["type"]);
            var raceMask = TryGetStr(data?["race"], "0");
            var attrMask = TryGetInt(data?["attribute"]);

            var fullImg = ResolveZipPath($"assets/cards/{idStr}.jpg", $"assets/cards/{idStr}.webp", $"assets/cards/{idStr}.png", $"assets/cards-thumb/{idStr}.jpg", cardBack);
            var thumbImg = ResolveZipPath($"assets/cards-thumb/{idStr}.jpg", $"assets/cards/{idStr}.jpg", cardBack);

            var item = new VanillaCardItem(
                id,
                name,
                zhName,
                desc,
                zhDesc,
                TryGetStr(c["type"]),
                TryGetStr(c["race"]),
                TryGetStr(c["attribute"]),
                TryGetInt(c["level"]),
                TryGetInt(c["atk"]),
                TryGetInt(c["defense"]),
                typeMask,
                raceMask,
                attrMask,
                fullImg,
                thumbImg);

            cards.Add(item);
            cardById[id] = item;
            cardByName.TryAdd(name, item);
        }

        // 2. Characters
        var playableSet = (content["playable"]?.AsArray() ?? [])
            .Select(n => TryGetInt(n))
            .ToHashSet();
        var characters = new List<VanillaCharacterItem>();
        var rawChars = content["characters"]?.AsArray() ?? [];
        for (var i = 0; i < rawChars.Count; i++)
        {
            if (rawChars[i] is not JsonObject ch) continue;
            var name = TryGetStr(ch["name"], $"Character {i}");
            var sprite = TryGetStr(ch["sprite"]);
            var imgPath = ResolveZipPath(
                string.IsNullOrWhiteSpace(sprite) ? null : $"assets/{sprite}",
                string.IsNullOrWhiteSpace(sprite) ? null : $"assets/opponents/{sprite}",
                $"assets/character-backgrounds/{i}.jpg");
            var bgPath = ResolveZipPath($"assets/character-backgrounds/{i}.jpg");

            var cardNames = (ch["cards"] as JsonArray ?? [])
                .Select(n => TryGetStr(n))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            var startPacks = (ch["starting_packs"] as JsonArray ?? [])
                .Select(n => TryGetStr(n))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            characters.Add(new VanillaCharacterItem(
                i,
                name,
                LookupZhUi(name),
                playableSet.Contains(i),
                sprite,
                TryGetStr(ch["race"]),
                TryGetStr(ch["attribute"]),
                TryGetStr(ch["color"], "#e5c58a"),
                TryGetStr(ch["theme"]),
                TryGetStr(ch["starting_relic"]),
                startPacks,
                cardNames,
                imgPath,
                bgPath));
        }

        // 3. Relics
        var relics = new List<VanillaRelicItem>();
        var artifacts = content["artifacts"] as JsonObject ?? new JsonObject();
        var artifactInfo = content["artifactInfo"] as JsonObject ?? new JsonObject();
        foreach (var (key, val) in artifacts)
        {
            var arr = val as JsonArray;
            var name = arr is { Count: > 0 } ? TryGetStr(arr[0], key) : key;
            var cost = arr is { Count: > 1 } ? TryGetInt(arr[1]) : 0;
            var desc = arr is { Count: > 2 } ? TryGetStr(arr[2]) : string.Empty;
            var info = artifactInfo[key] as JsonObject;
            var art = TryGetStr(info?["art"]);
            var tag = TryGetStr(info?["tag"]);
            var effect = TryGetStr(info?["effect"]);

            var imgPath = ResolveZipPath(
                string.IsNullOrWhiteSpace(art) ? null : $"assets/relics/{art}",
                string.IsNullOrWhiteSpace(art) ? null : $"assets/relics/{art}.jpg",
                string.IsNullOrWhiteSpace(art) ? null : $"assets/relics/{art}.png",
                string.IsNullOrWhiteSpace(art) ? null : $"assets/icons/{art}",
                $"assets/relics/{key}.jpg",
                $"assets/relics/{key}.png",
                cardBack);

            relics.Add(new VanillaRelicItem(
                key,
                name,
                LookupZhUi(name),
                cost,
                desc,
                LookupZhUi(desc),
                art,
                tag,
                effect,
                imgPath));
        }

        // 4. Booster Packs
        var packs = new List<VanillaPackItem>();
        foreach (var p in content["packs"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var id = TryGetStr(p["id"]);
            if (string.IsNullOrWhiteSpace(id)) continue;
            var name = TryGetStr(p["name"], id);
            var code = TryGetStr(p["code"]);
            var common = ReadIntList(p["common"]);
            var rare = ReadIntList(p["rare"]);
            var super = ReadIntList(p["super"]);
            var ultra = ReadIntList(p["ultra"]);

            // Some packs store all cards in common + rarities map; extract fallback preview card if pack image missing
            var firstCardId = ultra.FirstOrDefault() != 0 ? ultra[0] :
                              super.FirstOrDefault() != 0 ? super[0] :
                              rare.FirstOrDefault() != 0 ? rare[0] :
                              common.FirstOrDefault();

            var imgPath = ResolveZipPath(
                $"assets/packs/{id}.jpg",
                $"assets/packs/{id}.png",
                $"assets/packs/{id}.webp",
                string.IsNullOrWhiteSpace(code) ? null : $"assets/packs/EN-{code}.jpg",
                firstCardId > 0 ? $"assets/cards/{firstCardId}.jpg" : null,
                cardBack);

            packs.Add(new VanillaPackItem(
                id,
                name,
                LookupZhUi(name),
                TryGetStr(p["region"], "EN"),
                code,
                TryGetStr(p["date"]),
                TryGetInt(p["size"], 9),
                common,
                rare,
                super,
                ultra,
                imgPath));
        }

        // 5. Preconstructed Decks
        var decks = new List<VanillaPreconstructedDeckItem>();
        foreach (var d in content["preconstructedDecks"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var id = TryGetStr(d["id"]);
            if (string.IsNullOrWhiteSpace(id)) continue;
            var name = TryGetStr(d["name"], id);
            var art = TryGetStr(d["art"], id);
            var cardIds = ReadIntList(d["cards"]);
            var firstCardId = cardIds.FirstOrDefault();

            var imgPath = ResolveZipPath(
                $"assets/decks/{art}.jpg",
                $"assets/decks/{art}.png",
                $"assets/decks/EN-{art}.jpg",
                $"assets/decks/JP-{art}.jpg",
                $"assets/decks/{id}.jpg",
                $"assets/decks/EN-{id}.jpg",
                firstCardId > 0 ? $"assets/cards/{firstCardId}.jpg" : null,
                cardBack);

            decks.Add(new VanillaPreconstructedDeckItem(
                id,
                name,
                LookupZhUi(name),
                TryGetStr(d["date"]),
                art,
                cardIds,
                imgPath));
        }

        return new VanillaCatalogSnapshot
        {
            ZipPath = vanillaZipPath,
            Version = version,
            Cards = cards,
            Characters = characters,
            Relics = relics,
            Packs = packs,
            PreconstructedDecks = decks,
            CardById = cardById,
            CardByName = cardByName
        };
    }

    public static byte[]? ReadZipEntryBytes(string zipPath, string? entryPath)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || string.IsNullOrWhiteSpace(entryPath) || !File.Exists(zipPath))
        {
            return null;
        }

        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.GetEntry(entryPath);
            if (entry is null) return null;
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    public static string? ExportZipImageToModArt(string zipPath, string? entryPath, string modDir, string destFileNameWithoutExt)
    {
        var bytes = ReadZipEntryBytes(zipPath, entryPath);
        if (bytes is null || bytes.Length == 0 || string.IsNullOrWhiteSpace(entryPath)) return null;
        var ext = Path.GetExtension(entryPath).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp")) ext = ".jpg";
        var artDir = Path.Combine(modDir, "art");
        Directory.CreateDirectory(artDir);
        var dest = Path.Combine(artDir, destFileNameWithoutExt + ext);
        File.WriteAllBytes(dest, bytes);
        return dest;
    }

    public static string? ReadCardLuaScript(string zipPath, int cardId)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath)) return null;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var scripts = ModPatcher.ParseJson(ModPatcher.ReadZipText(zip, "scripts-0.json")).AsObject();
            return scripts[$"c{cardId}.lua"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
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

    private static List<int> ReadIntList(JsonNode? node)
    {
        var list = new List<int>();
        if (node is not JsonArray arr) return list;
        foreach (var item in arr)
        {
            var id = TryGetInt(item);
            if (id > 0) list.Add(id);
        }

        return list;
    }
}
