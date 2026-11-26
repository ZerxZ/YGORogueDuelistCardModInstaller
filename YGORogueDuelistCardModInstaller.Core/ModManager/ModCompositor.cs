using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace YGORogueDuelistCardModInstaller.Core.ModManager;

public sealed record ComposedModOutput(
    Dictionary<string, byte[]> Files,
    HashSet<string> NewlyAddedPaths,
    int AppliedModCount,
    int JsRuntimeModCount);

/// <summary>
/// Stacks any number of enabled mods in load order onto a clean vanilla web-X.Y.Z.zip
/// completely in memory without mutating the source archive.
/// Supports:
/// - Declarative cards/*.json, characters/*.json, relics/*.json, trainer.json
/// - Lua card scripts (lua/cards/*.lua &amp; scripts/*.lua) + Lua libraries (lua/lib/*.lua -> utility.lua)
/// - JS ES Module plugins (js/**) + CSS stylesheets (styles/**) + built-in F8 Trainer overlay via window.YgoModAPI
/// - Legacy data/mod.json, modular campaign/**, decks.json, zh-cards.json, zh-ui.json, art/, and loose overrides/**
/// </summary>
public static class ModCompositor
{
    private const string DefaultGetIdOld = "function GetID()\n\treturn self_table,self_code\nend";
    private const string DefaultGetIdNew = "function GetID()\n\tlocal offset=self_code<100000000 and 1 or 100\n\treturn self_table,self_code,offset\nend";

