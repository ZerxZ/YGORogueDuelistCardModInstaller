using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using YGORogueDuelistCardModInstaller.Core;
using YGORogueDuelistCardModInstaller.Core.ModManager;

namespace YGORogueDuelistCardModInstaller.Mcp;

public sealed class BufferingInteraction : IInstallerInteraction
{
    private readonly List<string> _lines = [];
    public IReadOnlyList<string> Lines => _lines;

    public void Log(LogLevel level, string message)
    {
        var prefix = level switch
        {
            LogLevel.Ok => "[OK] ",
            LogLevel.Warn => "[WARN] ",
            LogLevel.Error => "[ERROR] ",
            _ => ""
        };
        _lines.Add(prefix + message);
    }

    public void ReportProgress(string status, double? percent = null) { }
    public Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<string?> PromptForPicsDirAsync(string prompt, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
}

public static class McpToolDispatcher
{
    public static JsonArray BuildToolDefinitions() => new(
        Tool("get_mod_guide", "Get official Mod Spec v1 guides & schemas for creating cards, characters, relics, packs, decks, trainers, Xmake-style Lua/JS DSL, Lua scripts, or JS plugins.",
            Obj(("topic", Str("Topic: overview | dsl | card | character | relic | pack | deck | trainer | lua | js | bitmasks")))),

        Tool("get_manager_status", "Inspect the MOD manager status: detected game version, sync state, all managed mods, load order, and conflicts.",
            Obj(("root", Str("Optional workspace root path")))),

        Tool("search_game_data", "Search vanilla game content.json entities (cards, characters, relics, or packs) to find real IDs, names, indices, or relic keys.",
            Obj(
                ("kind", Str("Entity kind: cards | characters | relics | packs")),
                ("query", Str("Optional substring filter")),
                ("limit", Int("Max results (default 25, max 100)")),
                ("root", Str("Optional workspace root path"))),
            ["kind"]),

        Tool("create_mod", "Scaffold a brand-new standard v1 MOD project under mods/<id>/.",
            Obj(
                ("id", Str("Unique mod ID slug (e.g. my-dragon-pack)")),
                ("name", Str("Display name")),
                ("author", Str("Author name")),
                ("description", Str("Short description")),
                ("root", Str("Optional workspace root path"))),
            ["id"]),

        Tool("add_mod_card", "Create or update a declarative custom card in cards/*.json and optionally write its Lua effect script in lua/cards/c<id>.lua.",
            Obj(
                ("modId", Str("Target managed MOD ID")),
                ("id", Int("Unique numeric card ID (recommended 190010000-199999999)")),
                ("name", Str("English card name")),
                ("desc", Str("English card text")),
                ("cardType", Str("Display type, e.g. 'Effect Monster', 'Spell Card', 'Trap Card', 'Fusion Effect Monster'")),
                ("race", Str("Display race/subtype, e.g. 'Dragon', 'Spellcaster', 'Normal', 'Quick-Play', 'Continuous', 'Field'")),
                ("attribute", Str("Attribute, e.g. 'DARK', 'LIGHT', 'FIRE', or '' for Spell/Trap")),
                ("level", Int("Monster Level/Rank (0 for Spell/Trap)")),
                ("atk", Int("ATK (0 for Spell/Trap)")),
                ("defense", Int("DEF (0 for Spell/Trap)")),
                ("typeMask", Int("ocgcore data.type bitmask (e.g. 33=Effect Monster, 2=Normal Spell, 65538=Quick-Play, 4=Normal Trap)")),
                ("raceMask", Str("ocgcore data.race string mask (e.g. '2'=Spellcaster, '8192'=Dragon, '0'=Spell/Trap)")),
                ("attributeMask", Int("ocgcore data.attribute mask (1=EARTH,2=WATER,4=FIRE,8=WIND,16=LIGHT,32=DARK,64=DIVINE,0=none)")),
                ("zhName", Str("Optional Chinese card name")),
                ("zhDesc", Str("Optional Chinese card effect text")),
                ("luaScript", Str("Optional Lua script content saved to lua/cards/c<id>.lua")),
                ("fileName", Str("Optional JSON filename inside cards/ (default 'custom-cards.json')")),
                ("root", Str("Optional workspace root path"))),
            ["modId", "id", "name", "desc", "cardType", "race", "typeMask"]),

        Tool("add_mod_character", "Create a new playable character or edit an existing character in characters/*.json.",
            Obj(
                ("modId", Str("Target managed MOD ID")),
                ("name", Str("Character English name")),
                ("zhName", Str("Optional Chinese display name")),
                ("index", Int("Optional existing character index to edit; omit or -1 to add a new playable character")),
                ("sprite", Str("Portrait sprite filename (default 'dm01.png')")),
                ("race", Str("Signature monster race (default 'Spellcaster')")),
                ("attribute", Str("Signature attribute (default 'DARK')")),
                ("theme", Str("Theme label, e.g. 'Dragon / LIGHT'")),
                ("color", Str("Hex accent color, e.g. '#f4cf57'")),
                ("startingRelic", Str("Starting relic key (e.g. 'mage', 'dragon', or custom relic key)")),
                ("cards", StrArray("Starting signature card NAMES (array of card name strings)")),
                ("startingPacks", StrArray("Optional starting pack IDs (e.g. ['DRAFT-YAMI-YUGI','EN-MFC'])")),
                ("unlockedByDefault", Bool("Whether unlocked from the start (default true)")),
                ("fileName", Str("Optional JSON filename inside characters/ (default 'custom-characters.json')")),
                ("root", Str("Optional workspace root path"))),
            ["modId", "name"]),

        Tool("add_mod_relic", "Create or update a custom relic in relics/*.json (auto-registers art, i18n, approved_relics.py, and optional Lua duel effect).",
            Obj(
                ("modId", Str("Target managed MOD ID")),
                ("key", Str("Unique snake_case relic key (e.g. 'arcane_lens')")),
                ("name", Str("English relic name")),
                ("description", Str("English relic effect description")),
                ("cost", Int("Shop coin cost (default 80)")),
                ("zhName", Str("Optional Chinese relic name")),
                ("zhDescription", Str("Optional Chinese relic description")),
                ("art", Str("Stock relic image (e.g. 'Merchant Urn.png'), local file under art/, or data: URL")),
                ("tag", Str("Category tag: offense | defense | economy | healing | utility (default 'utility')")),
                ("amount", Int("Optional numeric amount")),
                ("filter", Str("Optional race/archetype filter")),
                ("luaEffect", Str("Optional ocgcore Lua duel script snippet applied when holding this relic")),
                ("startingForCharacters", IntArray("Optional character indices that start with this relic")),
                ("fileName", Str("Optional JSON filename inside relics/ (default 'custom-relics.json')")),
                ("root", Str("Optional workspace root path"))),
            ["modId", "key", "name", "description"]),

        Tool("configure_mod_trainer", "Create or update trainer.json inside a MOD (unlock all characters/variants, starting gold/LP, free shop, reward multiplier, F8 live overlay).",
            Obj(
                ("modId", Str("Target managed MOD ID")),
                ("unlockAllCharacters", Bool("Unlock all playable characters")),
                ("unlockAllVariants", Bool("Unlock all alternate character variants")),
                ("startingGold", Int("Override starting gold on new runs")),
                ("startingGoldBonus", Int("Extra bonus gold added on new runs")),
                ("startingLp", Int("Override starting LP on new runs")),
                ("freeShop", Bool("Set all shop items to 0 coins")),
                ("shopPriceMultiplier", Num("Shop price multiplier (e.g. 0.5 for 50% off)")),
                ("rewardGoldMultiplier", Num("Duel reward gold multiplier (e.g. 2.0)")),
                ("startingRelics", StrArray("Extra relic keys granted at the start of every run")),
                ("startingCards", IntArray("Extra card IDs added to the starting pool of every run")),
                ("inGameOverlay", Bool("Enable built-in F8 in-game Trainer overlay (default true)")),
                ("root", Str("Optional workspace root path"))),
            ["modId"]),

        Tool("write_mod_file", "Write or update any text file inside mods/<modId>/ (Lua scripts, Lua libs, JS plugins, CSS styles, campaign .py, JSON).",
            Obj(
                ("modId", Str("Target managed MOD ID")),
                ("relativePath", Str("Relative path inside mod folder, e.g. 'lua/cards/c190099001.lua' or 'js/main.js'")),
                ("content", Str("Full UTF-8 file content")),
                ("root", Str("Optional workspace root path"))),
            ["modId", "relativePath", "content"]),

        Tool("read_mod_file", "Read a text file inside mods/<modId>/.",
            Obj(
                ("modId", Str("Target managed MOD ID")),
                ("relativePath", Str("Relative path inside mod folder")),
                ("root", Str("Optional workspace root path"))),
            ["modId", "relativePath"]),

        Tool("validate_mod", "Validate a managed MOD's manifest, JSON files, card/Lua script pairing, and cross-mod conflicts.",
            Obj(
                ("modId", Str("Target managed MOD ID")),
                ("root", Str("Optional workspace root path"))),
            ["modId"]),

        Tool("add_mod_pack", "Create or update a custom booster pack in packs/*.json (synced to content.json and campaign data/packs.json).",
            Obj(
                ("modId", Str("Target managed MOD ID")),
                ("id", Str("Unique pack ID (e.g. 'EN-MYPACK')")),
                ("name", Str("English pack name")),
                ("zhName", Str("Optional Chinese pack name")),
                ("code", Str("Short pack code (e.g. 'MYP')")),
                ("size", Int("Cards per pack (default 9)")),
                ("common", IntArray("Common card IDs")),
                ("rare", IntArray("Rare card IDs")),
                ("super", IntArray("Super Rare card IDs")),
                ("ultra", IntArray("Ultra Rare card IDs")),
                ("root", Str("Optional workspace root path"))),
            ["modId", "id", "name"]),

        Tool("add_mod_preconstructed_deck", "Create or update a preconstructed deck in preconstructed/*.json (or character deck in decks.json).",
            Obj(
                ("modId", Str("Target managed MOD ID")),
                ("id", Str("Unique preconstructed deck ID (e.g. 'MOD-ARCANE')")),
                ("name", Str("English deck name")),
                ("zhName", Str("Optional Chinese deck name")),
                ("art", Str("Deck box art code (default 'SDK')")),
                ("cards", IntArray("Array of numeric card IDs in the deck")),
                ("root", Str("Optional workspace root path"))),
            ["modId", "id", "name", "cards"]),

        Tool("pack_mod_or_workspace", "Package a single MOD or the entire MOD workspace (with load order, enabled states & minGameVersion) into a distributable .zip file.",
            Obj(
                ("target", Str("Target: mod | workspace")),
                ("modId", Str("MOD ID (required when target='mod')")),
                ("outputZip", Str("Optional destination .zip path (defaults to dist/*.zip)")),
                ("packName", Str("Workspace modpack name (when target='workspace')")),
                ("packVersion", Str("Workspace modpack version (default '1.0.0')")),
                ("minGameVersion", Str("Optional minimum required game version (e.g. '0.2.46')")),
                ("enabledOnly", Bool("Only include enabled mods when target='workspace'")),
                ("root", Str("Optional workspace root path"))),
            ["target"]),

        Tool("apply_or_restore_mods", "Apply enabled mods to the game (default zero-touch VirtualBundle mode), dry-run BuildOnly export, or restore vanilla.",
            Obj(
                ("action", Str("Action: apply | build_only | restore")),
                ("mode", Str("Optional mode for apply: virtual (default, zero-touch) | manifest")),
                ("developerMode", Bool("Enable in-game Developer Mode (F10 Inspector, Sandbox unlock, window.YgoModDev)")),
                ("ignoreVersionRequirements", Bool("Ignore minGameVersion/minManagerVersion checks")),
                ("buildOnlyDir", Str("Output directory when action is build_only")),
                ("launch", Bool("Launch game after apply (default false)")),
                ("root", Str("Optional workspace root path"))),
            ["action"])
    );

