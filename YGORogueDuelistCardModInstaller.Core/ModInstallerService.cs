using System.Text;
using System.Text.Json.Nodes;

namespace YGORogueDuelistCardModInstaller.Core;

public sealed class ModInstallerService
{
    public async Task InstallAsync(
        InstallOptions options,
        IInstallerInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        var root = GameLocator.ResolveRootDirectory(options.RootDir);
        var dataFile = Path.Combine(root, "data", "mod.json");
        var backupDir = Path.Combine(root, "backup");
        var repo = string.IsNullOrWhiteSpace(options.Repo) ? InstallOptions.DefaultRepo : options.Repo.Trim();
        var isBuildOnly = !string.IsNullOrWhiteSpace(options.BuildOnlyDir);

        interaction.ReportProgress("正在读取安装包数据...", 5);
        if (!File.Exists(dataFile))
        {
            throw new ModOperationException(@"安装包不完整：缺少 data\mod.json");
        }

        var modText = await File.ReadAllTextAsync(dataFile, Encoding.UTF8, cancellationToken);
        var mod = ModPatcher.ParseJson(modText).AsObject();
        var modVersion = mod["version"]?.GetValue<string>() ?? string.Empty;
        var cardCount = mod["cards"]?.AsArray().Count ?? 0;
        var scriptCount = mod["scripts"]?.AsObject().Count ?? 0;
        interaction.Say($"安装包版本：{modVersion}，卡 {cardCount} 张，卡脚本 {scriptCount} 个");

        var explicitGameDir = !string.IsNullOrWhiteSpace(options.GameDir);
        var gameDir = explicitGameDir ? options.GameDir : GameLocator.FindGameDir();
        var installDir = string.IsNullOrWhiteSpace(options.InstallDir) ? GameLocator.FindInstallDir() : options.InstallDir;

        if (!isBuildOnly)
        {
            if (!string.IsNullOrWhiteSpace(installDir))
            {
                interaction.ReportProgress("正在检查游戏版本更新...", 8);
                installDir = await GitHubReleaseService.UpdateGameIfOutdatedAsync(installDir, repo, root, interaction, cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(installDir))
            {
                installDir = await GitHubReleaseService.GetOriginalGameAsync(repo, root, interaction, cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(installDir))
            {
                installDir = GameLocator.FindInstallDir();
            }

            if (!explicitGameDir)
            {
                gameDir = GameLocator.FindGameDir() ?? gameDir;
            }
        }

        if (string.IsNullOrWhiteSpace(installDir))
        {
            throw new ModOperationException(
                "找不到游戏安装目录（含 Yu-Gi-Oh! Rogue Duelist.dll）。请用 --install-dir 指定，或先安装/启动一次游戏。");
        }

        if (string.IsNullOrWhiteSpace(gameDir))
        {
            throw new ModOperationException(
                @"找不到游戏数据目录（%LOCALAPPDATA%\Yu-Gi-Oh! Rogue Duelist\Game）。请先启动一次游戏。");
        }

        string? version = null;
        var buildFile = Path.Combine(gameDir, "build.json");
        if (File.Exists(buildFile))
        {
            version = GameLocator.ReadGameVersion(gameDir);
            interaction.Say($"游戏版本：{version}（安装包按 {modVersion} 制作）");
            if (!string.Equals(version, modVersion, StringComparison.Ordinal))
            {
                interaction.Warn("版本不一致，补丁可能不适用。");
                if (!isBuildOnly && !options.AssumeYes)
                {
                    var proceed = await interaction.ConfirmAsync(
                        $"游戏版本 ({version}) 与安装包版本 ({modVersion}) 不一致，补丁可能不适用。继续吗？",
                        cancellationToken);
                    if (!proceed)
                    {
                        throw new ModOperationException("已取消。");
                    }
                }
            }
        }

        var zipPath = !string.IsNullOrWhiteSpace(version)
            ? Path.Combine(installDir, $"web-{version}.zip")
            : Path.Combine(installDir, $"web-{modVersion}.zip");

        if (!File.Exists(zipPath))
        {
            zipPath = Path.Combine(installDir, $"web-{modVersion}.zip");
        }

        if (!File.Exists(zipPath) && !isBuildOnly)
        {
            throw new ModOperationException("找不到原版数据包 web-<版本>.zip（应在游戏安装目录里）。");
        }

        // Card art ships with the package (art\<code>.webp); a YGOPro pics folder is only a fallback
        var bundledArtDir = Path.Combine(root, "art");
        var artShipped = Directory.Exists(bundledArtDir) && Directory.EnumerateFiles(bundledArtDir).Any();
        var picsDir = string.IsNullOrWhiteSpace(options.PicsDir) ? GameLocator.FindPicsDir() : options.PicsDir;
        if (!string.IsNullOrWhiteSpace(picsDir) && !Directory.Exists(picsDir))
        {
            picsDir = null;
        }

        if (string.IsNullOrWhiteSpace(picsDir) && !artShipped && !isBuildOnly)
        {
            interaction.Warn("安装包里没有卡图，也没找到 YGOPro 卡图目录，所有新卡会没有图。");
            var answer = await interaction.PromptForPicsDirAsync("输入卡图目录（留空跳过）", cancellationToken);
            if (!string.IsNullOrWhiteSpace(answer))
            {
                picsDir = answer.Trim();
            }
        }

        Dictionary<string, byte[]> files;
        if (File.Exists(zipPath))
        {
            files = await Task.Run(
                () => ModPatcher.BuildPatchedFiles(zipPath, mod, root, picsDir, interaction),
                cancellationToken);
        }
        else
        {
            interaction.Warn("没有原版 zip，跳过数据生成");
            files = [];
        }

        if (files.Count == 0)
        {
            throw new ModOperationException("没有生成任何文件。");
        }

        if (isBuildOnly)
        {
            interaction.ReportProgress("正在写入生成结果...", 95);
            WriteFilesToDirectory(options.BuildOnlyDir!, files);
            interaction.ReportProgress("完成", 100);
            interaction.Ok($"已生成到 {options.BuildOnlyDir}（未改动游戏）");
            return;
        }

        var dllPath = Path.Combine(installDir, GameLocator.GameDllName);
        if (!options.NoDll)
        {
            interaction.ReportProgress("正在更新启动器 DLL 哈希清单...", 94);
            ManifestPatcher.PatchLauncherDll(dllPath, backupDir, files, interaction);
        }

        interaction.ReportProgress("正在写入修补文件到游戏目录...", 97);
        WriteFilesToDirectory(gameDir, files);
        interaction.Ok($"已写入 {files.Count} 个文件到 {gameDir}");

        interaction.ReportProgress("正在清除游戏网页缓存...", 99);
        GameLocator.ClearWebViewCache();
        interaction.Ok("已清除游戏网页缓存");

        interaction.ReportProgress("安装完成", 100);
    }

    public Task UninstallAsync(
        UninstallOptions options,
        IInstallerInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var root = GameLocator.ResolveRootDirectory(options.RootDir);
        var backupFile = Path.Combine(root, "backup", "launcher.dll");
        var installDir = string.IsNullOrWhiteSpace(options.InstallDir)
            ? GameLocator.FindInstallDir()
            : options.InstallDir;

        interaction.ReportProgress("正在检测游戏目录与备份...", 20);
        if (string.IsNullOrWhiteSpace(installDir))
        {
            throw new ModOperationException("找不到游戏安装目录（Yu-Gi-Oh! Rogue Duelist.dll）。");
        }

        if (!File.Exists(backupFile))
        {
            throw new ModOperationException(@"没有找到备份 backup\launcher.dll，无法自动卸载。");
        }

        interaction.ReportProgress("正在还原启动器 DLL...", 60);
        var dllPath = Path.Combine(installDir, GameLocator.GameDllName);
        File.Copy(backupFile, dllPath, overwrite: true);
        interaction.Ok("已还原启动器 DLL");

        interaction.ReportProgress("正在清除游戏网页缓存...", 85);
        GameLocator.ClearWebViewCache();
        interaction.Ok("已清除网页缓存");

        interaction.ReportProgress("卸载完成", 100);
        return Task.CompletedTask;
    }

    private static void WriteFilesToDirectory(string targetDir, IReadOnlyDictionary<string, byte[]> files)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var (rel, bytes) in files)
        {
            var dest = Path.Combine(targetDir, rel.Replace('/', Path.DirectorySeparatorChar));
            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(dest, bytes);
        }
    }
}