    public static string ComputeConfigurationFingerprint(
        InjectionMode mode,
        string? baseVersion,
        IReadOnlyList<ManagedMod> orderedMods,
        bool developerMode = false)
    {
        var sb = new StringBuilder();
        sb.Append("mode=").Append(mode).Append('\n');
        sb.Append("base=").Append(baseVersion ?? "").Append('\n');
        sb.Append("dev=").Append(developerMode).Append('\n');
        sb.Append("runtime=").Append(ModRuntimeGenerator.RuntimeVersion).Append('\n');

        foreach (var mod in orderedMods.Where(m => m.Enabled).OrderBy(m => m.Order))
        {
            sb.Append("mod:").Append(mod.Id).Append('@').Append(mod.Order).Append('\n');
            if (Directory.Exists(mod.DirectoryPath))
            {
                foreach (var file in Directory.EnumerateFiles(mod.DirectoryPath, "*", SearchOption.AllDirectories)
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var rel = Path.GetRelativePath(mod.DirectoryPath, file).Replace('\\', '/');
                    if (string.Equals(rel, "README.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var fi = new FileInfo(file);
                    sb.Append("  ").Append(rel).Append('|').Append(fi.Length).Append('|')
                      .Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
                }
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(ModPatcher.GetUtf8Bytes(sb.ToString())))[..24];
    }

    public static ComposedModOutput Compose(
        string vanillaZipPath,
        IReadOnlyList<ManagedMod> orderedMods,
        string? picsDir,
        IInstallerInteraction interaction,
        CancellationToken cancellationToken = default,
        bool developerMode = false,
        int? hotReloadPort = null)
    {
        var enabledMods = orderedMods.Where(m => m.Enabled).OrderBy(m => m.Order).ToList();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var newlyAdded = new HashSet<string>(StringComparer.Ordinal);

        if (enabledMods.Count == 0 && !developerMode && hotReloadPort is null)
        {
            return new ComposedModOutput(files, newlyAdded, 0, 0);
        }

        using var zip = ZipFile.OpenRead(vanillaZipPath);
        var existingZipEntries = zip.Entries
            .Where(e => !e.FullName.EndsWith('/'))
            .Select(e => e.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string? baseGameVersion = null;
        if (existingZipEntries.Contains("live-version.json"))
        {
            try
            {
                baseGameVersion = ModPatcher.ParseJson(ModPatcher.ReadZipText(zip, "live-version.json"))["version"]?.GetValue<string>();
            }
            catch { }
        }

        interaction.Say($"读取纯净原版数据包：{vanillaZipPath}");
        interaction.Say($"按加载顺序合成 {enabledMods.Count} 个已启用 MOD...");

        JsonObject? content = null;
        JsonObject? engine = null;
        JsonObject? scripts = null;
        JsonObject? campaign = null;
        JsonObject? zh = null;
        var cardAssetsTouched = false;
        var difficultyPatched = false;
        var unlockAllVariantsJs = false;
        var uiArtPatched = false;
        var ensureGetId3Return = false;
        var enableBuiltInTrainerOverlay = false;
        JsonObject? trainerOverlayConfig = null;

        var inlineArtCodes = new List<string>();
        var inlineArtCodeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inlineArtFileByCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var looseOverrides = new Dictionary<string, string>(StringComparer.Ordinal);
        var runtimeDescriptors = new List<ModRuntimeDescriptor>();

        JsonObject GetContent() => content ??= ModPatcher.ParseJson(ModPatcher.ReadZipText(zip, "content.json")).AsObject();
        JsonObject GetEngine() => engine ??= ModPatcher.ParseJson(ModPatcher.ReadZipText(zip, "engine-data.json")).AsObject();
        JsonObject GetScripts() => scripts ??= ModPatcher.ParseJson(ModPatcher.ReadZipText(zip, "scripts-0.json")).AsObject();
        JsonObject GetCampaign() => campaign ??= ModPatcher.ParseJson(ModPatcher.ReadZipText(zip, "campaign-files.json")).AsObject();
        JsonObject GetZh() => zh ??= ModPatcher.ParseJson(ModPatcher.ReadZipText(zip, "i18n/zh.json")).AsObject();

        void RegisterCode(string code)
        {
            if (inlineArtCodeSet.Add(code))
            {
                inlineArtCodes.Add(code);
            }
        }

        for (var i = 0; i < enabledMods.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mod = enabledMods[i];
            var pct = 10 + (i * 55.0 / enabledMods.Count);
            interaction.ReportProgress($"正在叠加 MOD [{i + 1}/{enabledMods.Count}]：{mod.Manifest.Name}...", pct);
            interaction.Say($"--- [{i + 1}/{enabledMods.Count}] {mod.Manifest.Name} ({mod.Id} v{mod.Manifest.Version}) ---");

            try
            {
                var dsl = ModDslParser.TryParseModDirectory(mod.DirectoryPath);
                if (dsl is not null)
                {
                    interaction.Say($"DSL: 已解析{(dsl.HasLuaDsl ? " mod.lua" : "")}{(dsl.HasJsDsl ? " mod.js" : "")}（描述域 + 脚本域）");
                }

                // 1. Legacy/structured data/mod.json
                var legacyModPath = Path.Combine(mod.DirectoryPath, "data", "mod.json");
                if (File.Exists(legacyModPath))
                {
                    var modJson = ModPatcher.ParseJson(File.ReadAllText(legacyModPath, Encoding.UTF8)).AsObject();
                    ModPatcher.PatchContent(GetContent(), modJson, interaction);
                    ModPatcher.PatchEngine(GetEngine(), modJson, interaction);
                    ModPatcher.PatchScripts(GetScripts(), modJson, interaction);
                    ModPatcher.PatchCampaign(GetCampaign(), modJson, interaction);

                    var zhObj = GetZh();
                    var zhCards = zhObj["cards"]?.AsObject()
                        ?? throw new ModOperationException("i18n/zh.json 缺少 cards 字段");
                    var zhAdded = 0;
                    if (modJson["zh"] is JsonObject modZh)
                    {
                        foreach (var (k, v) in modZh)
                        {
                            if (!zhCards.ContainsKey(k)) zhAdded++;
                            zhCards[k] = v?.DeepClone();
                        }
                    }

                    if (modJson["fallbackZh"] is JsonObject fbZh)
                    {
                        foreach (var (k, v) in fbZh)
                        {
                            if (!zhCards.ContainsKey(k))
                            {
                                zhAdded++;
                                zhCards[k] = v?.DeepClone();
                            }
                        }
                    }

                    if (modJson["mage"] is JsonObject mage && zhObj["ui"] is JsonObject mageUi)
                    {
                        var zhOld = mage["zhOld"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(zhOld)) mageUi.Remove(zhOld);
                        mageUi[mage["descNew"]!.GetValue<string>()] = mage["zhNew"]!.GetValue<string>();
                    }

                    var uiAdded = 0;
                    if (modJson["relicUi"] is JsonObject relicUi && zhObj["ui"] is JsonObject ui)
                    {
                        foreach (var (k, v) in relicUi)
                        {
                            if (!ui.ContainsKey(k)) uiAdded++;
                            ui[k] = v?.DeepClone();
                        }
                    }

                    interaction.Say($"i18n/zh.json: 中文名/卡文 +{zhAdded} 条，遗物 ui +{uiAdded} 条");

                    if (modJson["codes"] is JsonArray codes)
                    {
                        cardAssetsTouched = true;
                        var modArtDir = Path.Combine(mod.DirectoryPath, "art");
                        foreach (var c in codes)
                        {
                            var code = c?.ToString() ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(code)) continue;
                            RegisterCode(code);
                            foreach (var ext in new[] { "webp", "jpg", "png" })
                            {
                                var cand = Path.Combine(modArtDir, $"{code}.{ext}");
                                if (File.Exists(cand))
                                {
                                    inlineArtFileByCode[code] = cand;
                                    break;
                                }
                            }
                        }
                    }

                    if (modJson["variant7"] is not null) difficultyPatched = true;
                    if (modJson["relic"] is not null) uiArtPatched = true;
                }

                // 2. Declarative cards/*.json + DSL card() blocks & add_cards() globs
                var cardObjects = CollectDeclarativeObjects(
                    ModRepository.EnumerateDeclarativeCardFiles(mod.DirectoryPath)
                        .Concat(dsl is not null ? ModDslParser.ExpandGlobs(mod.DirectoryPath, dsl.CardGlobs) : []),
                    dsl?.InlineCards);
                if (cardObjects.Count > 0)
                {
                    var cardCount = ApplyDeclarativeCardObjects(
                        cardObjects,
                        GetContent(),
                        GetEngine(),
                        GetCampaign(),
                        GetZh(),
                        mod.DirectoryPath,
                        RegisterCode,
                        inlineArtFileByCode,
                        ref cardAssetsTouched);
                    if (cardCount > 0)
                    {
                        interaction.Say($"cards: 自动注册/更新声明式卡片 {cardCount} 张（同步 content / engine / era / zh / pack）");
                    }
                }

                // 2b. Declarative packs/*.json + DSL pack() blocks & add_packs() globs
                var packObjects = CollectDeclarativeObjects(
                    ModRepository.EnumerateDeclarativePackFiles(mod.DirectoryPath)
                        .Concat(dsl is not null ? ModDslParser.ExpandGlobs(mod.DirectoryPath, dsl.PackGlobs) : []),
                    dsl?.InlinePacks);
                if (packObjects.Count > 0)
                {
                    var packCount = ApplyDeclarativePacks(packObjects, GetContent(), GetCampaign(), GetZh());
                    interaction.Say($"packs: 自动注册/更新补充卡包 {packCount} 个（同步 content / campaign / zh）");
                }

                // 2c. Declarative preconstructed/*.json + DSL preconstructed()/deck() blocks & decks/*.ydk
                var preconObjects = CollectDeclarativeObjects(
                    ModRepository.EnumerateDeclarativePreconstructedFiles(mod.DirectoryPath)
                        .Concat(dsl is not null ? ModDslParser.ExpandGlobs(mod.DirectoryPath, dsl.PreconstructedGlobs) : []),
                    dsl?.InlinePreconstructedDecks);
                if (preconObjects.Count > 0)
                {
                    var preconCount = ApplyDeclarativePreconstructed(preconObjects, GetContent(), GetCampaign(), GetZh());
                    interaction.Say($"preconstructed: 自动注册/更新预组卡组 {preconCount} 套");
                }

                var ydkFiles = ModRepository.EnumerateYdkFiles(mod.DirectoryPath)
                    .Concat(dsl is not null ? ModDslParser.ExpandGlobs(mod.DirectoryPath, dsl.YdkGlobs) : [])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (ydkFiles.Count > 0)
                {
                    var campObj = GetCampaign();
                    foreach (var ydk in ydkFiles)
                    {
                        campObj[$"data/decks/{Path.GetFileName(ydk)}"] = File.ReadAllText(ydk, Encoding.UTF8);
                    }

                    interaction.Say($"decks/*.ydk: 注入 YDK 卡组文件 {ydkFiles.Count} 个");
                }

                // 3. Declarative relics/*.json + DSL relic() blocks & add_relics() globs
                var relicObjects = CollectDeclarativeObjects(
                    ModRepository.EnumerateDeclarativeRelicFiles(mod.DirectoryPath)
                        .Concat(dsl is not null ? ModDslParser.ExpandGlobs(mod.DirectoryPath, dsl.RelicGlobs) : []),
                    dsl?.InlineRelics);
                if (relicObjects.Count > 0)
                {
                    var relicCount = ApplyDeclarativeRelicObjects(
                        relicObjects,
                        GetContent(),
                        GetCampaign(),
                        GetZh(),
                        mod.DirectoryPath,
                        ref uiArtPatched);
                    if (relicCount > 0)
                    {
                        interaction.Say($"relics: 自动注册/更新遗物 {relicCount} 个（同步 content / approved_relics / effects / zh）");
                    }
                }

                // 4. Declarative characters/*.json + DSL character() blocks & add_characters() globs
                var charObjects = CollectDeclarativeObjects(
                    ModRepository.EnumerateDeclarativeCharacterFiles(mod.DirectoryPath)
                        .Concat(dsl is not null ? ModDslParser.ExpandGlobs(mod.DirectoryPath, dsl.CharacterGlobs) : []),
                    dsl?.InlineCharacters);
                if (charObjects.Count > 0)
                {
                    var charCount = ApplyDeclarativeCharacterObjects(
                        charObjects,
                        GetContent(),
                        GetCampaign(),
                        GetZh(),
                        mod.DirectoryPath);
                    if (charCount > 0)
                    {
                        interaction.Say($"characters: 自动注册/更新角色 {charCount} 名（同步 playable / progression / signatures / zh）");
                    }
                }

                // 5. Declarative trainer.json + DSL trainer() block
                var trainerPath = Path.Combine(mod.DirectoryPath, "trainer.json");
                if (File.Exists(trainerPath))
                {
                    var trainer = ModPatcher.ParseJson(File.ReadAllText(trainerPath, Encoding.UTF8)).AsObject();
                    ApplyDeclarativeTrainer(
                        mod.Id,
                        trainer,
                        GetContent(),
                        GetCampaign(),
                        ref unlockAllVariantsJs,
                        ref enableBuiltInTrainerOverlay,
                        ref trainerOverlayConfig,
                        interaction);
                }

                if (dsl?.InlineTrainer is not null)
                {
                    ApplyDeclarativeTrainer(
                        mod.Id + "_dsl",
                        dsl.InlineTrainer,
                        GetContent(),
                        GetCampaign(),
                        ref unlockAllVariantsJs,
                        ref enableBuiltInTrainerOverlay,
                        ref trainerOverlayConfig,
                        interaction);
                }

                // 6. Lua card scripts (lua/cards/*.lua + scripts/*.lua + DSL add_lua_cards + inline on_duel_script)
                var luaCardFiles = ModRepository.EnumerateLuaCardScriptFiles(mod.DirectoryPath)
                    .Concat(dsl is not null ? ModDslParser.ExpandGlobs(mod.DirectoryPath, dsl.LuaCardGlobs) : [])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var inlineLuaCount = dsl?.InlineLuaCardScripts.Count ?? 0;
                if (luaCardFiles.Count > 0 || inlineLuaCount > 0)
                {
                    var scriptsObj = GetScripts();
                    foreach (var lua in luaCardFiles)
                    {
                        scriptsObj[Path.GetFileName(lua)] = File.ReadAllText(lua, Encoding.UTF8);
                    }

                    if (dsl is not null)
                    {
                        foreach (var (fname, code) in dsl.InlineLuaCardScripts)
                        {
                            scriptsObj[fname] = code;
                        }
                    }

                    ensureGetId3Return = true;
                    interaction.Say($"scripts-0.json: 注入 Lua 卡脚本 {luaCardFiles.Count + inlineLuaCount} 个");
                }

                // 7. Lua global libraries (lua/lib/*.lua + DSL add_lua_libs + on_utility_lua)
                var luaLibFiles = ModRepository.ResolveLuaLibs(mod.DirectoryPath, mod.Manifest)
                    .Select(rel => (Rel: rel, Abs: Path.Combine(mod.DirectoryPath, rel.Replace('/', Path.DirectorySeparatorChar))))
                    .Concat(dsl is not null
                        ? ModDslParser.ExpandGlobs(mod.DirectoryPath, dsl.LuaLibGlobs)
                            .Select(abs => (Rel: Path.GetRelativePath(mod.DirectoryPath, abs).Replace('\\', '/'), Abs: abs))
                        : [])
                    .DistinctBy(x => x.Abs, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var inlineUtilBlocks = dsl?.InlineLuaUtilityBlocks ?? [];
                if (luaLibFiles.Count > 0 || inlineUtilBlocks.Count > 0)
                {
                    var scriptsObj = GetScripts();
                    var util = scriptsObj["utility.lua"]?.GetValue<string>() ?? string.Empty;
                    var appended = 0;
                    foreach (var (relLib, absPath) in luaLibFiles)
                    {
                        var marker = $"-- [mod-lib:{mod.Id}/{relLib}]";
                        if (!util.Contains(marker, StringComparison.Ordinal))
                        {
                            util = util.TrimEnd() + "\n\n" + marker + "\n" + File.ReadAllText(absPath, Encoding.UTF8) + "\n";
                            appended++;
                        }
                    }

                    for (var bi = 0; bi < inlineUtilBlocks.Count; bi++)
                    {
                        var marker = $"-- [mod-dsl-lib:{mod.Id}#{bi}]";
                        if (!util.Contains(marker, StringComparison.Ordinal))
                        {
                            util = util.TrimEnd() + "\n\n" + marker + "\n" + inlineUtilBlocks[bi] + "\n";
                            appended++;
                        }
                    }

                    scriptsObj["utility.lua"] = util;
                    ensureGetId3Return = true;
                    if (appended > 0) interaction.Say($"utility.lua: 追加 Lua 辅助库 {appended} 个");
                }

                // 8. Modular art/<code>.{webp|jpg|png}
                var artDir = Path.Combine(mod.DirectoryPath, "art");
                if (Directory.Exists(artDir))
                {
                    var addedArtFiles = 0;
                    foreach (var artFile in Directory.EnumerateFiles(artDir))
                    {
                        var ext = Path.GetExtension(artFile).ToLowerInvariant();
                        if (ext is not (".webp" or ".jpg" or ".jpeg" or ".png")) continue;

                        var code = Path.GetFileNameWithoutExtension(artFile);
                        if (string.IsNullOrWhiteSpace(code) || !code.All(char.IsDigit)) continue;
                        RegisterCode(code);
                        inlineArtFileByCode[code] = artFile;
                        cardAssetsTouched = true;
                        addedArtFiles++;
                    }

                    if (addedArtFiles > 0 && !File.Exists(legacyModPath))
                    {
                        interaction.Say($"card-assets.js: 登记模块化卡图 {addedArtFiles} 张");
                    }
                }

                // 9. Modular campaign/**
                var campaignDir = Path.Combine(mod.DirectoryPath, "campaign");
                if (Directory.Exists(campaignDir))
                {
                    var campFiles = Directory.GetFiles(campaignDir, "*", SearchOption.AllDirectories);
                    if (campFiles.Length > 0)
                    {
                        var campObj = GetCampaign();
                        foreach (var cf in campFiles)
                        {
                            var rel = Path.GetRelativePath(campaignDir, cf).Replace('\\', '/');
                            campObj[rel] = File.ReadAllText(cf, Encoding.UTF8);
                        }

                        interaction.Say($"campaign-files.json: 覆盖/新增战役文件 {campFiles.Length} 个");
                    }
                }

                // 10. Modular decks.json
                var decksJsonPath = Path.Combine(mod.DirectoryPath, "decks.json");
                if (File.Exists(decksJsonPath))
                {
                    var decksObj = ModPatcher.ParseJson(File.ReadAllText(decksJsonPath, Encoding.UTF8)).AsObject();
                    var contentObj = GetContent();
                    var chars = contentObj["characters"]!.AsArray();
                    var campObj = GetCampaign();
                    var sig = ModPatcher.ParseJson(campObj["data/character-signatures.json"]!.GetValue<string>()).AsObject();

                    foreach (var (key, val) in decksObj)
                    {
                        if (int.TryParse(key, out var idx) && idx >= 0 && idx < chars.Count && val is JsonArray arr)
                        {
                            chars[idx]!["cards"] = arr.DeepClone();
                            sig[key] = arr.DeepClone();
                        }
                    }

                    campObj["data/character-signatures.json"] = ModPatcher.ToJsonText(sig);
                    interaction.Say($"decks.json: 改写 {decksObj.Count} 套角色起始卡组");
                }

                // 11. Modular zh-cards.json / zh-ui.json
                var zhCardsPath = Path.Combine(mod.DirectoryPath, "zh-cards.json");
                if (File.Exists(zhCardsPath))
                {
                    var extraCards = ModPatcher.ParseJson(File.ReadAllText(zhCardsPath, Encoding.UTF8)).AsObject();
                    var zhCards = GetZh()["cards"]!.AsObject();
                    foreach (var (k, v) in extraCards) zhCards[k] = v?.DeepClone();
                    interaction.Say($"i18n/zh.json: 注入模块化中文卡文 {extraCards.Count} 条");
                }

                var zhUiPath = Path.Combine(mod.DirectoryPath, "zh-ui.json");
                if (File.Exists(zhUiPath))
                {
                    var extraUi = ModPatcher.ParseJson(File.ReadAllText(zhUiPath, Encoding.UTF8)).AsObject();
                    var zhUi = GetZh()["ui"]!.AsObject();
                    foreach (var (k, v) in extraUi) zhUi[k] = v?.DeepClone();
                    interaction.Say($"i18n/zh.json: 注入模块化界面翻译 {extraUi.Count} 条");
                }

                // 12. JS ES Module plugins (js/** + DSL add_js_files + DSL Script Scope hooks) & CSS stylesheets (styles/** + DSL add_css_files)
                var jsEntries = ModRepository.ResolveJsEntries(mod.DirectoryPath, mod.Manifest)
                    .Concat(dsl is not null
                        ? ModDslParser.ExpandGlobs(mod.DirectoryPath, dsl.JsGlobs)
                            .Select(p => Path.GetRelativePath(mod.DirectoryPath, p).Replace('\\', '/'))
                        : [])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var cssEntries = ModRepository.ResolveCssEntries(mod.DirectoryPath, mod.Manifest)
                    .Concat(dsl is not null
                        ? ModDslParser.ExpandGlobs(mod.DirectoryPath, dsl.CssGlobs)
                            .Select(p => Path.GetRelativePath(mod.DirectoryPath, p).Replace('\\', '/'))
                        : [])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var hasDslJsHooks = dsl?.HasGeneratedJsPlugin == true;

                if (jsEntries.Count > 0 || cssEntries.Count > 0 || hasDslJsHooks)
                {
                    var bundlePrefix = $"mods/{mod.Id}";

                    foreach (var sub in new[] { "js", "styles" })
                    {
                        var subDir = Path.Combine(mod.DirectoryPath, sub);
                        if (!Directory.Exists(subDir)) continue;
                        foreach (var file in Directory.EnumerateFiles(subDir, "*", SearchOption.AllDirectories))
                        {
                            var relInMod = Path.GetRelativePath(mod.DirectoryPath, file).Replace('\\', '/');
                            var bundleRel = $"{bundlePrefix}/{relInMod}";
                            files[bundleRel] = File.ReadAllBytes(file);
                            if (!existingZipEntries.Contains(bundleRel)) newlyAdded.Add(bundleRel);
                        }
                    }

                    foreach (var relInMod in jsEntries.Concat(cssEntries))
                    {
                        var srcFile = Path.Combine(mod.DirectoryPath, relInMod.Replace('/', Path.DirectorySeparatorChar));
                        var bundleRel = $"{bundlePrefix}/{relInMod}";
                        if (File.Exists(srcFile) && !files.ContainsKey(bundleRel))
                        {
                            files[bundleRel] = File.ReadAllBytes(srcFile);
                            if (!existingZipEntries.Contains(bundleRel)) newlyAdded.Add(bundleRel);
                        }
                    }

                    var finalJsBundleEntries = jsEntries.Select(e => $"{bundlePrefix}/{e}").ToList();
                    if (hasDslJsHooks)
                    {
                        var dslJsRel = $"{bundlePrefix}/__dsl_runtime.js";
                        files[dslJsRel] = ModPatcher.GetUtf8Bytes(dsl!.BuildGeneratedJsModule());
                        newlyAdded.Add(dslJsRel);
                        finalJsBundleEntries.Add(dslJsRel);
                    }

                    runtimeDescriptors.Add(new ModRuntimeDescriptor
                    {
                        Id = mod.Id,
                        Name = mod.Manifest.Name,
                        Version = mod.Manifest.Version,
                        Author = mod.Manifest.Author,
                        Description = mod.Manifest.Description,
                        JsEntries = finalJsBundleEntries,
                        CssEntries = cssEntries.Select(e => $"{bundlePrefix}/{e}").ToList(),
                        Config = mod.Manifest.Config?.DeepClone().AsObject()
                    });

                    interaction.Say($"YgoModAPI: 注册前端插件（JS 入口 {finalJsBundleEntries.Count} 个，CSS 样式 {cssEntries.Count} 个）");
                }

                // 13. Loose overrides/**
                var overridesDir = Path.Combine(mod.DirectoryPath, "overrides");
                if (Directory.Exists(overridesDir))
                {
                    var overrideFiles = Directory.GetFiles(overridesDir, "*", SearchOption.AllDirectories);
                    foreach (var file in overrideFiles)
                    {
                        var rel = Path.GetRelativePath(overridesDir, file).Replace('\\', '/');
                        looseOverrides[rel] = file;
                    }

                    if (overrideFiles.Length > 0)
                    {
                        interaction.Say($"overrides: 登记覆盖/新增资源文件 {overrideFiles.Length} 个");
                    }
                }
            }
            catch (Exception ex)
            {
                throw new ModOperationException($"叠加 MOD「{mod.Manifest.Name}」({mod.Id}) 时出错：{ex.Message}", ex);
            }
        }

        // Built-in Developer Mode tools (F10 Dev Inspector + Sandbox Free Duel unlock + DEV quick bar)
        if (developerMode)
        {
            unlockAllVariantsJs = true;
            var campObj = GetCampaign();
            var storage = campObj["storage.py"]!.GetValue<string>();
            const string devMark = "# mod-dev-mode:sandbox";
            if (!storage.Contains(devMark, StringComparison.Ordinal))
            {
                var inject = $"\n p['wins']=max(int(p.get('wins') or 0),1);p['variants_unlocked']=True  {devMark}";
                storage = storage.Replace("p.setdefault(k,v)", "p.setdefault(k,v)" + inject, StringComparison.Ordinal);
                campObj["storage.py"] = storage;
            }

            files[ModRuntimeGenerator.DevToolsScriptPath] = ModPatcher.GetUtf8Bytes(ModRuntimeGenerator.BuildDevToolsScript());
            newlyAdded.Add(ModRuntimeGenerator.DevToolsScriptPath);
            runtimeDescriptors.Add(new ModRuntimeDescriptor
            {
                Id = "_builtin-devtools",
                Name = "MOD Developer Mode (F10)",
                Version = ModRuntimeGenerator.RuntimeVersion,
                Author = "YGO Mod Manager",
                Description = "Developer Inspector (F10), window.YgoModDev, Sandbox Free Duel unlock & quick reload bar.",
                JsEntries = [ModRuntimeGenerator.DevToolsScriptPath],
                CssEntries = []
            });
            interaction.Say("Developer Mode: 已注入开发者工具栏（F10 诊断器 + 沙盒自由决斗解锁 + window.YgoModDev）");
        }

        // Built-in F8 Trainer overlay if any trainer.json requested inGameOverlay: true
        if (enableBuiltInTrainerOverlay)
        {
            files[ModRuntimeGenerator.TrainerOverlayScriptPath] = ModPatcher.GetUtf8Bytes(ModRuntimeGenerator.BuildTrainerOverlayScript());
            newlyAdded.Add(ModRuntimeGenerator.TrainerOverlayScriptPath);
            runtimeDescriptors.Add(new ModRuntimeDescriptor
            {
                Id = "_builtin-trainer",
                Name = "Built-in Trainer Overlay (F8)",
                Version = ModRuntimeGenerator.RuntimeVersion,
                Author = "YGO Mod Manager",
                Description = "In-game F8 Trainer overlay for live gold/LP/card/relic editing.",
                JsEntries = [ModRuntimeGenerator.TrainerOverlayScriptPath],
                CssEntries = [],
                Config = trainerOverlayConfig
            });
            interaction.Say("YgoModAPI: 已启用内置游戏内修改器面板（按 F8 呼出）");
        }

        if (ensureGetId3Return && scripts is not null)
        {
            var util = scripts["utility.lua"]?.GetValue<string>() ?? string.Empty;
            if (util.Contains(DefaultGetIdOld, StringComparison.Ordinal))
            {
                scripts["utility.lua"] = util.Replace(DefaultGetIdOld, DefaultGetIdNew, StringComparison.Ordinal);
                interaction.Say("utility.lua: 自动升级 GetID 返回 3 个值以兼容现代 Lua 卡脚本");
            }
        }

        interaction.ReportProgress("正在序列化合成数据...", 72);

        if (content is not null) files["content.json"] = ModPatcher.GetUtf8Bytes(ModPatcher.ToJsonText(content));
        if (engine is not null) files["engine-data.json"] = ModPatcher.GetUtf8Bytes(ModPatcher.ToJsonText(engine));
        if (scripts is not null) files["scripts-0.json"] = ModPatcher.GetUtf8Bytes(ModPatcher.ToJsonText(scripts));
        if (campaign is not null) files["campaign-files.json"] = ModPatcher.GetUtf8Bytes(ModPatcher.ToJsonText(campaign));
        if (zh is not null) files["i18n/zh.json"] = ModPatcher.GetUtf8Bytes(ModPatcher.ToJsonText(zh));

        if (cardAssetsTouched)
        {
            interaction.ReportProgress("正在内联全部 MOD 卡图到 card-assets.js...", 78);
            var rawCardAssets = ModPatcher.ReadZipText(zip, "card-assets.js");
            files["card-assets.js"] = ModPatcher.GetUtf8Bytes(
                BuildMergedCardAssets(rawCardAssets, inlineArtCodes, inlineArtFileByCode, picsDir, interaction));
        }

        if (unlockAllVariantsJs)
        {
            var diffText = ModPatcher.ReadZipText(zip, "difficulty-preference.js");
            const string oldStr = "return !!profile.variants_unlocked||";
            if (diffText.Contains(oldStr, StringComparison.Ordinal))
            {
                diffText = diffText.Replace(oldStr, "return true||!!profile.variants_unlocked||", StringComparison.Ordinal);
            }

            files["difficulty-preference.js"] = ModPatcher.GetUtf8Bytes(diffText);
        }
        else if (difficultyPatched)
        {
            files["difficulty-preference.js"] = ModPatcher.GetUtf8Bytes(
                ModPatcher.PatchDifficulty(ModPatcher.ReadZipText(zip, "difficulty-preference.js")));
        }

        if (uiArtPatched)
        {
            files["ui-art.js"] = ModPatcher.GetUtf8Bytes(
                ModPatcher.PatchUiArt(ModPatcher.ReadZipText(zip, "ui-art.js")));
        }

        if (runtimeDescriptors.Count > 0 || hotReloadPort is > 0)
        {
            var indexHtml = ModPatcher.ReadZipText(zip, "index.html");
            if (runtimeDescriptors.Count > 0)
            {
                var registryJson = ModRuntimeGenerator.BuildRegistryJson(baseGameVersion, runtimeDescriptors);
                var runtimeJs = ModRuntimeGenerator.BuildRuntimeScript();
                files[ModRuntimeGenerator.RegistryJsonPath] = ModPatcher.GetUtf8Bytes(registryJson);
                files[ModRuntimeGenerator.RuntimeScriptPath] = ModPatcher.GetUtf8Bytes(runtimeJs);
                newlyAdded.Add(ModRuntimeGenerator.RegistryJsonPath);
                newlyAdded.Add(ModRuntimeGenerator.RuntimeScriptPath);
                indexHtml = ModRuntimeGenerator.PatchIndexHtml(indexHtml);
                interaction.Say($"index.html: 已注入 JS MOD 运行时（mod-runtime.js，托管 {runtimeDescriptors.Count} 个前端插件）");
            }

            if (hotReloadPort is { } hrPort and > 0)
            {
                files[ModHotReloadServer.HotReloadClientScriptPath] =
                    ModPatcher.GetUtf8Bytes(ModHotReloadServer.BuildHotReloadClientScript(hrPort));
                newlyAdded.Add(ModHotReloadServer.HotReloadClientScriptPath);
                indexHtml = ModHotReloadServer.PatchIndexHtmlForHotReload(indexHtml, hrPort);
                interaction.Say($"Hot-Reload: 已注入热加载桥接脚本 mod-hot-reload.js（连接到 http://127.0.0.1:{hrPort}）");
            }

            files["index.html"] = ModPatcher.GetUtf8Bytes(indexHtml);
        }

        foreach (var (rel, srcPath) in looseOverrides)
        {
            files[rel] = File.ReadAllBytes(srcPath);
            if (!existingZipEntries.Contains(rel)) newlyAdded.Add(rel);
        }

        if (files.Count > 0 && existingZipEntries.Contains("sw.js"))
        {
            interaction.ReportProgress("正在更新 sw.js 缓存哈希...", 84);
            var swText = files.TryGetValue("sw.js", out var customSw)
                ? Encoding.UTF8.GetString(customSw)
                : ModPatcher.ReadZipText(zip, "sw.js");
            files["sw.js"] = ModPatcher.GetUtf8Bytes(UpdateServiceWorkerHashes(swText, files, interaction));
        }

        return new ComposedModOutput(files, newlyAdded, enabledMods.Count, runtimeDescriptors.Count);
    }

    private static List<JsonObject> CollectDeclarativeObjects(IEnumerable<string> jsonFiles, IReadOnlyList<JsonObject>? inlineObjects)
    {
        var list = new List<JsonObject>();
        foreach (var file in jsonFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(file)) continue;
            var parsed = ModPatcher.ParseJson(File.ReadAllText(file, Encoding.UTF8));
            if (parsed is JsonArray arr)
            {
                foreach (var obj in arr.OfType<JsonObject>()) list.Add(obj);
            }
            else if (parsed is JsonObject single)
            {
                list.Add(single);
            }
        }

        if (inlineObjects is not null) list.AddRange(inlineObjects);
        return list;
    }

    private static int ApplyDeclarativePacks(
        IReadOnlyList<JsonObject> packDefs,
        JsonObject content,
        JsonObject campaign,
        JsonObject zh)
    {
        var contentPacks = content["packs"]!.AsArray();
        var campPacks = ModPatcher.ParseJson(campaign["data/packs.json"]!.GetValue<string>()).AsArray();
        var zhUi = zh["ui"]!.AsObject();
        var total = 0;

        foreach (var raw in packDefs)
        {
            var id = raw["id"]?.GetValue<string>() ?? throw new ModOperationException("卡包定义缺少 id");
            var name = raw["name"]?.GetValue<string>() ?? id;
            var zhName = raw["zhName"]?.GetValue<string>();

            var clean = raw.DeepClone().AsObject();
            clean.Remove("zhName");
            clean["region"] ??= "EN";
            clean["code"] ??= id.Contains('-') ? id.Split('-').Last() : id;
            clean["date"] ??= string.Empty;
            clean["image"] ??= string.Empty;
            clean["size"] ??= 9;
            clean["common"] ??= new JsonArray();

            UpsertByStringProperty(contentPacks, "id", id, clean);
            UpsertByStringProperty(campPacks, "id", id, clean);
            if (!string.IsNullOrWhiteSpace(zhName)) zhUi[name] = zhName;
            total++;
        }

        campaign["data/packs.json"] = ModPatcher.ToJsonText(campPacks);
        return total;
    }

    private static int ApplyDeclarativePreconstructed(
        IReadOnlyList<JsonObject> deckDefs,
        JsonObject content,
        JsonObject campaign,
        JsonObject zh)
    {
        var contentPrecon = content["preconstructedDecks"]!.AsArray();
        var campPreconRoot = ModPatcher.ParseJson(campaign["data/preconstructed.json"]!.GetValue<string>());
        JsonArray campPrecon;
        if (campPreconRoot is JsonObject campObj)
        {
            campPrecon = campObj["decks"] as JsonArray ?? new JsonArray();
            campObj["decks"] = campPrecon;
        }
        else
        {
            campPrecon = campPreconRoot.AsArray();
        }

        var zhUi = zh["ui"]!.AsObject();
        var total = 0;

        foreach (var raw in deckDefs)
        {
            var id = raw["id"]?.GetValue<string>() ?? throw new ModOperationException("预组卡组定义缺少 id");
            var name = raw["name"]?.GetValue<string>() ?? id;
            var zhName = raw["zhName"]?.GetValue<string>();

            var clean = raw.DeepClone().AsObject();
            clean.Remove("zhName");
            clean["date"] ??= string.Empty;
            clean["art"] ??= "SDK";
            clean["cards"] ??= new JsonArray();

            UpsertByStringProperty(contentPrecon, "id", id, clean);
            UpsertByStringProperty(campPrecon, "id", id, clean);
            if (!string.IsNullOrWhiteSpace(zhName)) zhUi[name] = zhName;
            total++;
        }

        campaign["data/preconstructed.json"] = ModPatcher.ToJsonText(campPreconRoot);
        return total;
    }

    private static void UpsertByStringProperty(JsonArray arr, string propName, string propVal, JsonObject item)
    {
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is JsonObject o && string.Equals(o[propName]?.GetValue<string>(), propVal, StringComparison.OrdinalIgnoreCase))
            {
                arr[i] = item.DeepClone();
                return;
            }
        }

        arr.Add(item.DeepClone());
    }

    private static int ApplyDeclarativeCardObjects(
        IReadOnlyList<JsonObject> cardDefs,
        JsonObject content,
        JsonObject engine,
        JsonObject campaign,
        JsonObject zh,
        string modDir,
        Action<string> registerCode,
        Dictionary<string, string> inlineArtFileByCode,
        ref bool cardAssetsTouched)
    {
        var contentCards = content["cards"]!.AsArray();
        var cardIndexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < contentCards.Count; i++)
        {
            var id = contentCards[i]?["id"]?.ToString();
            if (!string.IsNullOrEmpty(id)) cardIndexById[id] = i;
        }

        var eraArray = ModPatcher.ParseJson(campaign["data/era-cards.json"]!.GetValue<string>()).AsArray();
        var eraIndexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < eraArray.Count; i++)
        {
            var id = eraArray[i]?["id"]?.ToString();
            if (!string.IsNullOrEmpty(id)) eraIndexById[id] = i;
        }

