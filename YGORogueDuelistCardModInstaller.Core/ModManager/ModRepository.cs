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

            if (File.Exists(fullSource) && YpkImporter.IsYpkArchive(fullSource))
            {
                return YpkImporter.ImportYpk(this, fullSource, overwrite).Mod;
            }

            if (File.Exists(fullSource) && ModPackager.IsModArchiveExtension(fullSource))
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

    public static readonly string[] StandardCategories =
    [
        "cards",
        "characters",
        "relics",
        "packs_decks",
        "ui_theme",
        "script_plugin",
        "trainer",
        "overhaul",
        "other"
    ];

    public ManagedMod CreateTemplateMod(
        string id,
        string? name = null,
        string? author = null,
        string? description = null,
        string? gameVersion = null,
        string? minGameVersion = null,
        string? category = null,
        string? coverSourcePath = null)
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

        string? coverRel = null;
        if (!string.IsNullOrWhiteSpace(coverSourcePath) && File.Exists(coverSourcePath))
        {
            coverRel = CopyCoverImage(dir, coverSourcePath);
        }

        var displayName = string.IsNullOrWhiteSpace(name) ? slug : name.Trim();
        var configObj = new JsonObject { ["showBootToast"] = true };
        var starterSettings = new JsonArray(
            (JsonNode?)new JsonObject
            {
                ["key"] = "showBootToast",
                ["type"] = "boolean",
                ["label"] = "Show Boot Toast",
                ["zhLabel"] = "启动时显示加载提示",
                ["description"] = "Show a notification banner when this MOD initializes.",
                ["zhDescription"] = "游戏启动加载本 MOD 时在顶部显示提示条。",
                ["default"] = true
            });

        var manifest = new ModManifest
        {
            SchemaVersion = 1,
            Id = slug,
            Name = displayName,
            Version = "1.0.0",
            Author = string.IsNullOrWhiteSpace(author) ? Environment.UserName : author.Trim(),
            Description = string.IsNullOrWhiteSpace(description)
                ? "标准 v1 MOD 模板（已启用所有 Entries 自动导入 + 游戏内 F9 MOD 设置，支持卡牌/角色/遗物/卡包/卡组/Lua/JS/CSS）。"
                : description.Trim(),
            Category = NormalizeCategory(category),
            Cover = coverRel,
            AutoDiscoverEntries = true,
            GameVersion = gameVersion,
            MinGameVersion = string.IsNullOrWhiteSpace(minGameVersion) ? null : minGameVersion.Trim(),
            MinManagerVersion = ModManifest.CurrentManagerVersion,
            Tags = ["custom", "lua", "js"],
            Config = configObj,
            SettingsSchema = starterSettings
        };

        WriteManifest(dir, manifest);

        var jsStarter = $$"""
            // {{displayName}} - JS Plugin Entry (ES Module, auto-imported from js/)
            export async function init(ctx) {
              ctx.log('JS plugin initialized', ctx.mod);

              const showToast = ctx.settings.get('showBootToast', ctx.config?.showBootToast !== false);
              if (showToast) {
                ctx.showToast(`Loaded MOD: ${ctx.mod.name} v${ctx.mod.version}`);
              }

              ctx.settings.onChange((key, value) => {
                ctx.log('Setting changed:', key, '=', value);
              });

              ctx.onUiChange((root) => {
                // Safely inspect or enhance #app DOM here (keep mutations idempotent).
              });
            }
            """;
        File.WriteAllText(Path.Combine(dir, "js", "main.js"), jsStarter, ModPatcher.Utf8NoBom);

        var cssStarter = $"/* {displayName} - Custom stylesheet (all CSS files in styles/ and css/ are auto-imported) */\n";
        File.WriteAllText(Path.Combine(dir, "styles", "mod.css"), cssStarter, ModPatcher.Utf8NoBom);

        var readme = """
            # MOD Directory Layout (Mod Spec v1 + Auto-Import Entries + In-Game MOD Settings)

            - `mod-manifest.json`      : Metadata (`category`, `cover`, `autoDiscoverEntries: true`, `settingsSchema`, `config`)
            - `settings.json`          : Optional standalone in-game MOD settings schema (rendered automatically in F9 -> MOD Settings)
            - `cover.png|webp|jpg|svg` : MOD cover image (auto-detected if placed in MOD root)
            - `mod.lua` / `mod.js`     : Optional Xmake-style Description Scope + Script Scope DSL
            - `cards/*.json`           : Declarative custom cards (auto-synced to content/engine/era/zh)
            - `characters/*.json`      : New playable characters or edits to existing characters
            - `relics/*.json`          : New relics (with art, i18n, and Lua/Python duel effects)
            - `packs/*.json`           : Custom booster packs (common/rare/super/ultra pools)
            - `preconstructed/*.json`  : Custom preconstructed / structure decks
            - `decks/*.ydk`            : Standard YGOPro .ydk decklists
            - `trainer.json`           : Declarative trainer/modifier (F8 live overlay)
            - `lua/cards/c<code>.lua`  : ocgcore-wasm Lua card scripts
            - `lua/lib/**/*.lua`       : Auto-imported shared Lua helper libraries appended to utility.lua
            - `js/**/*.js|mjs`         : Auto-imported ES Module JS plugins using `window.YgoModAPI` & `ctx.settings`
            - `styles/**/*.css` / `css/**/*.css`: Auto-imported CSS stylesheets
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

    public ManagedMod CreateCompleteExampleMod(string? id = null, string? gameVersion = null)
    {
        Directory.CreateDirectory(ModsRoot);
        var baseSlug = string.IsNullOrWhiteSpace(id) ? "complete-showcase-example" : SanitizeId(id);
        var slug = baseSlug;
        if (string.IsNullOrWhiteSpace(id))
        {
            var suffix = 2;
            while (Directory.Exists(Path.Combine(ModsRoot, slug)))
            {
                slug = $"{baseSlug}-{suffix++}";
            }
        }
        else if (Directory.Exists(Path.Combine(ModsRoot, slug)))
        {
            throw new ModOperationException($"MOD \"{slug}\" 已存在。");
        }

        var dir = Path.Combine(ModsRoot, slug);
        Directory.CreateDirectory(Path.Combine(dir, "cards"));
        Directory.CreateDirectory(Path.Combine(dir, "characters"));
        Directory.CreateDirectory(Path.Combine(dir, "relics"));
        Directory.CreateDirectory(Path.Combine(dir, "packs"));
        Directory.CreateDirectory(Path.Combine(dir, "preconstructed"));
        Directory.CreateDirectory(Path.Combine(dir, "lua", "cards"));
        Directory.CreateDirectory(Path.Combine(dir, "lua", "lib"));
        Directory.CreateDirectory(Path.Combine(dir, "js"));
        Directory.CreateDirectory(Path.Combine(dir, "styles"));
        Directory.CreateDirectory(Path.Combine(dir, "css"));
        Directory.CreateDirectory(Path.Combine(dir, "art"));

        // 1. Vector Cover Image (cover.svg)
        const string coverSvg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 400 240">
              <defs>
                <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
                  <stop offset="0%" stop-color="#0b1020"/>
                  <stop offset="55%" stop-color="#172038"/>
                  <stop offset="100%" stop-color="#271b3d"/>
                </linearGradient>
                <linearGradient id="gold" x1="0" y1="0" x2="1" y2="1">
                  <stop offset="0%" stop-color="#fde68a"/>
                  <stop offset="50%" stop-color="#f59e0b"/>
                  <stop offset="100%" stop-color="#d97706"/>
                </linearGradient>
              </defs>
              <rect width="400" height="240" rx="20" fill="url(#bg)"/>
              <circle cx="320" cy="48" r="90" fill="#38bdf8" opacity="0.10"/>
              <circle cx="72" cy="196" r="80" fill="#f59e0b" opacity="0.12"/>
              <polygon points="200,32 252,126 148,126" fill="none" stroke="url(#gold)" stroke-width="5" stroke-linejoin="round"/>
              <circle cx="200" cy="96" r="14" fill="none" stroke="#fde68a" stroke-width="3.5"/>
              <circle cx="200" cy="96" r="5" fill="#fde68a"/>
              <text x="200" y="166" text-anchor="middle" fill="#f8fafc" font-family="Segoe UI, sans-serif" font-size="22" font-weight="700">CHRONO SHOWCASE</text>
              <text x="200" y="192" text-anchor="middle" fill="#94a3b8" font-family="Segoe UI, sans-serif" font-size="13">Full Mod Spec v1 · Auto-Entries · In-Game Settings</text>
              <rect x="108" y="206" width="184" height="20" rx="10" fill="#f59e0b" opacity="0.18" stroke="#f59e0b" stroke-opacity="0.4"/>
              <text x="200" y="220" text-anchor="middle" fill="#fde68a" font-family="Consolas, monospace" font-size="10.5">F9 Settings · Cards · Lua · JS · CSS</text>
            </svg>
            """;
        File.WriteAllText(Path.Combine(dir, "cover.svg"), coverSvg, ModPatcher.Utf8NoBom);

        // 2. Manifest with category, cover, autoDiscoverEntries=true, and rich in-game settingsSchema
        var settingsSchema = new JsonArray(
            (JsonNode?)new JsonObject
            {
                ["key"] = "difficultyPreset",
                ["type"] = "select",
                ["label"] = "Difficulty Preset",
                ["zhLabel"] = "游戏难度档位",
                ["description"] = "Adjusts difficulty badge and custom balance hooks.",
                ["zhDescription"] = "调整本 MOD 的难度档位（实时生效，JS 插件可读取 ctx.settings.get）。",
                ["default"] = "standard",
                ["options"] = new JsonArray(
                    (JsonNode?)new JsonObject { ["value"] = "casual", ["label"] = "Casual (0.75x)", ["zhLabel"] = "休闲模式 (0.75x)" },
                    (JsonNode?)new JsonObject { ["value"] = "standard", ["label"] = "Standard (1.0x)", ["zhLabel"] = "标准模式 (1.0x)" },
                    (JsonNode?)new JsonObject { ["value"] = "hard", ["label"] = "Hard (1.5x)", ["zhLabel"] = "困难模式 (1.5x)" },
                    (JsonNode?)new JsonObject { ["value"] = "hell", ["label"] = "Nightmare (2.0x)", ["zhLabel"] = "噩梦模式 (2.0x)" })
            },
            (JsonNode?)new JsonObject
            {
                ["key"] = "speedMultiplier",
                ["type"] = "range",
                ["label"] = "UI & Duel Animation Speed",
                ["zhLabel"] = "界面与动画倍速",
                ["description"] = "Scales CSS animation & transition speed live in-game.",
                ["zhDescription"] = "实时加速或放慢游戏界面过渡与发牌动画倍速（无需重启）。",
                ["default"] = 1.5,
                ["min"] = 0.5,
                ["max"] = 3.0,
                ["step"] = 0.25,
                ["unit"] = "x"
            },
            (JsonNode?)new JsonObject
            {
                ["key"] = "bonusGoldPerWin",
                ["type"] = "number",
                ["label"] = "Bonus Gold Target",
                ["zhLabel"] = "目标额外金币提示",
                ["description"] = "Numeric setting example readable from JS plugins.",
                ["zhDescription"] = "数值输入框示例（可在 JS 插件中通过 ctx.settings.get('bonusGoldPerWin') 读取）。",
                ["default"] = 150,
                ["min"] = 0,
                ["max"] = 9999,
                ["step"] = 50
            },
            (JsonNode?)new JsonObject
            {
                ["key"] = "showHudPill",
                ["type"] = "boolean",
                ["label"] = "Show Floating Status Pill",
                ["zhLabel"] = "显示右上角状态胶囊",
                ["description"] = "Displays active difficulty & speed pill; click it to open F9 MOD Settings.",
                ["zhDescription"] = "在游戏中显示当前难度与倍速胶囊，点击可直达 F9 MOD 设置面板。",
                ["default"] = true
            },
            (JsonNode?)new JsonObject
            {
                ["key"] = "accentColor",
                ["type"] = "color",
                ["label"] = "Theme Accent Color",
                ["zhLabel"] = "主题强调色",
                ["description"] = "Live CSS custom property applied via auto-imported CSS.",
                ["zhDescription"] = "实时修改由自动导入 CSS 驱动的强调色变量。",
                ["default"] = "#f4cf57"
            });

        var manifest = new ModManifest
        {
            SchemaVersion = 1,
            Id = slug,
            Name = "时空编织者 · 完整规范示例 MOD",
            Version = "1.0.0",
            Author = "YGO MOD Studio",
            Description = "官方完整功能示例 MOD：演示分类/封面、全部 Entries 自动导入（双 JS + styles/&css/ 双 CSS + Lua 公共库）、新卡牌/角色/遗物/卡包/预组、修改器，以及游戏内 F9 实时难度/倍速设置面板。",
            Category = "overhaul",
            Cover = "cover.svg",
            AutoDiscoverEntries = true,
            GameVersion = gameVersion,
            MinGameVersion = gameVersion ?? "0.2.46",
            MinManagerVersion = ModManifest.CurrentManagerVersion,
            Tags = ["example", "showcase", "cards", "relics", "settings", "auto-entries"],
            Config = new JsonObject { ["showcase"] = true },
            SettingsSchema = settingsSchema
        };
        WriteManifest(dir, manifest);

        // 3. Custom Cards (cards/showcase-cards.json)
        var cardsJson = """
            [
              {
                "id": 190088001,
                "name": "Millennium Chrono Pot",
                "desc": "Draw 2 cards, then gain 600 LP. You can only activate 1 \"Millennium Chrono Pot\" per turn.",
                "zhName": "千年时空之壶",
                "zhDesc": "这个卡名的卡在1回合只能发动1张。①：自己从卡组抽2张，那之后自己回复600基本分。",
                "type": "Spell Card",
                "race": "Normal",
                "attribute": "",
                "level": 0,
                "atk": 0,
                "defense": 0,
                "addToEraPool": true,
                "data": {
                  "code": 190088001,
                  "alias": 0,
                  "setcodes": [],
                  "type": 2,
                  "level": 0,
                  "attribute": 0,
                  "race": "0",
                  "attack": 0,
                  "defense": 0,
                  "lscale": 0,
                  "rscale": 0,
                  "link_marker": 0
                },
                "strings": ["","","","","","","","","","","","","","","",""]
              },
              {
                "id": 190088002,
                "name": "Chrono Archmage",
                "desc": "If this card is Normal or Special Summoned: You can draw 1 card and inflict 400 damage to your opponent.",
                "zhName": "时空大魔导士",
                "zhDesc": "①：这张卡召唤·特殊召唤成功的场合才能发动。自己抽1张，给与对方400伤害。",
                "type": "Effect Monster",
                "race": "Spellcaster",
                "attribute": "LIGHT",
                "level": 4,
                "atk": 1800,
                "defense": 1400,
                "addToEraPool": true,
                "data": {
                  "code": 190088002,
                  "alias": 0,
                  "setcodes": [],
                  "type": 33,
                  "level": 4,
                  "attribute": 16,
                  "race": "2",
                  "attack": 1800,
                  "defense": 1400,
                  "lscale": 0,
                  "rscale": 0,
                  "link_marker": 0
                },
                "strings": ["","","","","","","","","","","","","","","",""]
              }
            ]
            """;
        File.WriteAllText(Path.Combine(dir, "cards", "showcase-cards.json"), cardsJson, ModPatcher.Utf8NoBom);

        // 4. Shared Lua Helper Library (auto-imported from lua/lib/)
        const string luaLib = """
            -- Auto-imported Lua helper library (lua/lib/showcase_helpers.lua)
            ChronoShowcase = ChronoShowcase or {}
            function ChronoShowcase.DrawAndRecover(tp, drawCount, lpGain)
                local drawn = Duel.Draw(tp, drawCount, REASON_EFFECT)
                if drawn > 0 and lpGain > 0 then
                    Duel.BreakEffect()
                    Duel.Recover(tp, lpGain, REASON_EFFECT)
                end
                return drawn
            end
            """;
        File.WriteAllText(Path.Combine(dir, "lua", "lib", "showcase_helpers.lua"), luaLib, ModPatcher.Utf8NoBom);

        // 5. Card Lua Scripts (lua/cards/)
        const string card1Lua = """
            -- Millennium Chrono Pot (190088001)
            local s, id, o = GetID()
            function s.initial_effect(c)
                local e1 = Effect.CreateEffect(c)
                e1:SetCategory(CATEGORY_DRAW + CATEGORY_RECOVER)
                e1:SetType(EFFECT_TYPE_ACTIVATE)
                e1:SetCode(EVENT_FREE_CHAIN)
                e1:SetCountLimit(1, id, EFFECT_COUNT_CODE_OATH)
                e1:SetTarget(s.target)
                e1:SetOperation(s.activate)
                c:RegisterEffect(e1)
            end
            function s.target(e, tp, eg, ep, ev, re, r, rp, chk)
                if chk == 0 then return Duel.IsPlayerCanDraw(tp, 2) end
                Duel.SetOperationInfo(0, CATEGORY_DRAW, nil, 0, tp, 2)
                Duel.SetOperationInfo(0, CATEGORY_RECOVER, nil, 0, tp, 600)
            end
            function s.activate(e, tp, eg, ep, ev, re, r, rp)
                if ChronoShowcase and ChronoShowcase.DrawAndRecover then
                    ChronoShowcase.DrawAndRecover(tp, 2, 600)
                else
                    Duel.Draw(tp, 2, REASON_EFFECT)
                    Duel.Recover(tp, 600, REASON_EFFECT)
                end
            end
            """;
        File.WriteAllText(Path.Combine(dir, "lua", "cards", "c190088001.lua"), card1Lua, ModPatcher.Utf8NoBom);

        const string card2Lua = """
            -- Chrono Archmage (190088002)
            local s, id, o = GetID()
            function s.initial_effect(c)
                local e1 = Effect.CreateEffect(c)
                e1:SetCategory(CATEGORY_DRAW + CATEGORY_DAMAGE)
                e1:SetType(EFFECT_TYPE_SINGLE + EFFECT_TYPE_TRIGGER_O)
                e1:SetProperty(EFFECT_FLAG_DELAY)
                e1:SetCode(EVENT_SUMMON_SUCCESS)
                e1:SetTarget(s.drtg)
                e1:SetOperation(s.drop)
                c:RegisterEffect(e1)
                local e2 = e1:Clone()
                e2:SetCode(EVENT_SPSUMMON_SUCCESS)
                c:RegisterEffect(e2)
            end
            function s.drtg(e, tp, eg, ep, ev, re, r, rp, chk)
                if chk == 0 then return Duel.IsPlayerCanDraw(tp, 1) end
                Duel.SetOperationInfo(0, CATEGORY_DRAW, nil, 0, tp, 1)
                Duel.SetOperationInfo(0, CATEGORY_DAMAGE, nil, 0, 1 - tp, 400)
            end
            function s.drop(e, tp, eg, ep, ev, re, r, rp)
                if Duel.Draw(tp, 1, REASON_EFFECT) > 0 then
                    Duel.Damage(1 - tp, 400, REASON_EFFECT)
                end
            end
            """;
        File.WriteAllText(Path.Combine(dir, "lua", "cards", "c190088002.lua"), card2Lua, ModPatcher.Utf8NoBom);

        // 6. Custom Relic
        const string relicJson = """
            [
              {
                "key": "chrono_hourglass",
                "name": "Chrono Hourglass",
                "zhName": "时空沙漏",
                "cost": 85,
                "description": "Spellcaster monsters you control gain 300 ATK.",
                "zhDescription": "自己场上的魔法师族怪兽攻击力上升300。",
                "art": "Merchant Urn.png",
                "tag": "offense",
                "effect": "approved_chrono_hourglass",
                "amount": 300,
                "filter": "Spellcaster",
                "luaEffect": "do local e=Effect.GlobalEffect() e:SetType(EFFECT_TYPE_FIELD) e:SetCode(EFFECT_UPDATE_ATTACK) e:SetTargetRange(LOCATION_MZONE,0) e:SetTarget(function(e,c) return c:IsRace(RACE_SPELLCASTER) end) e:SetValue(300) Duel.RegisterEffect(e,0) end",
                "startingForCharacters": []
              }
            ]
            """;
        File.WriteAllText(Path.Combine(dir, "relics", "showcase-relic.json"), relicJson, ModPatcher.Utf8NoBom);

        // 7. Custom Character
        const string characterJson = """
            [
              {
                "name": "Chrono Weaver",
                "zhName": "时空编织者",
                "sprite": "dm01.png",
                "race": "Spellcaster",
                "attribute": "LIGHT",
                "theme": "Spellcaster / Chrono Example",
                "color": "#f4cf57",
                "bonus": "Starts with Chrono Hourglass and custom showcase cards.",
                "pack": "EN-CHRONO",
                "starting_packs": ["EN-CHRONO", "DRAFT-YAMI-YUGI"],
                "starting_relic": "chrono_hourglass",
                "unlockedByDefault": true,
                "cards": [
                  "Chrono Archmage",
                  "Millennium Chrono Pot",
                  "Dark Magician",
                  "Dark Magician Girl"
                ]
              }
            ]
            """;
        File.WriteAllText(Path.Combine(dir, "characters", "showcase-duelist.json"), characterJson, ModPatcher.Utf8NoBom);

        // 8. Booster Pack & Preconstructed Deck (minimum 9 distinct cards required by campaign_expansion.open_pack)
        const string packJson = """
            {
              "id": "EN-CHRONO",
              "name": "Chrono Showcase Booster",
              "zhName": "时空示例扩展包",
              "region": "EN",
              "code": "CHR",
              "date": "2026-01-01",
              "image": "",
              "size": 9,
              "common": [190088001, 190088002, 46986414, 38033121, 55144522, 5318639, 83764718, 44095762, 40640057, 70781052],
              "rare": [190088002, 38033121, 12580477],
              "super": [190088001, 89631139],
              "ultra": [46986414, 74677422]
            }
            """;
        File.WriteAllText(Path.Combine(dir, "packs", "showcase-pack.json"), packJson, ModPatcher.Utf8NoBom);

        const string deckJson = """
            {
              "id": "MOD-CHRONO-DECK",
              "name": "Chrono Showcase Starter",
              "zhName": "时空示例预组卡组",
              "date": "2026-01-01",
              "art": "SDK",
              "cards": [190088001, 190088001, 190088002, 190088002, 46986414, 38033121]
            }
            """;
        File.WriteAllText(Path.Combine(dir, "preconstructed", "showcase-deck.json"), deckJson, ModPatcher.Utf8NoBom);

        // 9. Trainer Example
        const string trainerJson = """
            {
              "unlockAllCharacters": false,
              "unlockAllVariants": false,
              "startingGoldBonus": 200,
              "freeShop": false,
              "inGameOverlay": true,
              "showTrainerHint": true
            }
            """;
        File.WriteAllText(Path.Combine(dir, "trainer.json"), trainerJson, ModPatcher.Utf8NoBom);

        // 10. Auto-Imported JS #1: Live Settings Controller (difficulty + speed multiplier + accent color)
        const string jsMain = """
            // Auto-imported JS Plugin #1 (js/main.js) - No manual jsEntries required!
            // Demonstrates reading & reacting to in-game F9 -> MOD Settings in real time.
            export async function init(ctx) {
              const applyLiveSettings = () => {
                const speed = Math.max(0.25, Number(ctx.settings.get('speedMultiplier', 1.5)) || 1);
                const accent = String(ctx.settings.get('accentColor', '#f4cf57') || '#f4cf57');
                const diff = String(ctx.settings.get('difficultyPreset', 'standard') || 'standard');

                document.documentElement.style.setProperty('--chrono-accent', accent);
                document.documentElement.style.setProperty('--chrono-speed', String(speed));
                document.documentElement.dataset.chronoDifficulty = diff;

                // Live animation/transition speed boost when speed != 1
                const scaleCss = speed !== 1
                  ? `#app, #app * { animation-duration: calc(var(--chrono-base-anim, 0.28s) / ${speed}) !important; transition-duration: calc(var(--chrono-base-trans, 0.18s) / ${speed}) !important; }`
                  : '';
                ctx.addStyle(scaleCss, 'speed-scale');

                ctx.events.emit('chrono:settings-changed', ctx.settings.getAll());
              };

              applyLiveSettings();
              ctx.settings.onChange((key, val) => {
                applyLiveSettings();
                ctx.showToast(`[${ctx.mod.name}] ${key} -> ${val}`, 2200);
              });

              ctx.log('Chrono Showcase main.js ready · settings =', ctx.settings.getAll());
            }
            """;
        File.WriteAllText(Path.Combine(dir, "js", "main.js"), jsMain, ModPatcher.Utf8NoBom);

        // 11. Auto-Imported JS #2: Floating HUD Pill (proves multi-file JS auto-discovery!)
        const string jsHud = """
            // Auto-imported JS Plugin #2 (js/hud-badge.js) - Automatically discovered alongside js/main.js!
            export async function init(ctx) {
              const DIFF_LABELS = {
                casual: ['Casual', '休闲'],
                standard: ['Standard', '标准'],
                hard: ['Hard', '困难'],
                hell: ['Nightmare', '噩梦']
              };

              const renderPill = () => {
                const show = Boolean(ctx.settings.get('showHudPill', true));
                let el = document.getElementById('chrono-showcase-hud-pill');
                if (!show) {
                  if (el) el.remove();
                  return;
                }
                if (!el) {
                  el = document.createElement('button');
                  el.id = 'chrono-showcase-hud-pill';
                  el.type = 'button';
                  el.className = 'chrono-hud-pill';
                  el.title = 'Click to open F9 MOD Settings';
                  el.addEventListener('click', () => ctx.settings.open());
                  document.body.appendChild(el);
                }
                const diff = String(ctx.settings.get('difficultyPreset', 'standard'));
                const speed = Number(ctx.settings.get('speedMultiplier', 1.5)) || 1;
                const isZh = (ctx.i18n?.getLang?.() || 'zh') === 'zh';
                const label = (DIFF_LABELS[diff] || DIFF_LABELS.standard)[isZh ? 1 : 0];
                el.textContent = `${label} · ${speed.toFixed(2).replace(/\.00$/, '')}x`;
              };

              ctx.onDomReady(renderPill);
              ctx.events.on('chrono:settings-changed', renderPill);
              ctx.settings.onChange(renderPill);
            }
            """;
        File.WriteAllText(Path.Combine(dir, "js", "hud-badge.js"), jsHud, ModPatcher.Utf8NoBom);

        // 12. Auto-Imported CSS #1 (styles/theme.css) & CSS #2 (css/accent.css)
        const string stylesThemeCss = """
            /* Auto-imported from styles/theme.css */
            :root {
              --chrono-accent: #f4cf57;
              --chrono-speed: 1.5;
            }
            .chrono-hud-pill {
              position: fixed;
              top: 10px;
              right: 12px;
              z-index: 99990;
              padding: 4px 10px;
              border-radius: 999px;
              border: 1px solid color-mix(in srgb, var(--chrono-accent) 55%, transparent);
              background: rgba(9, 9, 11, 0.84);
              color: var(--chrono-accent);
              font: 600 11px/1.2 'Segoe UI', system-ui, sans-serif;
              cursor: pointer;
              backdrop-filter: blur(8px);
              transition: transform 0.14s ease, border-color 0.14s ease;
            }
            .chrono-hud-pill:hover {
              transform: translateY(-1px);
              border-color: var(--chrono-accent);
            }
            """;
        File.WriteAllText(Path.Combine(dir, "styles", "theme.css"), stylesThemeCss, ModPatcher.Utf8NoBom);

        const string cssAccent = """
            /* Auto-imported from css/accent.css (both styles/ and css/ directories are auto-discovered!) */
            .chrono-hud-pill:focus-visible {
              outline: 2px solid var(--chrono-accent);
              outline-offset: 2px;
            }
            """;
        File.WriteAllText(Path.Combine(dir, "css", "accent.css"), cssAccent, ModPatcher.Utf8NoBom);

        // 13. Complete Bilingual README.md
        const string readmeMd = """
            # 时空编织者 · 完整规范示例 MOD (Complete Showcase Example)

            本示例 MOD 演示了 **Yu-Gi-Oh! Rogue Duelist Mod Spec v1** 的全部能力，可直接作为你开发 MOD 的参考蓝本：

            ## 1. 分类与封面 (`category` & `cover`)
            - `mod-manifest.json` 中声明 `"category": "overhaul"` 与 `"cover": "cover.svg"`。
            - 即使不写 `"cover"`，管理器也会自动识别根目录的 `cover.png|webp|jpg|svg`、`icon.*` 或 `art/` 下的首张卡图作为封面。

            ## 2. 所有 Entries 自动导入 (`autoDiscoverEntries: true`)
            - 默认开启后，无需手写 `jsEntries` / `cssEntries` / `luaLibs`：
              - `js/**/*.js|mjs` 全部自动加载（本示例包含 `js/main.js` 和 `js/hud-badge.js` 两个插件）
              - `styles/**/*.css` 与 `css/**/*.css` 全部自动注入（本示例同时包含 `styles/theme.css` 和 `css/accent.css`）
              - `lua/lib/**/*.lua` 全部自动并入 `utility.lua`（本示例包含 `lua/lib/showcase_helpers.lua`）
            - 如需排除某个仅用于内部 import 的辅助脚本，在对应数组中加入 `"!js/helper.js"` 即可。

            ## 3. 游戏内 MOD 设置 (`settingsSchema` + `ctx.settings`)
            - 在 `mod-manifest.json` 中写 `settingsSchema`（或单独新建 `settings.json`），游戏内按 **`F9` -> 「MOD 设置」**（或点左下角快捷栏「MOD设置」）即可自动渲染开关、滑动条、数值、下拉框、文本、颜色选择器！
            - JS 插件中通过 `ctx.settings.get('speedMultiplier', 1.5)` 读取、`ctx.settings.onChange((k, v) => ...)` 实时响应（支持难度档位、倍速、主题色等），修改后自动持久化到浏览器 `localStorage`。

            ## 4. 完整内容目录
            - `cards/showcase-cards.json` + `lua/cards/c190088001.lua` / `c190088002.lua`
            - `relics/showcase-relic.json`（含 `luaEffect` 决斗全局效果）
            - `characters/showcase-duelist.json`（全新可玩决斗者）
            - `packs/showcase-pack.json` + `preconstructed/showcase-deck.json`
            - `trainer.json`（F8 实时修改器配置）
            """;
        File.WriteAllText(Path.Combine(dir, "README.md"), readmeMd, ModPatcher.Utf8NoBom);

        var state = LoadState();
        var nextOrder = state.Mods.Count > 0 ? state.Mods.Max(m => m.Order) + 1 : 0;
        state.Mods.Add(new ModEntryState { Id = slug, Enabled = true, Order = nextOrder });
        SaveState(state);

        return LoadMods(state).First(m => string.Equals(m.Id, slug, StringComparison.OrdinalIgnoreCase));
    }

    public ManagedMod UpdateModMetadata(
        string id,
        string? name = null,
        string? version = null,
        string? author = null,
        string? description = null,
        string? category = null,
        string? minGameVersion = null,
        string? maxGameVersion = null,
        List<string>? tags = null,
        bool? autoDiscoverEntries = null,
        string? coverSourcePath = null,
        bool clearCover = false)
    {
        var state = LoadState();
        var mods = LoadMods(state);
        var target = mods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ModOperationException($"找不到 MOD：{id}");

        var manifest = target.Manifest;
        if (name is not null && !string.IsNullOrWhiteSpace(name)) manifest.Name = name.Trim();
        if (version is not null && !string.IsNullOrWhiteSpace(version)) manifest.Version = version.Trim();
        if (author is not null) manifest.Author = author.Trim();
        if (description is not null) manifest.Description = description.Trim();
        if (category is not null) manifest.Category = NormalizeCategory(category);
        if (minGameVersion is not null) manifest.MinGameVersion = string.IsNullOrWhiteSpace(minGameVersion) ? null : minGameVersion.Trim();
        if (maxGameVersion is not null) manifest.MaxGameVersion = string.IsNullOrWhiteSpace(maxGameVersion) ? null : maxGameVersion.Trim();
        if (tags is not null)
        {
            manifest.Tags = tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        if (autoDiscoverEntries.HasValue) manifest.AutoDiscoverEntries = autoDiscoverEntries.Value;

        if (clearCover)
        {
            manifest.Cover = null;
        }
        else if (!string.IsNullOrWhiteSpace(coverSourcePath))
        {
            if (File.Exists(coverSourcePath))
            {
                var fullSrc = Path.GetFullPath(coverSourcePath);
                var fullModDir = Path.GetFullPath(target.DirectoryPath);
                if (fullSrc.StartsWith(fullModDir, StringComparison.OrdinalIgnoreCase))
                {
                    manifest.Cover = Path.GetRelativePath(fullModDir, fullSrc).Replace('\\', '/');
                }
                else
                {
                    manifest.Cover = CopyCoverImage(target.DirectoryPath, fullSrc);
                }
            }
            else
            {
                var relCandidate = NormalizeRelPath(coverSourcePath);
                if (File.Exists(Path.Combine(target.DirectoryPath, relCandidate.Replace('/', Path.DirectorySeparatorChar))))
                {
                    manifest.Cover = relCandidate;
                }
            }
        }

        WriteManifest(target.DirectoryPath, manifest);
        return LoadMods(state).First(m => string.Equals(m.Id, target.Id, StringComparison.OrdinalIgnoreCase));
    }

    private static string CopyCoverImage(string modDir, string sourcePath)
    {
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ext is not (".png" or ".webp" or ".jpg" or ".jpeg" or ".svg" or ".gif"))
        {
            throw new ModOperationException($"不支持的封面图片格式：{ext}（请使用 .png / .webp / .jpg / .svg）");
        }

        foreach (var oldExt in new[] { ".png", ".webp", ".jpg", ".jpeg", ".svg", ".gif" })
        {
            var oldPath = Path.Combine(modDir, "cover" + oldExt);
            if (File.Exists(oldPath) && !string.Equals(oldExt, ext, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(oldPath); } catch { }
            }
        }

        var destRel = "cover" + ext;
        File.Copy(sourcePath, Path.Combine(modDir, destRel), overwrite: true);
        return destRel;
    }

    public static string? NormalizeCategory(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        var lower = trimmed.ToLowerInvariant();
        if (lower is "auto" or "all") return null;
        return StandardCategories.Contains(lower, StringComparer.OrdinalIgnoreCase) ? lower : trimmed;
    }

    public static string ResolveEffectiveCategory(ModManifest manifest, ModContentStats stats)
    {
        if (!string.IsNullOrWhiteSpace(manifest.Category))
        {
            var trimmed = manifest.Category.Trim();
            var lower = trimmed.ToLowerInvariant();
            return StandardCategories.Contains(lower, StringComparer.OrdinalIgnoreCase) ? lower : trimmed;
        }

        var domains = (stats.CardCount > 0 ? 1 : 0)
                    + (stats.CharacterCount > 0 ? 1 : 0)
                    + (stats.RelicCount > 0 ? 1 : 0)
                    + (stats.PackCount + stats.PreconstructedDeckCount + stats.DeckCount > 0 ? 1 : 0);

        if ((domains >= 2 && stats.CardCount + stats.CharacterCount + stats.RelicCount >= 3) ||
            stats.CampaignFileCount > 0)
            return "overhaul";
        if (stats.CardCount > 0) return "cards";
        if (stats.CharacterCount > 0) return "characters";
        if (stats.RelicCount > 0) return "relics";
        if (stats.PackCount > 0 || stats.PreconstructedDeckCount > 0 || stats.DeckCount > 0) return "packs_decks";
        if (stats.HasTrainer) return "trainer";
        if (stats.CssStyleCount > 0 && stats.JsPluginCount == 0) return "ui_theme";
        if (stats.JsPluginCount > 0 || stats.CssStyleCount > 0 || stats.LuaLibCount > 0) return "script_plugin";
        return "other";
    }

    public static string? ResolveCoverPath(string modDir, ModManifest manifest)
    {
        if (!string.IsNullOrWhiteSpace(manifest.Cover))
        {
            var rel = NormalizeRelPath(manifest.Cover);
            var full = Path.GetFullPath(Path.Combine(modDir, rel.Replace('/', Path.DirectorySeparatorChar)));
            var rootFull = Path.GetFullPath(modDir);
            if (full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
            {
                return full;
            }
        }

        string[] coverBases = ["cover", "thumbnail", "icon", "banner"];
        string[] exts = [".png", ".webp", ".jpg", ".jpeg", ".svg"];
        foreach (var b in coverBases)
        {
            foreach (var ext in exts)
            {
                var p = Path.Combine(modDir, b + ext);
                if (File.Exists(p)) return p;
                var artP = Path.Combine(modDir, "art", b + ext);
                if (File.Exists(artP)) return artP;
            }
        }

        var artDir = Path.Combine(modDir, "art");
        if (Directory.Exists(artDir))
        {
            var firstArt = Directory.EnumerateFiles(artDir, "*", SearchOption.TopDirectoryOnly)
                .Where(f => exts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (firstArt is not null) return firstArt;
        }

        return null;
    }

    public static JsonArray? ResolveSettingsSchema(string modDir, ModManifest manifest)
    {
        var merged = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AppendFrom(JsonNode? node)
        {
            JsonArray? arr = node as JsonArray;
            if (arr is null && node is JsonObject obj)
            {
                arr = obj["settings"] as JsonArray ?? obj["settingsSchema"] as JsonArray;
            }
            if (arr is null) return;

            foreach (var item in arr.OfType<JsonObject>())
            {
                var key = item["key"]?.GetValue<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(key) || !seen.Add(key)) continue;
                merged.Add(item.DeepClone());
            }
        }

        AppendFrom(manifest.SettingsSchema);

        var settingsFile = Path.Combine(modDir, "settings.json");
        if (File.Exists(settingsFile))
        {
            try
            {
                AppendFrom(JsonNode.Parse(File.ReadAllText(settingsFile, Encoding.UTF8)));
            }
            catch { }
        }

        return merged.Count > 0 ? merged : null;
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
        var discovered = new List<string>();
        var jsDir = Path.Combine(modDir, "js");
        if (Directory.Exists(jsDir))
        {
            var files = Directory.EnumerateFiles(jsDir, "*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase))
                .Select(p => Path.GetRelativePath(modDir, p).Replace('\\', '/'))
                .OrderBy(p => p.Equals("js/main.js", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase);
            discovered.AddRange(files);
        }

        return MergeAutoEntries(modDir, manifest.JsEntries, discovered, manifest.AutoDiscoverEntries);
    }

    public static List<string> ResolveCssEntries(string modDir, ModManifest manifest)
    {
        var discovered = new List<string>();
        foreach (var sub in new[] { "styles", "css" })
        {
            var cssDir = Path.Combine(modDir, sub);
            if (!Directory.Exists(cssDir)) continue;
            var files = Directory.EnumerateFiles(cssDir, "*.css", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(modDir, p).Replace('\\', '/'))
                .OrderBy(p => p.Equals("styles/mod.css", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase);
            discovered.AddRange(files);
        }

        return MergeAutoEntries(modDir, manifest.CssEntries, discovered, manifest.AutoDiscoverEntries);
    }

    public static List<string> ResolveLuaLibs(string modDir, ModManifest manifest)
    {
        var discovered = new List<string>();
        var libDir = Path.Combine(modDir, "lua", "lib");
        if (Directory.Exists(libDir))
        {
            discovered.AddRange(Directory.EnumerateFiles(libDir, "*.lua", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => Path.GetRelativePath(modDir, p).Replace('\\', '/')));
        }

        return MergeAutoEntries(modDir, manifest.LuaLibs, discovered, manifest.AutoDiscoverEntries);
    }

    private static List<string> MergeAutoEntries(
        string modDir,
        List<string>? explicitEntries,
        IEnumerable<string> discoveredEntries,
        bool autoDiscover)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var hasPositiveExplicit = false;
        if (explicitEntries is { Count: > 0 })
        {
            foreach (var raw in explicitEntries)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var trimmed = raw.Trim();
                if (trimmed.StartsWith('!'))
                {
                    excluded.Add(NormalizeRelPath(trimmed[1..]));
                }
                else
                {
                    hasPositiveExplicit = true;
                }
            }

            foreach (var raw in explicitEntries)
            {
                if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith('!')) continue;
                var rel = NormalizeRelPath(raw);
                if (excluded.Contains(rel) || !seen.Add(rel)) continue;
                if (File.Exists(Path.Combine(modDir, rel.Replace('/', Path.DirectorySeparatorChar))))
                {
                    result.Add(rel);
                }
            }
        }

        // Auto-discover ALL entries when AutoDiscoverEntries is enabled (default true), or when no explicit positive entries exist
        if (autoDiscover || !hasPositiveExplicit)
        {
            foreach (var rel in discoveredEntries)
            {
                var norm = NormalizeRelPath(rel);
                if (excluded.Contains(norm) || !seen.Add(norm)) continue;
                result.Add(norm);
            }
        }

        return result;
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
        Directory.Exists(Path.Combine(dir, "css")) ||
        Directory.Exists(Path.Combine(dir, "scripts")) ||
        Directory.Exists(Path.Combine(dir, "campaign")) ||
        Directory.Exists(Path.Combine(dir, "art")) ||
        Directory.Exists(Path.Combine(dir, "overrides")) ||
        File.Exists(Path.Combine(dir, "settings.json")) ||
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
