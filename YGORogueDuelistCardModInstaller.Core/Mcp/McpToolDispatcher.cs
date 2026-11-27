using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using YGORogueDuelistCardModInstaller.Core.ModManager;

namespace YGORogueDuelistCardModInstaller.Core.Mcp;

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
        // === Live Studio Focus & Script Authoring ===
        Tool("get_studio_context", "Get what the user is currently viewing/editing in the Desktop MOD Studio (active workspace, MOD ID, selected card/character/relic, open Lua/JS/DSL script path & current content).",
            Obj(("includeScriptContent", Bool("Include full content of the open script file if available (default true)")))),

        Tool("list_mod_files", "List files inside mods/<modId>/ (Lua scripts, JS plugins, CSS, JSON data, DSL files) with relative paths and sizes.",
            Obj(
                ("modId", Str("Target managed MOD ID")),
                ("subdir", Str("Optional subdirectory filter (e.g. 'lua/cards', 'js', 'cards')")),
                ("extension", Str("Optional file extension filter (e.g. '.lua', '.js', '.json')")),
                ("root", Str("Optional workspace root path"))),
            ["modId"]),

        Tool("get_vanilla_card_script", "Inspect any vanilla game card by numeric ID or English/Chinese name, returning its stats, EN/ZH card text, and exact official ocgcore-wasm Lua script (c<id>.lua) as a reference.",
            Obj(
                ("query", Str("Card ID (e.g. '55144522' or '46986414') or English/Chinese name (e.g. 'Pot of Greed', '黑魔术师')")),
                ("root", Str("Optional workspace root path"))),
            ["query"]),

        Tool("read_mod_file", "Read a text/script file inside mods/<modId>/.",
            Obj(
                ("modId", Str("Target managed MOD ID")),
                ("relativePath", Str("Relative path inside mod folder (e.g. 'lua/cards/c24068492.lua')")),
                ("root", Str("Optional workspace root path"))),
            ["modId", "relativePath"]),

        Tool("write_mod_file", "Write or overwrite any text/script file inside mods/<modId>/ (Lua scripts, Lua libs, JS plugins, CSS styles, campaign .py, mod.lua, mod.js, JSON). Automatically refreshes Studio & Hot-Reload.",
            Obj(
                ("modId", Str("Target managed MOD ID")),
                ("relativePath", Str("Relative path inside mod folder, e.g. 'lua/cards/c190099001.lua' or 'js/main.js'")),
                ("content", Str("Full UTF-8 file content")),
                ("root", Str("Optional workspace root path"))),
            ["modId", "relativePath", "content"]),

        Tool("patch_mod_file", "Targeted search-and-replace edit on a Lua/JS/CSS/DSL/JSON file inside mods/<modId>/. Safer and faster than rewriting whole scripts; auto-syncs Studio & Hot-Reload.",
            Obj(
                ("modId", Str("Target managed MOD ID")),
                ("relativePath", Str("Relative path inside mod folder, e.g. 'lua/cards/c24068492.lua'")),
                ("search", Str("Exact text snippet to find in the file")),
                ("replace", Str("Replacement text")),
                ("replaceAll", Bool("Replace all occurrences (default false: requires unique match)")),
                ("root", Str("Optional workspace root path"))),
            ["modId", "relativePath", "search", "replace"]),

        Tool("delete_mod_file", "Delete a file inside mods/<modId>/ safely.",
            Obj(
                ("modId", Str("Target managed MOD ID")),
                ("relativePath", Str("Relative path inside mod folder")),
                ("root", Str("Optional workspace root path"))),
            ["modId", "relativePath"]),

        Tool("trigger_hot_reload", "Trigger (or start) live MOD Hot-Reload in the Desktop Studio so script/card edits appear immediately in the embedded preview or desktop game.",
            Obj(
                ("reason", Str("Optional change summary (e.g. 'Edited c24068492.lua')")),
                ("startIfStopped", Bool("Start Hot-Reload server if not already running (default true)")))),

        // === Guides, Status & Vanilla Data Search ===
        Tool("get_mod_guide", "Get official Mod Spec v1 guides & schemas for creating cards, characters, relics, packs, decks, trainers, campaign/removals, relic system deep-dive, in-game MOD settings, Xmake-style Lua/JS DSL, Lua scripts, JS plugins, Post-Synchro modern era (Synchro/Xyz/Pendulum/Link), or Linux/Android/Delta packaging.",
            Obj(("topic", Str("Topic: overview | dsl | settings | card | character | relic | relic_deep | campaign | removals | pack | deck | trainer | lua | js | bitmasks | modern_era | platforms")))),

        Tool("get_manager_status", "Inspect the MOD manager status: detected game version, sync state, all managed mods (with category, cover, auto-imported entries & settings count), load order, and conflicts.",
            Obj(("root", Str("Optional workspace root path")))),

        Tool("search_game_data", "Search vanilla game content.json entities (cards, characters, relics, or packs) by English/Chinese name or ID.",
            Obj(
                ("kind", Str("Entity kind: cards | characters | relics | packs")),
                ("query", Str("Optional substring filter (supports EN, ZH, ID, key)")),
                ("limit", Int("Max results (default 25, max 100)")),
                ("root", Str("Optional workspace root path"))),
            ["kind"]),

        // === Declarative MOD Authoring ===
        Tool("create_mod", "Scaffold a brand-new standard v1 MOD project under mods/<id>/ (supports 'basic' template or 'complete' full showcase example).",
            Obj(
                ("id", Str("Unique mod ID slug (e.g. my-dragon-pack)")),
                ("name", Str("Display name")),
                ("author", Str("Author name")),
                ("description", Str("Short description")),
                ("category", Str("Optional category: cards | characters | relics | packs_decks | ui_theme | script_plugin | trainer | overhaul | other")),
                ("template", Str("Optional template type: basic (default) | complete (full showcase MOD with cover, cards, Lua, relic, character, pack, deck, auto-imported JS/CSS & F9 settings)")),
                ("root", Str("Optional workspace root path"))),
            ["id"]),

        Tool("update_mod_metadata", "Update a MOD's category, cover, name, version, author, description, tags, minGameVersion, or autoDiscoverEntries flag.",
            Obj(
                ("modId", Str("Target managed MOD ID")),
                ("name", Str("Optional new display name")),
                ("version", Str("Optional new version")),
                ("author", Str("Optional new author")),
                ("description", Str("Optional new description")),
                ("category", Str("Optional category: cards | characters | relics | packs_decks | ui_theme | script_plugin | trainer | overhaul | other | auto")),
                ("cover", Str("Optional cover path (relative path inside mod folder or local image path to copy as cover.<ext>)")),
                ("minGameVersion", Str("Optional minimum required game version")),
                ("autoDiscoverEntries", Bool("Auto-import all js/**, styles/**, css/**, lua/lib/** entries (default true)")),
                ("tags", StrArray("Optional replacement tag list")),
                ("root", Str("Optional workspace root path"))),
            ["modId"]),

        Tool("import_ypk", "Import a YGOPro / EDOPro / MDPro3 .ypk expansion archive (.cdb SQLite cards, script/c*.lua, pics/*, .ydk) into a standard Mod Spec v1 MOD.",
            Obj(
                ("ypkPath", Str("Full path to local .ypk file")),
                ("modId", Str("Optional custom MOD ID slug (defaults to filename)")),
                ("overwrite", Bool("Overwrite if MOD ID already exists (default true)")),
                ("root", Str("Optional workspace root path"))),
            ["ypkPath"]),

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

        // === Validation, Packaging & Injection ===
        Tool("validate_mod", "Validate a managed MOD's manifest, JSON files, card/Lua script pairing, and cross-mod conflicts.",
            Obj(
                ("modId", Str("Target managed MOD ID")),
                ("root", Str("Optional workspace root path"))),
            ["modId"]),

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

        Tool("organize_mods", "Organize the user's MOD checklist by assigning categories (standard or custom Chinese/English categories) and optimal load order. Pass mode='smart' for automatic local rule-based sorting, or pass explicit 'items' ([{id, category}]) to apply AI-designed organization directly.",
            Obj(
                ("mode", Str("smart (default auto-categorize & sort) | custom (apply provided items list)")),
                ("apply", Bool("Whether to apply immediately (default true)")),
                ("items", new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Optional ordered array of {id, category} objects to apply custom AI organization",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["id"] = Str("Exact managed MOD ID"),
                            ["category"] = Str("Category slug or custom display name")
                        }
                    }
                }),
                ("root", Str("Optional workspace root path")))),

        Tool("apply_or_restore_mods", "Apply enabled mods to the game (default zero-touch VirtualBundle mode), dry-run BuildOnly export, or restore vanilla.",
            Obj(
                ("action", Str("Action: apply | build_only | restore")),
                ("mode", Str("Optional mode for apply: virtual (default, zero-touch) | manifest")),
                ("developerMode", Bool("Enable in-game Developer Mode (F10 Dev/Debug, F8 Trainer, Sandbox unlock)")),
                ("ignoreVersionRequirements", Bool("Ignore minGameVersion/minManagerVersion checks")),
                ("buildOnlyDir", Str("Output directory when action is build_only")),
                ("launch", Bool("Launch game after apply (default false)")),
                ("root", Str("Optional workspace root path"))),
            ["action"])
    );

    public static async Task<string> ExecuteToolAsync(
        string name,
        JsonObject args,
        CancellationToken ct,
        McpHostHooks? hooks = null)
    {
        var sw = Stopwatch.StartNew();
        var explicitRoot = args["root"]?.GetValue<string>();
        var effectiveRoot = !string.IsNullOrWhiteSpace(explicitRoot)
            ? explicitRoot
            : hooks?.GetDefaultWorkspaceRoot?.Invoke();
        var engine = new ModInjectionEngine(effectiveRoot);

        string output;
        var ok = true;
        try
        {
            output = name switch
            {
                "get_studio_context" => HandleGetStudioContext(engine, args, hooks),
                "list_mod_files" => HandleListModFiles(engine, args),
                "get_vanilla_card_script" => HandleGetVanillaCardScript(engine, args),
                "patch_mod_file" => HandlePatchFile(engine, args, hooks),
                "delete_mod_file" => HandleDeleteFile(engine, args, hooks),
                "trigger_hot_reload" => await HandleTriggerHotReloadAsync(args, hooks, ct),
                "get_mod_guide" => ModKnowledgeBase.GetGuide(args["topic"]?.GetValue<string>()),
                "get_manager_status" => HandleStatus(engine),
                "search_game_data" => HandleSearchGameData(engine, args),
                "create_mod" => HandleCreateMod(engine, args, hooks),
                "update_mod_metadata" => HandleUpdateModMetadata(engine, args, hooks),
                "import_ypk" => HandleImportYpk(engine, args, hooks),
                "add_mod_card" => HandleAddCard(engine, args, hooks),
                "add_mod_character" => HandleAddCharacter(engine, args, hooks),
                "add_mod_relic" => HandleAddRelic(engine, args, hooks),
                "add_mod_pack" => HandleAddPack(engine, args, hooks),
                "add_mod_preconstructed_deck" => HandleAddPreconstructedDeck(engine, args, hooks),
                "pack_mod_or_workspace" => HandlePack(engine, args),
                "configure_mod_trainer" => HandleConfigureTrainer(engine, args, hooks),
                "write_mod_file" => HandleWriteFile(engine, args, hooks),
                "read_mod_file" => HandleReadFile(engine, args),
                "validate_mod" => HandleValidateMod(engine, args),
                "organize_mods" => HandleOrganizeMods(engine, args, hooks),
                "apply_or_restore_mods" => await HandleApplyOrRestoreAsync(engine, args, ct),
                _ => throw new ModOperationException($"Unknown MCP tool: {name}")
            };
        }
        catch (Exception ex)
        {
            ok = false;
            output = $"Error: {ex.Message}";
            RecordToolCall(hooks, name, args, ok, sw.ElapsedMilliseconds, output);
            throw;
        }

        RecordToolCall(hooks, name, args, ok, sw.ElapsedMilliseconds, output);
        return output;
    }

    private static void RecordToolCall(McpHostHooks? hooks, string name, JsonObject args, bool ok, long elapsedMs, string output)
    {
        if (hooks?.OnToolCalled is null) return;
        var argsText = args.ToJsonString(ModPatcher.JsonOptions);
        if (argsText.Length > 240) argsText = argsText[..240] + "…";
        var preview = output.Length > 320 ? output[..320] + "…" : output;
        hooks.OnToolCalled(new McpCallRecord(
            Guid.NewGuid().ToString("N")[..8],
            name,
            argsText,
            ok,
            elapsedMs,
            preview,
            DateTimeOffset.UtcNow));
    }

    private static string HandleGetStudioContext(ModInjectionEngine engine, JsonObject args, McpHostHooks? hooks)
    {
        var includeContent = args["includeScriptContent"]?.GetValue<bool>() ?? true;
        var focus = hooks?.GetStudioFocus?.Invoke();
        var status = engine.GetStatus();

        var obj = new JsonObject
        {
            ["hostedInDesktopStudio"] = hooks is not null,
            ["workspaceRoot"] = focus?.WorkspaceRoot ?? status.WorkspaceRoot,
            ["baseGameVersion"] = status.ActiveBaseGameVersion,
            ["modId"] = focus?.ModId ?? status.Mods.FirstOrDefault()?.Id,
            ["modName"] = focus?.ModName ?? status.Mods.FirstOrDefault()?.Manifest.Name,
            ["category"] = focus?.Category,
            ["itemKey"] = focus?.ItemKey,
            ["itemName"] = focus?.ItemName,
            ["scriptPath"] = focus?.ScriptPath,
            ["updatedAtUtc"] = focus?.UpdatedAtUtc.ToString("O")
        };

        var targetModId = obj["modId"]?.GetValue<string>();
        var scriptPath = focus?.ScriptPath;
        if (includeContent && !string.IsNullOrWhiteSpace(targetModId) && !string.IsNullOrWhiteSpace(scriptPath))
        {
            try
            {
                var mod = ResolveExistingMod(engine, targetModId);
                var full = ResolveSafeModPath(mod.DirectoryPath, scriptPath);
                if (File.Exists(full))
                {
                    obj["scriptExists"] = true;
                    obj["scriptContent"] = File.ReadAllText(full, Encoding.UTF8);
                }
                else
                {
                    obj["scriptExists"] = false;
                }
            }
            catch
            {
                obj["scriptExists"] = false;
            }
        }

        return obj.ToJsonString(PrettyNodeOptions);
    }

    private static string HandleListModFiles(ModInjectionEngine engine, JsonObject args)
    {
        var mod = ResolveExistingMod(engine, RequireString(args, "modId"));
        var subdir = args["subdir"]?.GetValue<string>()?.Trim();
        var ext = args["extension"]?.GetValue<string>()?.Trim();

        var searchRoot = string.IsNullOrWhiteSpace(subdir)
            ? mod.DirectoryPath
            : ResolveSafeModPath(mod.DirectoryPath, subdir);

        if (!Directory.Exists(searchRoot))
        {
            return new JsonObject
            {
                ["modId"] = mod.Id,
                ["count"] = 0,
                ["files"] = new JsonArray()
            }.ToJsonString(PrettyNodeOptions);
        }

        var filesArr = new JsonArray();
        foreach (var file in Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(ext) &&
                !file.EndsWith(ext.StartsWith('.') ? ext : "." + ext, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rel = Path.GetRelativePath(mod.DirectoryPath, file).Replace('\\', '/');
            var fi = new FileInfo(file);
            filesArr.Add((JsonNode?)new JsonObject
            {
                ["path"] = rel,
                ["sizeBytes"] = fi.Length,
                ["modifiedUtc"] = fi.LastWriteTimeUtc.ToString("O")
            });
            if (filesArr.Count >= 500) break;
        }

        return new JsonObject
        {
            ["modId"] = mod.Id,
            ["directory"] = mod.DirectoryPath,
            ["count"] = filesArr.Count,
            ["files"] = filesArr
        }.ToJsonString(PrettyNodeOptions);
    }

    private static string HandleGetVanillaCardScript(ModInjectionEngine engine, JsonObject args)
    {
        var query = RequireString(args, "query").Trim();
        var status = engine.GetStatus();
        var vanilla = ModInjectionEngine.FindBestVanillaSource(status.InstallDir, status.GameBaseDir, status.LauncherMeta)
            ?? throw new ModOperationException("Could not locate vanilla web-X.Y.Z.zip on this machine.");

        using var zip = ZipFile.OpenRead(vanilla.ZipPath);
        var content = ModPatcher.ParseJson(ModPatcher.ReadZipText(zip, "content.json")).AsObject();
        JsonObject? zhCards = null;
        if (zip.GetEntry("i18n/zh.json") is not null)
        {
            try { zhCards = ModPatcher.ParseJson(ModPatcher.ReadZipText(zip, "i18n/zh.json")).AsObject()["cards"] as JsonObject; }
            catch { }
        }

        JsonObject? matched = null;
        var numeric = int.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wantedId);
        foreach (var c in content["cards"]!.AsArray().OfType<JsonObject>())
        {
            var cid = c["id"] is JsonValue iv && iv.TryGetValue<int>(out var n) ? n : 0;
            var enName = c["name"]?.GetValue<string>() ?? "";
            var zhArr = zhCards?[cid.ToString(CultureInfo.InvariantCulture)] as JsonArray;
            var zhName = zhArr is { Count: > 0 } ? zhArr[0]?.GetValue<string>() ?? "" : "";

            if ((numeric && cid == wantedId) ||
                enName.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                zhName.Equals(query, StringComparison.OrdinalIgnoreCase))
            {
                matched = c;
                break;
            }
            if (matched is null &&
                (enName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 zhName.Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                matched = c;
            }
        }

        if (matched is null)
        {
            throw new ModOperationException($"No vanilla card matching '{query}' was found.");
        }

        var id = matched["id"] is JsonValue idv && idv.TryGetValue<int>(out var idNum) ? idNum : 0;
        var idStr = id.ToString(CultureInfo.InvariantCulture);
        var zhEntry = zhCards?[idStr] as JsonArray;

        string? luaScript = null;
        var scriptKey = $"c{idStr}.lua";
        if (zip.GetEntry("scripts-0.json") is not null)
        {
            var scripts = ModPatcher.ParseJson(ModPatcher.ReadZipText(zip, "scripts-0.json")).AsObject();
            if (scripts[scriptKey] is JsonValue sv && sv.TryGetValue<string>(out var sText))
            {
                luaScript = sText;
            }
        }

        return new JsonObject
        {
            ["gameVersion"] = vanilla.Version,
            ["id"] = id,
            ["name"] = matched["name"]?.DeepClone(),
            ["zhName"] = zhEntry is { Count: > 0 } ? zhEntry[0]?.DeepClone() : null,
            ["type"] = matched["type"]?.DeepClone(),
            ["race"] = matched["race"]?.DeepClone(),
            ["attribute"] = matched["attribute"]?.DeepClone(),
            ["level"] = matched["level"]?.DeepClone(),
            ["atk"] = matched["atk"]?.DeepClone(),
            ["defense"] = matched["defense"]?.DeepClone(),
            ["desc"] = matched["desc"]?.DeepClone(),
            ["zhDesc"] = zhEntry is { Count: > 1 } ? zhEntry[1]?.DeepClone() : null,
            ["data"] = matched["data"]?.DeepClone(),
            ["scriptKey"] = scriptKey,
            ["luaScript"] = luaScript ?? "-- (No Lua script: likely a Normal Monster or alias)"
        }.ToJsonString(PrettyNodeOptions);
    }

    private static string HandlePatchFile(ModInjectionEngine engine, JsonObject args, McpHostHooks? hooks)
    {
        var mod = ResolveExistingMod(engine, RequireString(args, "modId"));
        var rel = RequireString(args, "relativePath");
        var search = RequireString(args, "search");
        var replace = args["replace"]?.GetValue<string>() ?? string.Empty;
        var replaceAll = args["replaceAll"]?.GetValue<bool>() ?? false;

        var fullPath = ResolveSafeModPath(mod.DirectoryPath, rel);
        if (!File.Exists(fullPath)) throw new ModOperationException($"File not found: {rel}");

        var original = File.ReadAllText(fullPath, Encoding.UTF8);
        var firstIdx = original.IndexOf(search, StringComparison.Ordinal);
        if (firstIdx < 0)
        {
            throw new ModOperationException($"Search text not found in '{rel}'. Call read_mod_file first to inspect exact content.");
        }

        int count;
        string updated;
        if (replaceAll)
        {
            count = 0;
            var pos = 0;
            var sb = new StringBuilder(original.Length);
            while (true)
            {
                var idx = original.IndexOf(search, pos, StringComparison.Ordinal);
                if (idx < 0)
                {
                    sb.Append(original, pos, original.Length - pos);
                    break;
                }
                sb.Append(original, pos, idx - pos);
                sb.Append(replace);
                pos = idx + search.Length;
                count++;
            }
            updated = sb.ToString();
        }
        else
        {
            var secondIdx = original.IndexOf(search, firstIdx + search.Length, StringComparison.Ordinal);
            if (secondIdx >= 0)
            {
                throw new ModOperationException($"Multiple matches found in '{rel}'. Provide more surrounding context in 'search' or pass replaceAll=true.");
            }
            count = 1;
            updated = original[..firstIdx] + replace + original[(firstIdx + search.Length)..];
        }

        File.WriteAllText(fullPath, updated, ModPatcher.Utf8NoBom);
        hooks?.OnModMutated?.Invoke(mod.Id, rel.Replace('\\', '/'), "patch_mod_file");
        return $"Patched '{rel}' in MOD '{mod.Id}' ({count} replacement(s), {updated.Length} chars).";
    }

    private static string HandleDeleteFile(ModInjectionEngine engine, JsonObject args, McpHostHooks? hooks)
    {
        var mod = ResolveExistingMod(engine, RequireString(args, "modId"));
        var rel = RequireString(args, "relativePath");
        var fullPath = ResolveSafeModPath(mod.DirectoryPath, rel);
        if (!File.Exists(fullPath)) throw new ModOperationException($"File not found: {rel}");
        File.Delete(fullPath);
        hooks?.OnModMutated?.Invoke(mod.Id, rel.Replace('\\', '/'), "delete_mod_file");
        return $"Deleted '{rel}' from MOD '{mod.Id}'.";
    }

    private static async Task<string> HandleTriggerHotReloadAsync(JsonObject args, McpHostHooks? hooks, CancellationToken ct)
    {
        if (hooks?.OnTriggerHotReloadAsync is null)
        {
            return "Hot-Reload bridge is only available when connected to the Desktop Studio's built-in MCP server (http://127.0.0.1:17850/mcp).";
        }
        var reason = args["reason"]?.GetValue<string>() ?? "MCP trigger_hot_reload";
        var startFlag = (args["startIfStopped"]?.GetValue<bool>() ?? true) ? "start" : "reload";
        return await hooks.OnTriggerHotReloadAsync(startFlag, reason, ct);
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
            var resolvedJs = ModRepository.ResolveJsEntries(m.DirectoryPath, m.Manifest);
            var resolvedCss = ModRepository.ResolveCssEntries(m.DirectoryPath, m.Manifest);
            var resolvedLuaLibs = ModRepository.ResolveLuaLibs(m.DirectoryPath, m.Manifest);
            var settingsCount = ModRepository.ResolveSettingsSchema(m.DirectoryPath, m.Manifest)?.Count ?? 0;
            modsArr.Add((JsonNode?)new JsonObject
            {
                ["id"] = m.Id,
                ["name"] = m.Manifest.Name,
                ["version"] = m.Manifest.Version,
                ["category"] = m.Manifest.Category,
                ["effectiveCategory"] = ModRepository.ResolveEffectiveCategory(m.Manifest, m.Stats),
                ["cover"] = m.Manifest.Cover,
                ["autoDiscoverEntries"] = m.Manifest.AutoDiscoverEntries,
                ["settingsCount"] = settingsCount,
                ["enabled"] = m.Enabled,
                ["order"] = m.Order + 1,
                ["directory"] = m.DirectoryPath,
                ["resolvedJsEntries"] = new JsonArray(resolvedJs.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                ["resolvedCssEntries"] = new JsonArray(resolvedCss.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                ["resolvedLuaLibs"] = new JsonArray(resolvedLuaLibs.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
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
        JsonObject? zhCards = null;
        JsonObject? zhUi = null;
        if (zip.GetEntry("i18n/zh.json") is not null)
        {
            try
            {
                var zh = ModPatcher.ParseJson(ModPatcher.ReadZipText(zip, "i18n/zh.json")).AsObject();
                zhCards = zh["cards"] as JsonObject;
                zhUi = zh["ui"] as JsonObject;
            }
            catch { }
        }

        var results = new JsonArray();

        if (kind == "cards")
        {
            foreach (var c in content["cards"]!.AsArray().OfType<JsonObject>())
            {
                var name = c["name"]?.GetValue<string>() ?? "";
                var id = c["id"]?.ToString() ?? "";
                var zhArr = zhCards?[id] as JsonArray;
                var zhName = zhArr is { Count: > 0 } ? zhArr[0]?.GetValue<string>() ?? "" : "";
                if (!string.IsNullOrEmpty(query) &&
                    !name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !zhName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !id.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;

                results.Add((JsonNode?)new JsonObject
                {
                    ["id"] = c["id"]?.DeepClone(),
                    ["name"] = name,
                    ["zhName"] = string.IsNullOrEmpty(zhName) ? null : zhName,
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
                var zhName = zhUi?[name]?.GetValue<string>() ?? "";
                if (!string.IsNullOrEmpty(query) &&
                    !name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !zhName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !i.ToString(CultureInfo.InvariantCulture).Equals(query, StringComparison.Ordinal))
                    continue;

                results.Add((JsonNode?)new JsonObject
                {
                    ["index"] = i,
                    ["playable"] = playable.Contains(i),
                    ["name"] = name,
                    ["zhName"] = string.IsNullOrEmpty(zhName) ? null : zhName,
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
                var zhName = zhUi?[name]?.GetValue<string>() ?? "";
                var desc = arr?.Count > 2 ? arr[2]?.GetValue<string>() ?? "" : "";
                if (!string.IsNullOrEmpty(query) &&
                    !k.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !zhName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !desc.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;

                results.Add((JsonNode?)new JsonObject
                {
                    ["key"] = k,
                    ["name"] = name,
                    ["zhName"] = string.IsNullOrEmpty(zhName) ? null : zhName,
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
                var zhName = zhUi?[name]?.GetValue<string>() ?? "";
                if (!string.IsNullOrEmpty(query) &&
                    !id.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !zhName.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;

                results.Add((JsonNode?)new JsonObject
                {
                    ["id"] = id,
                    ["name"] = name,
                    ["zhName"] = string.IsNullOrEmpty(zhName) ? null : zhName,
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

    private static string HandleCreateMod(ModInjectionEngine engine, JsonObject args, McpHostHooks? hooks)
    {
        var id = RequireString(args, "id");
        var status = engine.GetStatus();
        var template = args["template"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        var created = template == "complete"
            ? engine.Repository.CreateCompleteExampleMod(id, status.ActiveBaseGameVersion)
            : engine.Repository.CreateTemplateMod(
                id,
                args["name"]?.GetValue<string>(),
                args["author"]?.GetValue<string>(),
                args["description"]?.GetValue<string>(),
                status.ActiveBaseGameVersion,
                category: args["category"]?.GetValue<string>());
        hooks?.OnModMutated?.Invoke(created.Id, "mod-manifest.json", "create_mod");
        return $"Created mod '{created.Id}' ({template ?? "basic"}) at {created.DirectoryPath}. Next: call add_mod_card / add_mod_character / add_mod_relic / configure_mod_trainer / write_mod_file, then trigger_hot_reload or apply_or_restore_mods(action='apply').";
    }

    private static string HandleUpdateModMetadata(ModInjectionEngine engine, JsonObject args, McpHostHooks? hooks)
    {
        var modId = RequireString(args, "modId");
        List<string>? tags = args["tags"] is JsonArray tArr
            ? tArr.Select(x => x?.GetValue<string>() ?? string.Empty).Where(s => s.Length > 0).ToList()
            : null;
        bool? autoEntries = args["autoDiscoverEntries"] is JsonValue av && av.TryGetValue<bool>(out var ab) ? ab : null;

        var updated = engine.Repository.UpdateModMetadata(
            modId,
            name: args["name"]?.GetValue<string>(),
            version: args["version"]?.GetValue<string>(),
            author: args["author"]?.GetValue<string>(),
            description: args["description"]?.GetValue<string>(),
            category: args["category"]?.GetValue<string>(),
            minGameVersion: args["minGameVersion"]?.GetValue<string>(),
            tags: tags,
            autoDiscoverEntries: autoEntries,
            coverSourcePath: args["cover"]?.GetValue<string>());

        hooks?.OnModMutated?.Invoke(updated.Id, "mod-manifest.json", "update_mod_metadata");
        return $"Updated metadata for MOD '{updated.Id}' (category={ModRepository.ResolveEffectiveCategory(updated.Manifest, updated.Stats)}, cover={updated.Manifest.Cover ?? "auto"}, autoDiscoverEntries={updated.Manifest.AutoDiscoverEntries}).";
    }

    private static string HandleImportYpk(ModInjectionEngine engine, JsonObject args, McpHostHooks? hooks)
    {
        var ypkPath = RequireString(args, "ypkPath");
        var modId = args["modId"]?.GetValue<string>();
        var overwrite = args["overwrite"]?.GetValue<bool>() ?? true;
        var res = YpkImporter.ImportYpk(engine.Repository, ypkPath, overwrite, modId);
        hooks?.OnModMutated?.Invoke(res.Mod.Id, "cards/ypk-cards.json", "import_ypk");
        return $"Imported .ypk into MOD '{res.Mod.Id}' ({res.CdbCount} .cdb, {res.CardCount} cards, {res.ScriptCount} Lua scripts, {res.LuaLibCount} Lua libs, {res.ArtCount} artworks, {res.DeckCount} .ydk decks) at {res.Mod.DirectoryPath}.";
    }

    private static string HandleAddCard(ModInjectionEngine engine, JsonObject args, McpHostHooks? hooks)
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

        hooks?.OnModMutated?.Invoke(mod.Id, luaPath is not null ? $"lua/cards/c{id}.lua" : $"cards/{fileName}", "add_mod_card");
        return luaPath is not null
            ? $"Added/updated card {id} ('{name}') in {cardFile} and Lua script in {luaPath}."
            : $"Added/updated card {id} ('{name}') in {cardFile}.";
    }

    private static string HandleAddCharacter(ModInjectionEngine engine, JsonObject args, McpHostHooks? hooks)
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
        hooks?.OnModMutated?.Invoke(mod.Id, $"characters/{fileName}", "add_mod_character");
        return $"Added/updated character '{name}' in {filePath}.";
    }

    private static string HandleAddRelic(ModInjectionEngine engine, JsonObject args, McpHostHooks? hooks)
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
        hooks?.OnModMutated?.Invoke(mod.Id, $"relics/{fileName}", "add_mod_relic");
        return $"Added/updated relic '{key}' ('{name}') in {filePath}.";
    }

    private static string HandleAddPack(ModInjectionEngine engine, JsonObject args, McpHostHooks? hooks)
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
        hooks?.OnModMutated?.Invoke(mod.Id, "packs/custom-packs.json", "add_mod_pack");
        return $"Added/updated booster pack '{id}' ('{name}') in {filePath}.";
    }

    private static string HandleAddPreconstructedDeck(ModInjectionEngine engine, JsonObject args, McpHostHooks? hooks)
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
        hooks?.OnModMutated?.Invoke(mod.Id, "preconstructed/custom-decks.json", "add_mod_preconstructed_deck");
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

    private static string HandleConfigureTrainer(ModInjectionEngine engine, JsonObject args, McpHostHooks? hooks)
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
        hooks?.OnModMutated?.Invoke(mod.Id, "trainer.json", "configure_mod_trainer");
        return $"Updated trainer configuration in {trainerPath}:\n{trainer.ToJsonString(PrettyNodeOptions)}";
    }

    private static string HandleWriteFile(ModInjectionEngine engine, JsonObject args, McpHostHooks? hooks)
    {
        var mod = ResolveExistingMod(engine, RequireString(args, "modId"));
        var rel = RequireString(args, "relativePath");
        var content = RequireString(args, "content");
        var fullPath = ResolveSafeModPath(mod.DirectoryPath, rel);

        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        File.WriteAllText(fullPath, content, ModPatcher.Utf8NoBom);
        hooks?.OnModMutated?.Invoke(mod.Id, rel.Replace('\\', '/'), "write_mod_file");
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

        var status = engine.GetStatus();
        var modConflicts = status.Conflicts
            .Where(c => c.ModIds.Any(id => string.Equals(id, mod.Id, StringComparison.OrdinalIgnoreCase)))
            .ToList();
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

    private static string HandleOrganizeMods(ModInjectionEngine engine, JsonObject args, McpHostHooks? hooks)
    {
        var state = engine.Repository.LoadState();
        var mods = engine.Repository.LoadMods(state);
        var conflicts = ModConflictAnalyzer.Analyze(mods);
        var apply = args["apply"] is not JsonValue av || !av.TryGetValue<bool>(out var ab) || ab;

        ModOrganizePlan plan;
        if (args["items"] is JsonArray itemsArr && itemsArr.Count > 0)
        {
            var byId = mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
            var list = new List<ModOrganizeItem>();
            foreach (var node in itemsArr)
            {
                if (node is not JsonObject obj) continue;
                var id = obj["id"]?.GetValue<string>()?.Trim() ?? obj["modId"]?.GetValue<string>()?.Trim() ?? string.Empty;
                if (!byId.TryGetValue(id, out var m)) continue;
                var oldCat = ModRepository.ResolveEffectiveCategory(m.Manifest, m.Stats);
                var newCat = ModRepository.NormalizeCategory(obj["category"]?.GetValue<string>()) ?? oldCat;
                list.Add(new ModOrganizeItem(m.Id, m.Manifest.Name, oldCat, newCat, m.Order, list.Count, "Organized by MCP AI"));
            }
            plan = new ModOrganizePlan("mcp", $"MCP AI organized {list.Count} MOD(s).", list, []);
        }
        else
        {
            plan = ModOrganizer.BuildLocalSmartPlan(mods, conflicts, preserveExistingCustomCategories: true);
        }

        if (apply)
        {
            ModOrganizer.ApplyPlan(engine.Repository, plan);
            hooks?.OnModMutated?.Invoke("*", null, "organize_mods");
        }

        var resArr = new JsonArray();
        foreach (var it in plan.Items)
        {
            resArr.Add((JsonNode?)new JsonObject
            {
                ["id"] = it.ModId,
                ["name"] = it.ModName,
                ["oldCategory"] = it.OldCategory,
                ["newCategory"] = it.NewCategory,
                ["oldOrder"] = it.OldOrder,
                ["newOrder"] = it.NewOrder,
                ["reason"] = it.Reason
            });
        }
        return new JsonObject
        {
            ["ok"] = true,
            ["applied"] = apply,
            ["summary"] = plan.Summary,
            ["items"] = resArr
        }.ToJsonString(PrettyNodeOptions);
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

    public static readonly System.Text.Json.JsonSerializerOptions PrettyNodeOptions = new()
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