        var zhCards = zh["cards"]!.AsObject();
        var artDir = Path.Combine(modDir, "art");
        var packAssignments = new List<(string PackId, int CardId)>();
        var total = 0;

        foreach (var rawCard in cardDefs)
        {
            var idNode = rawCard["id"] ?? throw new ModOperationException("卡片定义缺少 id");
            var idStr = idNode.ToString();
            var idInt = int.Parse(idStr, CultureInfo.InvariantCulture);

            var zhName = rawCard["zhName"]?.GetValue<string>();
            var zhDesc = rawCard["zhDesc"]?.GetValue<string>();
            var zhStrings = rawCard["zhStrings"]?.AsArray();
            var addToEraPool = rawCard["addToEraPool"]?.GetValue<bool>() ?? true;
            var packId = rawCard["packId"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(packId)) packAssignments.Add((packId, idInt));

            var cleanCard = rawCard.DeepClone().AsObject();
            cleanCard.Remove("zhName");
            cleanCard.Remove("zhDesc");
            cleanCard.Remove("zhStrings");
            cleanCard.Remove("addToEraPool");
            cleanCard.Remove("packId");

            cleanCard["date"] ??= string.Empty;
            cleanCard["sets"] ??= new JsonArray();
            cleanCard["image"] ??= $"https://images.ygoprodeck.com/images/cards/{idStr}.jpg";
            cleanCard["strings"] ??= new JsonArray(Enumerable.Range(0, 16).Select(_ => (JsonNode)JsonValue.Create(string.Empty)!).ToArray());

            if (cardIndexById.TryGetValue(idStr, out var existingIdx))
                contentCards[existingIdx] = cleanCard.DeepClone();
            else
            {
                cardIndexById[idStr] = contentCards.Count;
                contentCards.Add(cleanCard.DeepClone());
            }

            if (cleanCard["data"] is JsonObject dataObj)
                engine[idStr] = dataObj.DeepClone();

            if (addToEraPool)
            {
                if (eraIndexById.TryGetValue(idStr, out var existingEraIdx))
                    eraArray[existingEraIdx] = cleanCard.DeepClone();
                else
                {
                    eraIndexById[idStr] = eraArray.Count;
                    eraArray.Add(cleanCard.DeepClone());
                }
            }

            if (!string.IsNullOrWhiteSpace(zhName) || !string.IsNullOrWhiteSpace(zhDesc))
            {
                var zhEntry = new JsonArray(
                    JsonValue.Create(zhName ?? cleanCard["name"]?.GetValue<string>() ?? idStr),
                    JsonValue.Create(zhDesc ?? cleanCard["desc"]?.GetValue<string>() ?? string.Empty),
                    zhStrings is not null ? zhStrings.DeepClone() : new JsonArray());
                zhCards[idStr] = zhEntry;
            }

            foreach (var ext in new[] { "webp", "jpg", "jpeg", "png" })
            {
                var cand = Path.Combine(artDir, $"{idInt}.{ext}");
                if (File.Exists(cand))
                {
                    registerCode(idStr);
                    inlineArtFileByCode[idStr] = cand;
                    cardAssetsTouched = true;
                    break;
                }
            }

            total++;
        }

