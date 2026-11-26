using System.Text;
using CommandLine;
using YGORogueDuelistCardModInstaller;
using YGORogueDuelistCardModInstaller.Core;
using YGORogueDuelistCardModInstaller.Core.ModManager;

try
{
    Console.OutputEncoding = Encoding.UTF8;
    Console.InputEncoding = Encoding.UTF8;
}
catch
{
    // Ignore when console handles are not attached.
}

var normalizedArgs = ArgNormalizer.Normalize(args);
var interaction = new ConsoleInteraction();

Type[] verbTypes =
[
    typeof(ApplyVerbOptions),
    typeof(ListVerbOptions),
    typeof(ImportVerbOptions),
    typeof(EnableVerbOptions),
    typeof(DisableVerbOptions),
    typeof(OrderVerbOptions),
    typeof(CreateVerbOptions),
    typeof(RemoveVerbOptions),
    typeof(PackModVerbOptions),
    typeof(PackWorkspaceVerbOptions),
    typeof(ImportWorkspaceVerbOptions),
    typeof(DevVerbOptions),
    typeof(WorkspaceVerbOptions),
    typeof(HotReloadVerbOptions),
    typeof(RestoreVerbOptions),
    typeof(UpdateGameVerbOptions),
    typeof(LaunchVerbOptions),
    typeof(InstallVerbOptions),
    typeof(UninstallVerbOptions)
];

var parsed = Parser.Default.ParseArguments(normalizedArgs, verbTypes);

var exitCode = parsed switch
{
    Parsed<object> ok => ok.Value switch
    {
        ApplyVerbOptions o => await RunApplyAsync(o, interaction),
        ListVerbOptions o => RunList(o, interaction),
        ImportVerbOptions o => RunImport(o, interaction),
        EnableVerbOptions o => RunToggle(o.RootDir, o.ModId, enable: true, interaction),
        DisableVerbOptions o => RunToggle(o.RootDir, o.ModId, enable: false, interaction),
        OrderVerbOptions o => RunOrder(o, interaction),
        CreateVerbOptions o => RunCreate(o, interaction),
        RemoveVerbOptions o => RunRemove(o, interaction),
        PackModVerbOptions o => RunPackMod(o, interaction),
        PackWorkspaceVerbOptions o => RunPackWorkspace(o, interaction),
        ImportWorkspaceVerbOptions o => RunImportWorkspace(o, interaction),
        DevVerbOptions o => RunDevMode(o, interaction),
        WorkspaceVerbOptions o => RunWorkspace(o, interaction),
        HotReloadVerbOptions o => await RunHotReloadAsync(o, interaction),
        RestoreVerbOptions o => await RunRestoreAsync(o, interaction),
        UpdateGameVerbOptions o => await RunUpdateGameAsync(o, interaction),
        LaunchVerbOptions o => RunLaunch(o, interaction),
        InstallVerbOptions o => await RunLegacyInstallAsync(o, interaction),
        UninstallVerbOptions o => await RunLegacyUninstallAsync(o, interaction),
        _ => 1
    },
    NotParsed<object> err => err.Errors.IsHelp() || err.Errors.IsVersion() ? 0 : 1,
    _ => 1
};

return exitCode;

