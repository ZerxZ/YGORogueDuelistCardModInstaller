using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace YGORogueDuelistCardModInstaller.Core.ModManager;

public enum InjectionMode
{
    /// <summary>
    /// Zero-touch virtual bundle (Game\900.x.y + .zip + .sha256). Never modifies launcher DLL, supports adding arbitrary new files &amp; JS/CSS plugins.
    /// </summary>
    VirtualBundle,

    /// <summary>
    /// In-place patch of Game\&lt;ver&gt; + StartupHashes.json inside Yu-Gi-Oh! Rogue Duelist.dll (keeps original version number).
    /// </summary>
    LauncherManifest
}

public enum InjectionSyncState
{
    Vanilla,
    Synced,
    OutOfDate,
    GameUpdated
}

public sealed class ModManifest
{
    public static readonly string CurrentManagerVersion = ResolveCurrentManagerVersion();

    private static string ResolveCurrentManagerVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(ModManifest).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(ModManifest).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var cleaned = info.Split('+')[0].Trim().TrimStart('v', 'V');
            if (!string.IsNullOrWhiteSpace(cleaned)) return cleaned;
        }

        var v = asm.GetName().Version ?? typeof(ModManifest).Assembly.GetName().Version;
        if (v is not null && (v.Major > 0 || v.Minor > 0 || v.Build > 0))
        {
            return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }

        return "1.3.0";
    }

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Standard category slug: cards | characters | relics | packs_decks | ui_theme | script_plugin | trainer | overhaul | other (or custom).
    /// </summary>
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    /// <summary>
    /// Optional relative cover image path inside the MOD directory (e.g. "cover.png", "cover.webp", "cover.svg", "art/190098001.webp").
    /// When omitted, automatically discovers cover.* / icon.* or first art/* image.
    /// </summary>
    [JsonPropertyName("cover")]
    public string? Cover { get; set; }

    /// <summary>
    /// When true (default), all js/**/*.js|mjs, styles/**/*.css, css/**/*.css, and lua/lib/**/*.lua files
    /// are automatically discovered and imported in addition to any explicitly ordered entries.
    /// Prefix any entry with "!" to exclude a specific file from auto-import.
    /// </summary>
    [JsonPropertyName("autoDiscoverEntries")]
    public bool AutoDiscoverEntries { get; set; } = true;

    [JsonPropertyName("gameVersion")]
    public string? GameVersion { get; set; }

    [JsonPropertyName("minGameVersion")]
    public string? MinGameVersion { get; set; }

    [JsonPropertyName("maxGameVersion")]
    public string? MaxGameVersion { get; set; }

    [JsonPropertyName("minManagerVersion")]
    public string? MinManagerVersion { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("jsEntries")]
    public List<string>? JsEntries { get; set; }

    [JsonPropertyName("cssEntries")]
    public List<string>? CssEntries { get; set; }

    [JsonPropertyName("luaLibs")]
    public List<string>? LuaLibs { get; set; }

    /// <summary>
    /// Optional duel rules / Master Rule override for modern-era mods:
    /// "mr1" (default DM/GX) | "mr2" (Synchro/Xyz) | "mr3" (Pendulum) | "mr4" (Link EMZ) | "mr5" (Modern MR2020: EMZ + PZone + Free FSX).
    /// Also auto-upgraded when any enabled MOD adds Synchro/Xyz/Pendulum/Link cards.
    /// </summary>
    [JsonPropertyName("duelRules")]
    public string? DuelRules { get; set; }

    /// <summary>
    /// Declarative in-game MOD Settings schema (rendered automatically in the F9 MOD Settings panel
    /// and exposed to JS plugins via ctx.settings.get / onChange).
    /// </summary>
    [JsonPropertyName("settingsSchema")]
    public JsonArray? SettingsSchema { get; set; }

    /// <summary>
    /// AOT-safe free-form JSON object exposed to JS plugins via ctx.config.
    /// </summary>
    [JsonPropertyName("config")]
    public JsonObject? Config { get; set; }
}

public sealed class WorkspacePackManifest
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = "ygo-rogue-mod-workspace-v1";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "YGO Rogue Duelist Modpack";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("minGameVersion")]
    public string? MinGameVersion { get; set; }

    [JsonPropertyName("minManagerVersion")]
    public string MinManagerVersion { get; set; } = ModManifest.CurrentManagerVersion;

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("injectionMode")]
    [JsonConverter(typeof(JsonStringEnumConverter<InjectionMode>))]
    public InjectionMode InjectionMode { get; set; } = InjectionMode.VirtualBundle;

    [JsonPropertyName("mods")]
    public List<ModEntryState> Mods { get; set; } = [];
}

public sealed class ModEntryState
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("order")]
    public int Order { get; set; }
}

public sealed class LastInjectionRecord
{
    [JsonPropertyName("mode")]
    [JsonConverter(typeof(JsonStringEnumConverter<InjectionMode>))]
    public InjectionMode Mode { get; set; }

    [JsonPropertyName("baseGameVersion")]
    public string BaseGameVersion { get; set; } = string.Empty;

    [JsonPropertyName("virtualVersion")]
    public string? VirtualVersion { get; set; }

    [JsonPropertyName("appliedAtUtc")]
    public DateTimeOffset AppliedAtUtc { get; set; }