        campaign["data/era-cards.json"] = ModPatcher.ToJsonText(eraArray);

        if (packAssignments.Count > 0)
        {
            var contentPacks = content["packs"]!.AsArray();
            var campPacks = ModPatcher.ParseJson(campaign["data/packs.json"]!.GetValue<string>()).AsArray();
            foreach (var (pid, cid) in packAssignments)
            {
                AddCardToPack(contentPacks, pid, cid);
                AddCardToPack(campPacks, pid, cid);
            }

            campaign["data/packs.json"] = ModPatcher.ToJsonText(campPacks);
        }

        return total;
    }

    private static void AddCardToPack(JsonArray packs, string packId, int cardId)
    {
        foreach (var p in packs.OfType<JsonObject>())
        {
            if (!string.Equals(p["id"]?.GetValue<string>(), packId, StringComparison.OrdinalIgnoreCase)) continue;
            var common = p["common"] as JsonArray ?? new JsonArray();
            if (!common.Any(n => n is JsonValue v && v.TryGetValue<int>(out var x) && x == cardId))
            {
                common.Add((JsonNode?)JsonValue.Create(cardId));
                p["common"] = common;
            }
        }
    }

    private static int ApplyDeclarativeRelicObjects(
        IReadOnlyList<JsonObject> defs,
        JsonObject content,
        JsonObject campaign,
        JsonObject zh,
        string modDir,
        ref bool uiArtPatched)
    {
        var artifacts = content["artifacts"]!.AsObject();
        var artifactInfo = content["artifactInfo"]!.AsObject();
        var characters = content["characters"]!.AsArray();
        var zhUi = zh["ui"]!.AsObject();
        var prog = ModPatcher.ParseJson(campaign["data/character-progression.json"]!.GetValue<string>()).AsObject();
        var total = 0;

        foreach (var r in defs)
        {
            var key = r["key"]?.GetValue<string>() ?? throw new ModOperationException("遗物定义缺少 key");
            var name = r["name"]?.GetValue<string>() ?? key;
            var cost = r["cost"]?.GetValue<int>() ?? 80;
            var desc = r["description"]?.GetValue<string>() ?? string.Empty;
            var zhName = r["zhName"]?.GetValue<string>();
            var zhDesc = r["zhDescription"]?.GetValue<string>();
            var tag = r["tag"]?.GetValue<string>() ?? "utility";
            var filter = r["filter"]?.GetValue<string>() ?? string.Empty;
            var amount = r["amount"]?.GetValue<int>() ?? 0;
            var effectName = r["effect"]?.GetValue<string>() ?? $"approved_{key}";
            var luaEffect = r["luaEffect"]?.GetValue<string>();
            var themeCard = r["themeCard"]?.GetValue<string>() ?? string.Empty;

            var artVal = r["art"]?.GetValue<string>() ?? "Merchant Urn.png";
            if (!artVal.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var localArtPath = Path.Combine(modDir, artVal.Replace('/', Path.DirectorySeparatorChar));
                var localInArtDir = Path.Combine(modDir, "art", artVal);
                var resolvedFile = File.Exists(localArtPath) ? localArtPath :
                                   File.Exists(localInArtDir) ? localInArtDir : null;
                if (resolvedFile is not null)
                {
                    var mime = Path.GetExtension(resolvedFile).ToLowerInvariant() switch
                    {
                        ".webp" => "image/webp",
                        ".png" => "image/png",
                        ".svg" => "image/svg+xml",
                        _ => "image/jpeg"
                    };
                    artVal = $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(resolvedFile))}";
                    uiArtPatched = true;
                }
            }
            else
            {
                uiArtPatched = true;
            }

            artifacts[key] = new JsonArray(
                JsonValue.Create(name),
                JsonValue.Create(cost),
                JsonValue.Create(desc));
            artifactInfo[key] = new JsonObject
            {
                ["art"] = artVal,
                ["effect"] = effectName,
                ["amount"] = amount,
                ["tag"] = tag,
                ["filter"] = filter,
                ["parameters"] = r["parameters"] is JsonObject p ? p.DeepClone() : new JsonObject()
            };

            if (!string.IsNullOrWhiteSpace(zhName)) zhUi[name] = zhName;
            if (!string.IsNullOrWhiteSpace(zhDesc)) zhUi[desc] = zhDesc;

            var mark = $"# mod-relic:{key}";
            var rpy = campaign["approved_relics.py"]!.GetValue<string>();
            if (!rpy.Contains(mark, StringComparison.Ordinal))
            {
                var pyLine = $"\n\n{mark}\nREGULAR=REGULAR+[({PyStr(key)},{PyStr(name)},{cost},{PyStr(desc)},{PyStr(themeCard)})]\n";
                campaign["approved_relics.py"] = rpy.TrimEnd() + pyLine;
            }

            if (!string.IsNullOrWhiteSpace(luaEffect))
            {
                var epy = campaign["approved_relic_effects.py"]!.GetValue<string>();
                if (!epy.Contains(mark, StringComparison.Ordinal))
                {
                    var fnBase = $"_mod_eff_base_{ModRepository.SanitizeId(key).Replace('-', '_')}";
                    var luaConst = $"_MOD_LUA_{ModRepository.SanitizeId(key).Replace('-', '_').ToUpperInvariant()}";
                    var pyHook =
                        $"\n\n{mark}\n" +
                        $"{fnBase}=effect\n" +
                        $"{luaConst}={PyStr(luaEffect)}\n" +
                        $"def effect(kind):\n" +
                        $" if kind=={PyStr(effectName)}:return [{luaConst}]\n" +
                        $" return {fnBase}(kind)\n";
                    campaign["approved_relic_effects.py"] = epy.TrimEnd() + pyHook;
                }
            }

            if (r["startingForCharacters"] is JsonArray charIndices)
            {
                var cvs = campaign["character_variants.py"]!.GetValue<string>();
                foreach (var idxNode in charIndices)
                {
                    if (idxNode is not JsonValue v || !v.TryGetValue<int>(out var idx)) continue;
                    if (idx >= 0 && idx < characters.Count && characters[idx] is JsonObject chObj)
                        chObj["starting_relic"] = key;

                    prog["starting_relics"]![idx.ToString(CultureInfo.InvariantCulture)] = key;
                    var pattern = $@"characters\[{idx}\]\['starting_relic'\]='[A-Za-z0-9_]+'";
                    if (Regex.IsMatch(cvs, pattern))
                        cvs = Regex.Replace(cvs, pattern, $"characters[{idx}]['starting_relic']='{key}'");
                }

                campaign["character_variants.py"] = cvs;
            }

            total++;
        }

        campaign["data/character-progression.json"] = ModPatcher.ToJsonText(prog);
        return total;
    }

    private static int ApplyDeclarativeCharacterObjects(
        IReadOnlyList<JsonObject> defs,
        JsonObject content,
        JsonObject campaign,
        JsonObject zh,
        string modDir)
    {
        var characters = content["characters"]!.AsArray();
        var playable = content["playable"]!.AsArray();
        var zhUi = zh["ui"]!.AsObject();
        var prog = ModPatcher.ParseJson(campaign["data/character-progression.json"]!.GetValue<string>()).AsObject();
        var sig = ModPatcher.ParseJson(campaign["data/character-signatures.json"]!.GetValue<string>()).AsObject();
        var unlockedToEnsure = new List<int>();
        var total = 0;

        foreach (var ch in defs)
        {
            var explicitIndex = ch["index"] is JsonValue iv && iv.TryGetValue<int>(out var parsedIdx) ? parsedIdx : -1;
            var name = ch["name"]?.GetValue<string>() ?? $"Custom Duelist {characters.Count}";
            var zhName = ch["zhName"]?.GetValue<string>();
            var unlockedByDefault = ch["unlockedByDefault"]?.GetValue<bool>() ?? true;

            int targetIdx;
            JsonObject targetObj;

            if (explicitIndex >= 0 && explicitIndex < characters.Count && characters[explicitIndex] is JsonObject existing)
            {
                targetIdx = explicitIndex;
                targetObj = existing;
                foreach (var (k, v) in ch)
                {
                    if (k is "index" or "zhName" or "unlockedByDefault") continue;
                    targetObj[k] = v?.DeepClone();
                }
            }
            else
            {
                targetObj = characters[0]!.DeepClone().AsObject();
                targetObj.Remove("alternate");
                targetObj["name"] = name;
                targetObj["sprite"] = ch["sprite"]?.GetValue<string>() ?? "dm01.png";
                targetObj["race"] = ch["race"]?.GetValue<string>() ?? "Spellcaster";
                targetObj["attribute"] = ch["attribute"]?.GetValue<string>() ?? "DARK";
                targetObj["theme"] = ch["theme"]?.GetValue<string>() ?? $"{targetObj["race"]} / {targetObj["attribute"]}";
                targetObj["color"] = ch["color"]?.GetValue<string>() ?? "#f4cf57";
                targetObj["bonus"] = ch["bonus"]?.GetValue<string>() ?? "Shared rewards: summons, damage, powerful monsters and more.";
                targetObj["pack"] = ch["pack"]?.GetValue<string>() ?? "DRAFT-YAMI-YUGI";
                targetObj["starting_relic"] = ch["starting_relic"]?.GetValue<string>() ?? "mage";
                targetObj["type"] = ch["type"]?.GetValue<int>() ?? 2;

                if (ch["starting_packs"] is JsonArray sp) targetObj["starting_packs"] = sp.DeepClone();
                if (ch["cards"] is JsonArray cardsArr) targetObj["cards"] = cardsArr.DeepClone();
                if (ch["alternate"] is JsonObject altObj) targetObj["alternate"] = altObj.DeepClone();

                targetIdx = characters.Count;
                characters.Add((JsonNode?)targetObj);
            }

            if (!playable.Any(p => p is JsonValue pv && pv.TryGetValue<int>(out var n) && n == targetIdx))
                playable.Add((JsonNode?)JsonValue.Create(targetIdx));

            var idxStr = targetIdx.ToString(CultureInfo.InvariantCulture);
            if (targetObj["starting_relic"]?.GetValue<string>() is { Length: > 0 } sr)
                prog["starting_relics"]![idxStr] = sr;

            if (unlockedByDefault)
            {
                prog["unlocks"]![idxStr] = new JsonObject
                {
                    ["metric"] = "wins",
                    ["target"] = 0,
                    ["description"] = "Available from the beginning"
                };
                unlockedToEnsure.Add(targetIdx);
            }

            if (targetObj["cards"] is JsonArray finalCards)
                sig[idxStr] = finalCards.DeepClone();

            if (!string.IsNullOrWhiteSpace(zhName))
                zhUi[targetObj["name"]!.GetValue<string>()] = zhName;

            total++;
        }

        campaign["data/character-progression.json"] = ModPatcher.ToJsonText(prog);
        campaign["data/character-signatures.json"] = ModPatcher.ToJsonText(sig);

        if (unlockedToEnsure.Count > 0)
        {
            var storage = campaign["storage.py"]!.GetValue<string>();
            var mark = "# mod-characters-unlock";
            var idList = string.Join(",", unlockedToEnsure.Distinct());
            var injectLine = $"\n p['unlocked']=sorted(set(p.get('unlocked') or [])|set([{idList}]))  {mark}";
            if (!storage.Contains(mark, StringComparison.Ordinal))
            {
                storage = storage.Replace("p.setdefault(k,v)", "p.setdefault(k,v)" + injectLine, StringComparison.Ordinal);
                campaign["storage.py"] = storage;
            }
        }

        return total;
    }

    private static void ApplyDeclarativeTrainer(
        string modId,
        JsonObject trainer,
        JsonObject content,
        JsonObject campaign,
        ref bool unlockAllVariantsJs,
        ref bool enableBuiltInTrainerOverlay,
        ref JsonObject? trainerOverlayConfig,
        IInstallerInteraction interaction)
    {
        var unlockAllChars = trainer["unlockAllCharacters"]?.GetValue<bool>() ?? false;
        var unlockAllVars = trainer["unlockAllVariants"]?.GetValue<bool>() ?? false;
        var startingGold = trainer["startingGold"] is JsonValue sg && sg.TryGetValue<int>(out var sgVal) ? sgVal : (int?)null;
        var startingGoldBonus = trainer["startingGoldBonus"] is JsonValue sgb && sgb.TryGetValue<int>(out var sgbVal) ? sgbVal : 0;
        var startingLp = trainer["startingLp"] is JsonValue slp && slp.TryGetValue<int>(out var slpVal) ? slpVal : (int?)null;
        var freeShop = trainer["freeShop"]?.GetValue<bool>() ?? false;
        var shopPriceMult = trainer["shopPriceMultiplier"] is JsonValue spm && spm.TryGetValue<double>(out var spmVal) ? spmVal : (double?)null;
        var rewardGoldMult = trainer["rewardGoldMultiplier"] is JsonValue rgm && rgm.TryGetValue<double>(out var rgmVal) ? rgmVal : (double?)null;
        var inGameOverlay = trainer["inGameOverlay"]?.GetValue<bool>() ?? true;

        var extraRelics = trainer["startingRelics"] is JsonArray erArr
            ? erArr.Select(x => x?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList()
            : [];
        var extraCards = trainer["startingCards"] is JsonArray ecArr
            ? ecArr.Select(x => x is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0).Where(n => n > 0).ToList()
            : [];

        if (unlockAllChars)
        {
            var playableIds = content["playable"]!.AsArray()
                .Select(n => n is JsonValue v && v.TryGetValue<int>(out var idx) ? idx : -1)
                .Where(idx => idx >= 0)
                .Distinct()
                .ToList();
            var storage = campaign["storage.py"]!.GetValue<string>();
            const string mark = "# mod-trainer:unlock-all";
            if (!storage.Contains(mark, StringComparison.Ordinal))
            {
                var line = $"\n p['unlocked']=sorted(set(p.get('unlocked') or [])|set([{string.Join(",", playableIds)}]))  {mark}";
                storage = storage.Replace("p.setdefault(k,v)", "p.setdefault(k,v)" + line, StringComparison.Ordinal);
                campaign["storage.py"] = storage;
            }
        }

        if (unlockAllVars)
        {
            unlockAllVariantsJs = true;
            var cv = campaign["character_variants.py"]!.GetValue<string>();
            const string mark = "# mod-trainer:unlock-variants";
            if (!cv.Contains(mark, StringComparison.Ordinal))
            {
                cv = cv.Replace(
                    "def unlocked(profile,index):",
                    $"def unlocked(profile,index):\n return True  {mark}",
                    StringComparison.Ordinal);
                campaign["character_variants.py"] = cv;
            }
        }

        var needsCampaignHook = startingGold.HasValue || startingGoldBonus != 0 || startingLp.HasValue ||
                                freeShop || shopPriceMult.HasValue || rewardGoldMult.HasValue ||
                                extraRelics.Count > 0 || extraCards.Count > 0;

        if (needsCampaignHook)
        {
            var campPy = campaign["campaign.py"]!.GetValue<string>();
            var safeId = ModRepository.SanitizeId(modId).Replace('-', '_');
            var mark = $"# mod-trainer-hook:{safeId}";
            if (!campPy.Contains(mark, StringComparison.Ordinal))
            {
                var sb = new StringBuilder();
                sb.Append("\n\n").Append(mark).Append('\n');
                sb.Append($"_tr_new_run_{safeId}=new_run\n");
                sb.Append($"def new_run(*args,**kwargs):\n");
                sb.Append($" r=_tr_new_run_{safeId}(*args,**kwargs)\n");
                if (startingGold.HasValue) sb.Append($" r['gold']={startingGold.Value}\n");
                if (startingGoldBonus != 0) sb.Append($" r['gold']=int(r.get('gold',0))+{startingGoldBonus}\n");
                if (startingLp.HasValue) sb.Append($" r['lp']={startingLp.Value}\n");
                if (extraRelics.Count > 0)
                {
                    var relicsPy = string.Join(",", extraRelics.Select(PyStr));
                    sb.Append($" for _rk in [{relicsPy}]:\n");
                    sb.Append("  if _rk not in r.setdefault('artifacts',[]):r['artifacts'].append(_rk)\n");
                }

                if (extraCards.Count > 0)
                {
                    sb.Append($" r.setdefault('pool',[]).extend([{string.Join(",", extraCards)}])\n");
                }

                if (freeShop || shopPriceMult.HasValue)
                {
                    var mult = freeShop ? 0.0 : shopPriceMult!.Value;
                    sb.Append($" for _it in r.get('shop',[]):\n");
                    sb.Append($"  if isinstance(_it,dict) and 'price' in _it:_it['price']=max(0,int(_it['price']*{mult.ToString(CultureInfo.InvariantCulture)}))\n");
                }

                sb.Append(" return r\n");

                if (rewardGoldMult.HasValue)
                {
                    sb.Append($"_tr_rewards_{safeId}=rewards\n");
                    sb.Append("def rewards(*args,**kwargs):\n");
                    sb.Append($" _t,_g=_tr_rewards_{safeId}(*args,**kwargs)\n");
                    sb.Append($" return _t,int(_g*{rewardGoldMult.Value.ToString(CultureInfo.InvariantCulture)})\n");
                }

                if (freeShop || shopPriceMult.HasValue)
                {
                    var mult = freeShop ? 0.0 : shopPriceMult!.Value;
                    sb.Append($"if 'open_shop' in globals():\n");
                    sb.Append($" _tr_shop_{safeId}=open_shop\n");
                    sb.Append(" def open_shop(*args,**kwargs):\n");
                    sb.Append($"  _res=_tr_shop_{safeId}(*args,**kwargs)\n");
                    sb.Append("  _run=args[0] if args else None\n");
                    sb.Append("  if isinstance(_run,dict):\n");
                    sb.Append($"   for _it in _run.get('shop',[]):\n");
                    sb.Append($"    if isinstance(_it,dict) and 'price' in _it:_it['price']=max(0,int(_it['price']*{mult.ToString(CultureInfo.InvariantCulture)}))\n");
                    sb.Append("  return _res\n");
                }

                campaign["campaign.py"] = campPy.TrimEnd() + sb.ToString();
            }
        }

        if (inGameOverlay)
        {
            enableBuiltInTrainerOverlay = true;
            trainerOverlayConfig = trainer.DeepClone().AsObject();
        }

        interaction.Say($"trainer.json: 已应用修改器规则（全解锁={unlockAllChars}/{unlockAllVars}，起始金币={startingGold?.ToString() ?? $"+{startingGoldBonus}"}，免费商店={freeShop}，F8面板={inGameOverlay}）");
    }

    private static string PyStr(string text)
    {
        var escaped = text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return $"'{escaped}'";
    }

    private static string BuildMergedCardAssets(
        string text,
        IReadOnlyList<string> orderedCodes,
        IReadOnlyDictionary<string, string> bundledFileByCode,
        string? picsDir,
        IInstallerInteraction interaction)
    {
        const string trustedOld = "cardImages.set(id,`assets/cards/${id}.jpg`)";
        const string trustedNew = "cardImages.set(id,INLINE_ART[id]||`assets/cards/${id}.jpg`)";
        if (!text.Contains(trustedOld, StringComparison.Ordinal) &&
            !text.Contains(trustedNew, StringComparison.Ordinal))
        {
            throw new ModOperationException("card-assets.js 结构与预期不同（trusted 分支）");
        }

        text = text.Replace(trustedOld, trustedNew, StringComparison.Ordinal);

        const string workerAnchor = "  const path=`assets/cards/${id}.jpg`;";
        if (!text.Contains(workerAnchor, StringComparison.Ordinal))
        {
            throw new ModOperationException("card-assets.js 结构与预期不同（worker）");
        }

        const string shortcut = "  if(INLINE_ART[id]){cardImages.set(id,INLINE_ART[id]);loaded++;processed++;report();continue}\n";
        text = text.Replace(workerAnchor, shortcut + workerAnchor, StringComparison.Ordinal);

        var sb = new StringBuilder();
        sb.Append("const INLINE_ART={");
        var first = true;
        var artCount = 0;
        var missing = new List<string>();

        foreach (var code in orderedCodes)
        {
            if (bundledFileByCode.TryGetValue(code, out var bundled) && File.Exists(bundled))
            {
                var mime = Path.GetExtension(bundled).ToLowerInvariant() switch
                {
                    ".webp" => "image/webp",
                    ".png" => "image/png",
                    _ => "image/jpeg"
                };

                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(code).Append("\":\"data:").Append(mime).Append(";base64,");
                sb.Append(Convert.ToBase64String(File.ReadAllBytes(bundled)));
                sb.Append('"');
                artCount++;
                continue;
            }

            var picFile = string.IsNullOrWhiteSpace(picsDir) ? null : Path.Combine(picsDir, $"{code}.jpg");
            if (picFile is null || !File.Exists(picFile))
            {
                missing.Add(code);
                continue;
            }

            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(code).Append("\":\"data:image/jpeg;base64,");
            sb.Append(CardArtEncoder.ConvertArtToBase64(picFile));
            sb.Append('"');
            artCount++;
        }

        sb.Append("};\n");
        if (missing.Count > 0)
        {
            interaction.Warn("这些卡没找到卡图，会显示空白：" + string.Join(", ", missing));
        }

        interaction.Say($"card-assets.js: 共内联卡图 {artCount} 张");
        return sb.ToString() + text;
    }

    private static string UpdateServiceWorkerHashes(
        string swText,
        IReadOnlyDictionary<string, byte[]> files,
        IInstallerInteraction interaction)
    {
        var stamp = new StringBuilder();
        var updatedCount = 0;

        foreach (var rel in files.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (string.Equals(rel, "sw.js", StringComparison.Ordinal)) continue;
            var pattern = $"\"{Regex.Escape(rel)}\":\"[0-9a-f]{{16}}\"";
            if (!Regex.IsMatch(swText, pattern)) continue;

            var hash = ModPatcher.ComputeSha256Hex(files[rel])[..16];
            swText = Regex.Replace(swText, pattern, $"\"{rel}\":\"{hash}\"");
            stamp.Append(hash);
            updatedCount++;
        }

        if (updatedCount > 0)
        {
            var version2 = ModPatcher.ComputeSha256Hex(ModPatcher.GetUtf8Bytes(stamp.ToString()))[..16];
            swText = Regex.Replace(swText, @"const VERSION=""[0-9a-f]{16}""", $"const VERSION=\"{version2}\"");
            interaction.Say($"sw.js: 已更新 {updatedCount} 个哈希，缓存版本 {version2}");
        }

        return swText;
    }
}