    public static async Task<string> ExecuteToolAsync(string name, JsonObject args, CancellationToken ct)
    {
        var root = args["root"]?.GetValue<string>();
        var engine = new ModInjectionEngine(root);

        return name switch
        {
            "get_mod_guide" => ModKnowledgeBase.GetGuide(args["topic"]?.GetValue<string>()),
            "get_manager_status" => HandleStatus(engine),
            "search_game_data" => HandleSearchGameData(engine, args),
            "create_mod" => HandleCreateMod(engine, args),
            "add_mod_card" => HandleAddCard(engine, args),
            "add_mod_character" => HandleAddCharacter(engine, args),
            "add_mod_relic" => HandleAddRelic(engine, args),
            "add_mod_pack" => HandleAddPack(engine, args),
            "add_mod_preconstructed_deck" => HandleAddPreconstructedDeck(engine, args),
            "pack_mod_or_workspace" => HandlePack(engine, args),
            "configure_mod_trainer" => HandleConfigureTrainer(engine, args),
            "write_mod_file" => HandleWriteFile(engine, args),
            "read_mod_file" => HandleReadFile(engine, args),
            "validate_mod" => HandleValidateMod(engine, args),
            "apply_or_restore_mods" => await HandleApplyOrRestoreAsync(engine, args, ct),
            _ => throw new ModOperationException($"Unknown MCP tool: {name}")
        };
    }