static async Task<int> RunApplyAsync(ApplyVerbOptions opts, ConsoleInteraction interaction)
{
    var isBuildOnly = !string.IsNullOrWhiteSpace(opts.BuildOnly);
    WriteBanner("Yu-Gi-Oh! Rogue Duelist MOD 管理器 · 应用注入", "----------------------------------------------");

    try
    {
        var engine = new ModInjectionEngine(opts.RootDir);
        var state = engine.Repository.LoadState();
        var stateDirty = false;
        if (!string.IsNullOrWhiteSpace(opts.InstallDir)) { state.InstallDir = opts.InstallDir; stateDirty = true; }
        if (!string.IsNullOrWhiteSpace(opts.GameDir)) { state.GameDir = opts.GameDir; stateDirty = true; }
        if (!string.IsNullOrWhiteSpace(opts.PicsDir)) { state.PicsDir = opts.PicsDir; stateDirty = true; }
        if (stateDirty) engine.Repository.SaveState(state);

        InjectionMode? modeOverride = opts.Mode?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "virtual" or "virtualbundle" => InjectionMode.VirtualBundle,
            "manifest" or "launchermanifest" or "dll" => InjectionMode.LauncherManifest,
            _ => throw new ModOperationException($"未知注入模式：{opts.Mode}（可选：virtual 或 manifest）")
        };

        await engine.ApplyAsync(new ApplyModsOptions
        {
            ModeOverride = modeOverride,
            BuildOnlyDir = opts.BuildOnly,
            LaunchGameAfter = opts.Launch,
            AssumeYes = opts.AssumeYes,
            ForceKillRunningGame = opts.AssumeYes,
            DeveloperModeOverride = opts.DevMode ? true : null,
            IgnoreVersionRequirements = opts.IgnoreVersion
        }, interaction);

        if (!isBuildOnly)
        {
            WriteSuccessFooter("全部已启用 MOD 注入完成！");
            PauseIfInteractive(opts.NoPause);
        }

        return 0;
    }
    catch (Exception ex)
    {
        interaction.Error(ex.Message);
        if (!isBuildOnly) PauseIfInteractive(opts.NoPause);
        return 1;
    }
}

static int RunList(ListVerbOptions opts, ConsoleInteraction interaction)
{
    WriteBanner("Yu-Gi-Oh! Rogue Duelist MOD 管理器 · 状态概览", "----------------------------------------------");

    try
    {
        var engine = new ModInjectionEngine(opts.RootDir);
        var status = engine.GetStatus();

        interaction.Say($"工作目录：{status.WorkspaceRoot}");
        interaction.Say($"MOD 目录：{status.ModsRoot}");
        interaction.Say($"游戏目录：{status.InstallDir ?? "（未检测到）"}");
        interaction.Say($"原版版本：{status.ActiveBaseGameVersion ?? "（未知）"}");
        interaction.Say($"注入模式：{(status.Mode == InjectionMode.VirtualBundle ? "零侵入虚拟版本包 (virtual，不改动 DLL/ZIP)" : "启动器清单 (manifest)")}");
        interaction.Say($"开发模式：{(status.DeveloperMode ? "已开启 (F10 Dev Inspector / window.YgoModDev / 自由决斗)" : "关闭")}");
        interaction.Say($"同步状态：{status.SyncSummary}");
        Console.WriteLine();

        if (status.Mods.Count == 0)
        {
            interaction.Warn("当前 mods\\ 目录下还没有任何 MOD。可用 `import <zip/dir>` 导入或 `create <id>` 新建模板。");
            return 0;
        }

        Console.WriteLine("  顺序  状态    ID                        名称 & 内容");
        Console.WriteLine("  ----  ------  ------------------------  --------------------------------------");
        for (var i = 0; i < status.Mods.Count; i++)
        {
            var m = status.Mods[i];
            var flag = m.Enabled ? "[启用]" : "[禁用]";
            var summary = FormatModSummary(m);
            var minVerTag = string.IsNullOrWhiteSpace(m.Manifest.MinGameVersion) ? string.Empty : $" [≥v{m.Manifest.MinGameVersion}]";
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = m.Enabled ? ConsoleColor.Green : ConsoleColor.DarkGray;
            Console.Write($"  #{i + 1,-3}  {flag}  ");
            Console.ForegroundColor = prev;
            Console.WriteLine($"{m.Id,-24}  {m.Manifest.Name} v{m.Manifest.Version}{minVerTag} ({summary})");
        }

        if (status.VersionIssues.Count > 0)
        {
            Console.WriteLine();
            interaction.Warn($"检测到 {status.VersionIssues.Count} 处版本兼容性告警：");
            foreach (var issue in status.VersionIssues)
            {
                interaction.Say($"  [{issue.ModId}] {issue.Message}");
            }
        }

        if (status.Conflicts.Count > 0)
        {
            Console.WriteLine();
            interaction.Warn($"检测到 {status.Conflicts.Count} 处 MOD 资源冲突（按顺序后加载者生效）：");
            foreach (var c in status.Conflicts.Take(20))
            {
                interaction.Say($"  [{c.Category}] {c.ResourceKey}: {string.Join(" -> ", c.ModIds)}（生效：{c.WinnerModId}）");
            }

            if (status.Conflicts.Count > 20)
            {
                interaction.Say($"  ... 其余 {status.Conflicts.Count - 20} 处略");
            }
        }

        Console.WriteLine();
        return 0;
    }
    catch (Exception ex)
    {
        interaction.Error(ex.Message);
        return 1;
    }
}

