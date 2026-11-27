using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace YGORogueDuelistCardModInstaller.Core.ModManager;

/// <summary>
/// Full Mod Manager engine supporting:
/// 1. Zero-Touch Virtual Bundle injection (Game\900.x.y + .zip + .sha256): NEVER modifies game exe/dll or original web-*.zip!
/// 2. Legacy Launcher Manifest injection (StartupHashes.json in DLL): preserves exact original version number.
/// 3. BuildOnly export, conflict analysis, automatic official update check, one-click restore, and direct game launch.
/// </summary>
public sealed partial class ModInjectionEngine
{
    public const int VirtualMajorBase = 900;

    // Exact file extension allowlist enforced by Yu-Gi-Oh! Rogue Duelist's GameFiles.Extract (see tools/windows_release.py)
    private static readonly HashSet<string> AllowedBundleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".mjs", ".json", ".html", ".css", ".wasm", ".zip", ".gz", ".png",
        ".jpg", ".jpeg", ".webp", ".gif", ".svg", ".ico", ".woff", ".woff2",
        ".ttf", ".otf", ".mp3", ".wav", ".ogg", ".flac", ".webmanifest", ".txt",
        ".md", ".map", ".avif", ".dat", ".bin", ".webm", ".mp4", ".m4a"
    };

    private static readonly HashSet<string> AllowedBundleSpecialNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "_headers", "LICENSE"
    };

    [GeneratedRegex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex ThreePartVersionRegex();

    public ModRepository Repository { get; }

    public ModInjectionEngine(string? workspaceRoot = null)
    {
        Repository = new ModRepository(workspaceRoot);
    }

    public static bool IsVirtualVersion(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText) || !ThreePartVersionRegex().IsMatch(versionText))
        {
            return false;
        }

        return Version.TryParse(versionText, out var ver) && ver.Major >= VirtualMajorBase;
    }

    public static string ComputeVirtualVersion(string baseVersion)
    {
        if (!Version.TryParse(baseVersion, out var parsed))
        {
            return $"{VirtualMajorBase}.0.1";
        }

        var major = parsed.Major >= VirtualMajorBase ? parsed.Major : VirtualMajorBase + parsed.Major;
        var minor = Math.Max(0, parsed.Minor);
        var patch = Math.Max(0, parsed.Build);
        return $"{major}.{minor}.{patch}";
    }

    public static string ResolveGameBaseDir(string? explicitGameDir = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitGameDir))
        {
            var trimmed = explicitGameDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var leaf = Path.GetFileName(trimmed);
            if (ThreePartVersionRegex().IsMatch(leaf))
            {
                return Path.GetDirectoryName(trimmed) ?? trimmed;
            }

            return trimmed;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var primary = Path.Combine(localAppData, GameLocator.GameFolderName, "Game");
        if (Directory.Exists(primary))
        {
            return primary;
        }

        var legacy = Path.Combine(localAppData, GameLocator.LegacyFolderName, "Game");
        if (Directory.Exists(legacy))
        {
            return legacy;
        }

        return primary;
    }

    public static List<string> FindInstalledVirtualVersions(string? gameBaseDir)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(gameBaseDir) || !Directory.Exists(gameBaseDir))
        {
            return [];
        }

        foreach (var dir in Directory.GetDirectories(gameBaseDir))
        {
            var name = Path.GetFileName(dir);
            if (IsVirtualVersion(name))
            {
                results.Add(name);
            }
        }

        foreach (var file in Directory.GetFiles(gameBaseDir, "*.zip"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (IsVirtualVersion(name))
            {
                results.Add(name);
            }
        }

        return results.OrderByDescending(v => v, Comparer<string>.Create(GameLocator.CompareVersionText)).ToList();
    }

    /// <summary>
    /// Finds the highest official (non-virtual) vanilla ZIP across both
    /// InstallDir\web-&lt;ver&gt;.zip and GameBaseDir\&lt;ver&gt;.zip without touching either file.
    /// </summary>
    public static (string Version, string ZipPath, bool FromGameCache)? FindBestVanillaSource(
        string? installDir,
        string? gameBaseDir,
        LauncherMetadata? launcherMeta)
    {
        var candidates = new List<(string Version, string ZipPath, bool FromGameCache)>();

        if (!string.IsNullOrWhiteSpace(installDir) && Directory.Exists(installDir))
        {
            if (launcherMeta is not null)
            {
                var bundled = Path.Combine(installDir, launcherMeta.InitialBundleFileName);
                if (File.Exists(bundled))
                {
                    candidates.Add((launcherMeta.InitialBundleVersion, bundled, false));
                }
            }

            foreach (var zip in Directory.GetFiles(installDir, "web-*.zip"))
            {
                var stem = Path.GetFileNameWithoutExtension(zip);
                if (!stem.StartsWith("web-", StringComparison.OrdinalIgnoreCase)) continue;
                var ver = stem[4..];
                if (ThreePartVersionRegex().IsMatch(ver) && !IsVirtualVersion(ver) &&
                    !candidates.Any(c => string.Equals(c.ZipPath, zip, StringComparison.OrdinalIgnoreCase)))
                {
                    candidates.Add((ver, zip, false));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(gameBaseDir) && Directory.Exists(gameBaseDir))
        {
            foreach (var zip in Directory.GetFiles(gameBaseDir, "*.zip"))
            {
                var ver = Path.GetFileNameWithoutExtension(zip);
                if (ThreePartVersionRegex().IsMatch(ver) && !IsVirtualVersion(ver))
                {
                    candidates.Add((ver, zip, true));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        candidates.Sort((a, b) =>
        {
            var cmp = GameLocator.CompareVersionText(b.Version, a.Version);
            if (cmp != 0) return cmp;
            // Prefer InstallDir web-<ver>.zip over Game cache zip for the same version
            return a.FromGameCache.CompareTo(b.FromGameCache);
        });

        return candidates[0];
    }

    public ModManagerStatusSnapshot GetStatus()
    {
        var state = Repository.LoadState();
        var mods = Repository.LoadMods(state);
        var conflicts = ModConflictAnalyzer.Analyze(mods);

        var installDir = !string.IsNullOrWhiteSpace(state.InstallDir) && Directory.Exists(state.InstallDir)
            ? state.InstallDir
            : GameLocator.FindInstallDir();
        var gameBaseDir = ResolveGameBaseDir(state.GameDir);
        var dllPath = string.IsNullOrWhiteSpace(installDir) ? null : Path.Combine(installDir, GameLocator.GameDllName);
        var launcherMeta = LauncherInspector.TryReadMetadata(dllPath);
        var bestVanilla = FindBestVanillaSource(installDir, gameBaseDir, launcherMeta);
        var baseVersion = bestVanilla?.Version ?? launcherMeta?.InitialBundleVersion;
        var virtualVersions = FindInstalledVirtualVersions(gameBaseDir);
        var hasBackup = File.Exists(Path.Combine(Repository.BackupDir, "launcher.dll"));

        var fingerprint = ModCompositor.ComputeConfigurationFingerprint(state.InjectionMode, baseVersion, mods, state.DeveloperMode);
        var versionIssues = ModRepository.EvaluateVersionIssues(mods, baseVersion);
        var (syncState, syncSummary) = EvaluateSyncState(state, baseVersion, fingerprint, virtualVersions, mods);

        return new ModManagerStatusSnapshot(
            Repository.WorkspaceRoot,
            Repository.ModsRoot,
            installDir,
            gameBaseDir,
            baseVersion,
            launcherMeta,
            state.InjectionMode,
            state.DeveloperMode,
            syncState,
            syncSummary,
            fingerprint,
            mods,
            conflicts,
            versionIssues,
            hasBackup,
            virtualVersions);
    }

    private static (InjectionSyncState State, string Summary) EvaluateSyncState(
        ModManagerState state,
        string? baseVersion,
        string currentFingerprint,
        IReadOnlyList<string> virtualVersions,
        IReadOnlyList<ManagedMod> mods)
    {
        var enabledCount = mods.Count(m => m.Enabled);
        var last = state.LastInjection;

        if (last is null)
        {
            return virtualVersions.Count > 0
                ? (InjectionSyncState.OutOfDate, $"检测到已有虚拟 MOD 包 ({virtualVersions[0]})，建议重新应用同步")
                : (InjectionSyncState.Vanilla, enabledCount > 0 ? $"纯净原版（已勾选 {enabledCount} 个 MOD 待注入）" : "纯净原版（未启用 MOD）");
        }

        if (!string.IsNullOrWhiteSpace(baseVersion) &&
            !string.IsNullOrWhiteSpace(last.BaseGameVersion) &&
            !string.Equals(baseVersion, last.BaseGameVersion, StringComparison.Ordinal))
        {
            return (InjectionSyncState.GameUpdated, $"游戏已更新至 {baseVersion}（上次基于 {last.BaseGameVersion} 注入），请点击重新注入");
        }

        if (last.Mode == InjectionMode.VirtualBundle &&
            (string.IsNullOrWhiteSpace(last.VirtualVersion) ||
             !virtualVersions.Contains(last.VirtualVersion, StringComparer.OrdinalIgnoreCase)))
        {
            return (InjectionSyncState.OutOfDate, "虚拟 MOD 包已被移除，请重新点击应用注入");
        }

        if (string.Equals(last.ConfigFingerprint, currentFingerprint, StringComparison.Ordinal))
        {
            var modeLabel = last.Mode == InjectionMode.VirtualBundle
                ? $"零侵入虚拟包 {last.VirtualVersion}"
                : $"启动器清单 {last.BaseGameVersion}";
            return (InjectionSyncState.Synced, $"已同步（{modeLabel} · {last.AppliedModIds.Count} 个 MOD）");
        }

        return (InjectionSyncState.OutOfDate, $"MOD 列表或模式已变更（已勾选 {enabledCount} 个 MOD），需重新应用");
    }

    public async Task ApplyAsync(
        ApplyModsOptions options,
        IInstallerInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        var state = Repository.LoadState();
        var mods = Repository.LoadMods(state);
        var mode = options.ModeOverride ?? state.InjectionMode;
        var isBuildOnly = !string.IsNullOrWhiteSpace(options.BuildOnlyDir);

        var installDir = !string.IsNullOrWhiteSpace(state.InstallDir) && Directory.Exists(state.InstallDir)
            ? state.InstallDir
            : GameLocator.FindInstallDir();
        var gameBaseDir = ResolveGameBaseDir(state.GameDir);
        var picsDir = !string.IsNullOrWhiteSpace(state.PicsDir) && Directory.Exists(state.PicsDir)
            ? state.PicsDir
            : GameLocator.FindPicsDir();

        if (!isBuildOnly)
        {
            await EnsureGameNotRunningAsync(options.ForceKillRunningGame, interaction, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(installDir))
        {
            var standaloneVanilla = FindBestVanillaSource(null, gameBaseDir, null);
            if (standaloneVanilla is null)
            {
                if (isBuildOnly)
                {
                    throw new ModOperationException("找不到原版数据包 web-<版本>.zip（未安装原版游戏也未下载独立 web-*.zip）。");
                }

                var repo = string.IsNullOrWhiteSpace(state.Repo) ? InstallOptions.DefaultRepo : state.Repo;
                interaction.Say("未检测到原版 EXE，正在直接下载轻量数据包 web-*.zip（免装原版安装包，支持直接试玩与更新）...");
                var rel = await GitHubReleaseService.GetLatestReleaseAsync(repo, interaction, cancellationToken, state.GitHubProxy)
                    ?? throw new ModOperationException($"无法访问 GitHub，请在设置中启用代理加速或手动下载 web-*.zip。");
                await GitHubReleaseService.InstallWebBundleUpdateAsync(
                    rel,
                    gameBaseDir,
                    Path.Combine(Repository.WorkspaceRoot, "downloads"),
                    state.GitHubProxy,
                    interaction,
                    cancellationToken,
                    null);
            }
        }

        var dllPath = string.IsNullOrWhiteSpace(installDir) ? null : Path.Combine(installDir, GameLocator.GameDllName);
        var launcherMeta = LauncherInspector.TryReadMetadata(dllPath);
        var vanilla = FindBestVanillaSource(installDir, gameBaseDir, launcherMeta)
            ?? throw new ModOperationException($"在 {installDir ?? "(无 EXE)"} 或 {gameBaseDir} 中找不到原版数据包 web-<版本>.zip。");

        var enabledMods = mods.Where(m => m.Enabled).OrderBy(m => m.Order).ToList();
        var developerMode = options.DeveloperModeOverride ?? state.DeveloperMode;
        if (enabledMods.Count == 0 && !developerMode && !isBuildOnly)
        {
            interaction.Say("当前没有启用任何 MOD，正在还原为纯净原版...");
            await RestoreVanillaAsync(interaction, forceKillRunningGame: options.ForceKillRunningGame, cancellationToken);
            if (options.LaunchGameAfter)
            {
                LaunchGame(installDir, interaction);
            }

            return;
        }

        // Enforce minGameVersion / maxGameVersion / minManagerVersion constraints
        var versionIssues = ModRepository.EvaluateVersionIssues(mods, vanilla.Version);
        if (versionIssues.Count > 0)
        {
            var detail = string.Join("\n  ", versionIssues.Select(v => $"• {v.ModName} ({v.ModId}): {v.Message}"));
            interaction.Warn($"检测到 {versionIssues.Count} 项版本兼容性不满足：\n  {detail}");
            if (!isBuildOnly && !options.IgnoreVersionRequirements)
            {
                if (options.AssumeYes)
                {
                    throw new ModOperationException(
                        $"以下 MOD 不满足最低/最高版本要求，已阻止注入（如确认要强行测试请加上 --ignore-version）：\n  {detail}");
                }

                var proceed = await interaction.ConfirmAsync(
                    $"以下 MOD 不满足最低/最高版本要求：\n{detail}\n\n仍要强行继续注入吗？",
                    cancellationToken);
                if (!proceed)
                {
                    throw new ModOperationException("因 MOD 版本要求不满足，已取消注入。");
                }
            }
        }

        // Report conflicts transparently before composing
        var conflicts = ModConflictAnalyzer.Analyze(mods);
        if (conflicts.Count > 0)
        {
            interaction.Warn($"检测到 {conflicts.Count} 处 MOD 资源重叠，将按加载顺序以后加载的 MOD 为准。");
        }

        interaction.ReportProgress("正在从纯净原版合成 MOD...", 8);
        var composed = await Task.Run(
            () => ModCompositor.Compose(vanilla.ZipPath, mods, picsDir, interaction, cancellationToken, developerMode, options.HotReloadPort),
            cancellationToken);

        if (composed.Files.Count == 0)
        {
            throw new ModOperationException("所有已启用的 MOD 均未产生任何需要注入的文件。");
        }

        if (isBuildOnly)
        {
            interaction.ReportProgress("正在导出测试文件...", 95);
            WriteFilesToDirectory(options.BuildOnlyDir!, composed.Files);
            interaction.ReportProgress("导出完成", 100);
            interaction.Ok($"已将 {composed.AppliedModCount} 个 MOD 合成导出到 {options.BuildOnlyDir}（未改动游戏）");
            return;
        }

        string? appliedVirtualVersion = null;
        if (mode == InjectionMode.VirtualBundle)
        {
            // Ensure launcher DLL stays pristine if user previously used LauncherManifest mode
            if (!string.IsNullOrWhiteSpace(dllPath))
            {
                RestoreLauncherDllIfModified(dllPath, interaction);
            }

            appliedVirtualVersion = ComputeVirtualVersion(vanilla.Version);
            await Task.Run(
                () => ApplyVirtualBundleInjection(
                    vanilla.ZipPath,
                    vanilla.Version,
                    appliedVirtualVersion,
                    gameBaseDir,
                    launcherMeta,
                    composed,
                    interaction,
                    cancellationToken),
                cancellationToken);
        }
        else
        {
            if (composed.NewlyAddedPaths.Count > 0)
            {
                throw new ModOperationException(
                    $"检测到 {composed.NewlyAddedPaths.Count} 个原版不存在的新增文件（如 {composed.NewlyAddedPaths.First()}）。\n" +
                    "  「启动器清单注入」模式无法扩展固定长度的 StartupHashes.json，请切换到「零侵入虚拟版本包」模式！");
            }

            // Remove any virtual bundles so launcher loads the patched baseline version
            RemoveVirtualBundles(gameBaseDir, interaction);

            var targetGameDir = Path.Combine(gameBaseDir, vanilla.Version);
            if (!Directory.Exists(targetGameDir))
            {
                throw new ModOperationException($"找不到已解包的游戏数据目录：{targetGameDir}。请先启动一次游戏或使用「零侵入虚拟版本包」模式。");
            }

            if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
            {
                throw new ModOperationException("未检测到原版启动器 DLL，无法使用「启动器清单注入」模式；请使用「零侵入虚拟版本包」或独立 web-.zip 模式。");
            }

            interaction.ReportProgress("正在更新启动器 DLL 哈希清单...", 90);
            ManifestPatcher.PatchLauncherDll(dllPath, Repository.BackupDir, composed.Files, interaction);

            interaction.ReportProgress("正在写入修补文件到游戏目录...", 95);
            WriteFilesToDirectory(targetGameDir, composed.Files);
            interaction.Ok($"已写入 {composed.Files.Count} 个文件到 {targetGameDir}");
        }

        interaction.ReportProgress("正在清除 WebView2 网页缓存...", 98);
        GameLocator.ClearWebViewCache();
        interaction.Ok("已清除游戏网页缓存");

        var fingerprint = ModCompositor.ComputeConfigurationFingerprint(mode, vanilla.Version, mods, developerMode);
        state.InjectionMode = mode;
        state.DeveloperMode = developerMode;
        state.LastInjection = new LastInjectionRecord
        {
            Mode = mode,
            BaseGameVersion = vanilla.Version,
            VirtualVersion = appliedVirtualVersion,
            AppliedAtUtc = DateTimeOffset.UtcNow,
            ConfigFingerprint = fingerprint,
            AppliedModIds = enabledMods.Select(m => m.Id).ToList()
        };
        Repository.SaveState(state);

        interaction.ReportProgress("注入完成", 100);
        interaction.Ok(mode == InjectionMode.VirtualBundle
            ? $"零侵入虚拟版本包注入完成（原版 DLL 与原版 ZIP 零改动，虚拟版本 {appliedVirtualVersion}）"
            : $"启动器清单注入完成（版本 {vanilla.Version}）");

        if (options.LaunchGameAfter)
        {
            LaunchGame(installDir, interaction);
        }
    }

    private static void ApplyVirtualBundleInjection(
        string vanillaZipPath,
        string baseVersion,
        string virtualVersion,
        string gameBaseDir,
        LauncherMetadata? launcherMeta,
        ComposedModOutput composed,
        IInstallerInteraction interaction,
        CancellationToken cancellationToken)
    {
        ValidateBundleFileAllowlist(composed.Files.Keys);

        Directory.CreateDirectory(gameBaseDir);
        var desktopBoot = launcherMeta?.DesktopBootScript ?? LauncherInspector.DefaultDesktopBoot;

        // Prepare live-version.json for the virtual bundle (strictly required by GameFiles.SelectVerified)
        var bundleEntries = new Dictionary<string, byte[]>(composed.Files, StringComparer.Ordinal)
        {
            ["live-version.json"] = ModPatcher.GetUtf8Bytes($"{{\"version\":\"{virtualVersion}\"}}")
        };

        // Never overwrite web-boot.js inside the zip unless a mod explicitly added it;
        // on disk web-boot.js MUST equal DesktopBoot for VerifyInstalled to pass.
        var tempZipPath = Path.Combine(gameBaseDir, $".mod-zip-{Guid.NewGuid():N}.tmp");
        var finalZipPath = Path.Combine(gameBaseDir, $"{virtualVersion}.zip");
        var finalShaPath = Path.Combine(gameBaseDir, $"{virtualVersion}.sha256");
        var stageDir = Path.Combine(gameBaseDir, $".mod-stage-{Guid.NewGuid():N}");
        var finalDir = Path.Combine(gameBaseDir, virtualVersion);

        try
        {
            interaction.ReportProgress($"正在基于纯净原版 {baseVersion} 生成隔离虚拟包 {virtualVersion}.zip...", 86);
            // Copy untouched vanilla zip and update only changed/new entries (avoids recompressing 220 MB of media!)
            File.Copy(vanillaZipPath, tempZipPath, overwrite: true);

            string? replaysZipText = null;
            using (var zip = ZipFile.Open(tempZipPath, ZipArchiveMode.Update))
            {
                foreach (var (rel, data) in bundleEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var existing = zip.Entries.FirstOrDefault(e =>
                        string.Equals(e.FullName, rel, StringComparison.OrdinalIgnoreCase));
                    var entryName = existing?.FullName ?? rel;
                    existing?.Delete();

                    var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
                    using var entryStream = entry.Open();
                    entryStream.Write(data, 0, data.Length);
                }

                // Read final replays.js from the zip so we can apply AdaptReplay accurately on disk
                var replaysEntry = zip.GetEntry("replays.js");
                if (replaysEntry is not null)
                {
                    using var rs = replaysEntry.Open();
                    using var reader = new StreamReader(rs, Encoding.UTF8);
                    replaysZipText = reader.ReadToEnd();
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            interaction.ReportProgress("正在计算虚拟包 SHA-256 校验值...", 90);
            string zipSha256;
            using (var zipStream = File.OpenRead(tempZipPath))
            {
                zipSha256 = Convert.ToHexStringLower(SHA256.HashData(zipStream));
            }

            interaction.ReportProgress($"正在展开虚拟版本目录 Game\\{virtualVersion}...", 93);
            Directory.CreateDirectory(stageDir);

            // Fast extraction directly from our verified virtual zip guarantees 100% file-set parity with VerifyInstalled
            ZipFile.ExtractToDirectory(tempZipPath, stageDir, overwriteFiles: true);

            // Apply exact GameFiles.AdaptForDesktop transformations expected by VerifyInstalled
            File.WriteAllText(Path.Combine(stageDir, "web-boot.js"), desktopBoot, ModPatcher.Utf8NoBom);
            if (replaysZipText is not null)
            {
                File.WriteAllText(
                    Path.Combine(stageDir, "replays.js"),
                    LauncherInspector.AdaptReplayText(replaysZipText),
                    ModPatcher.Utf8NoBom);
            }

            cancellationToken.ThrowIfCancellationRequested();
            interaction.ReportProgress("正在原子切换虚拟版本包...", 96);

            // Clean up any stale virtual versions (including prior build of same virtualVersion)
            RemoveVirtualBundles(gameBaseDir, interaction, silent: true);

            File.Move(tempZipPath, finalZipPath, overwrite: true);
            File.WriteAllText(finalShaPath, zipSha256, ModPatcher.Utf8NoBom);
            Directory.Move(stageDir, finalDir);

            interaction.Ok($"已生成零侵入虚拟版本包：{finalDir}（含 {composed.Files.Count} 个注入/新增文件）");
            interaction.Say($"虚拟包校验：{virtualVersion}.sha256 = {zipSha256[..16]}...");
        }
        catch
        {
            TryDeleteFile(tempZipPath);
            TryDeleteDirectory(stageDir);
            throw;
        }
    }

    public async Task RestoreVanillaAsync(
        IInstallerInteraction interaction,
        bool forceKillRunningGame = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureGameNotRunningAsync(forceKillRunningGame, interaction, cancellationToken);

        var state = Repository.LoadState();
        var installDir = !string.IsNullOrWhiteSpace(state.InstallDir) && Directory.Exists(state.InstallDir)
            ? state.InstallDir
            : GameLocator.FindInstallDir();
        var gameBaseDir = ResolveGameBaseDir(state.GameDir);

        interaction.ReportProgress("正在清理虚拟 MOD 版本包...", 30);
        var removedVirtual = RemoveVirtualBundles(gameBaseDir, interaction);

        interaction.ReportProgress("正在检查并还原启动器 DLL...", 65);
        var restoredDll = false;
        if (!string.IsNullOrWhiteSpace(installDir))
        {
            var dllPath = Path.Combine(installDir, GameLocator.GameDllName);
            restoredDll = RestoreLauncherDllIfModified(dllPath, interaction);
        }

        interaction.ReportProgress("正在清除 WebView2 网页缓存...", 85);
        GameLocator.ClearWebViewCache();
        interaction.Ok("已清除游戏网页缓存");

        state.LastInjection = null;
        Repository.SaveState(state);

        if (removedVirtual == 0 && !restoredDll)
        {
            interaction.Say("当前已是纯净原版状态。");
        }

        interaction.ReportProgress("已还原纯净原版", 100);
        interaction.Ok("还原完成，下次启动游戏将运行纯净原版。");
    }

    public async Task UpdateOfficialGameAsync(
        IInstallerInteraction interaction,
        CancellationToken cancellationToken = default,
        bool preferWebBundle = true,
        bool forceDownload = false)
    {
        var state = Repository.LoadState();
        var installDir = !string.IsNullOrWhiteSpace(state.InstallDir) && Directory.Exists(state.InstallDir)
            ? state.InstallDir
            : GameLocator.FindInstallDir();
        var gameBaseDir = ResolveGameBaseDir(state.GameDir);
        var repo = string.IsNullOrWhiteSpace(state.Repo) ? InstallOptions.DefaultRepo : state.Repo;
        var proxy = state.GitHubProxy;

        await EnsureGameNotRunningAsync(forceKill: false, interaction, cancellationToken);

        // Standalone Web-Bundle mode: even without original .exe installer, allow downloading/updating web-X.Y.Z.zip (and .delta.gz!)
        if (string.IsNullOrWhiteSpace(installDir) && !preferWebBundle)
        {
            installDir = await GitHubReleaseService.GetOriginalGameAsync(repo, Repository.WorkspaceRoot, interaction, cancellationToken, proxy);
            state.InstallDir = installDir;
            Repository.SaveState(state);
            interaction.Ok($"原版游戏已下载安装到：{installDir}");
            return;
        }

        var dllPath = string.IsNullOrWhiteSpace(installDir) ? null : Path.Combine(installDir, GameLocator.GameDllName);
        var meta = LauncherInspector.TryReadMetadata(dllPath);
        var vanilla = FindBestVanillaSource(installDir, gameBaseDir, meta);
        var currentOfficialVersion = vanilla?.Version ?? meta?.InitialBundleVersion;

        interaction.ReportProgress("正在检查 GitHub 最新官方版本...", 15);
        var release = await GitHubReleaseService.GetLatestReleaseAsync(repo, interaction, cancellationToken, proxy);
        var latestTag = release?.EffectiveTag;
        if (string.IsNullOrWhiteSpace(latestTag))
        {
            interaction.Warn("无法获取 GitHub 最新版本信息。如访问受限可在设置中启用代理加速（如 https://ghfast.top/）。");
            return;
        }

        if (!forceDownload &&
            !string.IsNullOrWhiteSpace(currentOfficialVersion) &&
            GameLocator.CompareVersionText(latestTag, currentOfficialVersion) <= 0)
        {
            interaction.Ok($"本机原版数据包（{currentOfficialVersion}）已是最新（GitHub 最新 {latestTag}）。");
            interaction.ReportProgress("已是最新版本", 100);
            return;
        }

        if (preferWebBundle && GitHubReleaseService.FindWebBundleAsset(release!) is not null)
        {
            var modeDesc = string.IsNullOrWhiteSpace(installDir)
                ? "独立 web-.zip 模式（无需原版 EXE，下载后即可直接试玩/游玩）"
                : "极速增量/数据包更新（支持 .delta.gz 补丁 + 断点续传）";
            interaction.Say($"检测到版本 {latestTag}（本机原版：{currentOfficialVersion ?? "未下载"}），启用 {modeDesc}...");
            await GitHubReleaseService.InstallWebBundleUpdateAsync(
                release!,
                gameBaseDir,
                Path.Combine(Repository.WorkspaceRoot, "downloads"),
                proxy,
                interaction,
                cancellationToken,
                installDir);
            interaction.Ok(string.IsNullOrWhiteSpace(installDir)
                ? $"独立原版数据包已就绪 ({latestTag})！无需安装原版 EXE，点击「启动热加载」即可直接在内嵌试玩或浏览器开玩！"
                : $"原版数据包已更新至 {latestTag}！如需继续使用 MOD，请点击「应用注入」将全部 MOD 叠加到新版本。");
            interaction.ReportProgress("更新完成", 100);
            return;
        }

        interaction.Say($"检测到版本 {latestTag}（本机原版：{currentOfficialVersion ?? "未知"}），开始断点续传下载完整安装包...");
        var newDir = await GitHubReleaseService.InstallOriginalGameAsync(release!, repo, Repository.WorkspaceRoot, interaction, cancellationToken, proxy);
        state.InstallDir = newDir;
        Repository.SaveState(state);

        // Update backup copy to the fresh official launcher if a backup previously existed
        var backupFile = Path.Combine(Repository.BackupDir, "launcher.dll");
        var newDll = Path.Combine(newDir, GameLocator.GameDllName);
        if (File.Exists(backupFile) && File.Exists(newDll))
        {
            File.Copy(newDll, backupFile, overwrite: true);
        }

        interaction.Ok($"原版游戏已更新至 {latestTag}！如需继续使用 MOD，请点击「应用注入」将全部 MOD 叠加到新版本。");
        interaction.ReportProgress("更新完成", 100);
    }

    public static void LaunchGame(string? installDir, IInstallerInteraction? interaction = null)
    {
        var dir = !string.IsNullOrWhiteSpace(installDir) && Directory.Exists(installDir)
            ? installDir
            : GameLocator.FindInstallDir();
        if (string.IsNullOrWhiteSpace(dir))
        {
            throw new ModOperationException("找不到游戏安装目录，无法启动游戏。");
        }

        var exePath = Path.Combine(dir, GameLocator.GameExeName);
        if (!File.Exists(exePath))
        {
            throw new ModOperationException($"找不到游戏可执行文件：{exePath}");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = dir,
            UseShellExecute = true
        });
        interaction?.Ok($"已启动游戏：{exePath}");
    }

    private bool RestoreLauncherDllIfModified(string dllPath, IInstallerInteraction interaction)
    {
        var backupFile = Path.Combine(Repository.BackupDir, "launcher.dll");
        if (!File.Exists(backupFile) || !File.Exists(dllPath))
        {
            return false;
        }

        var backupHash = ModPatcher.ComputeSha256Hex(File.ReadAllBytes(backupFile));
        var currentHash = ModPatcher.ComputeSha256Hex(File.ReadAllBytes(dllPath));
        if (string.Equals(backupHash, currentHash, StringComparison.Ordinal))
        {
            return false;
        }

        File.Copy(backupFile, dllPath, overwrite: true);
        interaction.Ok("已还原原版启动器 DLL（保持原版零改动）");
        return true;
    }

    private static int RemoveVirtualBundles(string gameBaseDir, IInstallerInteraction interaction, bool silent = false)
    {
        var virtuals = FindInstalledVirtualVersions(gameBaseDir);
        foreach (var ver in virtuals)
        {
            TryDeleteDirectory(Path.Combine(gameBaseDir, ver));
            TryDeleteFile(Path.Combine(gameBaseDir, $"{ver}.zip"));
            TryDeleteFile(Path.Combine(gameBaseDir, $"{ver}.sha256"));
            if (!silent)
            {
                interaction.Ok($"已移除虚拟 MOD 版本包：{ver}");
            }
        }

        return virtuals.Count;
    }

    private static async Task EnsureGameNotRunningAsync(
        bool forceKill,
        IInstallerInteraction interaction,
        CancellationToken cancellationToken)
    {
        var running = Process.GetProcessesByName(GameLocator.GameFolderName);
        if (running.Length == 0)
        {
            return;
        }

        try
        {
            var shouldClose = forceKill || await interaction.ConfirmAsync(
                "检测到《Yu-Gi-Oh! Rogue Duelist》正在运行，注入/还原前需要先关闭游戏进程。立即关闭吗？",
                cancellationToken);
            if (!shouldClose)
            {
                throw new ModOperationException("游戏正在运行，已取消操作。请先关闭游戏再重试。");
            }

            foreach (var proc in running)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                    await proc.WaitForExitAsync(cancellationToken);
                }
                catch
                {
                    // Ignore already exited processes.
                }
            }

            interaction.Say("已关闭正在运行的游戏进程。");
        }
        finally
        {
            foreach (var proc in running)
            {
                proc.Dispose();
            }
        }
    }

    private static void ValidateBundleFileAllowlist(IEnumerable<string> relativePaths)
    {
        foreach (var rel in relativePaths)
        {
            var normalized = rel.Replace('\\', '/');
            if (normalized.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(normalized))
            {
                throw new ModOperationException($"不安全的覆盖路径：{rel}");
            }

            var fileName = Path.GetFileName(normalized);
            var ext = Path.GetExtension(fileName);
            if (AllowedBundleSpecialNames.Contains(fileName) || AllowedBundleExtensions.Contains(ext))
            {
                continue;
            }

            throw new ModOperationException(
                $"文件 \"{rel}\" 的扩展名不在启动器允许的白名单内（允许：{string.Join(", ", AllowedBundleExtensions.Order())}）。");
        }
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

    /// <summary>
    /// Exports a Linux-ready zero-touch virtual MOD bundle (.zip containing 900.x.y.zip + 900.x.y.sha256 + install-linux-mod.sh)
    /// compatible with YGO-Rogue-Linux-x86_64 (WebKitGTK launcher reads ~/.local/share/Yu-Gi-Oh! Rogue Duelist/Game/).
    /// </summary>
    public async Task<string> ExportLinuxBundleAsync(
        string outputDir,
        IInstallerInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        var state = Repository.LoadState();
        var mods = Repository.LoadMods(state);
        var installDir = !string.IsNullOrWhiteSpace(state.InstallDir) && Directory.Exists(state.InstallDir)
            ? state.InstallDir
            : GameLocator.FindInstallDir();
        var gameBaseDir = ResolveGameBaseDir(state.GameDir);
        var picsDir = !string.IsNullOrWhiteSpace(state.PicsDir) && Directory.Exists(state.PicsDir)
            ? state.PicsDir
            : GameLocator.FindPicsDir();

        var dllPath = string.IsNullOrWhiteSpace(installDir) ? null : Path.Combine(installDir, GameLocator.GameDllName);
        var launcherMeta = LauncherInspector.TryReadMetadata(dllPath);
        var vanilla = FindBestVanillaSource(installDir, gameBaseDir, launcherMeta)
            ?? throw new ModOperationException("找不到原版 web-X.Y.Z.zip，无法导出 Linux MOD 虚拟版本包。");

        var virtualVersion = ComputeVirtualVersion(vanilla.Version);
        interaction.ReportProgress($"正在合成 Linux 虚拟版本包 ({virtualVersion})...", 20);
        var composed = ModCompositor.Compose(vanilla.ZipPath, mods, picsDir, interaction, cancellationToken, state.DeveloperMode);
        ValidateBundleFileAllowlist(composed.Files.Keys);

        Directory.CreateDirectory(outputDir);
        var innerZipPath = Path.Combine(outputDir, $"{virtualVersion}.zip");
        var tempStage = Path.Combine(Path.GetTempPath(), $"ygo-linux-export-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempStage);
            ZipFile.ExtractToDirectory(vanilla.ZipPath, tempStage, overwriteFiles: true);
            WriteFilesToDirectory(tempStage, composed.Files);

            if (File.Exists(innerZipPath)) File.Delete(innerZipPath);
            ZipFile.CreateFromDirectory(tempStage, innerZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            var shaHex = ModPatcher.ComputeSha256Hex(await File.ReadAllBytesAsync(innerZipPath, cancellationToken));
            var shaPath = Path.Combine(outputDir, $"{virtualVersion}.sha256");
            await File.WriteAllTextAsync(shaPath, shaHex, ModPatcher.Utf8NoBom, cancellationToken);

            var installSh = $$"""
                #!/usr/bin/env bash
                set -euo pipefail
                VER="{{virtualVersion}}"
                SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
                DATA_BASE="${XDG_DATA_HOME:-$HOME/.local/share}/Yu-Gi-Oh! Rogue Duelist/Game"
                mkdir -p "$DATA_BASE"
                echo "[YGO MOD] Installing virtual MOD bundle $VER into $DATA_BASE ..."
                cp -f "$SCRIPT_DIR/$VER.zip" "$DATA_BASE/$VER.zip"
                cp -f "$SCRIPT_DIR/$VER.sha256" "$DATA_BASE/$VER.sha256"
                rm -rf "$DATA_BASE/$VER"
                mkdir -p "$DATA_BASE/$VER"
                unzip -qo "$DATA_BASE/$VER.zip" -d "$DATA_BASE/$VER"
                echo "[YGO MOD] Done! Launch ygo-rogue-duelist to play with all MODs."
                echo "[YGO MOD] To restore vanilla: rm -rf \"$DATA_BASE/$VER\" \"$DATA_BASE/$VER.zip\" \"$DATA_BASE/$VER.sha256\""
                """;
            var shPath = Path.Combine(outputDir, "install-linux-mod.sh");
            await File.WriteAllTextAsync(shPath, installSh.Replace("\r\n", "\n"), ModPatcher.Utf8NoBom, cancellationToken);

            var bundleZip = Path.Combine(outputDir, $"YGO-Rogue-Linux-ModBundle-{virtualVersion}.zip");
            if (File.Exists(bundleZip)) File.Delete(bundleZip);
            using (var archive = ZipFile.Open(bundleZip, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(innerZipPath, $"{virtualVersion}.zip", CompressionLevel.NoCompression);
                archive.CreateEntryFromFile(shaPath, $"{virtualVersion}.sha256", CompressionLevel.Optimal);
                archive.CreateEntryFromFile(shPath, "install-linux-mod.sh", CompressionLevel.Optimal);
            }

            TryDeleteFile(innerZipPath);
            TryDeleteFile(shaPath);
            TryDeleteFile(shPath);
            interaction.ReportProgress("Linux MOD 虚拟包导出完成", 100);
            interaction.Ok($"已导出 Linux / Steam Deck 一键安装包：{bundleZip}（解压后执行 bash install-linux-mod.sh 即可零侵入安装）");
            return bundleZip;
        }
        finally
        {
            TryDeleteDirectory(tempStage);
        }
    }

    /// <summary>
    /// Exports MODs for Android:
    /// - When sourceApkPath is supplied: patches assets/web/* directly inside the APK in pure C#, strips old META-INF signatures,
    ///   preserving STORED compression for resources.arsc &amp; native libs, outputting an unsigned APK ready for apksigner / MT Manager.
    /// - When sourceApkPath is omitted: exports an Android Mod Web Overlay ZIP (assets/web/* + patch-android-apk.py + instructions).
    /// </summary>
    public async Task<string> ExportAndroidBundleAsync(
        string outputDir,
        string? sourceApkPath,
        IInstallerInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        var state = Repository.LoadState();
        var mods = Repository.LoadMods(state);
        var installDir = !string.IsNullOrWhiteSpace(state.InstallDir) && Directory.Exists(state.InstallDir)
            ? state.InstallDir
            : GameLocator.FindInstallDir();
        var gameBaseDir = ResolveGameBaseDir(state.GameDir);
        var picsDir = !string.IsNullOrWhiteSpace(state.PicsDir) && Directory.Exists(state.PicsDir)
            ? state.PicsDir
            : GameLocator.FindPicsDir();

        var dllPath = string.IsNullOrWhiteSpace(installDir) ? null : Path.Combine(installDir, GameLocator.GameDllName);
        var launcherMeta = LauncherInspector.TryReadMetadata(dllPath);
        var vanilla = FindBestVanillaSource(installDir, gameBaseDir, launcherMeta)
            ?? throw new ModOperationException("找不到原版 web-X.Y.Z.zip，无法导出 Android MOD 包。");

        interaction.ReportProgress("正在合成 Android MOD 网页资源层...", 25);
        var composed = ModCompositor.Compose(vanilla.ZipPath, mods, picsDir, interaction, cancellationToken, state.DeveloperMode);
        ValidateBundleFileAllowlist(composed.Files.Keys);
        Directory.CreateDirectory(outputDir);

        if (!string.IsNullOrWhiteSpace(sourceApkPath) && File.Exists(sourceApkPath))
        {
            var outApk = Path.Combine(outputDir, $"YGO-Rogue-Android-Modded-v{vanilla.Version}-unsigned.apk");
            if (File.Exists(outApk)) File.Delete(outApk);

            interaction.ReportProgress("正在修补 Android APK (assets/web/*)...", 60);
            var replacedSet = new HashSet<string>(StringComparer.Ordinal);
            using (var srcZip = ZipFile.OpenRead(sourceApkPath))
            using (var dstZip = ZipFile.Open(outApk, ZipArchiveMode.Create))
            {
                foreach (var entry in srcZip.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fullName = entry.FullName;

                    // Strip old v1 signature files so re-signing succeeds cleanly
                    if (fullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) &&
                        (fullName.EndsWith(".SF", StringComparison.OrdinalIgnoreCase) ||
                         fullName.EndsWith(".RSA", StringComparison.OrdinalIgnoreCase) ||
                         fullName.EndsWith(".DSA", StringComparison.OrdinalIgnoreCase) ||
                         fullName.EndsWith(".EC", StringComparison.OrdinalIgnoreCase) ||
                         fullName.Equals("META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    if (fullName.StartsWith("assets/web/", StringComparison.Ordinal) &&
                        composed.Files.TryGetValue(fullName["assets/web/".Length..], out var modBytes))
                    {
                        var newEntry = dstZip.CreateEntry(fullName, CompressionLevel.Optimal);
                        await using var dstStream = newEntry.Open();
                        await dstStream.WriteAsync(modBytes, cancellationToken);
                        replacedSet.Add(fullName["assets/web/".Length..]);
                        continue;
                    }

                    // Preserve STORED for resources.arsc & .so libraries (Android R+ requirement!)
                    var level = fullName.Equals("resources.arsc", StringComparison.OrdinalIgnoreCase) ||
                                fullName.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
                        ? CompressionLevel.NoCompression
                        : CompressionLevel.Optimal;
                    var copyEntry = dstZip.CreateEntry(fullName, level);
                    await using var inStream = entry.Open();
                    await using var outStream = copyEntry.Open();
                    await inStream.CopyToAsync(outStream, cancellationToken);
                }

                // Append newly added MOD files into assets/web/
                foreach (var (rel, bytes) in composed.Files)
                {
                    if (replacedSet.Contains(rel)) continue;
                    var addEntry = dstZip.CreateEntry($"assets/web/{rel}", CompressionLevel.Optimal);
                    await using var outStream = addEntry.Open();
                    await outStream.WriteAsync(bytes, cancellationToken);
                }
            }

            interaction.ReportProgress("Android Modded APK 已生成（待签名）", 100);
            interaction.Ok($"已生成 Android MOD APK（未签名）：{outApk}。请用 uber-apk-signer / apksigner 或手机端 MT管理器 一键签名后安装！");
            return outApk;
        }

        // Export portable Android Web Overlay ZIP + 1-click patcher script
        var overlayZip = Path.Combine(outputDir, $"YGO-Rogue-Android-ModOverlay-v{vanilla.Version}.zip");
        if (File.Exists(overlayZip)) File.Delete(overlayZip);

        var patchScript = """
            #!/usr/bin/env python3
            \"\"\"Patch YGO-Rogue-Android.apk with assets/web/* from this overlay ZIP.
            Usage: python3 patch-android-apk.py YGO-Rogue-Android.apk [output-unsigned.apk]
            Then sign with: java -jar uber-apk-signer.jar -a output-unsigned.apk (or Android MT Manager).
            \"\"\"
            import sys, zipfile
            from pathlib import Path

            if len(sys.argv) < 2:
                print("Usage: python3 patch-android-apk.py <YGO-Rogue-Android.apk> [output-unsigned.apk]")
                sys.exit(1)

            apk_in = Path(sys.argv[1])
            apk_out = Path(sys.argv[2]) if len(sys.argv) >= 3 else apk_in.with_name(apk_in.stem + "-modded-unsigned.apk")
            overlay_dir = Path(__file__).resolve().parent / "assets" / "web"
            mod_files = {f"assets/web/{p.relative_to(overlay_dir).as_posix()}": p.read_bytes() for p in overlay_dir.rglob("*") if p.is_file()}
            written = set()

            with zipfile.ZipFile(apk_in, "r") as zin, zipfile.ZipFile(apk_out, "w") as zout:
                for info in zin.infolist():
                    name = info.filename
                    if name.startswith("META-INF/") and name.upper().endswith((".SF", ".RSA", ".DSA", ".EC", "MANIFEST.MF")):
                        continue
                    if name in mod_files:
                        zout.writestr(name, mod_files[name], compress_type=zipfile.ZIP_DEFLATED)
                        written.add(name)
                    else:
                        ct = zipfile.ZIP_STORED if (name == "resources.arsc" or name.endswith(".so")) else zipfile.ZIP_DEFLATED
                        zout.writestr(name, zin.read(name), compress_type=ct)
                for name, data in mod_files.items():
                    if name not in written:
                        zout.writestr(name, data, compress_type=zipfile.ZIP_DEFLATED)

            print(f"Patched {len(mod_files)} MOD files -> {apk_out}")
            print("Next: sign with uber-apk-signer / apksigner / MT Manager and install on Android.")
            """;

        using (var archive = ZipFile.Open(overlayZip, ZipArchiveMode.Create))
        {
            foreach (var (rel, bytes) in composed.Files)
            {
                var entry = archive.CreateEntry($"assets/web/{rel}", CompressionLevel.Optimal);
                await using var s = entry.Open();
                await s.WriteAsync(bytes, cancellationToken);
            }

            var pyEntry = archive.CreateEntry("patch-android-apk.py", CompressionLevel.Optimal);
            await using (var s = pyEntry.Open())
            {
                await s.WriteAsync(ModPatcher.GetUtf8Bytes(patchScript.Replace("\r\n", "\n")), cancellationToken);
            }
        }

        interaction.ReportProgress("Android MOD 覆盖包导出完成", 100);
        interaction.Ok($"已导出 Android MOD 覆盖包：{overlayZip}（含 assets/web/* 与 patch-android-apk.py；也可在下方直接选择原版 APK 一键注入生成）");
        return overlayZip;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