    private static string HandleStatus(ModInjectionEngine engine)
    {
        var status = engine.GetStatus();
        var root = new JsonObject
        {
            ["workspaceRoot"] = status.WorkspaceRoot,
            ["modsRoot"] = status.ModsRoot,
            ["installDir"] = status.InstallDir,
            ["gameBaseDir"] = status.GameBaseDir,
            ["baseGameVersion"] = status.ActiveBaseGameVersion,
            ["injectionMode"] = status.Mode.ToString(),
            ["syncState"] = status.SyncState.ToString(),
            ["syncSummary"] = status.SyncSummary,
            ["installedVirtualVersions"] = new JsonArray(status.InstalledVirtualVersions.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray())
        };

        var modsArr = new JsonArray();
        foreach (var m in status.Mods)
        {
            modsArr.Add((JsonNode?)new JsonObject
            {
                ["id"] = m.Id,
                ["name"] = m.Manifest.Name,
                ["version"] = m.Manifest.Version,
                ["enabled"] = m.Enabled,
                ["order"] = m.Order + 1,
                ["directory"] = m.DirectoryPath,
                ["cards"] = m.Stats.CardCount,
                ["characters"] = m.Stats.CharacterCount,
                ["relics"] = m.Stats.RelicCount,
                ["packs"] = m.Stats.PackCount,
                ["preconstructed"] = m.Stats.PreconstructedDeckCount,
                ["luaCards"] = m.Stats.ScriptCount,
                ["luaLibs"] = m.Stats.LuaLibCount,
                ["jsPlugins"] = m.Stats.JsPluginCount,
                ["cssStyles"] = m.Stats.CssStyleCount,
                ["hasTrainer"] = m.Stats.HasTrainer,
                ["hasLuaDsl"] = m.Stats.HasLuaDsl,
                ["hasJsDsl"] = m.Stats.HasJsDsl,
                ["overrides"] = m.Stats.OverrideFileCount
            });
        }

        root["mods"] = modsArr;

        var conflictsArr = new JsonArray();
        foreach (var c in status.Conflicts)
        {
            conflictsArr.Add((JsonNode?)new JsonObject
            {
                ["category"] = c.Category,
                ["resourceKey"] = c.ResourceKey,
                ["mods"] = new JsonArray(c.ModIds.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()),
                ["winner"] = c.WinnerModId
            });
        }

        root["conflicts"] = conflictsArr;
        return root.ToJsonString(ModPatcher.JsonOptions);
    }