static int RunImport(ImportVerbOptions opts, ConsoleInteraction interaction)
{
    try
    {
        var engine = new ModInjectionEngine(opts.RootDir);
        if (ModPackager.IsWorkspacePackZip(opts.SourcePath))
        {
            var (manifest, count) = ModPackager.ImportWorkspacePack(engine.Repository, opts.SourcePath, overwrite: opts.Force || true);
            interaction.Ok($"已导入工作区整合包：{manifest.Name} v{manifest.Version}（共 {count} 个 MOD，已恢复加载顺序）");
            if (!string.IsNullOrWhiteSpace(manifest.MinGameVersion))
            {
                interaction.Say($"整合包最低游戏版本要求：v{manifest.MinGameVersion}");
            }

            interaction.Say("执行 `apply` 即可全部注入游戏。");
            return 0;
        }

        var imported = engine.Repository.ImportMod(opts.SourcePath, opts.Force);
        interaction.Ok($"已导入 MOD：{imported.Manifest.Name} ({imported.Id}) -> {imported.DirectoryPath}");
        interaction.Say($"内容：{FormatModSummary(imported)}；执行 `apply` 即可注入游戏。");
        return 0;
    }
    catch (Exception ex)
    {
        interaction.Error(ex.Message);
        return 1;
    }
}

static int RunPackMod(PackModVerbOptions opts, ConsoleInteraction interaction)
{
    try
    {
        var engine = new ModInjectionEngine(opts.RootDir);
        var zipPath = ModPackager.PackSingleMod(engine.Repository, opts.ModId, opts.OutputPath);
        interaction.Ok($"已打包 MOD \"{opts.ModId}\" -> {zipPath}");
        return 0;
    }
    catch (Exception ex)
    {
        interaction.Error(ex.Message);
        return 1;
    }
}

static int RunPackWorkspace(PackWorkspaceVerbOptions opts, ConsoleInteraction interaction)
{
    try
    {
        var engine = new ModInjectionEngine(opts.RootDir);
        var (zipPath, modCount, minVer) = ModPackager.PackWorkspace(
            engine.Repository,
            outputZipPath: opts.OutputPath,
            packName: opts.Name,
            packVersion: opts.Version,
            description: opts.Description,
            explicitMinGameVersion: opts.MinGameVersion,
            enabledOnly: opts.EnabledOnly);
        interaction.Ok($"已打包工作区整合包（共 {modCount} 个 MOD）-> {zipPath}");
        if (!string.IsNullOrWhiteSpace(minVer))
        {
            interaction.Say($"生效最低游戏版本要求：v{minVer}");
        }

        return 0;
    }
    catch (Exception ex)
    {
        interaction.Error(ex.Message);
        return 1;
    }
}

