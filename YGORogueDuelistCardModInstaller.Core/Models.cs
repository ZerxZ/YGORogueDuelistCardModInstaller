namespace YGORogueDuelistCardModInstaller.Core;

public enum LogLevel
{
    Info,
    Ok,
    Warn,
    Error
}

public sealed record InstallOptions
{
    public string? RootDir { get; init; }
    public string? InstallDir { get; init; }
    public string? GameDir { get; init; }
    public string? PicsDir { get; init; }
    public string? BuildOnlyDir { get; init; }
    public string Repo { get; init; } = DefaultRepo;
    public bool NoDll { get; init; }
    public bool AssumeYes { get; init; }

    public const string DefaultRepo = "Thronior/ygo-rogue-duelist";
}

public sealed record UninstallOptions
{
    public string? RootDir { get; init; }
    public string? InstallDir { get; init; }
    public string? GameDir { get; init; }
}

public sealed record ModPackageSummary(
    string RootDir,
    string ModJsonPath,
    bool Exists,
    string? Version,
    int CardCount,
    int ScriptCount,
    int ArtFileCount,
    bool HasBackupLauncher,
    string BackupLauncherPath);

public sealed record DetectedEnvironment(
    string RootDir,
    string? InstallDir,
    string? GameDir,
    string? GameVersion,
    string? PicsDir,
    ModPackageSummary Package);

public sealed class GitHubRelease
{
    public string? TagName { get; set; }
    public string? Name { get; set; }
    public bool Draft { get; set; }
    public List<GitHubAsset> Assets { get; set; } = [];

    public string? EffectiveTag => !string.IsNullOrWhiteSpace(TagName) ? TagName : Name;
}

public sealed class GitHubAsset
{
    public string? Name { get; set; }
    public string? BrowserDownloadUrl { get; set; }
    public long Size { get; set; }
}

public sealed class ModOperationException : Exception
{
    public ModOperationException(string message) : base(message)
    {
    }

    public ModOperationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