    private static string HandleSearchGameData(ModInjectionEngine engine, JsonObject args)
    {
        var status = engine.GetStatus();
        var vanilla = ModInjectionEngine.FindBestVanillaSource(status.InstallDir, status.GameBaseDir, status.LauncherMeta)
            ?? throw new ModOperationException("Could not locate vanilla web-X.Y.Z.zip on this machine.");

        var kind = args["kind"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "cards";
        var query = args["query"]?.GetValue<string>()?.Trim() ?? string.Empty;
        var limit = Math.Clamp(args["limit"] is JsonValue lv && lv.TryGetValue<int>(out var l) ? l : 25, 1, 100);

        using var zip = ZipFile.OpenRead(vanilla.ZipPath);
        var content = ModPatcher.ParseJson(ModPatcher.ReadZipText(zip, "content.json")).AsObject();
        var results = new JsonArray();

        if (kind == "cards")
        {
            foreach (var c in content["cards"]!.AsArray().OfType<JsonObject>())
            {
                var name = c["name"]?.GetValue<string>() ?? "";
                var id = c["id"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(query) &&
                    !name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !id.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;

                results.Add((JsonNode?)new JsonObject
                {
                    ["id"] = c["id"]?.DeepClone(),
                    ["name"] = name,
                    ["type"] = c["type"]?.GetValue<string>(),
                    ["race"] = c["race"]?.GetValue<string>(),
                    ["attribute"] = c["attribute"]?.GetValue<string>(),
                    ["level"] = c["level"]?.DeepClone(),
                    ["atk"] = c["atk"]?.DeepClone(),
                    ["defense"] = c["defense"]?.DeepClone()
                });
                if (results.Count >= limit) break;
            }
        }
        else if (kind == "characters")
        {
            var playable = content["playable"]!.AsArray()
                .Select(n => n is JsonValue pv && pv.TryGetValue<int>(out var i) ? i : -1)
                .ToHashSet();
            var chars = content["characters"]!.AsArray();
            for (var i = 0; i < chars.Count; i++)
            {
                if (chars[i] is not JsonObject ch) continue;
                var name = ch["name"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrEmpty(query) &&
                    !name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !i.ToString(CultureInfo.InvariantCulture).Equals(query, StringComparison.Ordinal))
                    continue;

                results.Add((JsonNode?)new JsonObject
                {
                    ["index"] = i,
                    ["playable"] = playable.Contains(i),
                    ["name"] = name,
                    ["sprite"] = ch["sprite"]?.GetValue<string>(),
                    ["race"] = ch["race"]?.GetValue<string>(),
                    ["attribute"] = ch["attribute"]?.GetValue<string>(),
                    ["starting_relic"] = ch["starting_relic"]?.GetValue<string>(),
                    ["cards"] = ch["cards"]?.DeepClone()
                });
                if (results.Count >= limit) break;
            }
        }
        else if (kind == "relics")
        {
            var artifacts = content["artifacts"]!.AsObject();
            var info = content["artifactInfo"]!.AsObject();
            foreach (var (k, v) in artifacts)
            {
                var arr = v as JsonArray;
                var name = arr?[0]?.GetValue<string>() ?? "";
                var desc = arr?.Count > 2 ? arr[2]?.GetValue<string>() ?? "" : "";
                if (!string.IsNullOrEmpty(query) &&
                    !k.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !desc.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;

                results.Add((JsonNode?)new JsonObject
                {
                    ["key"] = k,
                    ["name"] = name,
                    ["cost"] = arr?.Count > 1 ? arr[1]?.DeepClone() : 0,
                    ["description"] = desc,
                    ["art"] = info[k]?["art"]?.DeepClone(),
                    ["tag"] = info[k]?["tag"]?.DeepClone()
                });
                if (results.Count >= limit) break;
            }
        }
        else if (kind == "packs")
        {
            foreach (var p in content["packs"]!.AsArray().OfType<JsonObject>())
            {
                var id = p["id"]?.GetValue<string>() ?? "";
                var name = p["name"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrEmpty(query) &&
                    !id.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;

                results.Add((JsonNode?)new JsonObject
                {
                    ["id"] = id,
                    ["name"] = name,
                    ["cardCount"] = p["common"]?.AsArray().Count ?? 0
                });
                if (results.Count >= limit) break;
            }
        }
        else
        {
            throw new ModOperationException($"Unsupported kind '{kind}'. Use cards, characters, relics, or packs.");
        }

        return new JsonObject
        {
            ["gameVersion"] = vanilla.Version,
            ["kind"] = kind,
            ["count"] = results.Count,
            ["items"] = results
        }.ToJsonString(ModPatcher.JsonOptions);
    }

    private static string HandleCreateMod(ModInjectionEngine engine, JsonObject args)
    {
        var id = RequireString(args, "id");
        var status = engine.GetStatus();
        var created = engine.Repository.CreateTemplateMod(
            id,
            args["name"]?.GetValue<string>(),
            args["author"]?.GetValue<string>(),
            args["description"]?.GetValue<string>(),
            status.ActiveBaseGameVersion);
        return $"Created mod '{created.Id}' at {created.DirectoryPath}. Next: call add_mod_card / add_mod_character / add_mod_relic / configure_mod_trainer / write_mod_file, then apply_or_restore_mods(action='apply').";
    }

    private static string HandleAddCard(ModInjectionEngine engine, JsonObject args)
    {
        var mod = ResolveExistingMod(engine, RequireString(args, "modId"));
        var id = RequireInt(args, "id");
        var name = RequireString(args, "name");
        var desc = RequireString(args, "desc");
        var cardType = RequireString(args, "cardType");
        var race = RequireString(args, "race");
        var typeMask = RequireInt(args, "typeMask");

        var attribute = args["attribute"]?.GetValue<string>() ?? string.Empty;
        var level = TryGetInt(args, "level", 0);
        var atk = TryGetInt(args, "atk", 0);
        var def = TryGetInt(args, "defense", 0);
        var raceMask = args["raceMask"]?.GetValue<string>() ?? "0";
        var attrMask = TryGetInt(args, "attributeMask", 0);
        var zhName = args["zhName"]?.GetValue<string>();
        var zhDesc = args["zhDesc"]?.GetValue<string>();
        var fileName = SanitizeJsonFileName(args["fileName"]?.GetValue<string>(), "custom-cards.json");

        var cardsDir = Path.Combine(mod.DirectoryPath, "cards");
        Directory.CreateDirectory(cardsDir);
        var cardFile = Path.Combine(cardsDir, fileName);

        var array = File.Exists(cardFile)
            ? (ModPatcher.ParseJson(File.ReadAllText(cardFile, Encoding.UTF8)) as JsonArray ?? new JsonArray())
            : new JsonArray();

        var entry = new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["desc"] = desc,
            ["type"] = cardType,
            ["race"] = race,
            ["attribute"] = attribute,
            ["level"] = level,
            ["atk"] = atk,
            ["defense"] = def,
            ["addToEraPool"] = true,
            ["data"] = new JsonObject
            {
                ["code"] = id,
                ["alias"] = 0,
                ["setcodes"] = new JsonArray(),
                ["type"] = typeMask,
                ["level"] = level,
                ["attribute"] = attrMask,
                ["race"] = raceMask,
                ["attack"] = atk,
                ["defense"] = def,
                ["lscale"] = 0,
                ["rscale"] = 0,
                ["link_marker"] = 0
            },
            ["strings"] = new JsonArray(Enumerable.Range(0, 16).Select(_ => (JsonNode)JsonValue.Create(string.Empty)!).ToArray())
        };

        if (!string.IsNullOrWhiteSpace(zhName)) entry["zhName"] = zhName;
        if (!string.IsNullOrWhiteSpace(zhDesc)) entry["zhDesc"] = zhDesc;

        UpsertByKey(array, "id", id.ToString(CultureInfo.InvariantCulture), entry);
        File.WriteAllText(cardFile, array.ToJsonString(PrettyNodeOptions), ModPatcher.Utf8NoBom);

        var luaScript = args["luaScript"]?.GetValue<string>();
        string? luaPath = null;
        if (!string.IsNullOrWhiteSpace(luaScript))
        {
            var luaDir = Path.Combine(mod.DirectoryPath, "lua", "cards");
            Directory.CreateDirectory(luaDir);
            luaPath = Path.Combine(luaDir, $"c{id}.lua");
            File.WriteAllText(luaPath, luaScript, ModPatcher.Utf8NoBom);
        }

        return luaPath is not null
            ? $"Added/updated card {id} ('{name}') in {cardFile} and Lua script in {luaPath}."
            : $"Added/updated card {id} ('{name}') in {cardFile}.";
    }

    private static string HandleAddCharacter(ModInjectionEngine engine, JsonObject args)
    {
        var mod = ResolveExistingMod(engine, RequireString(args, "modId"));
        var name = RequireString(args, "name");
        var fileName = SanitizeJsonFileName(args["fileName"]?.GetValue<string>(), "custom-characters.json");

        var charsDir = Path.Combine(mod.DirectoryPath, "characters");
        Directory.CreateDirectory(charsDir);
        var filePath = Path.Combine(charsDir, fileName);

        var array = File.Exists(filePath)
            ? (ModPatcher.ParseJson(File.ReadAllText(filePath, Encoding.UTF8)) as JsonArray ?? new JsonArray())
            : new JsonArray();

        var entry = new JsonObject
        {
            ["name"] = name,
            ["sprite"] = args["sprite"]?.GetValue<string>() ?? "dm01.png",
            ["race"] = args["race"]?.GetValue<string>() ?? "Spellcaster",
            ["attribute"] = args["attribute"]?.GetValue<string>() ?? "DARK",
            ["color"] = args["color"]?.GetValue<string>() ?? "#f4cf57",
            ["starting_relic"] = args["startingRelic"]?.GetValue<string>() ?? "mage",
            ["unlockedByDefault"] = args["unlockedByDefault"]?.GetValue<bool>() ?? true
        };

        if (args["index"] is JsonValue idxVal && idxVal.TryGetValue<int>(out var idx) && idx >= 0) entry["index"] = idx;
        if (args["zhName"]?.GetValue<string>() is { Length: > 0 } zhName) entry["zhName"] = zhName;
        if (args["theme"]?.GetValue<string>() is { Length: > 0 } theme) entry["theme"] = theme;
        if (args["cards"] is JsonArray cards) entry["cards"] = cards.DeepClone();
        if (args["startingPacks"] is JsonArray packs) entry["starting_packs"] = packs.DeepClone();

        UpsertByKey(array, "name", name, entry);
        File.WriteAllText(filePath, array.ToJsonString(PrettyNodeOptions), ModPatcher.Utf8NoBom);
        return $"Added/updated character '{name}' in {filePath}.";
    }

    private static string HandleAddRelic(ModInjectionEngine engine, JsonObject args)
    {
        var mod = ResolveExistingMod(engine, RequireString(args, "modId"));
        var key = ModRepository.SanitizeId(RequireString(args, "key")).Replace('-', '_');
        var name = RequireString(args, "name");
        var desc = RequireString(args, "description");
        var fileName = SanitizeJsonFileName(args["fileName"]?.GetValue<string>(), "custom-relics.json");

        var relicsDir = Path.Combine(mod.DirectoryPath, "relics");
        Directory.CreateDirectory(relicsDir);
        var filePath = Path.Combine(relicsDir, fileName);

        var array = File.Exists(filePath)
            ? (ModPatcher.ParseJson(File.ReadAllText(filePath, Encoding.UTF8)) as JsonArray ?? new JsonArray())
            : new JsonArray();

        var entry = new JsonObject
        {
            ["key"] = key,
            ["name"] = name,
            ["description"] = desc,
            ["cost"] = TryGetInt(args, "cost", 80),
            ["art"] = args["art"]?.GetValue<string>() ?? "Merchant Urn.png",
            ["tag"] = args["tag"]?.GetValue<string>() ?? "utility",
            ["effect"] = $"approved_{key}",
            ["amount"] = TryGetInt(args, "amount", 0),
            ["filter"] = args["filter"]?.GetValue<string>() ?? string.Empty
        };

        if (args["zhName"]?.GetValue<string>() is { Length: > 0 } zhName) entry["zhName"] = zhName;
        if (args["zhDescription"]?.GetValue<string>() is { Length: > 0 } zhDesc) entry["zhDescription"] = zhDesc;
        if (args["luaEffect"]?.GetValue<string>() is { Length: > 0 } lua) entry["luaEffect"] = lua;
        if (args["startingForCharacters"] is JsonArray sfc) entry["startingForCharacters"] = sfc.DeepClone();

        UpsertByKey(array, "key", key, entry);
        File.WriteAllText(filePath, array.ToJsonString(PrettyNodeOptions), ModPatcher.Utf8NoBom);
        return $"Added/updated relic '{key}' ('{name}') in {filePath}.";
    }

    private static string HandleAddPack(ModInjectionEngine engine, JsonObject args)
    {
        var mod = ResolveExistingMod(engine, RequireString(args, "modId"));
        var id = RequireString(args, "id");
        var name = RequireString(args, "name");
        var packsDir = Path.Combine(mod.DirectoryPath, "packs");
        Directory.CreateDirectory(packsDir);
        var filePath = Path.Combine(packsDir, "custom-packs.json");
        var array = File.Exists(filePath)
            ? (ModPatcher.ParseJson(File.ReadAllText(filePath, Encoding.UTF8)) as JsonArray ?? new JsonArray())
            : new JsonArray();

        var entry = new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["region"] = "EN",
            ["code"] = args["code"]?.GetValue<string>() ?? (id.Contains('-') ? id.Split('-').Last() : id),
            ["date"] = string.Empty,
            ["image"] = string.Empty,
            ["size"] = TryGetInt(args, "size", 9),
            ["common"] = args["common"] is JsonArray cm ? cm.DeepClone() : new JsonArray(),
            ["rare"] = args["rare"] is JsonArray rr ? rr.DeepClone() : new JsonArray(),
            ["super"] = args["super"] is JsonArray sr ? sr.DeepClone() : new JsonArray(),
            ["ultra"] = args["ultra"] is JsonArray ur ? ur.DeepClone() : new JsonArray()
        };
        if (args["zhName"]?.GetValue<string>() is { Length: > 0 } zn) entry["zhName"] = zn;

        UpsertByKey(array, "id", id, entry);
        File.WriteAllText(filePath, array.ToJsonString(PrettyNodeOptions), ModPatcher.Utf8NoBom);
        return $"Added/updated booster pack '{id}' ('{name}') in {filePath}.";
    }

    private static string HandleAddPreconstructedDeck(ModInjectionEngine engine, JsonObject args)
    {
        var mod = ResolveExistingMod(engine, RequireString(args, "modId"));
        var id = RequireString(args, "id");
        var name = RequireString(args, "name");
        var cards = args["cards"] as JsonArray ?? throw new ModOperationException("Missing required 'cards' array.");
        var dir = Path.Combine(mod.DirectoryPath, "preconstructed");
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "custom-decks.json");
        var array = File.Exists(filePath)
            ? (ModPatcher.ParseJson(File.ReadAllText(filePath, Encoding.UTF8)) as JsonArray ?? new JsonArray())
            : new JsonArray();

        var entry = new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["date"] = string.Empty,
            ["art"] = args["art"]?.GetValue<string>() ?? "SDK",
            ["cards"] = cards.DeepClone()
        };
        if (args["zhName"]?.GetValue<string>() is { Length: > 0 } zn) entry["zhName"] = zn;

        UpsertByKey(array, "id", id, entry);
        File.WriteAllText(filePath, array.ToJsonString(PrettyNodeOptions), ModPatcher.Utf8NoBom);
        return $"Added/updated preconstructed deck '{id}' ('{name}', {cards.Count} cards) in {filePath}.";
    }