static int RunImportWorkspace(ImportWorkspaceVerbOptions opts, ConsoleInteraction interaction)
{
    try
    {
        var engine = new ModInjectionEngine(opts.RootDir);
        var (manifest, count) = ModPackager.ImportWorkspacePack(engine.Repository, opts.SourcePath, overwrite: opts.Force);
        interaction.Ok($"已导入工作区整合包：{manifest.Name} v{manifest.Version}（共 {count} 个 MOD，已同步加载顺序与开关状态）");
        if (!string.IsNullOrWhiteSpace(manifest.MinGameVersion))
        {
            interaction.Say($"整合包最低游戏版本要求：v{manifest.MinGameVersion}");
        }

        return 0;
    }
    catch (Exception ex)
    {
        interaction.Error(ex.Message);
        return 1;
    }
}

static int RunDevMode(DevVerbOptions opts, ConsoleInteraction interaction)
{
    try
    {
        var engine = new ModInjectionEngine(opts.RootDir);
        var state = engine.Repository.LoadState();
        var cmd = opts.State.Trim().ToLowerInvariant();
        switch (cmd)
        {
            case "on" or "true" or "1" or "enable":
                state.DeveloperMode = true;
                engine.Repository.SaveState(state);
                interaction.Ok("已开启开发者模式（注入后自动启用 F10 调试面板、window.YgoModDev、解锁沙盒自由决斗）。执行 `apply` 使其生效。");
                return 0;
            case "off" or "false" or "0" or "disable":
                state.DeveloperMode = false;
                engine.Repository.SaveState(state);
                interaction.Ok("已关闭开发者模式。执行 `apply` 更新游戏状态。");
                return 0;
            case "status":
                interaction.Say($"当前开发者模式：{(state.DeveloperMode ? "已开启 (ON)" : "关闭 (OFF)")}");
                return 0;
            default:
                throw new ModOperationException($"未知参数：{opts.State}（可选：on / off / status）");
        }
    }
    catch (Exception ex)
    {
        interaction.Error(ex.Message);
        return 1;
    }
}

static int RunWorkspace(WorkspaceVerbOptions opts, ConsoleInteraction interaction)
{
    try
    {
        var registry = new WorkspaceRegistry();
        var action = opts.Action.Trim().ToLowerInvariant();
        switch (action)
        {
            case "list":
                var defaultPath = ModRepository.ResolveWorkspaceRoot();
                var list = registry.ListWorkspaces(defaultPath);
                var active = registry.GetActiveWorkspace(defaultPath);
                WriteBanner("Yu-Gi-Oh! Rogue Duelist · 多 MOD 工作区列表", "------------------------------------------");
                foreach (var ws in list)
                {
                    var mark = string.Equals(ws.Id, active?.Id, StringComparison.OrdinalIgnoreCase) ? "[当前]" : "      ";
                    interaction.Say($"{mark} {ws.Id,-20} {ws.Name} -> {ws.Path}");
                }
                return 0;

            case "new":
                if (string.IsNullOrWhiteSpace(opts.Target)) throw new ModOperationException("请指定新工作区目录路径。");
                var created = registry.CreateWorkspace(opts.Name ?? Path.GetFileName(opts.Target), opts.Target, opts.Description);
                interaction.Ok($"已新建并切换到工作区：{created.Name} ({created.Id}) -> {created.Path}");
                return 0;

            case "add":
                if (string.IsNullOrWhiteSpace(opts.Target)) throw new ModOperationException("请指定要添加的工作区目录路径。");
                var added = registry.EnsureRegistered(opts.Target, opts.Name, opts.Description, setActive: true);
                interaction.Ok($"已登记并切换到工作区：{added.Name} ({added.Id}) -> {added.Path}");
                return 0;

            case "use":
                if (string.IsNullOrWhiteSpace(opts.Target)) throw new ModOperationException("请指定要切换的工作区 ID、名称或路径。");
                var switched = registry.SetActiveWorkspace(opts.Target);
                interaction.Ok($"已切换到工作区：{switched.Name} ({switched.Id}) -> {switched.Path}");
                return 0;

            case "remove":
                if (string.IsNullOrWhiteSpace(opts.Target)) throw new ModOperationException("请指定要从列表移除的工作区 ID 或名称。");
                if (registry.RemoveWorkspace(opts.Target))
                    interaction.Ok($"已从工作区列表移除：{opts.Target}（磁盘目录未删除）");
                else
                    interaction.Warn($"未找到工作区：{opts.Target}");
                return 0;

            default:
                throw new ModOperationException($"未知工作区操作：{opts.Action}（可选：list | new | add | use | remove）");
        }
    }
    catch (Exception ex)
    {
        interaction.Error(ex.Message);
        return 1;
    }
}