    [JsonPropertyName("configFingerprint")]
    public string ConfigFingerprint { get; set; } = string.Empty;

    [JsonPropertyName("appliedModIds")]
    public List<string> AppliedModIds { get; set; } = [];
}

public sealed class ModManagerState
{
    [JsonPropertyName("injectionMode")]
    [JsonConverter(typeof(JsonStringEnumConverter<InjectionMode>))]
    public InjectionMode InjectionMode { get; set; } = InjectionMode.VirtualBundle;

    [JsonPropertyName("language")]
    public string Language { get; set; } = "zh";

    [JsonPropertyName("developerMode")]
    public bool DeveloperMode { get; set; }

    /// <summary>
    /// When true (default), hides the "Online" multiplayer menu entry whenever MODs / Trainer / Dev Mode are active,
    /// protecting vanilla multiplayer fun. Pure vanilla runs always show Online normally.
    /// </summary>
    [JsonPropertyName("hideOnlineWhenModded")]
    public bool HideOnlineWhenModded { get; set; } = true;

    /// <summary>
    /// When true (default), injects the built-in F8 in-game Trainer overlay even outside Developer Mode
    /// (so players keep Trainer & gameplay helpers without enabling the MOD Editor / Developer Mode).
    /// </summary>
    [JsonPropertyName("enableBuiltInTrainer")]
    public bool EnableBuiltInTrainer { get; set; } = true;

    [JsonPropertyName("customCategories")]
    public List<string> CustomCategories { get; set; } = [];

    [JsonPropertyName("aiApiBaseUrl")]
    public string AiApiBaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("aiApiModel")]
    public string AiApiModel { get; set; } = string.Empty;

    [JsonPropertyName("aiApiKey")]
    public string AiApiKey { get; set; } = string.Empty;

    [JsonPropertyName("installDir")]
    public string? InstallDir { get; set; }

    [JsonPropertyName("gameDir")]
    public string? GameDir { get; set; }

    [JsonPropertyName("picsDir")]
    public string? PicsDir { get; set; }

    [JsonPropertyName("repo")]
    public string Repo { get; set; } = InstallOptions.DefaultRepo;

    /// <summary>
    /// Optional GitHub mirror/proxy prefix (e.g. "https://ghfast.top/").
    /// Empty string (default) uses official GitHub directly.
    /// </summary>
    [JsonPropertyName("githubProxy")]
    public string GitHubProxy { get; set; } = string.Empty;

    [JsonPropertyName("managerRepo")]
    public string ManagerRepo { get; set; } = InstallOptions.DefaultManagerRepo;

    [JsonPropertyName("mods")]
    public List<ModEntryState> Mods { get; set; } = [];

    [JsonPropertyName("lastInjection")]
    public LastInjectionRecord? LastInjection { get; set; }
}

public sealed record ModContentStats(
    bool HasLegacyModJson,
    bool HasLuaDsl,
    bool HasJsDsl,
    int CardCount,
    int CharacterCount,
    int RelicCount,
    int PackCount,
    int PreconstructedDeckCount,
    int ScriptCount,
    int LuaLibCount,
    int JsPluginCount,
    int CssStyleCount,
    int DeckCount,
    int ArtCount,
    int CampaignFileCount,
    int OverrideFileCount,
    bool HasTrainer,
    IReadOnlyList<string> OverrideRelativePaths);

public sealed class ManagedMod
{
    public required string Id { get; init; }
    public required string DirectoryPath { get; init; }
    public required ModManifest Manifest { get; init; }
    public required ModContentStats Stats { get; init; }
    public bool Enabled { get; set; }
    public int Order { get; set; }
}

public sealed record ModVersionIssue(
    string ModId,
    string ModName,
    string Message);

public sealed record ModConflict(
    string Category,
    string ResourceKey,
    IReadOnlyList<string> ModIds,
    string WinnerModId);

public sealed record LauncherMetadata(
    string InitialBundleVersion,
    string InitialBundleFileName,
    string InitialBundleSha256,
    string DesktopBootScript,
    int LauncherBuild);

public sealed record ApplyModsOptions
{
    public InjectionMode? ModeOverride { get; init; }
    public string? BuildOnlyDir { get; init; }
    public bool LaunchGameAfter { get; init; }
    public bool AssumeYes { get; init; }
    public bool ForceKillRunningGame { get; init; }
    public bool IgnoreVersionRequirements { get; init; }
    public bool? DeveloperModeOverride { get; init; }
    public bool? HideOnlineWhenModdedOverride { get; init; }
    public bool? EnableBuiltInTrainerOverride { get; init; }
    public int? HotReloadPort { get; init; }
}

public sealed record ModManagerStatusSnapshot(
    string WorkspaceRoot,
    string ModsRoot,
    string? InstallDir,
    string? GameBaseDir,
    string? ActiveBaseGameVersion,
    LauncherMetadata? LauncherMeta,
    InjectionMode Mode,
    bool DeveloperMode,
    bool HideOnlineWhenModded,
    bool EnableBuiltInTrainer,
    InjectionSyncState SyncState,
    string SyncSummary,
    string CurrentFingerprint,
    IReadOnlyList<ManagedMod> Mods,
    IReadOnlyList<ModConflict> Conflicts,
    IReadOnlyList<ModVersionIssue> VersionIssues,
    bool HasLauncherBackup,
    IReadOnlyList<string> InstalledVirtualVersions);