    private static string HandlePack(ModInjectionEngine engine, JsonObject args)
    {
        var target = RequireString(args, "target").ToLowerInvariant();
        var outZip = args["outputZip"]?.GetValue<string>();
        if (target == "mod")
        {
            var path = ModPackager.PackSingleMod(engine.Repository, RequireString(args, "modId"), outZip);
            return $"Packaged MOD to: {path}";
        }

        if (target == "workspace")
        {
            var (path, count, minVer) = ModPackager.PackWorkspace(
                engine.Repository,
                outZip,
                args["packName"]?.GetValue<string>(),
                args["packVersion"]?.GetValue<string>(),
                description: null,
                explicitMinGameVersion: args["minGameVersion"]?.GetValue<string>(),
                enabledOnly: args["enabledOnly"]?.GetValue<bool>() ?? false);
            return $"Packaged workspace modpack ({count} mods, minGameVersion={minVer ?? "any"}) to: {path}";
        }

        throw new ModOperationException($"Unknown pack target '{target}'. Use 'mod' or 'workspace'.");
    }

    private static string HandleConfigureTrainer(ModInjectionEngine engine, JsonObject args)
    {
        var mod = ResolveExistingMod(engine, RequireString(args, "modId"));
        var trainerPath = Path.Combine(mod.DirectoryPath, "trainer.json");
        var trainer = File.Exists(trainerPath)
            ? (ModPatcher.ParseJson(File.ReadAllText(trainerPath, Encoding.UTF8)) as JsonObject ?? new JsonObject())
            : new JsonObject();

        string[] keys =
        [
            "unlockAllCharacters", "unlockAllVariants", "startingGold", "startingGoldBonus",
            "startingLp", "freeShop", "shopPriceMultiplier", "rewardGoldMultiplier",
            "startingRelics", "startingCards", "inGameOverlay"
        ];
        foreach (var k in keys)
        {
            if (args.ContainsKey(k) && args[k] is not null)
            {
                trainer[k] = args[k]!.DeepClone();
            }
        }

        if (!trainer.ContainsKey("inGameOverlay"))
        {
            trainer["inGameOverlay"] = true;
        }

        File.WriteAllText(trainerPath, trainer.ToJsonString(PrettyNodeOptions), ModPatcher.Utf8NoBom);
        return $"Updated trainer configuration in {trainerPath}:\n{trainer.ToJsonString(PrettyNodeOptions)}";
    }