static async Task<int> RunHotReloadAsync(HotReloadVerbOptions opts, ConsoleInteraction interaction)
{
    WriteBanner("Yu-Gi-Oh! Rogue Duelist · MOD 实时热加载开发模式", "------------------------------------------------");
    try
    {
        var resolvedRoot = ResolveRootOrActiveWorkspace(opts.RootDir);
        var engine = new ModInjectionEngine(resolvedRoot);
        using var server = new ModHotReloadServer(engine, (lvl, msg) => interaction.Log(lvl, msg));
        await server.StartAsync(opts.Port);

        if (opts.InjectGame)
        {
            await engine.ApplyAsync(new ApplyModsOptions
            {
                ModeOverride = InjectionMode.VirtualBundle,
                DeveloperModeOverride = true,
                HotReloadPort = server.Port,
                AssumeYes = true,
                ForceKillRunningGame = false
            }, interaction);
        }

        if (opts.OpenBrowser)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = server.BaseUrl,
                UseShellExecute = true
            });
        }

        interaction.Say($"实时热预览地址：{server.BaseUrl}");
        interaction.Say("正在监听 mods\\ 变动，修改任意卡/Lua/JS/CSS/角色/遗物/卡包/DSL 文件将自动热重载。按 Ctrl+C 停止...");

        using var stopEvent = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopEvent.Set(); };
        await Task.Run(() => stopEvent.Wait());
        server.Stop();
        return 0;
    }
    catch (Exception ex)
    {
        interaction.Error(ex.Message);
        return 1;
    }
}

static string? ResolveRootOrActiveWorkspace(string? explicitRoot)
{
    if (!string.IsNullOrWhiteSpace(explicitRoot)) return explicitRoot;
    return new WorkspaceRegistry().GetActiveWorkspace()?.Path;
}

