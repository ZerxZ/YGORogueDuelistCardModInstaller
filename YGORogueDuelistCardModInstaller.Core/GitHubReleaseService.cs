using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YGORogueDuelistCardModInstaller.Core;

public sealed record ProxyTestResult(
    bool Ok,
    int StatusCode,
    long LatencyMs,
    string EffectiveUrl,
    string? Error);

public sealed record ResumableDownloadProgress(
    string FileName,
    string PartPath,
    long DownloadedBytes,
    long TotalBytes);

public static partial class GitHubReleaseService
{
    public static readonly (string Id, string NameZh, string NameEn, string Prefix)[] BuiltInProxyPresets =
    [
        ("official", "官方 GitHub 直连（默认）", "Official GitHub Direct (Default)", ""),
        ("ghfast", "ghfast 代理加速 (ghfast.top)", "ghfast Mirror (ghfast.top)", "https://ghfast.top/"),
        ("gh-proxy", "gh-proxy 代理加速 (gh-proxy.com)", "gh-proxy Mirror (gh-proxy.com)", "https://gh-proxy.com/"),
        ("ghproxy-net", "ghproxy.net 代理加速", "ghproxy.net Mirror", "https://ghproxy.net/")
    ];

    [GeneratedRegex(@"\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExeRegex();

    [GeneratedRegex(@"\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ZipRegex();

    [GeneratedRegex(@"^web-([0-9]+\.[0-9]+\.[0-9]+)\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WebBundleZipRegex();

    [GeneratedRegex(@"^web-([0-9]+\.[0-9]+\.[0-9]+)-to-([0-9]+\.[0-9]+\.[0-9]+)\.delta\.gz$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WebDeltaRegex();

    [GeneratedRegex(@"setup|install|win", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SetupNameRegex();

    [GeneratedRegex(@"/releases/tag/(v?[0-9]+\.[0-9]+(?:\.[0-9]+)?(?:-[A-Za-z0-9._-]+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseTagRedirectRegex();

    /// <summary>
    /// Normalizes a user-configured GitHub mirror prefix (e.g. "https://ghfast.top/").
    /// Returns empty string when using official GitHub directly (default).
    /// </summary>
    public static string NormalizeProxyPrefix(string? proxyPrefix)
    {
        if (string.IsNullOrWhiteSpace(proxyPrefix)) return string.Empty;
        var p = proxyPrefix.Trim();
        if (p.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("direct", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("official", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (!p.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !p.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            p = "https://" + p;
        }

        return p.EndsWith('/') ? p : p + "/";
    }

    /// <summary>
    /// Rewrites GitHub download/web URLs through an optional proxy prefix (e.g. https://ghfast.top/https://github.com/...).
    /// Leaves URL untouched when proxyPrefix is empty (official GitHub default).
    /// </summary>
    public static string ApplyGitHubProxy(string url, string? proxyPrefix)
    {
        var prefix = NormalizeProxyPrefix(proxyPrefix);
        if (string.IsNullOrEmpty(prefix)) return url;
        if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return url;

        if (url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://objects.githubusercontent.com/", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://release-assets.githubusercontent.com/", StringComparison.OrdinalIgnoreCase))
        {
            return prefix + url;
        }

        return url;
    }

    private static HttpClient CreateClient(TimeSpan timeout, bool allowAutoRedirect = true)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ygo-rogue-duelist-mod-installer/2.0");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static async Task<ProxyTestResult> TestProxyAsync(
        string? proxyPrefix,
        string repo = InstallOptions.DefaultRepo,
        CancellationToken ct = default)
    {
        var normalized = NormalizeProxyPrefix(proxyPrefix);
        var probeUrl = string.IsNullOrEmpty(normalized)
            ? $"https://api.github.com/repos/{repo}/releases/latest"
            : ApplyGitHubProxy($"https://github.com/{repo}/releases/latest", normalized);

        var sw = Stopwatch.StartNew();
        try
        {
            using var client = CreateClient(TimeSpan.FromSeconds(15));
            using var req = new HttpRequestMessage(HttpMethod.Head, probeUrl);
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)resp.StatusCode == 405 || (int)resp.StatusCode == 403)
            {
                using var getResp = await client.GetAsync(probeUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                sw.Stop();
                return new ProxyTestResult(getResp.IsSuccessStatusCode, (int)getResp.StatusCode, sw.ElapsedMilliseconds, probeUrl,
                    getResp.IsSuccessStatusCode ? null : $"HTTP {(int)getResp.StatusCode}");
            }
            sw.Stop();
            return new ProxyTestResult(resp.IsSuccessStatusCode, (int)resp.StatusCode, sw.ElapsedMilliseconds, probeUrl,
                resp.IsSuccessStatusCode ? null : $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProxyTestResult(false, 0, sw.ElapsedMilliseconds, probeUrl, ex.Message);
        }
    }

    public static async Task<GitHubRelease?> GetLatestReleaseAsync(
        string repo,
        IInstallerInteraction interaction,
        CancellationToken cancellationToken = default,
        string? githubProxy = null)
    {
        using var client = CreateClient(TimeSpan.FromSeconds(25));
        GitHubRelease? release = null;

        // 1. Official GitHub REST API (gives full asset list, exact byte sizes, and sha256 digests)
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
                release = null;
            }
        }

        // 2. Proxy fallback when official api.github.com is unreachable (e.g. CN users with ghfast.top)
        var proxy = NormalizeProxyPrefix(githubProxy);
        if (release is null && !string.IsNullOrEmpty(proxy))
        {
            try
            {
                interaction.Say($"官方 GitHub API 不可达，尝试通过代理 {proxy} 检测最新版本...");
                release = await TryDiscoverReleaseViaProxyAsync(repo, proxy, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                interaction.Warn($"代理版本检查失败：{ex.Message}");
            }
        }

        if (release is null)
        {
            interaction.Warn("无法访问 GitHub，跳过在线版本检查。");
            return null;
        }

        if (string.IsNullOrWhiteSpace(release.TagName) && !string.IsNullOrWhiteSpace(release.Name))
        {
            release.TagName = release.Name;
        }

        return release;
    }

    private static async Task<GitHubRelease?> TryDiscoverReleaseViaProxyAsync(string repo, string proxyPrefix, CancellationToken ct)
    {
        using var client = CreateClient(TimeSpan.FromSeconds(25), allowAutoRedirect: true);
        var url = ApplyGitHubProxy($"https://github.com/{repo}/releases/latest", proxyPrefix);
        using var resp = await client.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? string.Empty;
        var html = await resp.Content.ReadAsStringAsync(ct);

        var tagMatch = ReleaseTagRedirectRegex().Match(finalUrl);
        if (!tagMatch.Success) tagMatch = ReleaseTagRedirectRegex().Match(html);
        if (!tagMatch.Success) return null;

        var tag = tagMatch.Groups[1].Value;
        var ver = tag.TrimStart('v', 'V');
        var release = new GitHubRelease
        {
            TagName = tag,
            Name = tag,
            HtmlUrl = $"https://github.com/{repo}/releases/tag/{tag}",
            Assets = []
        };

        // Synthesize standard known assets for official game repo when API JSON wasn't reachable
        if (repo.Equals(InstallOptions.DefaultRepo, StringComparison.OrdinalIgnoreCase))
        {
            release.Assets.Add(new GitHubAsset
            {
                Name = $"web-{ver}.zip",
                BrowserDownloadUrl = $"https://github.com/{repo}/releases/download/{tag}/web-{ver}.zip"
            });
            release.Assets.Add(new GitHubAsset
            {
                Name = "YGO-Rogue-Windows-Setup.exe",
                BrowserDownloadUrl = $"https://github.com/{repo}/releases/download/{tag}/YGO-Rogue-Windows-Setup.exe"
            });
        }
        else
        {
            // Scrape /releases/download/<tag>/<filename> links from HTML if present
            var dlPattern = new Regex($@"/releases/download/{Regex.Escape(tag)}/([A-Za-z0-9._-]+)", RegexOptions.IgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in dlPattern.Matches(html))
            {
                var name = m.Groups[1].Value;
                if (seen.Add(name))
                {
                    release.Assets.Add(new GitHubAsset
                    {
                        Name = name,
                        BrowserDownloadUrl = $"https://github.com/{repo}/releases/download/{tag}/{name}"
                    });
                }
            }
        }

        return release;
    }

    public static GitHubAsset? FindWebBundleAsset(GitHubRelease release) =>
        release.Assets.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Name) && WebBundleZipRegex().IsMatch(a.Name));

    public static (GitHubAsset Asset, string FromVersion, string ToVersion, string BaseZipPath)? FindApplicableWebDelta(
        GitHubRelease release,
        string? installDir,
        string? gameBaseDir)
    {
        var deltaCandidates = new List<(GitHubAsset Asset, string FromVer, string ToVer)>();
        foreach (var asset in release.Assets)
        {
            if (string.IsNullOrWhiteSpace(asset.Name) || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl)) continue;
            var m = WebDeltaRegex().Match(asset.Name);
            if (!m.Success) continue;
            deltaCandidates.Add((asset, m.Groups[1].Value, m.Groups[2].Value));
        }

        if (deltaCandidates.Count == 0) return null;

        // Prefer newest matching base version (smallest delta)
        deltaCandidates.Sort((a, b) => GameLocator.CompareVersionText(b.FromVer, a.FromVer));

        foreach (var (asset, fromVer, toVer) in deltaCandidates)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(gameBaseDir))
                candidates.Add(Path.Combine(gameBaseDir, $"{fromVer}.zip"));
            if (!string.IsNullOrWhiteSpace(installDir))
                candidates.Add(Path.Combine(installDir, $"web-{fromVer}.zip"));

            foreach (var baseZip in candidates)
            {
                if (File.Exists(baseZip))
                {
                    return (asset, fromVer, toVer, baseZip);
                }
            }
        }

        return null;
    }

    public static GitHubAsset? FindLinuxLauncherAsset(GitHubRelease release) =>
        release.Assets.FirstOrDefault(a =>
            !string.IsNullOrWhiteSpace(a.Name) &&
            a.Name.Contains("Linux", StringComparison.OrdinalIgnoreCase) &&
            (a.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || a.Name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase)));

    public static GitHubAsset? FindAndroidApkAsset(GitHubRelease release) =>
        release.Assets.FirstOrDefault(a =>
            !string.IsNullOrWhiteSpace(a.Name) &&
            a.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));

    public static GitHubAsset? FindInstallerAsset(GitHubRelease release)
    {
        var assets = release.Assets
            .Where(a => !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl))
            .ToList();
        var exeAsset = assets
            .Where(a => ExeRegex().IsMatch(a.Name!))
            .OrderBy(a => SetupNameRegex().IsMatch(a.Name!) ? 0 : 1)
            .FirstOrDefault();
        var zipAsset = assets.FirstOrDefault(a => ZipRegex().IsMatch(a.Name!) && !WebBundleZipRegex().IsMatch(a.Name!))
            ?? assets.FirstOrDefault(a => ZipRegex().IsMatch(a.Name!));
        return exeAsset ?? zipAsset;
    }

    public static List<ResumableDownloadProgress> ListPartialDownloads(string downloadDir)
    {
        var list = new List<ResumableDownloadProgress>();
        if (!Directory.Exists(downloadDir)) return list;
        foreach (var part in Directory.EnumerateFiles(downloadDir, "*.part"))
        {
            var fi = new FileInfo(part);
            if (fi.Length <= 0) continue;
            var fileName = Path.GetFileNameWithoutExtension(part);
            list.Add(new ResumableDownloadProgress(fileName, part, fi.Length, 0));
        }
        return list;
    }

    /// <summary>
    /// Resumable HTTP Range downloader (.part file + Range: bytes=&lt;offset&gt;- + auto-retry + SHA-256 verification).
    /// Preserves the .part file on cancellation or network interruption so subsequent runs resume seamlessly!
    /// </summary>
    public static async Task<string> DownloadResumableAsync(
        string originalUrl,
        string destinationPath,
        long expectedSize,
        string? expectedSha256,
        string? githubProxy,
        IInstallerInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        var effectiveUrl = ApplyGitHubProxy(originalUrl, githubProxy);
        var proxyLabel = string.IsNullOrWhiteSpace(NormalizeProxyPrefix(githubProxy))
            ? "官方 GitHub"
            : $"代理 {NormalizeProxyPrefix(githubProxy)}";

        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var fileName = Path.GetFileName(destinationPath);
        var partPath = destinationPath + ".part";

        // If destination already exists and matches expectedSha256 (or exact size when no sha), skip re-download!
        if (File.Exists(destinationPath))
        {
            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                var existingHash = ComputeFileSha256Hex(destinationPath);
                if (existingHash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    interaction.Say($"本地文件已存在且 SHA-256 校验通过，跳过重复下载：{fileName}");
                    interaction.ReportProgress($"已就绪 {fileName} (100%)", 100);
                    return destinationPath;
                }
            }
            else if (expectedSize > 0 && new FileInfo(destinationPath).Length == expectedSize)
            {
                interaction.Say($"本地文件已存在且大小一致，跳过重复下载：{fileName}");
                interaction.ReportProgress($"已就绪 {fileName} (100%)", 100);
                return destinationPath;
            }
        }

        const int maxAttempts = 5;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long existingBytes = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            if (existingBytes > 0 && expectedSize > 0 && existingBytes > expectedSize)
            {
                // Corrupt oversized partial file; restart cleanly
                File.Delete(partPath);
                existingBytes = 0;
            }

            if (attempt == 1)
            {
                if (existingBytes > 0)
                {
                    var resumedMb = existingBytes / (1024.0 * 1024.0);
                    var resumePct = expectedSize > 0 ? Math.Clamp(existingBytes * 100.0 / expectedSize, 0, 99) : (double?)null;
                    interaction.Say($"检测到断点缓存（已下载 {resumedMb:0.0} MB），通过「{proxyLabel}」继续断点续传 {fileName}...");
                    interaction.ReportProgress($"正在续传 {fileName}（已缓存 {resumedMb:0.1} MB · {proxyLabel}）...", resumePct);
                }
                else
                {
                    var sizeText = expectedSize > 0 ? $"（{expectedSize / (1024.0 * 1024.0):0.0} MB）" : "";
                    interaction.Say($"开始通过「{proxyLabel}」下载 {fileName}{sizeText}（支持断点续传）...");
                    interaction.ReportProgress($"正在连接并下载 {fileName}{sizeText}（{proxyLabel}）...", 0);
                }
            }
            else
            {
                interaction.Warn($"连接中断，正在第 {attempt}/{maxAttempts} 次自动断点续传（已保存 {existingBytes / (1024.0 * 1024.0):0.0} MB）...");
            }

            try
            {
                using var client = CreateClient(TimeSpan.FromMinutes(30));
                using var request = new HttpRequestMessage(HttpMethod.Get, effectiveUrl);
                if (existingBytes > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(existingBytes, null);
                }

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existingBytes > 0)
                {
                    // File already completely downloaded in .part
                    interaction.Say($"断点文件 {fileName} 已完整接收。");
                    break;
                }

                response.EnsureSuccessStatusCode();

                var isPartial = response.StatusCode == HttpStatusCode.PartialContent;
                long totalBytes;
                FileMode fileMode;
                if (isPartial && existingBytes > 0)
                {
                    totalBytes = response.Content.Headers.ContentRange?.Length
                        ?? (response.Content.Headers.ContentLength is { } cl ? existingBytes + cl : expectedSize);
                    fileMode = FileMode.Append;
                }
                else
                {
                    if (existingBytes > 0)
                    {
                        interaction.Warn("当前服务器不支持 Range 断点续传，已自动回退为从头下载。");
                    }
                    existingBytes = 0;
                    totalBytes = response.Content.Headers.ContentLength ?? expectedSize;
                    fileMode = FileMode.Create;
                }

                await using var srcStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var dstStream = new FileStream(partPath, fileMode, FileAccess.Write, FileShare.Read, 81920, useAsync: true);

                var buffer = new byte[81920];
                long writtenThisRun = 0;
                int read;
                var speedClock = Stopwatch.StartNew();
                var lastReport = DateTime.MinValue;

                while ((read = await srcStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await dstStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    writtenThisRun += read;

                    if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 150)
                    {
                        var currentTotal = existingBytes + writtenThisRun;
                        var secs = Math.Max(0.1, speedClock.Elapsed.TotalSeconds);
                        var bytesPerSec = writtenThisRun / secs;
                        var mbps = bytesPerSec / (1024.0 * 1024.0);
                        if (totalBytes > 0)
                        {
                            var pct = Math.Clamp(currentTotal * 100.0 / totalBytes, 0, 100);
                            var etaSec = bytesPerSec > 1024 ? Math.Max(0, (int)((totalBytes - currentTotal) / bytesPerSec)) : -1;
                            var etaText = etaSec >= 60 ? $" · 剩余 {etaSec / 60}m{etaSec % 60:D2}s"
                                : etaSec >= 0 ? $" · 剩余 {etaSec}s" : "";
                            interaction.ReportProgress(
                                $"正在下载 {fileName} ({pct:0.0}% · {currentTotal / (1024.0 * 1024.0):0.1}/{totalBytes / (1024.0 * 1024.0):0.1} MB · {mbps:0.2} MB/s{etaText})",
                                pct);
                        }
                        else
                        {
                            interaction.ReportProgress(
                                $"正在下载 {fileName} ({currentTotal / (1024.0 * 1024.0):0.1} MB · {mbps:0.2} MB/s)",
                                null);
                        }
                        lastReport = DateTime.UtcNow;
                    }
                }

                await dstStream.FlushAsync(cancellationToken);
                lastError = null;
                break;
            }
            catch (OperationCanceledException)
            {
                var saved = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                interaction.Warn($"下载已暂停，断点缓存已保留（{saved / (1024.0 * 1024.0):0.1} MB），下次点击可继续续传。");
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(8, attempt * 2)), cancellationToken);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (lastError is not null)
        {
            throw new ModOperationException(
                $"下载 {fileName} 失败（已保留断点 .part 文件，可切换代理后继续续传）：{lastError.Message}",
                lastError);
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            interaction.ReportProgress($"正在校验 {fileName} SHA-256...", 99);
            var actualHash = ComputeFileSha256Hex(partPath);
            if (!actualHash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(partPath);
                throw new ModOperationException(
                    $"文件 {fileName} SHA-256 校验不匹配（期望 {expectedSha256[..12]}...，实际 {actualHash[..12]}...），已清理损坏缓存，请重试。");
            }
            interaction.Log(LogLevel.Ok, $"SHA-256 校验通过：{actualHash[..16]}...");
        }

        if (File.Exists(destinationPath)) File.Delete(destinationPath);
        File.Move(partPath, destinationPath);
        interaction.ReportProgress($"下载完成：{fileName}", 100);
        interaction.Log(LogLevel.Ok, $"下载完成：{destinationPath}");
        return destinationPath;
    }

    /// <summary>
    /// Fast vanilla data update mirroring Yu-Gi-Oh! Rogue Duelist's official Windows &amp; Linux updater:
    /// 1) If a matching web-&lt;old&gt;-to-&lt;new&gt;.delta.gz (~5 MB!) exists for a local vanilla zip, downloads the tiny delta
    ///    and applies YGODLT01 locally to reconstruct exact SHA-256-verified web-&lt;new&gt;.zip (~97% smaller!).
    /// 2) Automatically falls back to resumable full web-&lt;new&gt;.zip download if no matching base exists or delta fails.
    /// </summary>
    public static async Task<string> InstallWebBundleUpdateAsync(
        GitHubRelease release,
        string gameBaseDir,
        string downloadDir,
        string? githubProxy,
        IInstallerInteraction interaction,
        CancellationToken cancellationToken = default,
        string? installDir = null)
    {
        var webAsset = FindWebBundleAsset(release)
            ?? throw new ModOperationException($"该发布版本 ({release.EffectiveTag}) 不包含 web-X.Y.Z.zip 轻量更新包，请改用完整安装包更新。");

        var verMatch = WebBundleZipRegex().Match(webAsset.Name!);
        var version = verMatch.Success ? verMatch.Groups[1].Value : (release.EffectiveTag ?? "0.0.0").TrimStart('v', 'V');
        var zipDest = Path.Combine(downloadDir, webAsset.Name!);
        string? downloadedZip = null;

        // 1. Try YGODLT01 Delta Patch first (~5 MB vs 228 MB full web zip!)
        var deltaMatch = FindApplicableWebDelta(release, installDir, gameBaseDir);
        if (deltaMatch is { } dm && dm.ToVersion.Equals(version, StringComparison.OrdinalIgnoreCase))
        {
            var deltaMb = dm.Asset.Size > 0 ? $"（仅 {dm.Asset.Size / (1024.0 * 1024.0):0.2} MB，免下完整包）" : "";
            interaction.Say($"命中官方增量补丁：{dm.Asset.Name}{deltaMb}，将基于本地 v{dm.FromVersion} 无损合成 v{version}！");
            try
            {
                var deltaPath = Path.Combine(downloadDir, dm.Asset.Name!);
                await DownloadResumableAsync(
                    dm.Asset.BrowserDownloadUrl!,
                    deltaPath,
                    dm.Asset.Size,
                    dm.Asset.ExpectedSha256,
                    githubProxy,
                    interaction,
                    cancellationToken);

                await DeltaPatchService.ApplyDeltaAsync(
                    dm.BaseZipPath,
                    deltaPath,
                    zipDest,
                    webAsset.ExpectedSha256,
                    interaction,
                    cancellationToken);

                interaction.Log(LogLevel.Ok, $"增量补丁合成成功！已精确重建 {webAsset.Name}（字节级一致 + SHA-256 通过）");
                downloadedZip = zipDest;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                interaction.Warn($"增量补丁合成失败（{ex.Message}），自动回退为断点续传下载完整 {webAsset.Name}...");
            }
        }

        // 2. Fallback to full web-X.Y.Z.zip (resumable + optional proxy)
        downloadedZip ??= await DownloadResumableAsync(
            webAsset.BrowserDownloadUrl!,
            zipDest,
            webAsset.Size,
            webAsset.ExpectedSha256,
            githubProxy,
            interaction,
            cancellationToken);

        interaction.ReportProgress($"正在解包并校验原版数据包 v{version}...", 96);
        Directory.CreateDirectory(gameBaseDir);
        var targetZip = Path.Combine(gameBaseDir, $"{version}.zip");
        var targetSha = Path.Combine(gameBaseDir, $"{version}.sha256");
        var targetDir = Path.Combine(gameBaseDir, version);

        File.Copy(downloadedZip, targetZip, overwrite: true);
        var shaHex = ComputeFileSha256Hex(targetZip);
        File.WriteAllText(targetSha, shaHex, ModPatcher.Utf8NoBom);

        if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
        Directory.CreateDirectory(targetDir);
        ZipFile.ExtractToDirectory(targetZip, targetDir, overwriteFiles: true);

        interaction.ReportProgress($"原版数据包已更新至 v{version}", 100);
        interaction.Log(LogLevel.Ok, $"极速更新完成！已部署原版数据包 v{version} 到 {targetDir}（SHA-256: {shaHex[..16]}...）");
        return version;
    }

    public static string ExpandOriginalZip(string zipPath, string targetDir)
    {
        if (Directory.Exists(targetDir))
        {
            try { Directory.Delete(targetDir, recursive: true); } catch { }
        }

        Directory.CreateDirectory(targetDir);
        ZipFile.ExtractToDirectory(zipPath, targetDir, overwriteFiles: true);

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
        CancellationToken cancellationToken = default,
        string? githubProxy = null)
    {
        var tag = release.EffectiveTag ?? "unknown";
        interaction.Say($"版本：{tag}");

        var asset = FindInstallerAsset(release)
            ?? throw new ModOperationException($"该发布版本没有可下载的 .exe / .zip 安装文件，请到 https://github.com/{repo}/releases 手动下载。");

        var downloadsDir = Path.Combine(rootDir, "downloads");
        Directory.CreateDirectory(downloadsDir);
        var downloadPath = Path.Combine(downloadsDir, asset.Name!);

        await DownloadResumableAsync(
            asset.BrowserDownloadUrl!,
            downloadPath,
            asset.Size,
            asset.ExpectedSha256,
            githubProxy,
            interaction,
            cancellationToken);

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
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    finally { proc.Dispose(); }
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
        CancellationToken cancellationToken = default,
        string? githubProxy = null)
    {
        interaction.Say($"没有找到已安装的游戏，尝试从 GitHub（{repo}）下载最新版...");
        var release = await GetLatestReleaseAsync(repo, interaction, cancellationToken, githubProxy);
        if (release is null)
        {
            throw new ModOperationException(
                $"无法访问 GitHub，请在设置中启用代理加速（如 https://ghfast.top/）或手动下载：https://github.com/{repo}/releases");
        }

        interaction.Say($"最新版本：{release.EffectiveTag}");
        return await InstallOriginalGameAsync(release, repo, rootDir, interaction, cancellationToken, githubProxy);
    }

    public static async Task<string> UpdateGameIfOutdatedAsync(
        string installDir,
        string repo,
        string rootDir,
        IInstallerInteraction interaction,
        CancellationToken cancellationToken = default,
        string? githubProxy = null,
        bool preferWebBundle = true,
        string? gameBaseDir = null)
    {
        var dataDir = GameLocator.FindGameDir();
        var build = GameLocator.ReadGameVersion(dataDir);

        var release = await GetLatestReleaseAsync(repo, interaction, cancellationToken, githubProxy);
        var tag = release?.EffectiveTag;
        if (string.IsNullOrWhiteSpace(tag))
        {
            interaction.Say($"本机版本：{build ?? "未安装"}（无法获取 GitHub 最新版本，跳过更新）");
            return installDir;
        }

        if (!string.IsNullOrWhiteSpace(build) && GameLocator.CompareVersionText(tag, build) <= 0)
        {
            interaction.Say($"本机版本：{build}，已是最新（GitHub 最新 {tag}）");
            return installDir;
        }

        // Fast path: if launcher is installed and release includes web-X.Y.Z.zip, update lightweight web bundle directly!
        if (preferWebBundle && !string.IsNullOrWhiteSpace(installDir) && FindWebBundleAsset(release!) is not null)
        {
            var resolvedGameBase = !string.IsNullOrWhiteSpace(gameBaseDir)
                ? gameBaseDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), GameLocator.GameFolderName, "Game");
            interaction.Say($"本机版本：{build ?? "未知"}，GitHub 最新 {tag}，启用极速数据包更新（web-.zip + 断点续传）...");
            await InstallWebBundleUpdateAsync(release!, resolvedGameBase, Path.Combine(rootDir, "downloads"), githubProxy, interaction, cancellationToken);
            return installDir;
        }

        interaction.Say($"本机版本：{build ?? "未安装"}，GitHub 上有更新版本 {tag}，开始断点续传下载完整安装包...");
        var newDir = await InstallOriginalGameAsync(release!, repo, rootDir, interaction, cancellationToken, githubProxy);
        return string.IsNullOrWhiteSpace(newDir) ? installDir : newDir;
    }

    private static string ComputeFileSha256Hex(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(sha.ComputeHash(fs));
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
