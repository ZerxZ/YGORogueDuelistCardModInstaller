using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace YGORogueDuelistCardModInstaller.Core.ModManager;

/// <summary>
/// Result of parsing Xmake-style dual-scope DSL files (mod.lua and/or mod.js):
/// - Description Scope: metadata, minGameVersion/minManagerVersion, wildcard asset declarations, inline card/relic/character/trainer blocks.
/// - Script Scope: inline Lua duel scripts/effects/helpers AND inline JS runtime hooks (on_load, on_fetch, on_ui_change, on_hotkey).
/// 100% pure Native AOT C# parser with zero external interpreter dependencies.
/// </summary>
public sealed class ParsedModDsl
{
    public bool HasLuaDsl { get; set; }
    public bool HasJsDsl { get; set; }

    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? GameVersion { get; set; }
    public string? MinGameVersion { get; set; }
    public string? MaxGameVersion { get; set; }
    public string? MinManagerVersion { get; set; }
    public List<string> Tags { get; } = [];

    // Wildcard path patterns relative to mod directory
    public List<string> CardGlobs { get; } = [];
    public List<string> CharacterGlobs { get; } = [];
    public List<string> RelicGlobs { get; } = [];
    public List<string> PackGlobs { get; } = [];
    public List<string> PreconstructedGlobs { get; } = [];
    public List<string> YdkGlobs { get; } = [];
    public List<string> LuaCardGlobs { get; } = [];
    public List<string> LuaLibGlobs { get; } = [];
    public List<string> JsGlobs { get; } = [];
    public List<string> CssGlobs { get; } = [];
    public List<string> ArtGlobs { get; } = [];
    public List<string> CampaignGlobs { get; } = [];
    public List<string> OverrideGlobs { get; } = [];
    public List<string> TrainerPaths { get; } = [];

    // Inline declared targets from card(...) / relic(...) / character(...) / pack(...) / preconstructed(...) / trainer() blocks
    public List<JsonObject> InlineCards { get; } = [];
    public Dictionary<string, string> InlineLuaCardScripts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> InlineLuaUtilityBlocks { get; } = [];
    public List<JsonObject> InlineRelics { get; } = [];
    public List<JsonObject> InlineCharacters { get; } = [];
    public List<JsonObject> InlinePacks { get; } = [];
    public List<JsonObject> InlinePreconstructedDecks { get; } = [];
    public JsonObject? InlineTrainer { get; set; }

    // Compiled JS Script Scope hooks extracted from mod.js (on_load / on_fetch / on_ui_change / on_hotkey)
    public List<string> JsLoadCallbacks { get; } = [];
    public List<(string MatcherRaw, string HandlerRaw)> JsFetchHooks { get; } = [];
    public List<string> JsUiCallbacks { get; } = [];
    public List<(string Hotkey, string HandlerRaw)> JsHotkeyCallbacks { get; } = [];
    public List<string> JsTopLevelImports { get; } = [];

    public bool HasGeneratedJsPlugin =>
        JsLoadCallbacks.Count > 0 || JsFetchHooks.Count > 0 || JsUiCallbacks.Count > 0 || JsHotkeyCallbacks.Count > 0;

    public string BuildGeneratedJsModule()
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated from mod.js Script Scope (Xmake-style JS DSL)");
        foreach (var imp in JsTopLevelImports)
        {
            sb.AppendLine(imp);
        }

        sb.AppendLine("export async function init(ctx) {");

        foreach (var (matcher, handler) in JsFetchHooks)
        {
            sb.AppendLine($"  ctx.onFetch({matcher}, async (response, url, init) => {{");
            sb.AppendLine($"    const __fn = ({handler});");
            sb.AppendLine("    return await __fn(response, url, ctx, init);");
            sb.AppendLine("  });");
        }

        foreach (var uiFn in JsUiCallbacks)
        {
            sb.AppendLine("  ctx.onUiChange((root) => {");
            sb.AppendLine($"    const __fn = ({uiFn});");
            sb.AppendLine("    __fn(root, ctx);");
            sb.AppendLine("  });");
        }

