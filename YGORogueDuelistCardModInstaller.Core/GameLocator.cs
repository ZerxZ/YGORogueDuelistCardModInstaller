using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace YGORogueDuelistCardModInstaller.Core;

public static partial class GameLocator
{
    public const string GameDllName = "Yu-Gi-Oh! Rogue Duelist.dll";
    public const string GameExeName = "Yu-Gi-Oh! Rogue Duelist.exe";
    public const string GameFolderName = "Yu-Gi-Oh! Rogue Duelist";
    public const string LegacyFolderName = "Shadow Run";

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    [GeneratedRegex(@"^[0-9]+(\.[0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionFolderRegex();

    [GeneratedRegex(@"[^0-9\.]", RegexOptions.CultureInvariant)]
    private static partial Regex NonVersionCharsRegex();

    public static string ResolveRootDirectory(string? explicitRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            return Path.GetFullPath(explicitRoot.Trim());
        }

        string[] seeds = [AppContext.BaseDirectory, Environment.CurrentDirectory];

        foreach (var seed in seeds)
        {
            var found = WalkUpForPackageRoot(seed, requireModJson: true);
            if (found is not null)
            {
                return found;
            }
        }

        foreach (var seed in seeds)
        {
            var found = WalkUpForPackageRoot(seed, requireModJson: false);
            if (found is not null)
            {
                return found;
            }
        }

        return Path.GetFullPath(AppContext.BaseDirectory);
    }

    private static string? WalkUpForPackageRoot(string? startDir, bool requireModJson)
    {
        if (string.IsNullOrWhiteSpace(startDir))
        {
            return null;
        }

        var dir = new DirectoryInfo(Path.GetFullPath(startDir));
        for (var depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
        {
            var modJson = Path.Combine(dir.FullName, "data", "mod.json");
            if (File.Exists(modJson))
            {
                return dir.FullName;
            }

            if (!requireModJson)
            {
                var backupDll = Path.Combine(dir.FullName, "backup", "launcher.dll");
                if (File.Exists(backupDll))
                {
                    return dir.FullName;
                }
            }
        }

        return null;
    }

    public static bool TestGameExe(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return false;
        }

        try
        {
            return File.Exists(Path.Combine(dir, GameExeName)) ||
                   File.Exists(Path.Combine(dir, GameDllName));
        }
        catch
        {
            return false;
        }
    }

    public static string? FindGameDir(bool excludeVirtual = true)
    {
        var baseDir = Path.Combine(LocalAppData, GameFolderName, "Game");
        if (!Directory.Exists(baseDir))
        {
            baseDir = Path.Combine(LocalAppData, LegacyFolderName, "Game");
        }

        if (!Directory.Exists(baseDir))
        {
            return null;
        }

        try
        {
            // Only consider pure numeric version folders; ignore logs/backups/virtual mod bundles.
            var candidates = Directory.GetDirectories(baseDir)
                .Select(path => new DirectoryInfo(path))
                .Where(d => VersionFolderRegex().IsMatch(d.Name))
                .Where(d => !excludeVirtual || !ModManager.ModInjectionEngine.IsVirtualVersion(d.Name))
                .ToList();

            if (candidates.Count == 0)
            {
                return null;
            }

            candidates.Sort((a, b) => CompareVersionText(b.Name, a.Name));
            return candidates[0].FullName;
        }
        catch
        {
            return null;
        }
    }

    public static string? FindInstallDir()
    {
        var candidates = new List<string>();
        var localPrograms = Path.Combine(LocalAppData, "Programs", GameFolderName);
        if (!string.IsNullOrEmpty(localPrograms))
        {
            candidates.Add(localPrograms);
        }

        var drives = GetReadyDriveRoots();
        foreach (var root in drives)
        {
            candidates.Add(Path.Combine(root, GameFolderName));
        }

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(Path.Combine(candidate, GameDllName)))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore inaccessible paths.
            }
        }

        // Depth-1 scan on every ready drive (matches PowerShell Get-ChildItem -Directory -Depth 1)
        foreach (var root in drives)
        {
            foreach (var firstLevel in SafeGetDirectories(root))
            {
                if (SafeFileExists(Path.Combine(firstLevel, GameDllName)))
                {
                    return firstLevel;
                }

                foreach (var secondLevel in SafeGetDirectories(firstLevel))
                {
                    if (SafeFileExists(Path.Combine(secondLevel, GameDllName)))
                    {
                        return secondLevel;
                    }
                }
            }
        }

        return null;
    }

    public static string? FindPicsDir()
    {
        foreach (var root in GetReadyDriveRoots())
        {
            var candidate = Path.Combine(root, "YGOPro", "pics");
            try
            {
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore inaccessible drives.
            }
        }

        return null;
    }

    public static string? ReadGameVersion(string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir))
        {
            return null;
        }

        var buildFile = Path.Combine(gameDir, "build.json");
        if (!File.Exists(buildFile))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(buildFile, Encoding.UTF8);
            var node = JsonNode.Parse(text)?.AsObject();
            return node?["version"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns 1 when a is newer than b, -1 when older, 0 when equal or unparsable.
    /// </summary>
    public static int CompareVersionText(string? a, string? b)
    {
        var na = ExtractVersionParts(a);
        var nb = ExtractVersionParts(b);
        if (na.Length == 0 || nb.Length == 0)
        {
            return 0;
        }

        var count = Math.Max(na.Length, nb.Length);
        for (var i = 0; i < count; i++)
        {
            var x = i < na.Length ? na[i] : 0;
            var y = i < nb.Length ? nb[i] : 0;
            if (x > y) return 1;
            if (x < y) return -1;
        }

        return 0;
    }

    private static long[] ExtractVersionParts(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var cleaned = NonVersionCharsRegex().Replace(text, string.Empty);
        var tokens = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<long>(tokens.Length);
        foreach (var token in tokens)
        {
            if (long.TryParse(token, out var value))
            {
                result.Add(value);
            }
        }

        return result.ToArray();
    }

    public static void ClearWebViewCache()
    {
        var primary = Path.Combine(LocalAppData, GameFolderName, "WebView", "EBWebView", "Default");
        var legacy = Path.Combine(LocalAppData, LegacyFolderName, "WebView", "EBWebView", "Default");

        var targets = new List<string>();
        if (Directory.Exists(primary)) targets.Add(primary);
        if (Directory.Exists(legacy)) targets.Add(legacy);

        foreach (var webview in targets)
        {
            foreach (var name in new[] { "Service Worker", "Cache" })
            {
                var path = Path.Combine(webview, name);
                if (!Directory.Exists(path))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(path, recursive: true);
                }
                catch
                {
                    // Match PowerShell -ErrorAction SilentlyContinue
                }
            }
        }
    }

    public static ModPackageSummary InspectPackage(string rootDir)
    {
        var modJsonPath = Path.Combine(rootDir, "data", "mod.json");
        var artDir = Path.Combine(rootDir, "art");
        var backupLauncherPath = Path.Combine(rootDir, "backup", "launcher.dll");

        var artCount = 0;
        if (Directory.Exists(artDir))
        {
            try
            {
                artCount = Directory.EnumerateFiles(artDir).Count();
            }
            catch
            {
                artCount = 0;
            }
        }

        var hasBackup = File.Exists(backupLauncherPath);

        if (!File.Exists(modJsonPath))
        {
            return new ModPackageSummary(
                rootDir,
                modJsonPath,
                Exists: false,
                Version: null,
                CardCount: 0,
                ScriptCount: 0,
                ArtFileCount: artCount,
                HasBackupLauncher: hasBackup,
                BackupLauncherPath: backupLauncherPath);
        }

        try
        {
            var text = File.ReadAllText(modJsonPath, Encoding.UTF8);
            var mod = JsonNode.Parse(text)?.AsObject();
            var version = mod?["version"]?.GetValue<string>();
            var cards = mod?["cards"]?.AsArray().Count ?? 0;
            var scripts = mod?["scripts"]?.AsObject().Count ?? 0;
            return new ModPackageSummary(
                rootDir,
                modJsonPath,
                Exists: true,
                Version: version,
                CardCount: cards,
                ScriptCount: scripts,
                ArtFileCount: artCount,
                HasBackupLauncher: hasBackup,
                BackupLauncherPath: backupLauncherPath);
        }
        catch
        {
            return new ModPackageSummary(
                rootDir,
                modJsonPath,
                Exists: true,
                Version: null,
                CardCount: 0,
                ScriptCount: 0,
                ArtFileCount: artCount,
                HasBackupLauncher: hasBackup,
                BackupLauncherPath: backupLauncherPath);
        }
    }

    public static DetectedEnvironment InspectEnvironment(
        string? explicitRoot = null,
        string? explicitInstallDir = null,
        string? explicitGameDir = null,
        string? explicitPicsDir = null)
    {
        var root = ResolveRootDirectory(explicitRoot);
        var installDir = string.IsNullOrWhiteSpace(explicitInstallDir) ? FindInstallDir() : explicitInstallDir;
        var gameDir = string.IsNullOrWhiteSpace(explicitGameDir) ? FindGameDir() : explicitGameDir;
        var picsDir = string.IsNullOrWhiteSpace(explicitPicsDir) ? FindPicsDir() : explicitPicsDir;
        if (!string.IsNullOrWhiteSpace(picsDir) && !Directory.Exists(picsDir))
        {
            picsDir = null;
        }

        var gameVersion = ReadGameVersion(gameDir);
        var package = InspectPackage(root);

        return new DetectedEnvironment(root, installDir, gameDir, gameVersion, picsDir, package);
    }

    private static List<string> GetReadyDriveRoots()
    {
        var roots = new List<string>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.IsReady)
                    {
                        roots.Add(drive.RootDirectory.FullName);
                    }
                }
                catch
                {
                    // Skip unready/inaccessible drives.
                }
            }
        }
        catch
        {
            // Ignore enumeration errors.
        }

        return roots;
    }

    private static IEnumerable<string> SafeGetDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch
        {
            return [];
        }
    }

    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}
