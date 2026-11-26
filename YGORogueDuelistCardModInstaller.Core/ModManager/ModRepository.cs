using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace YGORogueDuelistCardModInstaller.Core.ModManager;

/// <summary>
/// Manages the local mods/ library, mod-manifest.json discovery, importing zip/folder mods,
/// bootstrapping legacy root data/mod.json + art/, and persisting manager-state.json.
/// 100% Native AOT compatible via CoreJsonContext.
/// </summary>
public sealed partial class ModRepository
{
    public const string ManifestFileName = "mod-manifest.json";
    public const string StateFileName = "manager-state.json";
    public const string DefaultLegacyModId = "mdpro3-card-pack";

    [GeneratedRegex(@"[^a-zA-Z0-9_\-]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonSlugCharsRegex();

    public string WorkspaceRoot { get; }
    public string ModsRoot => Path.Combine(WorkspaceRoot, "mods");
    public string StatePath => Path.Combine(WorkspaceRoot, StateFileName);
    public string BackupDir => Path.Combine(WorkspaceRoot, "backup");

    public ModRepository(string? workspaceRoot = null)
    {
        WorkspaceRoot = ResolveWorkspaceRoot(workspaceRoot);
    }

    public static string ResolveWorkspaceRoot(string? explicitRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            return Path.GetFullPath(explicitRoot.Trim());
        }

        string[] seeds = [AppContext.BaseDirectory, Environment.CurrentDirectory];
        foreach (var seed in seeds)
        {
            var dir = new DirectoryInfo(Path.GetFullPath(seed));
            for (var depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, StateFileName)) ||
                    Directory.Exists(Path.Combine(dir.FullName, "mods")) ||
                    File.Exists(Path.Combine(dir.FullName, "data", "mod.json")))
                {
                    return dir.FullName;
                }
            }
        }

        return GameLocator.ResolveRootDirectory();
    }

    public ModManagerState LoadState()
    {
        if (!File.Exists(StatePath))
        {
            return new ModManagerState();
        }

        try
        {
            var text = File.ReadAllText(StatePath, Encoding.UTF8);
            return JsonSerializer.Deserialize(text, CoreJsonContext.PrettyUtf8.ModManagerState) ?? new ModManagerState();
        }
        catch
        {
            return new ModManagerState();
        }
    }

    public void SaveState(ModManagerState state)
    {
        Directory.CreateDirectory(WorkspaceRoot);
        var json = JsonSerializer.Serialize(state, CoreJsonContext.PrettyUtf8.ModManagerState);
        File.WriteAllText(StatePath, json, ModPatcher.Utf8NoBom);
    }

    public bool EnsureLegacyModBootstrapped()
    {
        Directory.CreateDirectory(ModsRoot);
        var legacyModJson = Path.Combine(WorkspaceRoot, "data", "mod.json");
        if (!File.Exists(legacyModJson))
        {
            return false;
        }

        var existingMods = Directory.GetDirectories(ModsRoot);
        if (existingMods.Length > 0)
        {
            return false;
        }

        var targetDir = Path.Combine(ModsRoot, DefaultLegacyModId);
        Directory.CreateDirectory(Path.Combine(targetDir, "data"));
        File.Copy(legacyModJson, Path.Combine(targetDir, "data", "mod.json"), overwrite: true);

        var legacyArtDir = Path.Combine(WorkspaceRoot, "art");
        if (Directory.Exists(legacyArtDir))
        {
            CopyDirectory(legacyArtDir, Path.Combine(targetDir, "art"));
        }

        var summary = InspectLegacyModJson(legacyModJson);
        var manifest = new ModManifest
        {
            Id = DefaultLegacyModId,
            Name = "MDPro3 扩展卡包 & 遗物 MOD",
            Version = summary.Version ?? "1.0.0",
            Author = "Community",
            Description = $"从原安装包自动导入：{summary.CardCount} 张卡片、{summary.ScriptCount} 个 Lua 脚本、{summary.DeckCount} 套角色起始卡组{(summary.HasRelic ? "与自定义遗物" : string.Empty)}。",
            GameVersion = summary.Version,
            Tags = ["cards", "lua", "decks", "relics"]
        };

        WriteManifest(targetDir, manifest);

        var state = LoadState();
        if (!state.Mods.Any(m => string.Equals(m.Id, manifest.Id, StringComparison.OrdinalIgnoreCase)))
        {
            state.Mods.Add(new ModEntryState { Id = manifest.Id, Enabled = true, Order = 0 });
            SaveState(state);
        }

        return true;
    }

    public List<ManagedMod> LoadMods(ModManagerState? state = null)
    {
        EnsureLegacyModBootstrapped();
        state ??= LoadState();

        var stateById = state.Mods
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var discovered = new List<ManagedMod>();
        if (!Directory.Exists(ModsRoot))
        {
            return discovered;
        }

        var nextOrder = state.Mods.Count > 0 ? state.Mods.Max(m => m.Order) + 1 : 0;
        var stateChanged = false;

        foreach (var dir in Directory.GetDirectories(ModsRoot).OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
        {
            var folderName = Path.GetFileName(dir);
            if (folderName.StartsWith('.'))
            {
                continue;
            }

            var manifest = ReadOrCreateManifest(dir, folderName);
            var stats = InspectModDirectory(dir, manifest);

            if (!stateById.TryGetValue(manifest.Id, out var entry))
            {
                entry = new ModEntryState
                {
                    Id = manifest.Id,
                    Enabled = true,
                    Order = nextOrder++
                };
                state.Mods.Add(entry);
                stateById[manifest.Id] = entry;
                stateChanged = true;
            }

            discovered.Add(new ManagedMod
            {
                Id = manifest.Id,
                DirectoryPath = dir,
                Manifest = manifest,
                Stats = stats,
                Enabled = entry.Enabled,
                Order = entry.Order
            });
        }

        var liveIds = discovered.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (state.Mods.RemoveAll(m => !liveIds.Contains(m.Id)) > 0)
        {
            stateChanged = true;
        }

        discovered.Sort((a, b) =>
        {
            var cmp = a.Order.CompareTo(b.Order);
            return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(a.Id, b.Id);
        });

        for (var i = 0; i < discovered.Count; i++)
        {
            if (discovered[i].Order != i)
            {
                discovered[i].Order = i;
                stateById[discovered[i].Id].Order = i;
                stateChanged = true;
            }
        }

        if (stateChanged)
        {
            SaveState(state);
        }

        return discovered;
    }

    public void SaveModOrderAndFlags(IReadOnlyList<ManagedMod> mods, ModManagerState state)
    {
        state.Mods = mods.Select((m, idx) =>
        {
            m.Order = idx;
            return new ModEntryState
            {
                Id = m.Id,
                Enabled = m.Enabled,
                Order = idx
            };
        }).ToList();

        SaveState(state);
    }

    public ManagedMod ImportMod(string sourcePath, bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ModOperationException("请指定要导入的 MOD 文件（.zip）或目录。");
        }

        var fullSource = Path.GetFullPath(sourcePath);
        Directory.CreateDirectory(ModsRoot);

        string? tempDir = null;
        try
        {
            string modSourceDir;
            string suggestedName;

            if (File.Exists(fullSource) && fullSource.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                tempDir = Path.Combine(Path.GetTempPath(), "ygo-mod-import-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                ZipFile.ExtractToDirectory(fullSource, tempDir, overwriteFiles: true);
                modSourceDir = UnwrapSingleFolder(tempDir);
                suggestedName = Path.GetFileNameWithoutExtension(fullSource);
            }
            else if (Directory.Exists(fullSource))
            {
                modSourceDir = fullSource;
                suggestedName = Path.GetFileName(fullSource.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            else
            {
                throw new ModOperationException($"找不到要导入的路径：{fullSource}");
            }

            if (!LooksLikeModFolder(modSourceDir))
            {
                throw new ModOperationException(
                    "所选目录/压缩包不包含可识别的 MOD 内容（需包含 mod-manifest.json、cards\\、characters\\、relics\\、trainer.json、lua\\、js\\、styles\\、data\\mod.json、scripts\\、campaign\\、art\\ 或 overrides\\ 之一）。");
            }

            var manifest = ReadOrCreateManifest(modSourceDir, suggestedName, persistIfMissing: false);
            var slug = SanitizeId(string.IsNullOrWhiteSpace(manifest.Id) ? suggestedName : manifest.Id);
            manifest.Id = slug;

            var destDir = Path.Combine(ModsRoot, slug);
            if (Directory.Exists(destDir))
            {
                if (!overwrite)
                {
                    throw new ModOperationException($"MOD \"{slug}\" 已存在。如需覆盖请先删除旧版本或指定覆盖导入。");
                }

                Directory.Delete(destDir, recursive: true);
            }

            CopyDirectory(modSourceDir, destDir);
            WriteManifest(destDir, manifest);

            var state = LoadState();
            var existing = state.Mods.FirstOrDefault(m => string.Equals(m.Id, slug, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                var nextOrder = state.Mods.Count > 0 ? state.Mods.Max(m => m.Order) + 1 : 0;
                state.Mods.Add(new ModEntryState { Id = slug, Enabled = true, Order = nextOrder });
                SaveState(state);
            }

            return LoadMods(state).First(m => string.Equals(m.Id, slug, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (tempDir is not null && Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    public ManagedMod CreateTemplateMod(
        string id,
        string? name = null,
        string? author = null,
        string? description = null,
        string? gameVersion = null,
        string? minGameVersion = null)
    {
        var slug = SanitizeId(id);
        if (string.IsNullOrWhiteSpace(slug))
        {
            throw new ModOperationException("MOD ID 不能为空。");
        }

        Directory.CreateDirectory(ModsRoot);
        var dir = Path.Combine(ModsRoot, slug);
        if (Directory.Exists(dir))
        {
            throw new ModOperationException($"MOD \"{slug}\" 已存在：{dir}");
        }

        Directory.CreateDirectory(Path.Combine(dir, "cards"));
        Directory.CreateDirectory(Path.Combine(dir, "characters"));
        Directory.CreateDirectory(Path.Combine(dir, "relics"));
        Directory.CreateDirectory(Path.Combine(dir, "packs"));
        Directory.CreateDirectory(Path.Combine(dir, "preconstructed"));
        Directory.CreateDirectory(Path.Combine(dir, "decks"));
        Directory.CreateDirectory(Path.Combine(dir, "lua", "cards"));
        Directory.CreateDirectory(Path.Combine(dir, "lua", "lib"));
        Directory.CreateDirectory(Path.Combine(dir, "js"));
        Directory.CreateDirectory(Path.Combine(dir, "styles"));
        Directory.CreateDirectory(Path.Combine(dir, "art"));
        Directory.CreateDirectory(Path.Combine(dir, "campaign"));
        Directory.CreateDirectory(Path.Combine(dir, "overrides"));

        var displayName = string.IsNullOrWhiteSpace(name) ? slug : name.Trim();
        var configObj = new JsonObject { ["showBootToast"] = true };
        var manifest = new ModManifest
        {
            SchemaVersion = 1,
            Id = slug,
            Name = displayName,
            Version = "1.0.0",
            Author = string.IsNullOrWhiteSpace(author) ? Environment.UserName : author.Trim(),
            Description = string.IsNullOrWhiteSpace(description)
                ? "标准 v1 MOD 模板（支持 Xmake 风格 mod.lua/mod.js DSL、新卡牌、角色、遗物、卡包、预组卡组、修改器 trainer.json、Lua 与 JS 插件）。"
                : description.Trim(),
            GameVersion = gameVersion,
            MinGameVersion = string.IsNullOrWhiteSpace(minGameVersion) ? null : minGameVersion.Trim(),
            MinManagerVersion = ModManifest.CurrentManagerVersion,
            Tags = ["custom", "lua", "js"],
            JsEntries = ["js/main.js"],
            CssEntries = ["styles/mod.css"],
            Config = configObj
        };

        WriteManifest(dir, manifest);

        var jsStarter = $$"""
            // {{displayName}} - JS Plugin Entry (ES Module)
            export async function init(ctx) {
              ctx.log('JS plugin initialized', ctx.mod);

              if (ctx.config?.showBootToast !== false) {
                ctx.showToast(`Loaded MOD: ${ctx.mod.name} v${ctx.mod.version}`);
              }

              ctx.onUiChange((root) => {
                // Safely inspect or enhance #app DOM here (keep mutations idempotent).
              });
            }
            """;
        File.WriteAllText(Path.Combine(dir, "js", "main.js"), jsStarter, ModPatcher.Utf8NoBom);

        var cssStarter = $"/* {displayName} - Custom stylesheet (auto-loaded by mod-runtime.js) */\n";
        File.WriteAllText(Path.Combine(dir, "styles", "mod.css"), cssStarter, ModPatcher.Utf8NoBom);

        var readme = """
            # MOD Directory Layout (Mod Spec v1 + Xmake-style Dual-Scope DSL)

            - `mod-manifest.json`      : Metadata, minGameVersion/minManagerVersion, JS/CSS/Lua entry points, and `config`
            - `mod.lua` / `mod.js`     : Optional Xmake-style Description Scope + Script Scope DSL
            - `cards/*.json`           : Declarative custom cards (auto-synced to content/engine/era/zh)
            - `characters/*.json`      : New playable characters or edits to existing characters
            - `relics/*.json`          : New relics (with art, i18n, and Lua/Python duel effects)
            - `packs/*.json`           : Custom booster packs (common/rare/super/ultra pools)
            - `preconstructed/*.json`  : Custom preconstructed / structure decks
            - `decks/*.ydk`            : Standard YGOPro .ydk decklists (auto-converted to preconstructed decks)
            - `trainer.json`           : Declarative trainer/modifier (unlock all, starting gold/LP, free shop, F8 overlay)
            - `lua/cards/c<code>.lua`  : ocgcore-wasm Lua card scripts
            - `lua/lib/*.lua`          : Shared Lua helper libraries appended to utility.lua
            - `js/main.js`             : ES Module JS plugin using `window.YgoModAPI`
            - `styles/*.css`           : Auto-injected CSS stylesheets
            - `art/<code>.webp|jpg|png`: Inline card / relic artwork
            - `campaign/...`           : Direct overrides into campaign-files.json
            - `overrides/...`          : Arbitrary static asset overrides or new files (VirtualBundle mode)
            """;
        File.WriteAllText(Path.Combine(dir, "README.txt"), readme, ModPatcher.Utf8NoBom);

        var state = LoadState();
        var nextOrder = state.Mods.Count > 0 ? state.Mods.Max(m => m.Order) + 1 : 0;
        state.Mods.Add(new ModEntryState { Id = slug, Enabled = true, Order = nextOrder });
        SaveState(state);

        return LoadMods(state).First(m => string.Equals(m.Id, slug, StringComparison.OrdinalIgnoreCase));
    }

    public void DeleteMod(string id)
    {
        var state = LoadState();
        var mods = LoadMods(state);
        var target = mods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ModOperationException($"找不到 MOD：{id}");

        if (Directory.Exists(target.DirectoryPath))
        {
            Directory.Delete(target.DirectoryPath, recursive: true);
        }

        state.Mods.RemoveAll(m => string.Equals(m.Id, target.Id, StringComparison.OrdinalIgnoreCase));
        SaveState(state);
    }

    public static List<string> ResolveJsEntries(string modDir, ModManifest manifest)
    {
        if (manifest.JsEntries is { Count: > 0 })
        {
            return manifest.JsEntries
                .Select(NormalizeRelPath)
                .Where(rel => File.Exists(Path.Combine(modDir, rel.Replace('/', Path.DirectorySeparatorChar))))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var jsDir = Path.Combine(modDir, "js");
        if (!Directory.Exists(jsDir)) return [];

        if (File.Exists(Path.Combine(jsDir, "main.js"))) return ["js/main.js"];

        return Directory.EnumerateFiles(jsDir, "*.js", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => Path.GetRelativePath(modDir, p).Replace('\\', '/'))
            .ToList();
    }

    public static List<string> ResolveCssEntries(string modDir, ModManifest manifest)
    {
        if (manifest.CssEntries is { Count: > 0 })
        {
            return manifest.CssEntries
                .Select(NormalizeRelPath)
                .Where(rel => File.Exists(Path.Combine(modDir, rel.Replace('/', Path.DirectorySeparatorChar))))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var stylesDir = Path.Combine(modDir, "styles");
        if (!Directory.Exists(stylesDir)) return [];

        return Directory.EnumerateFiles(stylesDir, "*.css", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => Path.GetRelativePath(modDir, p).Replace('\\', '/'))
            .ToList();
    }

    public static List<string> ResolveLuaLibs(string modDir, ModManifest manifest)
    {
        if (manifest.LuaLibs is { Count: > 0 })
        {
            return manifest.LuaLibs
                .Select(NormalizeRelPath)
                .Where(rel => File.Exists(Path.Combine(modDir, rel.Replace('/', Path.DirectorySeparatorChar))))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var libDir = Path.Combine(modDir, "lua", "lib");
        if (!Directory.Exists(libDir)) return [];

        return Directory.EnumerateFiles(libDir, "*.lua", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => Path.GetRelativePath(modDir, p).Replace('\\', '/'))
            .ToList();
    }

    public static IEnumerable<string> EnumerateLuaCardScriptFiles(string modDir)
    {
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var luaCardsDir = Path.Combine(modDir, "lua", "cards");
        if (Directory.Exists(luaCardsDir))
        {
            foreach (var file in Directory.EnumerateFiles(luaCardsDir, "*.lua", SearchOption.TopDirectoryOnly)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (seenNames.Add(Path.GetFileName(file))) yield return file;
            }
        }

        var scriptsDir = Path.Combine(modDir, "scripts");
        if (Directory.Exists(scriptsDir))
        {
            foreach (var file in Directory.EnumerateFiles(scriptsDir, "*.lua", SearchOption.TopDirectoryOnly)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (seenNames.Add(Path.GetFileName(file))) yield return file;
            }
        }
    }

    public static IEnumerable<string> EnumerateDeclarativeJsonFiles(string modDir, string subFolder)
    {
        var target = Path.Combine(modDir, subFolder);
        if (!Directory.Exists(target)) yield break;

        foreach (var file in Directory.EnumerateFiles(target, "*.json", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            yield return file;
        }
    }

    public static IEnumerable<string> EnumerateDeclarativeCardFiles(string modDir) =>
        EnumerateDeclarativeJsonFiles(modDir, "cards");

    public static IEnumerable<string> EnumerateDeclarativeCharacterFiles(string modDir) =>
        EnumerateDeclarativeJsonFiles(modDir, "characters");

    public static IEnumerable<string> EnumerateDeclarativeRelicFiles(string modDir) =>
        EnumerateDeclarativeJsonFiles(modDir, "relics");

    public static IEnumerable<string> EnumerateDeclarativePackFiles(string modDir) =>
        EnumerateDeclarativeJsonFiles(modDir, "packs");

    public static IEnumerable<string> EnumerateDeclarativePreconstructedFiles(string modDir) =>
        EnumerateDeclarativeJsonFiles(modDir, "preconstructed");

    public static IEnumerable<string> EnumerateYdkFiles(string modDir)
    {
        var dir = Path.Combine(modDir, "decks");
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.ydk", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            yield return f;
        }
    }

    public static List<ModVersionIssue> EvaluateVersionIssues(
        IReadOnlyList<ManagedMod> orderedMods,
        string? baseGameVersion)
    {
        var issues = new List<ModVersionIssue>();
        foreach (var mod in orderedMods.Where(m => m.Enabled))
        {
            var m = mod.Manifest;
            if (!string.IsNullOrWhiteSpace(m.MinManagerVersion) &&
                GameLocator.CompareVersionText(ModManifest.CurrentManagerVersion, m.MinManagerVersion) < 0)
            {
                issues.Add(new ModVersionIssue(
                    mod.Id,
                    m.Name,
                    $"要求管理器最低版本 v{m.MinManagerVersion}（当前 v{ModManifest.CurrentManagerVersion}）"));
            }

            if (!string.IsNullOrWhiteSpace(baseGameVersion))
            {
                if (!string.IsNullOrWhiteSpace(m.MinGameVersion) &&
                    GameLocator.CompareVersionText(baseGameVersion, m.MinGameVersion) < 0)
                {
                    issues.Add(new ModVersionIssue(
                        mod.Id,
                        m.Name,
                        $"要求游戏最低版本 v{m.MinGameVersion}（当前原版 v{baseGameVersion}）"));
                }

                if (!string.IsNullOrWhiteSpace(m.MaxGameVersion) &&
                    GameLocator.CompareVersionText(baseGameVersion, m.MaxGameVersion) > 0)
                {
                    issues.Add(new ModVersionIssue(
                        mod.Id,
                        m.Name,
                        $"最高适配游戏版本 v{m.MaxGameVersion}（当前原版 v{baseGameVersion}）"));
                }
            }
        }

        return issues;
    }

    public static ModContentStats InspectModDirectory(string dir, ModManifest? manifest = null)
    {
        manifest ??= ReadOrCreateManifest(dir, Path.GetFileName(dir), persistIfMissing: false);
        var dsl = ModDslParser.TryParseModDirectory(dir);

        var legacyModPath = Path.Combine(dir, "data", "mod.json");
        var hasLegacy = File.Exists(legacyModPath);
        var cards = 0;
        var characters = 0;
        var relics = 0;
        var packs = 0;
        var preconDecks = 0;
        var scripts = 0;
        var decks = 0;

        if (hasLegacy)
        {
            var info = InspectLegacyModJson(legacyModPath);
            cards += info.CardCount;
            scripts += info.ScriptCount;
            decks += info.DeckCount;
            if (info.HasRelic) relics++;
        }

        cards += CountJsonEntries(EnumerateDeclarativeCardFiles(dir)) + (dsl?.InlineCards.Count ?? 0);
        characters += CountJsonEntries(EnumerateDeclarativeCharacterFiles(dir)) + (dsl?.InlineCharacters.Count ?? 0);
        relics += CountJsonEntries(EnumerateDeclarativeRelicFiles(dir)) + (dsl?.InlineRelics.Count ?? 0);
        packs += CountJsonEntries(EnumerateDeclarativePackFiles(dir)) + (dsl?.InlinePacks.Count ?? 0);
        preconDecks += CountJsonEntries(EnumerateDeclarativePreconstructedFiles(dir)) + (dsl?.InlinePreconstructedDecks.Count ?? 0);
        scripts += EnumerateLuaCardScriptFiles(dir).Count() + (dsl?.InlineLuaCardScripts.Count ?? 0);

        var luaLibCount = ResolveLuaLibs(dir, manifest).Count + (dsl?.InlineLuaUtilityBlocks.Count ?? 0);
        var jsPluginCount = ResolveJsEntries(dir, manifest).Count + (dsl?.HasGeneratedJsPlugin == true ? 1 : 0);
        var cssStyleCount = ResolveCssEntries(dir, manifest).Count;
        var hasTrainer = File.Exists(Path.Combine(dir, "trainer.json")) || dsl?.InlineTrainer is not null;

        var decksJsonPath = Path.Combine(dir, "decks.json");
        if (File.Exists(decksJsonPath))
        {
            try
            {
                var obj = JsonNode.Parse(File.ReadAllText(decksJsonPath, Encoding.UTF8))?.AsObject();
                decks += obj?.Count ?? 0;
            }
            catch { }
        }

        decks += EnumerateYdkFiles(dir).Count();

        var artCount = 0;
        var artDir = Path.Combine(dir, "art");
        if (Directory.Exists(artDir))
        {
            artCount = Directory.EnumerateFiles(artDir).Count();
        }

        var campaignCount = 0;
        var campaignDir = Path.Combine(dir, "campaign");
        if (Directory.Exists(campaignDir))
        {
            campaignCount = Directory.EnumerateFiles(campaignDir, "*", SearchOption.AllDirectories).Count();
        }

        var overridePaths = new List<string>();
        var overridesDir = Path.Combine(dir, "overrides");
        if (Directory.Exists(overridesDir))
        {
            foreach (var file in Directory.EnumerateFiles(overridesDir, "*", SearchOption.AllDirectories))
            {
                overridePaths.Add(Path.GetRelativePath(overridesDir, file).Replace('\\', '/'));
            }

            overridePaths.Sort(StringComparer.OrdinalIgnoreCase);
        }

        return new ModContentStats(
            hasLegacy,
            dsl?.HasLuaDsl ?? false,
            dsl?.HasJsDsl ?? false,
            cards,
            characters,
            relics,
            packs,
            preconDecks,
            scripts,
            luaLibCount,
            jsPluginCount,
            cssStyleCount,
            decks,
            artCount,
            campaignCount,
            overridePaths.Count,
            hasTrainer,
            overridePaths);
    }

    private static int CountJsonEntries(IEnumerable<string> files)
    {
        var count = 0;
        foreach (var f in files)
        {
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(f, Encoding.UTF8));
                if (node is JsonArray arr) count += arr.Count;
                else if (node is JsonObject) count += 1;
            }
            catch { }
        }

        return count;
    }

    private static ModManifest ReadOrCreateManifest(string dir, string fallbackId, bool persistIfMissing = true)
    {
        ModManifest? manifest = null;
        var manifestPath = Path.Combine(dir, ManifestFileName);
        if (File.Exists(manifestPath))
        {
            try
            {
                manifest = JsonSerializer.Deserialize(File.ReadAllText(manifestPath, Encoding.UTF8), CoreJsonContext.PrettyUtf8.ModManifest);
            }
            catch { }
        }

        var dsl = ModDslParser.TryParseModDirectory(dir);
        if (manifest is null)
        {
            var legacyModPath = Path.Combine(dir, "data", "mod.json");
            var legacyInfo = File.Exists(legacyModPath) ? InspectLegacyModJson(legacyModPath) : default;
            var slug = SanitizeId(dsl?.Id ?? fallbackId);
            manifest = new ModManifest
            {
                Id = slug,
                Name = dsl?.Name ?? fallbackId,
                Version = dsl?.Version ?? legacyInfo.Version ?? "1.0.0",
                Author = dsl?.Author ?? "Unknown",
                Description = dsl?.Description ?? (File.Exists(legacyModPath)
                    ? $"Card MOD ({legacyInfo.CardCount} cards, {legacyInfo.ScriptCount} scripts)"
                    : "Custom Lua / JS / Content MOD"),
                GameVersion = dsl?.GameVersion ?? legacyInfo.Version
            };
        }

        if (dsl is not null)
        {
            ModDslParser.MergeIntoManifest(dsl, manifest);
        }

        if (string.IsNullOrWhiteSpace(manifest.Id)) manifest.Id = SanitizeId(fallbackId);
        if (string.IsNullOrWhiteSpace(manifest.Name)) manifest.Name = manifest.Id;

        if (persistIfMissing && !File.Exists(manifestPath) && dsl is null)
        {
            try { WriteManifest(dir, manifest); } catch { }
        }

        return manifest;
    }

    public static void WriteManifest(string dir, ModManifest manifest)
    {
        var path = Path.Combine(dir, ManifestFileName);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, CoreJsonContext.PrettyUtf8.ModManifest), ModPatcher.Utf8NoBom);
    }

    private static (string? Version, int CardCount, int ScriptCount, int DeckCount, bool HasRelic) InspectLegacyModJson(string path)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))?.AsObject();
            if (node is null) return default;
            var version = node["version"]?.GetValue<string>();
            var cards = node["cards"]?.AsArray().Count ?? 0;
            var scripts = node["scripts"]?.AsObject().Count ?? 0;
            var decks = node["decks"]?.AsObject().Count ?? 0;
            var hasRelic = node["relic"] is JsonObject;
            return (version, cards, scripts, decks, hasRelic);
        }
        catch
        {
            return default;
        }
    }

    private static bool LooksLikeModFolder(string dir) =>
        File.Exists(Path.Combine(dir, ManifestFileName)) ||
        File.Exists(Path.Combine(dir, ModDslParser.LuaDslFileName)) ||
        File.Exists(Path.Combine(dir, ModDslParser.JsDslFileName)) ||
        File.Exists(Path.Combine(dir, ModDslParser.MjsDslFileName)) ||
        File.Exists(Path.Combine(dir, "data", "mod.json")) ||
        File.Exists(Path.Combine(dir, "trainer.json")) ||
        Directory.Exists(Path.Combine(dir, "cards")) ||
        Directory.Exists(Path.Combine(dir, "characters")) ||
        Directory.Exists(Path.Combine(dir, "relics")) ||
        Directory.Exists(Path.Combine(dir, "packs")) ||
        Directory.Exists(Path.Combine(dir, "preconstructed")) ||
        Directory.Exists(Path.Combine(dir, "decks")) ||
        Directory.Exists(Path.Combine(dir, "lua")) ||
        Directory.Exists(Path.Combine(dir, "js")) ||
        Directory.Exists(Path.Combine(dir, "styles")) ||
        Directory.Exists(Path.Combine(dir, "scripts")) ||
        Directory.Exists(Path.Combine(dir, "campaign")) ||
        Directory.Exists(Path.Combine(dir, "art")) ||
        Directory.Exists(Path.Combine(dir, "overrides")) ||
        File.Exists(Path.Combine(dir, "zh-cards.json")) ||
        File.Exists(Path.Combine(dir, "zh-ui.json")) ||
        File.Exists(Path.Combine(dir, "decks.json"));

    private static string UnwrapSingleFolder(string dir)
    {
        if (LooksLikeModFolder(dir)) return dir;
        var subDirs = Directory.GetDirectories(dir);
        var files = Directory.GetFiles(dir);
        return subDirs.Length == 1 && files.Length == 0 ? subDirs[0] : dir;
    }

    private static string NormalizeRelPath(string raw) =>
        raw.Trim().Replace('\\', '/').TrimStart('/');

    public static string SanitizeId(string raw)
    {
        var cleaned = NonSlugCharsRegex().Replace(raw.Trim(), "-").Trim('-').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(cleaned) ? "custom-mod" : cleaned;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
        }
    }
}