    private static string HandleWriteFile(ModInjectionEngine engine, JsonObject args)
    {
        var mod = ResolveExistingMod(engine, RequireString(args, "modId"));
        var rel = RequireString(args, "relativePath");
        var content = RequireString(args, "content");
        var fullPath = ResolveSafeModPath(mod.DirectoryPath, rel);

        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        File.WriteAllText(fullPath, content, ModPatcher.Utf8NoBom);
        return $"Wrote {content.Length} chars to {fullPath}.";
    }

    private static string HandleReadFile(ModInjectionEngine engine, JsonObject args)
    {
        var mod = ResolveExistingMod(engine, RequireString(args, "modId"));
        var rel = RequireString(args, "relativePath");
        var fullPath = ResolveSafeModPath(mod.DirectoryPath, rel);
        if (!File.Exists(fullPath)) throw new ModOperationException($"File not found: {rel}");
        return File.ReadAllText(fullPath, Encoding.UTF8);
    }

    private static string HandleValidateMod(ModInjectionEngine engine, JsonObject args)
    {
        var mod = ResolveExistingMod(engine, RequireString(args, "modId"));
        var issues = new List<string>();
        var notes = new List<string>();

        // 1. Validate all JSON files in the mod
        foreach (var jf in Directory.EnumerateFiles(mod.DirectoryPath, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                ModPatcher.ParseJson(File.ReadAllText(jf, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                issues.Add($"Invalid JSON in {Path.GetRelativePath(mod.DirectoryPath, jf)}: {ex.Message}");
            }
        }

        // 2. Check declarative cards have matching Lua scripts (unless normal monster)
        var luaNames = ModRepository.EnumerateLuaCardScriptFiles(mod.DirectoryPath)
            .Select(f => Path.GetFileName(f))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var cf in ModRepository.EnumerateDeclarativeCardFiles(mod.DirectoryPath))
        {
            try
            {
                var node = ModPatcher.ParseJson(File.ReadAllText(cf, Encoding.UTF8));
                var objs = node is JsonArray arr ? arr.OfType<JsonObject>() : node is JsonObject o ? [o] : [];
                foreach (var c in objs)
                {
                    var id = c["id"]?.ToString();
                    var typeMask = c["data"]?["type"] is JsonValue tv && tv.TryGetValue<int>(out var tm) ? tm : 0;
                    var isNormalMonster = (typeMask & 16) != 0 && (typeMask & 32) == 0;
                    if (!string.IsNullOrEmpty(id) && !isNormalMonster && !luaNames.Contains($"c{id}.lua"))
                    {
                        notes.Add($"Card {id} ('{c["name"]}') has no lua/cards/c{id}.lua script yet (may rely on vanilla script).");
                    }
                }
            }
            catch { }
        }

        // 3. Check conflicts with other enabled mods
        var status = engine.GetStatus();
        var modConflicts = status.Conflicts.Where(c => c.ModIds.Contains(mod.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var c in modConflicts)
        {
            notes.Add($"Conflict [{c.Category}] {c.ResourceKey}: {string.Join(" -> ", c.ModIds)} (winner: {c.WinnerModId})");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Validation for MOD '{mod.Id}' ({mod.Manifest.Name} v{mod.Manifest.Version}):");
        sb.AppendLine(issues.Count == 0 ? "Status: VALID (0 errors)" : $"Status: INVALID ({issues.Count} errors)");
        foreach (var err in issues) sb.AppendLine("  [ERROR] " + err);
        foreach (var note in notes) sb.AppendLine("  [NOTE] " + note);
        return sb.ToString().TrimEnd();
    }

    private static async Task<string> HandleApplyOrRestoreAsync(ModInjectionEngine engine, JsonObject args, CancellationToken ct)
    {
        var action = RequireString(args, "action").ToLowerInvariant();
        var log = new BufferingInteraction();

        if (action == "restore")
        {
            await engine.RestoreVanillaAsync(log, forceKillRunningGame: false, ct);
        }
        else if (action is "apply" or "build_only")
        {
            InjectionMode? mode = args["mode"]?.GetValue<string>()?.ToLowerInvariant() switch
            {
                "manifest" => InjectionMode.LauncherManifest,
                "virtual" => InjectionMode.VirtualBundle,
                _ => null
            };
            var buildOnlyDir = action == "build_only"
                ? (args["buildOnlyDir"]?.GetValue<string>() ?? Path.Combine(Path.GetTempPath(), "ygo-mod-mcp-build"))
                : args["buildOnlyDir"]?.GetValue<string>();

            await engine.ApplyAsync(new ApplyModsOptions
            {
                ModeOverride = mode,
                BuildOnlyDir = buildOnlyDir,
                LaunchGameAfter = args["launch"]?.GetValue<bool>() ?? false,
                DeveloperModeOverride = args["developerMode"]?.GetValue<bool>(),
                IgnoreVersionRequirements = args["ignoreVersionRequirements"]?.GetValue<bool>() ?? false,
                AssumeYes = true
            }, log, ct);
        }
        else
        {
            throw new ModOperationException($"Unknown action '{action}'. Expected apply, build_only, or restore.");
        }

        return string.Join("\n", log.Lines);
    }

    private static ManagedMod ResolveExistingMod(ModInjectionEngine engine, string modId)
    {
        var mods = engine.Repository.LoadMods();
        return mods.FirstOrDefault(m => string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ModOperationException($"MOD '{modId}' not found. Call create_mod first.");
    }

    private static string ResolveSafeModPath(string modDir, string relativePath)
    {
        var fullRoot = Path.GetFullPath(modDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullTarget = Path.GetFullPath(Path.Combine(modDir, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullTarget.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ModOperationException($"Path traversal blocked: '{relativePath}' escapes MOD directory.");
        }

        return fullTarget;
    }

    private static void UpsertByKey(JsonArray array, string keyName, string keyVal, JsonObject newObj)
    {
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is JsonObject existing &&
                string.Equals(existing[keyName]?.ToString(), keyVal, StringComparison.OrdinalIgnoreCase))
            {
                array[i] = newObj;
                return;
            }
        }

        array.Add((JsonNode?)newObj);
    }

    private static string SanitizeJsonFileName(string? raw, string fallback)
    {
        var name = string.IsNullOrWhiteSpace(raw) ? fallback : Path.GetFileName(raw.Trim());
        if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name += ".json";
        return name;
    }

    private static string RequireString(JsonObject args, string key) =>
        args[key]?.GetValue<string>() is { Length: > 0 } s
            ? s
            : throw new ModOperationException($"Missing required string parameter '{key}'.");

    private static int RequireInt(JsonObject args, string key) =>
        args[key] is JsonValue v && v.TryGetValue<int>(out var n)
            ? n
            : throw new ModOperationException($"Missing required integer parameter '{key}'.");

    private static int TryGetInt(JsonObject args, string key, int fallback) =>
        args[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : fallback;

    private static readonly System.Text.Json.JsonSerializerOptions PrettyNodeOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static JsonObject Tool(string name, string description, JsonObject properties, string[]? required = null)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties
        };
        if (required is { Length: > 0 })
        {
            schema["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        }

        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema
        };
    }

    private static JsonObject Obj(params (string Key, JsonObject Value)[] props)
    {
        var obj = new JsonObject();
        foreach (var (k, v) in props) obj[k] = v;
        return obj;
    }

    private static JsonObject Str(string desc) => new() { ["type"] = "string", ["description"] = desc };
    private static JsonObject Int(string desc) => new() { ["type"] = "integer", ["description"] = desc };
    private static JsonObject Num(string desc) => new() { ["type"] = "number", ["description"] = desc };
    private static JsonObject Bool(string desc) => new() { ["type"] = "boolean", ["description"] = desc };
    private static JsonObject StrArray(string desc) => new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = desc };
    private static JsonObject IntArray(string desc) => new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" }, ["description"] = desc };
}