        foreach (var (hotkey, fn) in JsHotkeyCallbacks)
        {
            var hk = hotkey.Trim().ToLowerInvariant();
            var wantAlt = hk.Contains("alt+");
            var wantCtrl = hk.Contains("ctrl+") || hk.Contains("control+");
            var wantShift = hk.Contains("shift+");
            var key = hk.Split('+').Last().Trim();
            sb.AppendLine("  window.addEventListener('keydown', (e) => {");
            sb.AppendLine($"    if (!e.repeat && e.altKey === {(wantAlt ? "true" : "false")} && e.ctrlKey === {(wantCtrl ? "true" : "false")} && e.shiftKey === {(wantShift ? "true" : "false")} && e.key.toLowerCase() === '{key.Replace("'", "\\'")}') {{");
            sb.AppendLine("      e.preventDefault();");
            sb.AppendLine($"      const __fn = ({fn});");
            sb.AppendLine("      __fn(ctx, e);");
            sb.AppendLine("    }");
            sb.AppendLine("  });");
        }

        foreach (var loadFn in JsLoadCallbacks)
        {
            sb.AppendLine($"  await ({loadFn})(ctx);");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }
}

public static class ModDslParser
{
    public const string LuaDslFileName = "mod.lua";
    public const string JsDslFileName = "mod.js";
    public const string MjsDslFileName = "mod.mjs";

    public static ParsedModDsl? TryParseModDirectory(string modDir)
    {
        var luaPath = Path.Combine(modDir, LuaDslFileName);
        var jsPath = Path.Combine(modDir, JsDslFileName);
        if (!File.Exists(jsPath))
        {
            var mjsPath = Path.Combine(modDir, MjsDslFileName);
            if (File.Exists(mjsPath)) jsPath = mjsPath;
        }

        var hasLua = File.Exists(luaPath);
        var hasJs = File.Exists(jsPath);
        if (!hasLua && !hasJs)
        {
            return null;
        }

        var dsl = new ParsedModDsl { HasLuaDsl = hasLua, HasJsDsl = hasJs };
        if (hasLua)
        {
            ParseInto(File.ReadAllText(luaPath, Encoding.UTF8), isJs: false, dsl);
        }

        if (hasJs)
        {
            ParseInto(File.ReadAllText(jsPath, Encoding.UTF8), isJs: true, dsl);
        }

        return dsl;
    }

    public static void MergeIntoManifest(ParsedModDsl dsl, ModManifest manifest)
    {
        if (!string.IsNullOrWhiteSpace(dsl.Id)) manifest.Id = ModRepository.SanitizeId(dsl.Id);
        if (!string.IsNullOrWhiteSpace(dsl.Name)) manifest.Name = dsl.Name;
        if (!string.IsNullOrWhiteSpace(dsl.Version)) manifest.Version = dsl.Version;
        if (!string.IsNullOrWhiteSpace(dsl.Author)) manifest.Author = dsl.Author;
        if (!string.IsNullOrWhiteSpace(dsl.Description)) manifest.Description = dsl.Description;
        if (!string.IsNullOrWhiteSpace(dsl.GameVersion)) manifest.GameVersion = dsl.GameVersion;
        if (!string.IsNullOrWhiteSpace(dsl.MinGameVersion)) manifest.MinGameVersion = dsl.MinGameVersion;
        if (!string.IsNullOrWhiteSpace(dsl.MaxGameVersion)) manifest.MaxGameVersion = dsl.MaxGameVersion;
        if (!string.IsNullOrWhiteSpace(dsl.MinManagerVersion)) manifest.MinManagerVersion = dsl.MinManagerVersion;

        foreach (var tag in dsl.Tags)
        {
            if (!manifest.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                manifest.Tags.Add(tag);
            }
        }
    }

