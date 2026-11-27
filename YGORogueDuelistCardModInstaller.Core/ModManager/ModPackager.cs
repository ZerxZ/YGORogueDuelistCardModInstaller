using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace YGORogueDuelistCardModInstaller.Core.ModManager;

/// <summary>
/// Packages individual MODs or entire MOD workspaces (with load order, enabled states, and min-version metadata)
/// into portable .zip archives, and imports workspace modpacks in one click.
/// </summary>
public static class ModPackager
{
    public const string WorkspacePackFileName = "workspace-pack.json";
    public const string ModPackageExtension = ".yrdpk";

    public static bool IsModArchiveExtension(string path) =>
        path.EndsWith(".yrdpk", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    public static string PackSingleMod(ModRepository repository, string modId, string? outputZipPath = null)
    {
        var mods = repository.LoadMods();
        var mod = mods.FirstOrDefault(m => string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ModOperationException($"找不到 MOD：{modId}");

        var dest = string.IsNullOrWhiteSpace(outputZipPath)
            ? Path.Combine(repository.WorkspaceRoot, "dist", $"{mod.Id}-v{mod.Manifest.Version}{ModPackageExtension}")
            : Path.GetFullPath(outputZipPath);

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        WriteZipSafely(dest, zip => AddDirectoryToZip(zip, mod.DirectoryPath, prefix: string.Empty));
        return dest;
    }

    public static void PackSingleModToStream(ModRepository repository, string modId, Stream targetStream)
    {
        var mods = repository.LoadMods();
        var mod = mods.FirstOrDefault(m => string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ModOperationException($"找不到 MOD：{modId}");

        using var zip = new ZipArchive(targetStream, ZipArchiveMode.Create, leaveOpen: true);
        AddDirectoryToZip(zip, mod.DirectoryPath, prefix: string.Empty);
    }

    public static (string ZipPath, int ModCount, string? EffectiveMinGameVersion) PackWorkspace(
        ModRepository repository,
        string? outputZipPath = null,
        string? packName = null,
        string? packVersion = null,
        string? description = null,
        string? explicitMinGameVersion = null,
        bool enabledOnly = false)
    {
        var state = repository.LoadState();
        var allMods = repository.LoadMods(state);
        var selected = (enabledOnly ? allMods.Where(m => m.Enabled) : allMods)
            .OrderBy(m => m.Order)
            .ToList();

        if (selected.Count == 0)
        {
            throw new ModOperationException(enabledOnly
                ? "没有已启用的 MOD 可供打包。"
                : "当前工作区没有任何 MOD 可供打包。");
        }

        // Compute highest required minGameVersion across included mods
        var minGameVer = explicitMinGameVersion;
        foreach (var m in selected)
        {
            if (!string.IsNullOrWhiteSpace(m.Manifest.MinGameVersion) &&
                (string.IsNullOrWhiteSpace(minGameVer) ||
                 GameLocator.CompareVersionText(m.Manifest.MinGameVersion, minGameVer) > 0))
            {
                minGameVer = m.Manifest.MinGameVersion;
            }
        }

        var ver = string.IsNullOrWhiteSpace(packVersion) ? "1.0.0" : packVersion.Trim();
        var name = string.IsNullOrWhiteSpace(packName) ? "YGO-Rogue-Modpack" : packName.Trim();
        var safeSlug = ModRepository.SanitizeId(name);
        var dest = string.IsNullOrWhiteSpace(outputZipPath)
            ? Path.Combine(repository.WorkspaceRoot, "dist", $"{safeSlug}-v{ver}{ModPackageExtension}")
            : Path.GetFullPath(outputZipPath);

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        var manifest = new WorkspacePackManifest
        {
            Name = name,
            Version = ver,
            Description = description?.Trim() ?? $"包含 {selected.Count} 个 MOD 的工作区整合包。",
            MinGameVersion = minGameVer,
            MinManagerVersion = ModManifest.CurrentManagerVersion,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            InjectionMode = state.InjectionMode,
            Mods = selected.Select((m, idx) => new ModEntryState
            {
                Id = m.Id,
                Enabled = m.Enabled,
                Order = idx
            }).ToList()
        };

        WriteZipSafely(dest, zip =>
        {
            var manifestEntry = zip.CreateEntry(WorkspacePackFileName, CompressionLevel.Optimal);
            using (var stream = manifestEntry.Open())
            {
                var bytes = ModPatcher.GetUtf8Bytes(
                    JsonSerializer.Serialize(manifest, CoreJsonContext.PrettyUtf8.WorkspacePackManifest));
                stream.Write(bytes, 0, bytes.Length);
            }

            foreach (var mod in selected)
            {
                AddDirectoryToZip(zip, mod.DirectoryPath, prefix: $"mods/{mod.Id}/");
            }
        });

        return (dest, selected.Count, minGameVer);
    }

    private static void WriteZipSafely(string destPath, Action<ZipArchive> populate)
    {
        try
        {
            using var fs = new FileStream(destPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);
            populate(zip);
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"无法写入目标文件：{destPath}\n" +
                "这通常是由于 Windows 安全中心「受控文件夹访问权限」(勒索软件防护) 或 OneDrive 阻止写入「文档」目录导致的。\n" +
                "请将导出路径选择到工作区 dist\\ 目录或非 OneDrive 目录重试。", ex);
        }
    }

    public static bool IsWorkspacePackZip(string zipPath)
    {
        if (!File.Exists(zipPath) || !IsModArchiveExtension(zipPath))
        {
            return false;
        }

        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return zip.Entries.Any(e => string.Equals(e.FullName, WorkspacePackFileName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public static (WorkspacePackManifest Manifest, int ImportedCount) ImportWorkspacePack(
        ModRepository repository,
        string zipPath,
        bool overwrite = true)
    {
        if (!File.Exists(zipPath))
        {
            throw new ModOperationException($"找不到工作区压缩包：{zipPath}");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "ygo-ws-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            ZipFile.ExtractToDirectory(zipPath, tempDir, overwriteFiles: true);

            var packJsonPath = Path.Combine(tempDir, WorkspacePackFileName);
            if (!File.Exists(packJsonPath))
            {
                throw new ModOperationException($"该压缩包不是有效的 MOD 工作区包（缺少 {WorkspacePackFileName}）。");
            }

            var pack = JsonSerializer.Deserialize(
                File.ReadAllText(packJsonPath, Encoding.UTF8),
                CoreJsonContext.PrettyUtf8.WorkspacePackManifest)
                ?? throw new ModOperationException("无法解析 workspace-pack.json。");

            if (!string.IsNullOrWhiteSpace(pack.MinManagerVersion) &&
                GameLocator.CompareVersionText(ModManifest.CurrentManagerVersion, pack.MinManagerVersion) < 0)
            {
                throw new ModOperationException(
                    $"该工作区整合包要求 MOD 管理器最低版本 {pack.MinManagerVersion}（当前版本 {ModManifest.CurrentManagerVersion}）。");
            }

            var extractedModsDir = Path.Combine(tempDir, "mods");
            if (!Directory.Exists(extractedModsDir))
            {
                throw new ModOperationException("工作区压缩包中缺少 mods/ 目录。");
            }

            var importedIds = new List<string>();
            foreach (var dir in Directory.GetDirectories(extractedModsDir))
            {
                var imported = repository.ImportMod(dir, overwrite: overwrite);
                importedIds.Add(imported.Id);
            }

            // Apply saved order & enabled state from workspace-pack.json
            var state = repository.LoadState();
            var loaded = repository.LoadMods(state);
            var packMap = pack.Mods
                .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var m in loaded)
            {
                if (packMap.TryGetValue(m.Id, out var entry))
                {
                    m.Enabled = entry.Enabled;
                    m.Order = entry.Order;
                }
            }

            loaded.Sort((a, b) => a.Order.CompareTo(b.Order));
            state.InjectionMode = pack.InjectionMode;
            repository.SaveModOrderAndFlags(loaded, state);

            return (pack, importedIds.Count);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void AddDirectoryToZip(ZipArchive zip, string sourceDir, string prefix)
    {
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(file);
            if (fileName is "Thumbs.db" or ".DS_Store" or "Desktop.ini") continue;

            var rel = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
            var entryName = string.IsNullOrEmpty(prefix) ? rel : prefix + rel;
            zip.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
        }
    }
}
