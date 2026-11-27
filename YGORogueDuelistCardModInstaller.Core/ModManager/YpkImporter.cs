using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace YGORogueDuelistCardModInstaller.Core.ModManager;

/// <summary>
/// Pure-C#, zero-dependency, 100% Native AOT compatible YGOPro / EDOPro / MDPro3 `.ypk` importer.
/// Directly parses SQLite3 `.cdb` databases (datas + texts tables, B-tree interior/leaf & overflow pages),
/// extracts `script/c*.lua`, `pics/*`, `.ydk` decks, and generates a complete Mod Spec v1 MOD folder.
/// </summary>
public static partial class YpkImporter
{
    [GeneratedRegex(@"^c(\d+)\.lua$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CardLuaRegex();

    [GeneratedRegex(@"^(\d+)\.(png|webp|jpg|jpeg)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CardArtRegex();

    [GeneratedRegex(@"[\u4e00-\u9fff]", RegexOptions.CultureInvariant)]
    private static partial Regex CjkRegex();

    public sealed record YpkImportResult(
        ManagedMod Mod,
        int CardCount,
        int ScriptCount,
        int LuaLibCount,
        int ArtCount,
        int DeckCount,
        int CdbCount);

    public static bool IsYpkArchive(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath)) return false;
        if (archivePath.EndsWith(".ypk", StringComparison.OrdinalIgnoreCase)) return true;
        if (!archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            var hasModSpec = false;
            var hasCdb = false;
            var hasYgoScript = false;
            foreach (var entry in zip.Entries)
            {
                var norm = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (norm.EndsWith("mod-manifest.json", StringComparison.OrdinalIgnoreCase) ||
                    norm.EndsWith("workspace-pack.json", StringComparison.OrdinalIgnoreCase) ||
                    norm.EndsWith("mod.lua", StringComparison.OrdinalIgnoreCase) ||
                    norm.EndsWith("mod.js", StringComparison.OrdinalIgnoreCase))
                {
                    hasModSpec = true;
                    break;
                }

                if (norm.EndsWith(".cdb", StringComparison.OrdinalIgnoreCase)) hasCdb = true;
                if (norm.StartsWith("script/", StringComparison.OrdinalIgnoreCase) &&
                    norm.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                {
                    hasYgoScript = true;
                }
            }

            return !hasModSpec && (hasCdb || hasYgoScript);
        }
        catch
        {
            return false;
        }
    }

    public static YpkImportResult ImportYpk(
        ModRepository repository,
        string ypkPath,
        bool overwrite = true,
        string? explicitModId = null)
    {
        if (!File.Exists(ypkPath))
        {
            throw new ModOperationException($"找不到要导入的 .ypk 文件：{ypkPath}");
        }

        var baseName = Path.GetFileNameWithoutExtension(ypkPath);
        var slug = ModRepository.SanitizeId(string.IsNullOrWhiteSpace(explicitModId) ? baseName : explicitModId);

        Directory.CreateDirectory(repository.ModsRoot);
        var destDir = Path.Combine(repository.ModsRoot, slug);
        if (Directory.Exists(destDir))
        {
            if (!overwrite)
            {
                throw new ModOperationException($"MOD \"{slug}\" 已存在。如需覆盖请先删除旧版本。");
            }
            Directory.Delete(destDir, recursive: true);
        }

        Directory.CreateDirectory(destDir);

        var cardsById = new Dictionary<int, YgoCardData>();
        var textsById = new Dictionary<int, YgoCardText>();
        var cdbCount = 0;
        var scriptCount = 0;
        var luaLibCount = 0;
        var artCount = 0;
        var deckCount = 0;
        string? coverRel = null;

        var preferredArtExtRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [".webp"] = 3,
            [".png"] = 2,
            [".jpg"] = 1,
            [".jpeg"] = 1
        };
        var writtenArtByCode = new Dictionary<string, (int Rank, bool IsThumb, string Path)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var zip = ZipFile.OpenRead(ypkPath);
            foreach (var entry in zip.Entries)
            {
                if (entry.Length == 0 || entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
                var norm = entry.FullName.Replace('\\', '/').TrimStart('/');
                var fileName = Path.GetFileName(norm);
                if (string.IsNullOrWhiteSpace(fileName)) continue;

                // 1. SQLite3 .cdb databases
                if (fileName.EndsWith(".cdb", StringComparison.OrdinalIgnoreCase))
                {
                    using var ms = new MemoryStream();
                    using (var es = entry.Open()) es.CopyTo(ms);
                    var dbBytes = ms.ToArray();
                    if (TryReadYgoCdb(dbBytes, cardsById, textsById))
                    {
                        cdbCount++;
                    }
                    continue;
                }

                // 2. Card Lua scripts & helper Lua libraries
                if (fileName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                {
                    var cardMatch = CardLuaRegex().Match(fileName);
                    if (cardMatch.Success)
                    {
                        var targetDir = Path.Combine(destDir, "lua", "cards");
                        Directory.CreateDirectory(targetDir);
                        var targetPath = Path.Combine(targetDir, $"c{cardMatch.Groups[1].Value}.lua");
                        ExtractEntryNormalizedUtf8(entry, targetPath);
                        scriptCount++;
                    }
                    else if (!fileName.Equals("constant.lua", StringComparison.OrdinalIgnoreCase) &&
                             !fileName.Equals("utility.lua", StringComparison.OrdinalIgnoreCase) &&
                             !fileName.Equals("archetype_setcode_constants.lua", StringComparison.OrdinalIgnoreCase))
                    {
                        var targetDir = Path.Combine(destDir, "lua", "lib");
                        Directory.CreateDirectory(targetDir);
                        ExtractEntryNormalizedUtf8(entry, Path.Combine(targetDir, fileName));
                        luaLibCount++;
                    }
                    continue;
                }

                // 3. Card artwork (pics/<code>.jpg|png|webp) & explicit cover
                var ext = Path.GetExtension(fileName);
                if (preferredArtExtRank.TryGetValue(ext, out var extRank))
                {
                    if (fileName.StartsWith("cover.", StringComparison.OrdinalIgnoreCase) && coverRel is null)
                    {
                        var covPath = Path.Combine(destDir, "cover" + ext.ToLowerInvariant());
                        using var es = entry.Open();
                        using var fs = File.Create(covPath);
                        es.CopyTo(fs);
                        coverRel = "cover" + ext.ToLowerInvariant();
                        continue;
                    }

                    var artMatch = CardArtRegex().Match(fileName);
                    if (artMatch.Success && !norm.Contains("/field/", StringComparison.OrdinalIgnoreCase))
                    {
                        var code = artMatch.Groups[1].Value;
                        var isThumb = norm.Contains("/thumbnail/", StringComparison.OrdinalIgnoreCase);
                        if (writtenArtByCode.TryGetValue(code, out var prev))
                        {
                            // Keep full-res over thumbnail; or higher quality format
                            if (!prev.IsThumb && isThumb) continue;
                            if (prev.IsThumb == isThumb && prev.Rank >= extRank) continue;
                            if (File.Exists(prev.Path))
                            {
                                try { File.Delete(prev.Path); } catch { }
                            }
                        }
                        else
                        {
                            artCount++;
                        }

                        var artDir = Path.Combine(destDir, "art");
                        Directory.CreateDirectory(artDir);
                        var outPath = Path.Combine(artDir, $"{code}{ext.ToLowerInvariant()}");
                        using (var es = entry.Open())
                        using (var fs = File.Create(outPath))
                        {
                            es.CopyTo(fs);
                        }

                        writtenArtByCode[code] = (extRank, isThumb, outPath);
                    }
                    continue;
                }

                // 4. Bundled .ydk decks
                if (fileName.EndsWith(".ydk", StringComparison.OrdinalIgnoreCase))
                {
                    var decksDir = Path.Combine(destDir, "decks");
                    Directory.CreateDirectory(decksDir);
                    ExtractEntryNormalizedUtf8(entry, Path.Combine(decksDir, fileName));
                    deckCount++;
                }
            }

            if (cardsById.Count == 0 && scriptCount == 0 && artCount == 0 && deckCount == 0)
            {
                throw new ModOperationException($"所选 .ypk 压缩包中未检测到有效的 .cdb 数据库、Lua 卡牌脚本、卡图或 .ydk 卡组：{Path.GetFileName(ypkPath)}");
            }

            // Build declarative cards/ypk-cards.json + optional booster pack
            var importedCardIds = new List<int>();
            if (cardsById.Count > 0)
            {
                var cardsArr = new JsonArray();
                foreach (var kv in cardsById.OrderBy(k => k.Key))
                {
                    var d = kv.Value;
                    textsById.TryGetValue(d.Id, out var txt);
                    var cardObj = BuildDeclarativeCardJson(d, txt);
                    cardsArr.Add((JsonNode?)cardObj);
                    importedCardIds.Add(d.Id);
                }

                var cardsDir = Path.Combine(destDir, "cards");
                Directory.CreateDirectory(cardsDir);
                File.WriteAllText(
                    Path.Combine(cardsDir, "ypk-cards.json"),
                    cardsArr.ToJsonString(ModPatcher.JsonOptions),
                    ModPatcher.Utf8NoBom);

                // Also create a booster pack so imported cards are immediately discoverable in-game
                var packsDir = Path.Combine(destDir, "packs");
                Directory.CreateDirectory(packsDir);
                var packCode = slug.Length >= 3 ? slug[..3].ToUpperInvariant() : "YPK";
                var packId = $"YPK-{slug.ToUpperInvariant()}";
                var packObj = new JsonObject
                {
                    ["id"] = packId,
                    ["name"] = $"{baseName} (YPK Pack)",
                    ["zhName"] = $"{baseName} 扩展卡包",
                    ["region"] = "EN",
                    ["code"] = packCode,
                    ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["image"] = string.Empty,
                    ["size"] = Math.Clamp(importedCardIds.Count, 1, 9),
                    ["common"] = new JsonArray(importedCardIds.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
                    ["rare"] = new JsonArray(),
                    ["super"] = new JsonArray(),
                    ["ultra"] = new JsonArray()
                };
                File.WriteAllText(
                    Path.Combine(packsDir, $"{slug}-pack.json"),
                    packObj.ToJsonString(ModPatcher.JsonOptions),
                    ModPatcher.Utf8NoBom);
            }

            var manifest = new ModManifest
            {
                SchemaVersion = 1,
                Id = slug,
                Name = baseName,
                Version = "1.0.0",
                Author = "YPK Import",
                Description = $"从 YGOPro/MDPro3 扩展包 ({Path.GetFileName(ypkPath)}) 自动导入：{importedCardIds.Count} 张卡牌、{scriptCount} 个 Lua 脚本、{artCount} 张卡图{(deckCount > 0 ? $"、{deckCount} 套卡组" : string.Empty)}。",
                Category = "cards",
                Cover = coverRel,
                AutoDiscoverEntries = true,
                MinManagerVersion = ModManifest.CurrentManagerVersion,
                Tags = ["ypk", "cards", "lua"]
            };
            ModRepository.WriteManifest(destDir, manifest);

            var state = repository.LoadState();
            var existing = state.Mods.FirstOrDefault(m => string.Equals(m.Id, slug, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                var nextOrder = state.Mods.Count > 0 ? state.Mods.Max(m => m.Order) + 1 : 0;
                state.Mods.Add(new ModEntryState { Id = slug, Enabled = true, Order = nextOrder });
                repository.SaveState(state);
            }

            var managed = repository.LoadMods(state)
                .First(m => string.Equals(m.Id, slug, StringComparison.OrdinalIgnoreCase));
            return new YpkImportResult(managed, importedCardIds.Count, scriptCount, luaLibCount, artCount, deckCount, cdbCount);
        }
        catch
        {
            if (Directory.Exists(destDir))
            {
                try { Directory.Delete(destDir, recursive: true); } catch { }
            }
            throw;
        }
    }

    private static void ExtractEntryNormalizedUtf8(ZipArchiveEntry entry, string destPath)
    {
        using var sr = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = sr.ReadToEnd();
        File.WriteAllText(destPath, text, ModPatcher.Utf8NoBom);
    }

    // ==================== YGOPro CARD BITMASK -> MOD SPEC V1 CONVERSION ====================

    private static JsonObject BuildDeclarativeCardJson(YgoCardData d, YgoCardText? txt)
    {
        var rawName = string.IsNullOrWhiteSpace(txt?.Name) ? $"Card #{d.Id}" : txt!.Name.Trim();
        var rawDesc = (txt?.Desc ?? string.Empty).Replace("\r\n", "\n").Trim();
        var isCjk = CjkRegex().IsMatch(rawName) || CjkRegex().IsMatch(rawDesc);

        var isMonster = (d.Type & 0x1) != 0;
        var isSpell = (d.Type & 0x2) != 0;
        var isTrap = (d.Type & 0x4) != 0;
        var isLink = (d.Type & 0x4000000) != 0;

        var baseLevel = (int)(d.Level & 0xFF);
        var lscale = (int)((d.Level >> 24) & 0xFF);
        var rscale = (int)((d.Level >> 16) & 0xFF);

        var atk = isMonster ? d.Atk : 0;
        var defense = isMonster && !isLink ? d.Def : 0;
        var linkMarker = isMonster && isLink ? d.Def : 0;

        var displayType = ResolveDisplayType(d.Type, isMonster, isSpell, isTrap);
        var displayRace = isMonster
            ? ResolveMonsterRaceName(d.Race)
            : ResolveSpellTrapSubtype(d.Type, isSpell);
        var displayAttr = isMonster ? ResolveAttributeName(d.Attribute) : string.Empty;

        var setcodes = new JsonArray();
        var sc = (ulong)d.Setcode;
        for (var i = 0; i < 4; i++)
        {
            var part = (int)((sc >> (i * 16)) & 0xFFFF);
            if (part != 0) setcodes.Add((JsonNode?)JsonValue.Create(part));
        }

        var stringsArr = new JsonArray();
        for (var i = 0; i < 16; i++)
        {
            var s = txt is not null && i < txt.Strings.Length ? (txt.Strings[i] ?? string.Empty) : string.Empty;
            stringsArr.Add((JsonNode?)JsonValue.Create(s));
        }

        var obj = new JsonObject
        {
            ["id"] = d.Id,
            ["name"] = rawName,
            ["desc"] = rawDesc,
            ["type"] = displayType,
            ["race"] = displayRace,
            ["attribute"] = displayAttr,
            ["level"] = isMonster ? baseLevel : 0,
            ["atk"] = atk,
            ["defense"] = defense,
            ["addToEraPool"] = true,
            ["data"] = new JsonObject
            {
                ["code"] = d.Id,
                ["alias"] = d.Alias,
                ["setcodes"] = setcodes,
                ["type"] = d.Type,
                ["level"] = baseLevel,
                ["attribute"] = isMonster ? d.Attribute : 0,
                ["race"] = isMonster ? d.Race.ToString(CultureInfo.InvariantCulture) : "0",
                ["attack"] = atk,
                ["defense"] = defense,
                ["lscale"] = lscale,
                ["rscale"] = rscale,
                ["link_marker"] = linkMarker
            },
            ["strings"] = stringsArr
        };

        if (isCjk)
        {
            obj["zhName"] = rawName;
            obj["zhDesc"] = rawDesc;
        }

        return obj;
    }

    private static string ResolveDisplayType(int t, bool isMonster, bool isSpell, bool isTrap)
    {
        if (isSpell) return "Spell Card";
        if (isTrap) return "Trap Card";
        if (!isMonster) return "Spell Card";

        var hasEffect = (t & 0x20) != 0;
        var hasPendulum = (t & 0x1000000) != 0;
        if ((t & 0x4000000) != 0) return "Link Monster";
        if ((t & 0x800000) != 0) return hasPendulum ? "XYZ Pendulum Effect Monster" : "XYZ Monster";
        if ((t & 0x2000) != 0) return hasPendulum ? "Synchro Pendulum Effect Monster" : "Synchro Monster";
        if ((t & 0x40) != 0) return hasPendulum ? "Pendulum Effect Fusion Monster" : (hasEffect ? "Fusion Effect Monster" : "Fusion Monster");
        if ((t & 0x80) != 0) return hasEffect ? "Ritual Effect Monster" : "Ritual Monster";
        if (hasPendulum) return hasEffect ? "Pendulum Effect Monster" : "Pendulum Normal Monster";
        if ((t & 0x1000) != 0) return hasEffect ? "Tuner Effect Monster" : "Tuner Monster";
        if ((t & 0x200000) != 0) return "Flip Effect Monster";
        if ((t & 0x400000) != 0) return "Toon Monster";
        if ((t & 0x200) != 0) return "Spirit Monster";
        if ((t & 0x400) != 0) return "Union Effect Monster";
        if ((t & 0x800) != 0) return "Gemini Monster";
        if ((t & 0x10) != 0) return "Normal Monster";
        return "Effect Monster";
    }

    private static string ResolveSpellTrapSubtype(int t, bool isSpell)
    {
        if ((t & 0x10000) != 0 && isSpell) return "Quick-Play";
        if ((t & 0x20000) != 0) return "Continuous";
        if ((t & 0x40000) != 0 && isSpell) return "Equip";
        if ((t & 0x80000) != 0 && isSpell) return "Field";
        if ((t & 0x80) != 0 && isSpell) return "Ritual";
        if ((t & 0x100000) != 0 && !isSpell) return "Counter";
        return "Normal";
    }

    private static string ResolveAttributeName(int attr) => attr switch
    {
        1 => "EARTH",
        2 => "WATER",
        4 => "FIRE",
        8 => "WIND",
        16 => "LIGHT",
        32 => "DARK",
        64 => "DIVINE",
        _ => string.Empty
    };

    private static string ResolveMonsterRaceName(long race) => race switch
    {
        0x1 => "Warrior",
        0x2 => "Spellcaster",
        0x4 => "Fairy",
        0x8 => "Fiend",
        0x10 => "Zombie",
        0x20 => "Machine",
        0x40 => "Aqua",
        0x80 => "Pyro",
        0x100 => "Rock",
        0x200 => "Winged Beast",
        0x400 => "Plant",
        0x800 => "Insect",
        0x1000 => "Thunder",
        0x2000 => "Dragon",
        0x4000 => "Beast",
        0x8000 => "Beast-Warrior",
        0x10000 => "Dinosaur",
        0x20000 => "Fish",
        0x40000 => "Sea Serpent",
        0x80000 => "Reptile",
        0x100000 => "Psychic",
        0x200000 => "Divine-Beast",
        0x400000 => "Creator-God",
        0x800000 => "Wyrm",
        0x1000000 => "Cyberse",
        0x2000000 => "Illusion",
        _ => "Warrior"
    };

    // ==================== PURE-C# NATIVE AOT SQLITE3 .CDB READER ====================

    private sealed record YgoCardData(
        int Id,
        int Ot,
        int Alias,
        long Setcode,
        int Type,
        int Atk,
        int Def,
        long Level,
        long Race,
        int Attribute,
        long Category);

    private sealed record YgoCardText(int Id, string Name, string Desc, string[] Strings);

    private static bool TryReadYgoCdb(
        byte[] db,
        Dictionary<int, YgoCardData> cardsById,
        Dictionary<int, YgoCardText> textsById)
    {
        if (db.Length < 100) return false;
        var magic = Encoding.ASCII.GetString(db, 0, 16);
        if (!magic.Equals("SQLite format 3\0", StringComparison.Ordinal)) return false;

        var rawPageSize = BinaryPrimitives.ReadUInt16BigEndian(db.AsSpan(16, 2));
        var pageSize = rawPageSize == 1 ? 65536 : (int)rawPageSize;
        if (pageSize < 512 || pageSize > 65536) return false;

        var reserved = db[20];
        var usablePageSize = pageSize - reserved;
        var textEncoding = BinaryPrimitives.ReadUInt32BigEndian(db.AsSpan(56, 4));
        var encoding = textEncoding switch
        {
            2 => Encoding.Unicode,
            3 => Encoding.BigEndianUnicode,
            _ => Encoding.UTF8
        };

        // 1. Read sqlite_master (always rooted at page 1)
        var masterRows = ReadTableRows(db, pageSize, usablePageSize, encoding, rootPage: 1);
        var datasRoot = 0;
        var textsRoot = 0;
        foreach (var (_, cols) in masterRows)
        {
            if (cols.Count < 4) continue;
            var objType = cols[0] as string;
            var tblName = cols[1] as string;
            if (!string.Equals(objType, "table", StringComparison.OrdinalIgnoreCase)) continue;
            var rp = ToInt(cols[3]);
            if (string.Equals(tblName, "datas", StringComparison.OrdinalIgnoreCase)) datasRoot = rp;
            else if (string.Equals(tblName, "texts", StringComparison.OrdinalIgnoreCase)) textsRoot = rp;
        }

        if (datasRoot <= 0 && textsRoot <= 0) return false;

        if (datasRoot > 0)
        {
            foreach (var (rowId, cols) in ReadTableRows(db, pageSize, usablePageSize, encoding, datasRoot))
            {
                if (cols.Count < 10) continue;
                var id = cols[0] is null ? (int)rowId : ToInt(cols[0]);
                if (id <= 0) continue;
                cardsById[id] = new YgoCardData(
                    Id: id,
                    Ot: ToInt(cols[1]),
                    Alias: ToInt(cols[2]),
                    Setcode: ToLong(cols[3]),
                    Type: ToInt(cols[4]),
                    Atk: ToInt(cols[5]),
                    Def: ToInt(cols[6]),
                    Level: ToLong(cols[7]),
                    Race: ToLong(cols[8]),
                    Attribute: ToInt(cols[9]),
                    Category: cols.Count > 10 ? ToLong(cols[10]) : 0);
            }
        }

        if (textsRoot > 0)
        {
            foreach (var (rowId, cols) in ReadTableRows(db, pageSize, usablePageSize, encoding, textsRoot))
            {
                if (cols.Count < 3) continue;
                var id = cols[0] is null ? (int)rowId : ToInt(cols[0]);
                if (id <= 0) continue;
                var name = cols[1]?.ToString() ?? string.Empty;
                var desc = cols[2]?.ToString() ?? string.Empty;
                var strs = new string[16];
                for (var i = 0; i < 16; i++)
                {
                    strs[i] = cols.Count > 3 + i ? (cols[3 + i]?.ToString() ?? string.Empty) : string.Empty;
                }
                textsById[id] = new YgoCardText(id, name, desc, strs);
            }
        }

        return true;
    }

    private static List<(long RowId, List<object?> Columns)> ReadTableRows(
        byte[] db,
        int pageSize,
        int usablePageSize,
        Encoding encoding,
        int rootPage)
    {
        var results = new List<(long, List<object?>)>();
        var visited = new HashSet<int>();
        VisitTablePage(db, pageSize, usablePageSize, encoding, rootPage, visited, results);
        return results;
    }

    private static void VisitTablePage(
        byte[] db,
        int pageSize,
        int usablePageSize,
        Encoding encoding,
        int pageNumber,
        HashSet<int> visited,
        List<(long, List<object?>)> results)
    {
        if (pageNumber <= 0 || !visited.Add(pageNumber)) return;
        var pageOffset = (pageNumber - 1) * pageSize;
        if (pageOffset < 0 || pageOffset + 12 > db.Length) return;

        var headerOffset = pageNumber == 1 ? 100 : pageOffset;
        var pageType = db[headerOffset];
        var cellCount = BinaryPrimitives.ReadUInt16BigEndian(db.AsSpan(headerOffset + 3, 2));

        if (pageType == 0x05)
        {
            // Table B-Tree Interior Page
            var rightMost = (int)BinaryPrimitives.ReadUInt32BigEndian(db.AsSpan(headerOffset + 8, 4));
            var ptrArrayStart = headerOffset + 12;
            for (var i = 0; i < cellCount; i++)
            {
                var ptrPos = ptrArrayStart + i * 2;
                if (ptrPos + 2 > db.Length) break;
                var cellRel = BinaryPrimitives.ReadUInt16BigEndian(db.AsSpan(ptrPos, 2));
                var cellPos = pageOffset + cellRel;
                if (cellPos + 4 > db.Length) continue;
                var leftChild = (int)BinaryPrimitives.ReadUInt32BigEndian(db.AsSpan(cellPos, 4));
                VisitTablePage(db, pageSize, usablePageSize, encoding, leftChild, visited, results);
            }

            VisitTablePage(db, pageSize, usablePageSize, encoding, rightMost, visited, results);
            return;
        }

        if (pageType != 0x0D) return; // Not a Table Leaf Page

        var leafPtrStart = headerOffset + 8;
        var maxLocal = usablePageSize - 35;
        var minLocal = ((usablePageSize - 12) * 32 / 255) - 23;

        for (var i = 0; i < cellCount; i++)
        {
            var ptrPos = leafPtrStart + i * 2;
            if (ptrPos + 2 > db.Length) break;
            var cellRel = BinaryPrimitives.ReadUInt16BigEndian(db.AsSpan(ptrPos, 2));
            var pos = pageOffset + cellRel;
            if (pos >= db.Length) continue;

            var payloadSize = (int)ReadVarint(db, ref pos);
            var rowId = ReadVarint(db, ref pos);
            if (payloadSize <= 0) continue;

            byte[] payload;
            if (payloadSize <= maxLocal)
            {
                if (pos + payloadSize > db.Length) continue;
                payload = db.AsSpan(pos, payloadSize).ToArray();
            }
            else
            {
                var k = minLocal + ((payloadSize - minLocal) % (usablePageSize - 4));
                var localSize = k <= maxLocal ? k : minLocal;
                if (pos + localSize + 4 > db.Length) continue;

                payload = new byte[payloadSize];
                Buffer.BlockCopy(db, pos, payload, 0, localSize);
                var copied = localSize;
                var overflowPage = (int)BinaryPrimitives.ReadUInt32BigEndian(db.AsSpan(pos + localSize, 4));
                var guard = 0;

                while (overflowPage > 0 && copied < payloadSize && guard++ < 4096)
                {
                    var ovOffset = (overflowPage - 1) * pageSize;
                    if (ovOffset < 0 || ovOffset + 4 > db.Length) break;
                    var nextOv = (int)BinaryPrimitives.ReadUInt32BigEndian(db.AsSpan(ovOffset, 4));
                    var chunk = Math.Min(usablePageSize - 4, payloadSize - copied);
                    if (ovOffset + 4 + chunk > db.Length) break;
                    Buffer.BlockCopy(db, ovOffset + 4, payload, copied, chunk);
                    copied += chunk;
                    overflowPage = nextOv;
                }
            }

            var cols = DecodeRecord(payload, encoding);
            results.Add((rowId, cols));
        }
    }

    private static List<object?> DecodeRecord(byte[] payload, Encoding encoding)
    {
        var cols = new List<object?>();
        if (payload.Length == 0) return cols;

        var hdrPos = 0;
        var headerSize = (int)ReadVarint(payload, ref hdrPos);
        if (headerSize <= 0 || headerSize > payload.Length) return cols;

        var serialTypes = new List<long>();
        while (hdrPos < headerSize)
        {
            serialTypes.Add(ReadVarint(payload, ref hdrPos));
        }

        var bodyPos = headerSize;
        foreach (var st in serialTypes)
        {
            if (st == 0)
            {
                cols.Add(null);
            }
            else if (st == 1)
            {
                if (bodyPos + 1 > payload.Length) break;
                cols.Add((long)(sbyte)payload[bodyPos]);
                bodyPos += 1;
            }
            else if (st == 2)
            {
                if (bodyPos + 2 > payload.Length) break;
                cols.Add((long)BinaryPrimitives.ReadInt16BigEndian(payload.AsSpan(bodyPos, 2)));
                bodyPos += 2;
            }
            else if (st == 3)
            {
                if (bodyPos + 3 > payload.Length) break;
                var v = (payload[bodyPos] << 16) | (payload[bodyPos + 1] << 8) | payload[bodyPos + 2];
                if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                cols.Add((long)v);
                bodyPos += 3;
            }
            else if (st == 4)
            {
                if (bodyPos + 4 > payload.Length) break;
                cols.Add((long)BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(bodyPos, 4)));
                bodyPos += 4;
            }
            else if (st == 5)
            {
                if (bodyPos + 6 > payload.Length) break;
                long v = 0;
                for (var b = 0; b < 6; b++) v = (v << 8) | payload[bodyPos + b];
                if ((v & 0x800000000000L) != 0) v |= unchecked((long)0xFFFF000000000000L);
                cols.Add(v);
                bodyPos += 6;
            }
            else if (st == 6)
            {
                if (bodyPos + 8 > payload.Length) break;
                cols.Add(BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(bodyPos, 8)));
                bodyPos += 8;
            }
            else if (st == 7)
            {
                if (bodyPos + 8 > payload.Length) break;
                var bits = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(bodyPos, 8));
                cols.Add(BitConverter.Int64BitsToDouble(bits));
                bodyPos += 8;
            }
            else if (st == 8)
            {
                cols.Add(0L);
            }
            else if (st == 9)
            {
                cols.Add(1L);
            }
            else if (st >= 12 && (st & 1) == 0)
            {
                var len = (int)((st - 12) / 2);
                if (bodyPos + len > payload.Length) break;
                cols.Add(payload.AsSpan(bodyPos, len).ToArray());
                bodyPos += len;
            }
            else if (st >= 13 && (st & 1) == 1)
            {
                var len = (int)((st - 13) / 2);
                if (bodyPos + len > payload.Length) break;
                cols.Add(encoding.GetString(payload, bodyPos, len));
                bodyPos += len;
            }
            else
            {
                cols.Add(null);
            }
        }

        return cols;
    }

    private static long ReadVarint(byte[] data, ref int pos)
    {
        ulong result = 0;
        for (var i = 0; i < 8; i++)
        {
            if (pos >= data.Length) return (long)result;
            var b = data[pos++];
            result = (result << 7) | (ulong)(uint)(b & 0x7F);
            if ((b & 0x80) == 0) return (long)result;
        }
        if (pos < data.Length)
        {
            result = (result << 8) | data[pos++];
        }
        return (long)result;
    }

    private static int ToInt(object? v) => v switch
    {
        long l => unchecked((int)l),
        int i => i,
        double d => (int)d,
        string s when int.TryParse(s, out var p) => p,
        _ => 0
    };

    private static long ToLong(object? v) => v switch
    {
        long l => l,
        int i => i,
        double d => (long)d,
        string s when long.TryParse(s, out var p) => p,
        _ => 0
    };
}