    public static IEnumerable<string> ExpandGlobs(string modDir, IReadOnlyList<string> patterns)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPattern in patterns)
        {
            var pattern = rawPattern.Trim().Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrWhiteSpace(pattern) || pattern.Contains("..", StringComparison.Ordinal))
            {
                continue;
            }

            if (!pattern.Contains('*') && !pattern.Contains('?'))
            {
                var exact = Path.Combine(modDir, pattern.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(exact) && seen.Add(exact))
                {
                    yield return exact;
                }

                continue;
            }

            var regexPattern = "^" + Regex.Escape(pattern)
                .Replace(@"\*\*/", "(?:.+/)?")
                .Replace(@"\*\*", ".*")
                .Replace(@"\*", "[^/]*")
                .Replace(@"\?", ".") + "$";
            var regex = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (!Directory.Exists(modDir)) continue;
            foreach (var file in Directory.EnumerateFiles(modDir, "*", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var rel = Path.GetRelativePath(modDir, file).Replace('\\', '/');
                if (regex.IsMatch(rel) && seen.Add(file))
                {
                    yield return file;
                }
            }
        }
    }

    private enum ScopeKind { Global, Card, Relic, Character, Pack, Preconstructed, Trainer }

    private static void ParseInto(string source, bool isJs, ParsedModDsl dsl)
    {
        if (isJs)
        {
            // Collect top-level ES import lines for generated script-scope JS module
            foreach (var line in source.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("import ", StringComparison.Ordinal))
                {
                    dsl.JsTopLevelImports.Add(trimmed);
                }
            }
        }

        var scope = ScopeKind.Global;
        JsonObject? currentCard = null;
        int currentCardId = 0;
        JsonObject? currentRelic = null;
        JsonObject? currentChar = null;
        JsonObject? currentPack = null;
        JsonObject? currentPrecon = null;

        var i = 0;
        var len = source.Length;
        while (i < len)
        {
            SkipTrivia(source, ref i, isJs);
            if (i >= len) break;

            // Skip top-level JS import statement line
            if (isJs && StartsWithKeyword(source, i, "import"))
            {
                while (i < len && source[i] != '\n') i++;
                continue;
            }

            if (!IsIdentStart(source[i]))
            {
                i++;
                continue;
            }

            var identStart = i;
            while (i < len && IsIdentChar(source[i])) i++;
            var ident = source[identStart..i];

            SkipTrivia(source, ref i, isJs);
            if (i >= len || source[i] != '(')
            {
                continue;
            }

            var argsSpan = ReadBalancedParenContent(source, ref i, isJs);
            var rawArgs = SplitTopLevelArgs(argsSpan, isJs);

            // Handle scope transitions (Xmake-style target blocks)
            switch (ident)
            {
                case "card":
                    if (rawArgs.Count >= 1 && TryParseIntArg(rawArgs[0], out var cid))
                    {
                        scope = ScopeKind.Card;
                        currentCardId = cid;
                        currentCard = new JsonObject
                        {
                            ["id"] = cid,
                            ["name"] = $"Card {cid}",
                            ["desc"] = string.Empty,
                            ["type"] = "Spell Card",
                            ["race"] = "Normal",
                            ["attribute"] = string.Empty,
                            ["level"] = 0,
                            ["atk"] = 0,
                            ["defense"] = 0,
                            ["addToEraPool"] = true,
                            ["data"] = new JsonObject
                            {
                                ["code"] = cid,
                                ["alias"] = 0,
                                ["setcodes"] = new JsonArray(),
                                ["type"] = 2,
                                ["level"] = 0,
                                ["attribute"] = 0,
                                ["race"] = "0",
                                ["attack"] = 0,
                                ["defense"] = 0,
                                ["lscale"] = 0,
                                ["rscale"] = 0,
                                ["link_marker"] = 0
                            }
                        };
                        dsl.InlineCards.Add(currentCard);
                    }
                    continue;

                case "relic":
                    if (rawArgs.Count >= 1 && TryParseStringArg(rawArgs[0], isJs, out var rkey))
                    {
                        scope = ScopeKind.Relic;
                        var cleanKey = ModRepository.SanitizeId(rkey).Replace('-', '_');
                        currentRelic = new JsonObject
                        {
                            ["key"] = cleanKey,
                            ["name"] = cleanKey,
                            ["cost"] = 80,
                            ["description"] = string.Empty,
                            ["art"] = "Merchant Urn.png",
                            ["tag"] = "utility",
                            ["effect"] = $"approved_{cleanKey}",
                            ["amount"] = 0,
                            ["filter"] = string.Empty
                        };
                        dsl.InlineRelics.Add(currentRelic);
                    }
                    continue;

                case "character":
                    if (rawArgs.Count >= 1 && TryParseStringArg(rawArgs[0], isJs, out var cname))
                    {
                        scope = ScopeKind.Character;
                        currentChar = new JsonObject
                        {
                            ["name"] = cname,
                            ["sprite"] = "dm01.png",
                            ["race"] = "Spellcaster",
                            ["attribute"] = "DARK",
                            ["color"] = "#f4cf57",
                            ["starting_relic"] = "mage",
                            ["unlockedByDefault"] = true
                        };
                        if (rawArgs.Count >= 2 && TryParseIntArg(rawArgs[1], out var cidx) && cidx >= 0)
                        {
                            currentChar["index"] = cidx;
                        }

                        dsl.InlineCharacters.Add(currentChar);
                    }
                    continue;

                case "pack":
                    if (rawArgs.Count >= 1 && TryParseStringArg(rawArgs[0], isJs, out var pid))
                    {
                        scope = ScopeKind.Pack;
                        currentPack = new JsonObject
                        {
                            ["id"] = pid,
                            ["name"] = pid,
                            ["region"] = "EN",
                            ["code"] = pid.Contains('-') ? pid.Split('-').Last() : pid,
                            ["date"] = string.Empty,
                            ["image"] = string.Empty,
                            ["size"] = 9,
                            ["common"] = new JsonArray(),
                            ["rare"] = new JsonArray(),
                            ["super"] = new JsonArray(),
                            ["ultra"] = new JsonArray()
                        };
                        dsl.InlinePacks.Add(currentPack);
                    }
                    continue;

                case "preconstructed" or "deck":
                    if (rawArgs.Count >= 1 && TryParseStringArg(rawArgs[0], isJs, out var did))
                    {
                        scope = ScopeKind.Preconstructed;
                        currentPrecon = new JsonObject
                        {
                            ["id"] = did,
                            ["name"] = did,
                            ["date"] = string.Empty,
                            ["art"] = "SDK",
                            ["cards"] = new JsonArray()
                        };
                        dsl.InlinePreconstructedDecks.Add(currentPrecon);
                    }
                    continue;

                case "trainer":
                    scope = ScopeKind.Trainer;
                    dsl.InlineTrainer ??= new JsonObject { ["inGameOverlay"] = true };
                    continue;

                case "target_end":
                case "end_target":
                    scope = ScopeKind.Global;
                    continue;
            }

            // Global metadata & version constraints (work from any scope)
            switch (ident)
            {
                case "set_mod" or "set_project" or "set_id":
                    if (TryGetStr(rawArgs, 0, isJs, out var id)) dsl.Id = id;
                    continue;
                case "set_version":
                    if (TryGetStr(rawArgs, 0, isJs, out var ver)) dsl.Version = ver;
                    continue;
                case "set_author":
                    if (TryGetStr(rawArgs, 0, isJs, out var auth)) dsl.Author = auth;
                    continue;
                case "set_gamever":
                    if (TryGetStr(rawArgs, 0, isJs, out var gv)) dsl.GameVersion = gv;
                    continue;
                case "set_min_gamever" or "set_min_game_version":
                    if (TryGetStr(rawArgs, 0, isJs, out var mgv)) dsl.MinGameVersion = mgv;
                    continue;
                case "set_max_gamever" or "set_max_game_version":
                    if (TryGetStr(rawArgs, 0, isJs, out var maxgv)) dsl.MaxGameVersion = maxgv;
                    continue;
                case "set_min_managerver" or "set_xmakever" or "set_min_manager_version":
                    if (TryGetStr(rawArgs, 0, isJs, out var mmv)) dsl.MinManagerVersion = mmv;
                    continue;
                case "add_tags":
                    dsl.Tags.AddRange(ParseStringList(rawArgs, isJs));
                    continue;
                case "add_cards":
                    dsl.CardGlobs.AddRange(ParseStringList(rawArgs, isJs));
                    continue;
                case "add_characters":
                    dsl.CharacterGlobs.AddRange(ParseStringList(rawArgs, isJs));
                    continue;
                case "add_relics":
                    dsl.RelicGlobs.AddRange(ParseStringList(rawArgs, isJs));
                    continue;
                case "add_packs":
                    dsl.PackGlobs.AddRange(ParseStringList(rawArgs, isJs));
                    continue;
                case "add_preconstructed":
                    dsl.PreconstructedGlobs.AddRange(ParseStringList(rawArgs, isJs));
                    continue;
                case "add_ydk":
                    dsl.YdkGlobs.AddRange(ParseStringList(rawArgs, isJs));
                    continue;
                case "add_lua_cards" or "add_lua_files":
                    dsl.LuaCardGlobs.AddRange(ParseStringList(rawArgs, isJs));
                    continue;
                case "add_lua_libs":
                    dsl.LuaLibGlobs.AddRange(ParseStringList(rawArgs, isJs));
                    continue;
                case "add_js_files":
                    dsl.JsGlobs.AddRange(ParseStringList(rawArgs, isJs));
                    continue;
                case "add_css_files":
                    dsl.CssGlobs.AddRange(ParseStringList(rawArgs, isJs));
                    continue;
                case "add_art":
                    dsl.ArtGlobs.AddRange(ParseStringList(rawArgs, isJs));
                    continue;
                case "add_campaign":
                    dsl.CampaignGlobs.AddRange(ParseStringList(rawArgs, isJs));
                    continue;
                case "add_overrides":
                    dsl.OverrideGlobs.AddRange(ParseStringList(rawArgs, isJs));
                    continue;
                case "add_trainer":
                    dsl.TrainerPaths.AddRange(ParseStringList(rawArgs, isJs));
                    continue;
                case "on_utility_lua":
                    if (TryGetStr(rawArgs, 0, isJs, out var ucode)) dsl.InlineLuaUtilityBlocks.Add(ucode);
                    continue;
                case "on_load" when isJs:
                    if (rawArgs.Count >= 1) dsl.JsLoadCallbacks.Add(rawArgs[0].Trim());
                    continue;
                case "on_fetch" when isJs:
                    if (rawArgs.Count >= 2) dsl.JsFetchHooks.Add((rawArgs[0].Trim(), rawArgs[1].Trim()));
                    continue;
                case "on_ui_change" when isJs:
                    if (rawArgs.Count >= 1) dsl.JsUiCallbacks.Add(rawArgs[0].Trim());
                    continue;
                case "on_hotkey" when isJs:
                    if (rawArgs.Count >= 2 && TryParseStringArg(rawArgs[0], true, out var hk))
                        dsl.JsHotkeyCallbacks.Add((hk, rawArgs[1].Trim()));
                    continue;
            }

            // Scope-specific directives
            if (scope == ScopeKind.Global)
            {
                if (ident == "set_name" && TryGetStr(rawArgs, 0, isJs, out var n)) dsl.Name = n;
                else if (ident is "set_description" or "set_desc" && TryGetStr(rawArgs, 0, isJs, out var d)) dsl.Description = d;
            }
            else if (scope == ScopeKind.Card && currentCard is not null)
            {
                switch (ident)
                {
                    case "set_name" when TryGetStr(rawArgs, 0, isJs, out var v): currentCard["name"] = v; break;
                    case "set_zh_name" when TryGetStr(rawArgs, 0, isJs, out var v): currentCard["zhName"] = v; break;
                    case "set_desc" or "set_description" when TryGetStr(rawArgs, 0, isJs, out var v): currentCard["desc"] = v; break;
                    case "set_zh_desc" when TryGetStr(rawArgs, 0, isJs, out var v): currentCard["zhDesc"] = v; break;
                    case "set_kind":
                        if (TryGetStr(rawArgs, 0, isJs, out var ct)) currentCard["type"] = ct;
                        if (TryGetStr(rawArgs, 1, isJs, out var cr)) currentCard["race"] = cr;
                        break;
                    case "set_attribute" when TryGetStr(rawArgs, 0, isJs, out var a): currentCard["attribute"] = a; break;
                    case "set_stats":
                        if (rawArgs.Count >= 1 && TryParseIntArg(rawArgs[0], out var lv)) { currentCard["level"] = lv; currentCard["data"]!["level"] = lv; }
                        if (rawArgs.Count >= 2 && TryParseIntArg(rawArgs[1], out var atk)) { currentCard["atk"] = atk; currentCard["data"]!["attack"] = atk; }
                        if (rawArgs.Count >= 3 && TryParseIntArg(rawArgs[2], out var def)) { currentCard["defense"] = def; currentCard["data"]!["defense"] = def; }
                        break;
                    case "set_data_masks":
                        if (rawArgs.Count >= 1 && TryParseIntArg(rawArgs[0], out var tm)) currentCard["data"]!["type"] = tm;
                        if (rawArgs.Count >= 2 && TryGetStr(rawArgs, 1, isJs, out var rm)) currentCard["data"]!["race"] = rm;
                        if (rawArgs.Count >= 3 && TryParseIntArg(rawArgs[2], out var am)) currentCard["data"]!["attribute"] = am;
                        break;
                    case "set_pack" when TryGetStr(rawArgs, 0, isJs, out var p): currentCard["packId"] = p; break;
                    case "on_duel_script" when TryGetStr(rawArgs, 0, isJs, out var lua):
                        dsl.InlineLuaCardScripts[$"c{currentCardId}.lua"] = lua.Trim();
                        break;
                }
            }
            else if (scope == ScopeKind.Pack && currentPack is not null)
            {
                switch (ident)
                {
                    case "set_name" when TryGetStr(rawArgs, 0, isJs, out var v): currentPack["name"] = v; break;
                    case "set_zh_name" when TryGetStr(rawArgs, 0, isJs, out var v): currentPack["zhName"] = v; break;
                    case "set_region" when TryGetStr(rawArgs, 0, isJs, out var v): currentPack["region"] = v; break;
                    case "set_code" when TryGetStr(rawArgs, 0, isJs, out var v): currentPack["code"] = v; break;
                    case "set_size" when rawArgs.Count >= 1 && TryParseIntArg(rawArgs[0], out var sz): currentPack["size"] = sz; break;
                    case "add_common": AppendIntIds(currentPack["common"]!.AsArray(), rawArgs); break;
                    case "add_rare": AppendIntIds(currentPack["rare"]!.AsArray(), rawArgs); break;
                    case "add_super": AppendIntIds(currentPack["super"]!.AsArray(), rawArgs); break;
                    case "add_ultra": AppendIntIds(currentPack["ultra"]!.AsArray(), rawArgs); break;
                }
            }
            else if (scope == ScopeKind.Preconstructed && currentPrecon is not null)
            {
                switch (ident)
                {
                    case "set_name" when TryGetStr(rawArgs, 0, isJs, out var v): currentPrecon["name"] = v; break;
                    case "set_zh_name" when TryGetStr(rawArgs, 0, isJs, out var v): currentPrecon["zhName"] = v; break;
                    case "set_art" when TryGetStr(rawArgs, 0, isJs, out var v): currentPrecon["art"] = v; break;
                    case "set_date" when TryGetStr(rawArgs, 0, isJs, out var v): currentPrecon["date"] = v; break;
                    case "add_cards": AppendIntIds(currentPrecon["cards"]!.AsArray(), rawArgs); break;
                }
            }
            else if (scope == ScopeKind.Relic && currentRelic is not null)
            {
                switch (ident)
                {
                    case "set_name" when TryGetStr(rawArgs, 0, isJs, out var v): currentRelic["name"] = v; break;
                    case "set_zh_name" when TryGetStr(rawArgs, 0, isJs, out var v): currentRelic["zhName"] = v; break;
                    case "set_desc" or "set_description" when TryGetStr(rawArgs, 0, isJs, out var v): currentRelic["description"] = v; break;
                    case "set_zh_desc" when TryGetStr(rawArgs, 0, isJs, out var v): currentRelic["zhDescription"] = v; break;
                    case "set_cost" when rawArgs.Count >= 1 && TryParseIntArg(rawArgs[0], out var cost): currentRelic["cost"] = cost; break;
                    case "set_art" when TryGetStr(rawArgs, 0, isJs, out var v): currentRelic["art"] = v; break;
                    case "set_tag" when TryGetStr(rawArgs, 0, isJs, out var v): currentRelic["tag"] = v; break;
                    case "set_amount" when rawArgs.Count >= 1 && TryParseIntArg(rawArgs[0], out var amt): currentRelic["amount"] = amt; break;
                    case "set_filter" when TryGetStr(rawArgs, 0, isJs, out var v): currentRelic["filter"] = v; break;
                    case "add_starting_for":
                        var arr = currentRelic["startingForCharacters"] as JsonArray ?? new JsonArray();
                        foreach (var a in rawArgs) if (TryParseIntArg(a, out var idx)) arr.Add((JsonNode?)JsonValue.Create(idx));
                        currentRelic["startingForCharacters"] = arr;
                        break;
                    case "on_duel_effect" when TryGetStr(rawArgs, 0, isJs, out var lua):
                        currentRelic["luaEffect"] = lua.Trim();
                        break;
                }
            }
            else if (scope == ScopeKind.Character && currentChar is not null)
            {
                switch (ident)
                {
                    case "set_zh_name" when TryGetStr(rawArgs, 0, isJs, out var v): currentChar["zhName"] = v; break;
                    case "set_sprite" when TryGetStr(rawArgs, 0, isJs, out var v): currentChar["sprite"] = v; break;
                    case "set_race" when TryGetStr(rawArgs, 0, isJs, out var v): currentChar["race"] = v; break;
                    case "set_attribute" when TryGetStr(rawArgs, 0, isJs, out var v): currentChar["attribute"] = v; break;
                    case "set_theme" when TryGetStr(rawArgs, 0, isJs, out var v): currentChar["theme"] = v; break;
                    case "set_color" when TryGetStr(rawArgs, 0, isJs, out var v): currentChar["color"] = v; break;
                    case "set_starting_relic" when TryGetStr(rawArgs, 0, isJs, out var v): currentChar["starting_relic"] = v; break;
                    case "set_unlocked" when rawArgs.Count >= 1 && TryParseBoolArg(rawArgs[0], out var b): currentChar["unlockedByDefault"] = b; break;
                    case "add_deck_cards":
                        var deckArr = currentChar["cards"] as JsonArray ?? new JsonArray();
                        foreach (var s in ParseStringList(rawArgs, isJs)) deckArr.Add((JsonNode?)JsonValue.Create(s));
                        currentChar["cards"] = deckArr;
                        break;
                    case "add_starting_packs":
                        var packArr = currentChar["starting_packs"] as JsonArray ?? new JsonArray();
                        foreach (var s in ParseStringList(rawArgs, isJs)) packArr.Add((JsonNode?)JsonValue.Create(s));
                        currentChar["starting_packs"] = packArr;
                        break;
                }
            }
            else if (scope == ScopeKind.Trainer && dsl.InlineTrainer is not null)
            {
                var tr = dsl.InlineTrainer;
                switch (ident)
                {
                    case "set_unlock_all_characters" when rawArgs.Count >= 1 && TryParseBoolArg(rawArgs[0], out var b): tr["unlockAllCharacters"] = b; break;
                    case "set_unlock_all_variants" when rawArgs.Count >= 1 && TryParseBoolArg(rawArgs[0], out var b): tr["unlockAllVariants"] = b; break;
                    case "set_starting_gold" when rawArgs.Count >= 1 && TryParseIntArg(rawArgs[0], out var n): tr["startingGold"] = n; break;
                    case "set_starting_gold_bonus" when rawArgs.Count >= 1 && TryParseIntArg(rawArgs[0], out var n): tr["startingGoldBonus"] = n; break;
                    case "set_starting_lp" when rawArgs.Count >= 1 && TryParseIntArg(rawArgs[0], out var n): tr["startingLp"] = n; break;
                    case "set_free_shop" when rawArgs.Count >= 1 && TryParseBoolArg(rawArgs[0], out var b): tr["freeShop"] = b; break;
                    case "set_shop_price_multiplier" when rawArgs.Count >= 1 && TryParseDoubleArg(rawArgs[0], out var d): tr["shopPriceMultiplier"] = d; break;
                    case "set_reward_gold_multiplier" when rawArgs.Count >= 1 && TryParseDoubleArg(rawArgs[0], out var d): tr["rewardGoldMultiplier"] = d; break;
                    case "set_ingame_overlay" when rawArgs.Count >= 1 && TryParseBoolArg(rawArgs[0], out var b): tr["inGameOverlay"] = b; break;
                    case "add_starting_relics":
                        var rr = tr["startingRelics"] as JsonArray ?? new JsonArray();
                        foreach (var s in ParseStringList(rawArgs, isJs)) rr.Add((JsonNode?)JsonValue.Create(s));
                        tr["startingRelics"] = rr;
                        break;
                    case "add_starting_cards":
                        var cc = tr["startingCards"] as JsonArray ?? new JsonArray();
                        foreach (var a in rawArgs) if (TryParseIntArg(a, out var idVal)) cc.Add((JsonNode?)JsonValue.Create(idVal));
                        tr["startingCards"] = cc;
                        break;
                }
            }
        }
    }

    private static void SkipTrivia(string s, ref int i, bool isJs)
    {
        var len = s.Length;
        while (i < len)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }
            if (!isJs && i + 1 < len && s[i] == '-' && s[i + 1] == '-')
            {
                i += 2;
                if (i + 1 < len && s[i] == '[' && s[i + 1] == '[')
                {
                    i += 2;
                    var end = s.IndexOf("]]", i, StringComparison.Ordinal);
                    i = end >= 0 ? end + 2 : len;
                }
                else
                {
                    while (i < len && s[i] != '\n') i++;
                }

                continue;
            }

            if (isJs && i + 1 < len && s[i] == '/' && s[i + 1] == '/')
            {
                i += 2;
                while (i < len && s[i] != '\n') i++;
                continue;
            }

            if (isJs && i + 1 < len && s[i] == '/' && s[i + 1] == '*')
            {
                i += 2;
                var end = s.IndexOf("*/", i, StringComparison.Ordinal);
                i = end >= 0 ? end + 2 : len;
                continue;
            }

            break;
        }
    }

    private static string ReadBalancedParenContent(string s, ref int i, bool isJs)
    {
        // s[i] == '('
        i++;
        var start = i;
        var depth = 1;
        var len = s.Length;

        while (i < len && depth > 0)
        {
            var c = s[i];
            if (!isJs && c == '-' && i + 1 < len && s[i + 1] == '-')
            {
                SkipTrivia(s, ref i, isJs: false);
                continue;
            }

            if (isJs && c == '/' && i + 1 < len && (s[i + 1] == '/' || s[i + 1] == '*'))
            {
                SkipTrivia(s, ref i, isJs: true);
                continue;
            }

            if (!isJs && c == '[' && i + 1 < len && s[i + 1] == '[')
            {
                var end = s.IndexOf("]]", i + 2, StringComparison.Ordinal);
                i = end >= 0 ? end + 2 : len;
                continue;
            }

            if (c is '"' or '\'' || (isJs && c == '`'))
            {
                i++;
                while (i < len && s[i] != c)
                {
                    if (s[i] == '\\') i++;
                    i++;
                }

                if (i < len) i++;
                continue;
            }

            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    var inner = s[start..i];
                    i++;
                    return inner;
                }
            }

            i++;
        }

        return s[start..];
    }

    private static List<string> SplitTopLevelArgs(string span, bool isJs)
    {
        var args = new List<string>();
        var len = span.Length;
        var start = 0;
        var paren = 0;
        var brace = 0;
        var bracket = 0;

        for (var i = 0; i < len; i++)
        {
            var c = span[i];
            if (!isJs && c == '[' && i + 1 < len && span[i + 1] == '[')
            {
                var end = span.IndexOf("]]", i + 2, StringComparison.Ordinal);
                i = end >= 0 ? end + 1 : len - 1;
                continue;
            }

            if (c is '"' or '\'' || (isJs && c == '`'))
            {
                i++;
                while (i < len && span[i] != c)
                {
                    if (span[i] == '\\') i++;
                    i++;
                }

                continue;
            }

            if (c == '(') paren++;
            else if (c == ')') paren--;
            else if (c == '{') brace++;
            else if (c == '}') brace--;
            else if (c == '[') bracket++;
            else if (c == ']') bracket--;
            else if (c == ',' && paren == 0 && brace == 0 && bracket == 0)
            {
                var piece = span[start..i].Trim();
                if (piece.Length > 0) args.Add(piece);
                start = i + 1;
            }
        }

        var tail = span[start..].Trim();
        if (tail.Length > 0) args.Add(tail);
        return args;
    }

    private static void AppendIntIds(JsonArray arr, List<string> rawArgs)
    {
        foreach (var a in rawArgs)
        {
            if (TryParseIntArg(a, out var id)) arr.Add((JsonNode?)JsonValue.Create(id));
        }
    }

    private static List<string> ParseStringList(List<string> rawArgs, bool isJs)
    {
        var list = new List<string>();
        foreach (var raw in rawArgs)
        {
            if (TryParseStringArg(raw, isJs, out var str)) list.Add(str);
        }

        return list;
    }

    private static bool TryGetStr(List<string> rawArgs, int idx, bool isJs, out string value)
    {
        value = string.Empty;
        return idx < rawArgs.Count && TryParseStringArg(rawArgs[idx], isJs, out value);
    }

    private static bool TryParseStringArg(string raw, bool isJs, out string value)
    {
        value = string.Empty;
        var t = raw.Trim();
        if (t.Length < 2) return false;

        if (!isJs && t.StartsWith("[[", StringComparison.Ordinal) && t.EndsWith("]]", StringComparison.Ordinal))
        {
            value = t[2..^2].TrimStart('\r', '\n');
            return true;
        }

        var q = t[0];
        if ((q == '"' || q == '\'' || (isJs && q == '`')) && t[^1] == q)
        {
            var sb = new StringBuilder(t.Length - 2);
            for (var i = 1; i < t.Length - 1; i++)
            {
                if (t[i] == '\\' && i + 1 < t.Length - 1)
                {
                    i++;
                    sb.Append(t[i] switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        '\\' => '\\',
                        '"' => '"',
                        '\'' => '\'',
                        '`' => '`',
                        _ => t[i]
                    });
                }
                else
                {
                    sb.Append(t[i]);
                }
            }

            value = sb.ToString();
            return true;
        }

        return false;
    }

    private static bool TryParseIntArg(string raw, out int value) =>
        int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool TryParseDoubleArg(string raw, out double value) =>
        double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryParseBoolArg(string raw, out bool value) =>
        bool.TryParse(raw.Trim(), out value);

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool StartsWithKeyword(string s, int i, string kw) =>
        i + kw.Length <= s.Length &&
        s.AsSpan(i, kw.Length).SequenceEqual(kw) &&
        (i + kw.Length == s.Length || !IsIdentChar(s[i + kw.Length]));
}
