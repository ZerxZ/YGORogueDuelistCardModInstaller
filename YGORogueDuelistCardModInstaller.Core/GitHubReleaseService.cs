using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace YGORogueDuelistCardModInstaller.Core;

public static partial class GitHubReleaseService
{
    [GeneratedRegex(@"\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExeRegex();

    [GeneratedRegex(@"\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ZipRegex();

    [GeneratedRegex(@"setup|install|win", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SetupNameRegex();

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ygo-rogue-duelist-mod-installer");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static async Task<GitHubRelease?> GetLatestReleaseAsync(
        string repo,
        IInstallerInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(TimeSpan.FromSeconds(60));
        GitHubRelease? release = null;

        try
        {
            using var resp = await client.GetAsync($"https://api.github.com/repos/{repo}/releases/latest", cancellationToken);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            var dto = await JsonSerializer.DeserializeAsync(stream, CoreJsonContext.Default.GitHubReleaseDto, cancellationToken);
            release = dto?.ToModel();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            try
            {
                using var resp = await client.GetAsync($"https://api.github.com/repos/{repo}/releases", cancellationToken);
                resp.EnsureSuccessStatusCode();
                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                var list = await JsonSerializer.DeserializeAsync(stream, CoreJsonContext.Default.ListGitHubReleaseDto, cancellationToken);
                release = list?.FirstOrDefault(r => !r.Draft)?.ToModel();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                interaction.Warn("无法访问 GitHub，跳过在线版本检查。");
                return null;
            }
        }

        if (release is not null && string.IsNullOrWhiteSpace(release.TagName) && !string.IsNullOrWhiteSpace(release.Name))
        {
            release.TagName = release.Name;
        }

        return release;
    }

    public static string ExpandOriginalZip(string zipPath, string targetDir)
    {
        if (Directory.Exists(targetDir))
        {
            try
            {
                Directory.Delete(targetDir, recursive: true);
            }
            catch
            {
                // Match PowerShell -ErrorAction SilentlyContinue
            }
        }

        Directory.CreateDirectory(targetDir);
        ZipFile.ExtractToDirectory(zipPath, targetDir, overwriteFiles: true);

        // If the zip contains a single wrapper folder, descend into it
        var inner = Directory.GetDirectories(targetDir).FirstOrDefault();
        if (inner is not null && !GameLocator.TestGameExe(targetDir) && GameLocator.TestGameExe(inner))
        {
            return inner;
        }

        return targetDir;
    }

    public static async Task<string> InstallOriginalGameAsync(
        GitHubRelease release,
        string repo,
        string rootDir,
        IInstallerInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        var tag = release.EffectiveTag ?? "unknown";
        interaction.Say($"版本：{tag}");

        var assets = release.Assets
            .Where(a => !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl))
            .ToList();

        if (assets.Count == 0)
        {
            throw new ModOperationException($"该发布版本没有可下载的文件，请到 https://github.com/{repo}/releases 手动下载。");
        }

        // Prefer installer (.exe), then archive (.zip)
        var exeAsset = assets
            .Where(a => ExeRegex().IsMatch(a.Name!))
            .OrderBy(a => SetupNameRegex().IsMatch(a.Name!) ? 0 : 1)
            .FirstOrDefault();
        var zipAsset = assets.FirstOrDefault(a => ZipRegex().IsMatch(a.Name!));
        var asset = exeAsset ?? zipAsset;

        if (asset is null)
        {
            throw new ModOperationException($"发布里没有 .exe / .zip 文件，请到 https://github.com/{repo}/releases 手动下载。");
        }

        Directory.CreateDirectory(rootDir);
        var downloadPath = Path.Combine(rootDir, asset.Name!);
        var sizeMb = Math.Round(asset.Size / (1024.0 * 1024.0), 1);
        interaction.Say($"下载 {asset.Name}（{sizeMb:0.0} MB）到 {rootDir} ...");
        interaction.ReportProgress($"正在下载 {asset.Name}...", 0);

        try
        {
            using var client = CreateClient(TimeSpan.FromMinutes(30));
            using var response = await client.GetAsync(
                asset.BrowserDownloadUrl!,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? asset.Size;
            await using var srcStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var dstStream = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long copied = 0;
            int read;
            var lastReport = DateTime.UtcNow;

            while ((read = await srcStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await dstStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                copied += read;
                if (totalBytes > 0 && (DateTime.UtcNow - lastReport).TotalMilliseconds >= 200)
                {
                    var pct = Math.Clamp(copied * 100.0 / totalBytes, 0, 100);
                    interaction.ReportProgress($"正在下载 {asset.Name} ({pct:0.0}%)...", pct);
                    lastReport = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ModOperationException(
                $"下载失败：{ex.Message}\n  请到 https://github.com/{repo}/releases 手动下载。",
                ex);
        }

        interaction.Say($"下载完成：{downloadPath}");
        interaction.ReportProgress("正在安装原版游戏...", null);

        string? dir;
        if (ZipRegex().IsMatch(downloadPath))
        {
            dir = ExpandOriginalZip(downloadPath, Path.Combine(rootDir, GameLocator.GameFolderName));
            interaction.Say($"已解压到：{dir}");
        }
        else
        {
            interaction.Say("运行安装程序（Inno Setup 静默安装）...");
            var silentOk = false;
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = downloadPath,
                    Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
                    UseShellExecute = true
                });
                if (proc is not null)
                {
                    await proc.WaitForExitAsync(cancellationToken);
                    silentOk = proc.ExitCode == 0;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                silentOk = false;
            }

            if (!silentOk)
            {
                interaction.Say("静默安装不可用，改为交互安装，请按窗口提示完成安装...");
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = downloadPath,
                    UseShellExecute = true
                });
                if (proc is not null)
                {
                    await proc.WaitForExitAsync(cancellationToken);
                }
            }

            dir = GameLocator.FindInstallDir();
            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs",
                    GameLocator.GameFolderName);
            }

            if (!GameLocator.TestGameExe(dir))
            {
                dir = null;
            }
        }

        if (string.IsNullOrWhiteSpace(dir))
        {
            throw new ModOperationException(
                "安装程序已运行，但没检测到游戏目录。\n" +
                "  请启动游戏一次（进入主界面再退出），然后重新运行本安装器，或用 --install-dir 指定目录。");
        }

        // First launch extracts web assets into %LOCALAPPDATA%\Yu-Gi-Oh! Rogue Duelist\Game
        if (GameLocator.FindGameDir() is null)
        {
            var exePath = Path.Combine(dir, GameLocator.GameExeName);
            if (File.Exists(exePath))
            {
                interaction.Say("首次启动游戏，等待它解包网页数据（最多 3 分钟）...");
                interaction.ReportProgress("等待游戏首次解包网页数据...", null);
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = dir,
                    UseShellExecute = true
                });

                var deadline = DateTime.UtcNow.AddSeconds(180);
                while (DateTime.UtcNow < deadline && GameLocator.FindGameDir() is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

                foreach (var proc in Process.GetProcessesByName(GameLocator.GameFolderName))
                {
                    try
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // Ignore already exited processes.
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }

                if (GameLocator.FindGameDir() is null)
                {
                    throw new ModOperationException(
                        "游戏已启动但没有生成 %LOCALAPPDATA%\\Yu-Gi-Oh! Rogue Duelist\\Game。\n" +
                        "  请手动启动游戏一次（进入主界面再退出），然后重新运行本安装器。");
                }
            }
        }

        interaction.Say($"游戏目录：{dir}");
        return dir;
    }

    public static async Task<string> GetOriginalGameAsync(
        string repo,
        string rootDir,
        IInstallerInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        interaction.Say($"没有找到已安装的游戏，尝试从 GitHub（{repo}）下载最新版...");
        var release = await GetLatestReleaseAsync(repo, interaction, cancellationToken);
        if (release is null)
        {
            throw new ModOperationException(
                $"无法访问 GitHub，请手动下载游戏：https://github.com/{repo}/releases\n" +
                "  下载后安装，启动游戏一次（进入主界面再退出），然后重新运行本安装器。");
        }

        interaction.Say($"最新版本：{release.EffectiveTag}");
        return await InstallOriginalGameAsync(release, repo, rootDir, interaction, cancellationToken);
    }

    public static async Task<string> UpdateGameIfOutdatedAsync(
        string installDir,
        string repo,
        string rootDir,
        IInstallerInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return installDir;
        }

        var dataDir = GameLocator.FindGameDir();
        var build = GameLocator.ReadGameVersion(dataDir);
        if (string.IsNullOrWhiteSpace(build))
        {
            return installDir;
        }

        var release = await GetLatestReleaseAsync(repo, interaction, cancellationToken);
        var tag = release?.EffectiveTag;
        if (string.IsNullOrWhiteSpace(tag))
        {
            interaction.Say($"本机版本：{build}（无法获取 GitHub 最新版本，跳过更新）");
            return installDir;
        }

        if (GameLocator.CompareVersionText(tag, build) <= 0)
        {
            interaction.Say($"本机版本：{build}，已是最新（GitHub 最新 {tag}）");
            return installDir;
        }

        interaction.Say($"本机版本：{build}，GitHub 上有更新版本 {tag}，开始下载更新...");
        var newDir = await InstallOriginalGameAsync(release!, repo, rootDir, interaction, cancellationToken);
        return string.IsNullOrWhiteSpace(newDir) ? installDir : newDir;
    }
}