static int RunToggle(string? rootDir, string modId, bool enable, ConsoleInteraction interaction)
{
    try
    {
        var engine = new ModInjectionEngine(rootDir);
        var state = engine.Repository.LoadState();
        var mods = engine.Repository.LoadMods(state);

        if (string.Equals(modId, "all", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var m in mods) m.Enabled = enable;
            engine.Repository.SaveModOrderAndFlags(mods, state);
            interaction.Ok($"已{(enable ? "启用" : "禁用")}全部 {mods.Count} 个 MOD。执行 `apply` 使其生效。");
            return 0;
        }

        var target = mods.FirstOrDefault(m => string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ModOperationException($"找不到 MOD：{modId}");

        target.Enabled = enable;
        engine.Repository.SaveModOrderAndFlags(mods, state);
        interaction.Ok($"已{(enable ? "启用" : "禁用")} MOD：{target.Manifest.Name} ({target.Id})。执行 `apply` 使其生效。");
        return 0;
    }
    catch (Exception ex)
    {
        interaction.Error(ex.Message);
        return 1;
    }
}

static int RunOrder(OrderVerbOptions opts, ConsoleInteraction interaction)
{
    try
    {
        var engine = new ModInjectionEngine(opts.RootDir);
        var state = engine.Repository.LoadState();
        var mods = engine.Repository.LoadMods(state);

        var idx = mods.FindIndex(m => string.Equals(m.Id, opts.ModId, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            throw new ModOperationException($"找不到 MOD：{opts.ModId}");
        }

        var targetIdx = Math.Clamp(opts.Position - 1, 0, mods.Count - 1);
        var item = mods[idx];
        mods.RemoveAt(idx);
        mods.Insert(targetIdx, item);
        engine.Repository.SaveModOrderAndFlags(mods, state);
        interaction.Ok($"已将 {item.Manifest.Name} ({item.Id}) 移动到第 #{targetIdx + 1} 位。执行 `apply` 使其生效。");
        return 0;
    }
    catch (Exception ex)
    {
        interaction.Error(ex.Message);
        return 1;
    }
}

static int RunCreate(CreateVerbOptions opts, ConsoleInteraction interaction)
{
    try
    {
        var engine = new ModInjectionEngine(opts.RootDir);
        var status = engine.GetStatus();
        var created = engine.Repository.CreateTemplateMod(
            opts.ModId,
            opts.Name,
            opts.Author,
            opts.Description,
            status.ActiveBaseGameVersion,
            opts.MinGameVersion);
        interaction.Ok($"已创建 MOD 模板：{created.DirectoryPath}");
        interaction.Say("支持 mod.lua / mod.js DSL 及 cards/characters/relics/packs/preconstructed/decks/trainer.json/lua/js，执行 `apply` 即可注入。");
        return 0;
    }
    catch (Exception ex)
    {
        interaction.Error(ex.Message);
        return 1;
    }
}

static int RunRemove(RemoveVerbOptions opts, ConsoleInteraction interaction)
{
    try
    {
        var engine = new ModInjectionEngine(opts.RootDir);
        engine.Repository.DeleteMod(opts.ModId);
        interaction.Ok($"已删除 MOD：{opts.ModId}。执行 `apply` 更新游戏注入状态。");
        return 0;
    }
    catch (Exception ex)
    {
        interaction.Error(ex.Message);
        return 1;
    }
}

static async Task<int> RunRestoreAsync(RestoreVerbOptions opts, ConsoleInteraction interaction)
{
    WriteBanner("Yu-Gi-Oh! Rogue Duelist MOD 管理器 · 还原纯净原版", "--------------------------------------------------");
    try
    {
        var engine = new ModInjectionEngine(opts.RootDir);
        await engine.RestoreVanillaAsync(interaction, forceKillRunningGame: opts.AssumeYes);
        WriteSuccessFooter("已恢复纯净原版。");
        PauseIfInteractive(opts.NoPause);
        return 0;
    }
    catch (Exception ex)
    {
        interaction.Error(ex.Message);
        PauseIfInteractive(opts.NoPause);
        return 1;
    }
}

static async Task<int> RunUpdateGameAsync(UpdateGameVerbOptions opts, ConsoleInteraction interaction)
{
    WriteBanner("Yu-Gi-Oh! Rogue Duelist MOD 管理器 · 检查官方更新", "--------------------------------------------------");
    try
    {
        var engine = new ModInjectionEngine(opts.RootDir);
        await engine.UpdateOfficialGameAsync(interaction);
        PauseIfInteractive(opts.NoPause);
        return 0;
    }
    catch (Exception ex)
    {
        interaction.Error(ex.Message);
        PauseIfInteractive(opts.NoPause);
        return 1;
    }
}

static int RunLaunch(LaunchVerbOptions opts, ConsoleInteraction interaction)
{
    try
    {
        var engine = new ModInjectionEngine(opts.RootDir);
        var state = engine.Repository.LoadState();
        ModInjectionEngine.LaunchGame(opts.InstallDir ?? state.InstallDir, interaction);
        return 0;
    }
    catch (Exception ex)
    {
        interaction.Error(ex.Message);
        return 1;
    }
}

static async Task<int> RunLegacyInstallAsync(InstallVerbOptions opts, ConsoleInteraction interaction)
{
    var isBuildOnly = !string.IsNullOrWhiteSpace(opts.BuildOnly);
    WriteBanner("Yu-Gi-Oh! Rogue Duelist 卡片 MOD 安装器（兼容模式）", "--------------------------------------------------");

    try
    {
        var service = new ModInstallerService();
        await service.InstallAsync(new InstallOptions
        {
            RootDir = opts.RootDir,
            InstallDir = opts.InstallDir,
            GameDir = opts.GameDir,
            PicsDir = opts.PicsDir,
            BuildOnlyDir = opts.BuildOnly,
            Repo = opts.Repo,
            NoDll = opts.NoDll,
            AssumeYes = opts.AssumeYes
        }, interaction);

        if (!isBuildOnly)
        {
            WriteSuccessFooter("安装完成，直接启动游戏即可。");
            PauseIfInteractive(opts.NoPause);
        }

        return 0;
    }
    catch (Exception ex)
    {
        interaction.Error(ex.Message);
        if (!isBuildOnly) PauseIfInteractive(opts.NoPause);
        return 1;
    }
}

static async Task<int> RunLegacyUninstallAsync(UninstallVerbOptions opts, ConsoleInteraction interaction)
{
    WriteBanner("Yu-Gi-Oh! Rogue Duelist 卡片 MOD 卸载", "--------------------------------------");

    try
    {
        var engine = new ModInjectionEngine(opts.RootDir);
        var state = engine.Repository.LoadState();
        if (!string.IsNullOrWhiteSpace(opts.InstallDir))
        {
            state.InstallDir = opts.InstallDir;
            engine.Repository.SaveState(state);
        }

        await engine.RestoreVanillaAsync(interaction);
        WriteSuccessFooter("卸载完成。下次启动游戏时会自动还原成原版数据。");
        PauseIfInteractive(opts.NoPause);
        return 0;
    }
    catch (Exception ex)
    {
        interaction.Error(ex.Message);
        PauseIfInteractive(opts.NoPause);
        return 1;
    }
}

static string FormatModSummary(ManagedMod m)
{
    var s = m.Stats;
    var parts = new List<string>();
    if (s.HasLuaDsl) parts.Add("mod.lua");
    if (s.HasJsDsl) parts.Add("mod.js");
    if (s.CardCount > 0) parts.Add($"{s.CardCount}卡");
    if (s.CharacterCount > 0) parts.Add($"{s.CharacterCount}角色");
    if (s.RelicCount > 0) parts.Add($"{s.RelicCount}遗物");
    if (s.PackCount > 0) parts.Add($"{s.PackCount}卡包");
    if (s.PreconstructedDeckCount > 0) parts.Add($"{s.PreconstructedDeckCount}预组");
    if (s.ScriptCount > 0) parts.Add($"{s.ScriptCount}Lua卡");
    if (s.LuaLibCount > 0) parts.Add($"{s.LuaLibCount}Lua库");
    if (s.JsPluginCount > 0) parts.Add($"{s.JsPluginCount}JS");
    if (s.CssStyleCount > 0) parts.Add($"{s.CssStyleCount}CSS");
    if (s.DeckCount > 0) parts.Add($"{s.DeckCount}起始卡组");
    if (s.ArtCount > 0) parts.Add($"{s.ArtCount}图");
    if (s.CampaignFileCount > 0) parts.Add($"{s.CampaignFileCount}战役");
    if (s.HasTrainer) parts.Add("修改器");
    if (s.OverrideFileCount > 0) parts.Add($"{s.OverrideFileCount}覆盖");
    return parts.Count > 0 ? string.Join(" · ", parts) : "空模板";
}

static void WriteBanner(string title, string underline)
{
    Console.WriteLine();
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"  {title}");
    Console.ForegroundColor = prev;
    Console.WriteLine($"  {underline}");
}

static void WriteSuccessFooter(string message)
{
    Console.WriteLine();
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"  {message}");
    Console.ForegroundColor = prev;
    Console.WriteLine();
}

static void PauseIfInteractive(bool noPause)
{
    if (!noPause && !Console.IsInputRedirected)
    {
        Console.Write("按回车键退出");
        Console.ReadLine();
    }
}
