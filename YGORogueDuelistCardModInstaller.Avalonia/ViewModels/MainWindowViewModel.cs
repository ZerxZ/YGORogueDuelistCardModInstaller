using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using YGORogueDuelistCardModInstaller.Avalonia.Services;
using YGORogueDuelistCardModInstaller.Core;
using YGORogueDuelistCardModInstaller.Core.ModManager;

namespace YGORogueDuelistCardModInstaller.Avalonia.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private static readonly IBrush GoodBrush = new ImmutableSolidColorBrush(Color.Parse("#4ADE80"));
    private static readonly IBrush WarnBrush = new ImmutableSolidColorBrush(Color.Parse("#FACC15"));
    private static readonly IBrush AlertBrush = new ImmutableSolidColorBrush(Color.Parse("#FB923C"));
    private static readonly IBrush MutedBrush = new ImmutableSolidColorBrush(Color.Parse("#94A3B8"));

    private readonly IDialogService _dialogService;
    private readonly WorkspaceRegistry _workspaceRegistry;
    private ModInjectionEngine _engine;
    private ModHotReloadServer? _hotReloadServer;
    private CancellationTokenSource? _cts;
    private bool _suppressStateSave;
    private ModManagerStatusSnapshot? _lastStatus;

    private string _workspaceRoot = string.Empty;
    private string _installDir = string.Empty;
    private string _gameDir = string.Empty;
    private string _picsDir = string.Empty;
    private string _buildOnlyDir = Path.Combine(Path.GetTempPath(), "ygo-mod-export");
    private string _repo = InstallOptions.DefaultRepo;

    private IReadOnlyList<InjectionModeOption> _injectionModeOptions = [];
    private InjectionModeOption _selectedInjectionMode = null!;
    private WorkspaceProfile? _selectedWorkspace;
    private ModItemViewModel? _selectedMod;
    private int _selectedMainTabIndex;
    private bool _developerMode;

    private bool _isHotReloadRunning;
    private string _hotReloadBadgeText = string.Empty;

    private string _gameVersionBadge = "...";
    private IBrush _gameVersionBrush = MutedBrush;
    private string _syncStatusText = "...";
    private IBrush _syncStatusBrush = MutedBrush;
    private string _modCountText = "0";

    private bool _isBusy;
    private bool _isProgressIndeterminate;
    private double _progressValue;
    private string _statusText = "就绪";

    public MainWindowViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;
        _workspaceRegistry = new WorkspaceRegistry();

        var defaultPath = ModRepository.ResolveWorkspaceRoot();
        var activeProfile = _workspaceRegistry.GetActiveWorkspace(defaultPath);
        _engine = new ModInjectionEngine(activeProfile?.Path ?? defaultPath);

        Loc = new LocalizationService();
        Studio = new ModStudioViewModel(
            _dialogService,
            Loc,
            () => _engine,
            AppendLogUiSafe,
            OnStudioFileSavedAsync);

        var initialState = _engine.Repository.LoadState();
        Loc.SetLanguage(initialState.Language);
        RebuildInjectionModeOptions(initialState.InjectionMode);

        ToggleLanguageCommand = new RelayCommand(ToggleLanguage);
        ApplyCommand = new AsyncRelayCommand(() => RunApplyAsync(launchAfter: false), () => !IsBusy);
        ApplyAndLaunchCommand = new AsyncRelayCommand(() => RunApplyAsync(launchAfter: true), () => !IsBusy);
        RestoreVanillaCommand = new AsyncRelayCommand(RunRestoreAsync, () => !IsBusy);
        LaunchGameCommand = new RelayCommand(RunLaunchGame, () => !IsBusy);
        UpdateGameCommand = new AsyncRelayCommand(RunUpdateGameAsync, () => !IsBusy);
        ExportBuildOnlyCommand = new AsyncRelayCommand(RunExportBuildOnlyAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(CancelOperation, () => IsBusy);

        // Multi-workspace commands
        NewWorkspaceCommand = new AsyncRelayCommand(RunNewWorkspaceAsync, () => !IsBusy);
        RemoveWorkspaceCommand = new AsyncRelayCommand(RunRemoveWorkspaceAsync, () => !IsBusy && Workspaces.Count > 1 && SelectedWorkspace is not null);
        OpenWorkspaceFolderCommand = new RelayCommand(() => _dialogService.OpenFolderInExplorer(_engine.Repository.WorkspaceRoot));

        // Hot-reload commands
        ToggleHotReloadCommand = new AsyncRelayCommand(RunToggleHotReloadAsync, () => !IsBusy);
        OpenLivePreviewCommand = new RelayCommand(RunOpenLivePreview, () => IsHotReloadRunning);
        InjectHotReloadBridgeCommand = new AsyncRelayCommand(RunInjectHotReloadBridgeAsync, () => !IsBusy);
        TriggerHotReloadNowCommand = new AsyncRelayCommand(RunTriggerHotReloadNowAsync, () => IsHotReloadRunning);

        // MOD Library commands
        RefreshCommand = new RelayCommand(ReloadAll, () => !IsBusy);
        ImportZipCommand = new AsyncRelayCommand(RunImportZipAsync, () => !IsBusy);
        ImportFolderCommand = new AsyncRelayCommand(RunImportFolderAsync, () => !IsBusy);
        CreateTemplateCommand = new AsyncRelayCommand(RunCreateTemplateAsync, () => !IsBusy);
        PackWorkspaceCommand = new AsyncRelayCommand(RunPackWorkspaceAsync, () => !IsBusy && Mods.Count > 0);
        OpenModsFolderCommand = new RelayCommand(() => _dialogService.OpenFolderInExplorer(_engine.Repository.ModsRoot));
        EditSelectedModInStudioCommand = new RelayCommand(OpenSelectedModInStudio, () => SelectedMod is not null);
        EnableAllCommand = new RelayCommand(() => SetAllEnabled(true), () => !IsBusy && Mods.Count > 0);
        DisableAllCommand = new RelayCommand(() => SetAllEnabled(false), () => !IsBusy && Mods.Count > 0);

        BrowseWorkspaceCommand = new AsyncRelayCommand(async () =>
        {
            var picked = await _dialogService.PickFolderAsync(
                Loc.T("选择 MOD 管理器工作目录（包含 mods\\）", "Select MOD Manager Workspace Folder"),
                WorkspaceRoot);
            if (!string.IsNullOrWhiteSpace(picked))
            {
                var profile = _workspaceRegistry.EnsureRegistered(picked, setActive: true);
                SwitchToWorkspaceProfile(profile);
            }
        }, () => !IsBusy);

        BrowseInstallDirCommand = new AsyncRelayCommand(async () =>
        {
            var picked = await _dialogService.PickFolderAsync(
                Loc.T("选择游戏安装目录（含 Yu-Gi-Oh! Rogue Duelist.dll）", "Select Game Install Directory"),
                InstallDir);
            if (!string.IsNullOrWhiteSpace(picked))
            {
                InstallDir = picked;
                PersistPathsAndRefreshStatus();
            }
        }, () => !IsBusy);

        BrowseGameDirCommand = new AsyncRelayCommand(async () =>
        {
            var picked = await _dialogService.PickFolderAsync(
                Loc.T("选择游戏数据根目录（%LOCALAPPDATA%\\Yu-Gi-Oh! Rogue Duelist\\Game）", "Select Game Data Root Folder"),
                GameDir);
            if (!string.IsNullOrWhiteSpace(picked))
            {
                GameDir = picked;
                PersistPathsAndRefreshStatus();
            }
        }, () => !IsBusy);

        BrowsePicsDirCommand = new AsyncRelayCommand(async () =>
        {
            var picked = await _dialogService.PickFolderAsync(
                Loc.T("选择备用 YGOPro 卡图目录（可选）", "Select Fallback YGOPro Card Art Folder (Optional)"),
                PicsDir);
            if (!string.IsNullOrWhiteSpace(picked))
            {
                PicsDir = picked;
                PersistPathsAndRefreshStatus();
            }
        }, () => !IsBusy);

        BrowseBuildOnlyDirCommand = new AsyncRelayCommand(async () =>
        {
            var picked = await _dialogService.PickFolderAsync(
                Loc.T("选择 BuildOnly 导出测试目录", "Select BuildOnly Export Folder"),
                BuildOnlyDir);
            if (!string.IsNullOrWhiteSpace(picked)) BuildOnlyDir = picked;
        }, () => !IsBusy);

        SaveSettingsCommand = new RelayCommand(PersistPathsAndRefreshStatus, () => !IsBusy);
        ClearLogsCommand = new RelayCommand(() => Logs.Clear());
        CopyLogsCommand = new AsyncRelayCommand(CopyLogsAsync);

        StatusText = Loc.T("就绪", "Ready");
        LoadWorkspaceProfiles(_engine.Repository.WorkspaceRoot);
        ReloadAll();
        AppendLog(LogLevel.Info, Loc.T(
            $"MOD 管理器已就绪（当前工作区：{_engine.Repository.WorkspaceRoot}）",
            $"MOD Manager ready (Workspace: {_engine.Repository.WorkspaceRoot})"));
    }

    public LocalizationService Loc { get; }
    public ModStudioViewModel Studio { get; }

    public IReadOnlyList<InjectionModeOption> InjectionModeOptions
    {
        get => _injectionModeOptions;
        private set => SetProperty(ref _injectionModeOptions, value);
    }

    public ObservableCollection<WorkspaceProfile> Workspaces { get; } = [];
    public ObservableCollection<ModItemViewModel> Mods { get; } = [];
    public ObservableCollection<ConflictItemViewModel> Conflicts { get; } = [];
    public ObservableCollection<VersionIssueItemViewModel> VersionIssues { get; } = [];
    public ObservableCollection<LogItem> Logs { get; } = [];

    public WorkspaceProfile? SelectedWorkspace
    {
        get => _selectedWorkspace;
        set
        {
            if (value is not null && SetProperty(ref _selectedWorkspace, value) && !_suppressStateSave)
            {
                SwitchToWorkspaceProfile(value);
            }
        }
    }

    public int SelectedMainTabIndex
    {
        get => _selectedMainTabIndex;
        set => SetProperty(ref _selectedMainTabIndex, value);
    }

    public InjectionModeOption SelectedInjectionMode
    {
        get => _selectedInjectionMode;
        set
        {
            if (value is not null && SetProperty(ref _selectedInjectionMode, value))
            {
                OnPropertyChanged(nameof(InjectionModeDescription));
                if (!_suppressStateSave)
                {
                    var state = _engine.Repository.LoadState();
                    state.InjectionMode = value.Mode;
                    _engine.Repository.SaveState(state);
                    RefreshStatusOnly();
                }
            }
        }
    }

    public bool DeveloperMode
    {
        get => _developerMode;
        set
        {
            if (SetProperty(ref _developerMode, value) && !_suppressStateSave)
            {
                var state = _engine.Repository.LoadState();
                state.DeveloperMode = value;
                _engine.Repository.SaveState(state);
                RefreshStatusOnly();
                AppendLog(LogLevel.Info, value
                    ? Loc.T("已开启开发者模式（点击「应用注入」后在游戏内按 F10 打开调试面板）", "Developer Mode enabled (click Apply MODs, then press F10 in-game)")
                    : Loc.T("已关闭开发者模式（点击「应用注入」更新）", "Developer Mode disabled (click Apply MODs to update)"));
            }
        }
    }

    public bool IsHotReloadRunning
    {
        get => _isHotReloadRunning;
        private set
        {
            if (SetProperty(ref _isHotReloadRunning, value))
            {
                OnPropertyChanged(nameof(HotReloadToggleLabel));
                OpenLivePreviewCommand.NotifyCanExecuteChanged();
                TriggerHotReloadNowCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string HotReloadToggleLabel => IsHotReloadRunning ? Loc.StopHotReloadButton : Loc.StartHotReloadButton;

    public string HotReloadBadgeText
    {
        get => _hotReloadBadgeText;
        private set => SetProperty(ref _hotReloadBadgeText, value);
    }

    public string InjectionModeDescription => SelectedInjectionMode?.Description ?? string.Empty;

    public ModItemViewModel? SelectedMod
    {
        get => _selectedMod;
        set
        {
            if (SetProperty(ref _selectedMod, value))
            {
                OnPropertyChanged(nameof(HasSelectedMod));
                EditSelectedModInStudioCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasSelectedMod => SelectedMod is not null;
    public bool HasConflicts => Conflicts.Count > 0;
    public bool HasVersionIssues => VersionIssues.Count > 0;

    public string ConflictsHeader => Conflicts.Count == 0
        ? Loc.T("冲突检测：无冲突", "Conflict Check: None")
        : Loc.T(
            $"冲突检测：发现 {Conflicts.Count} 处重叠（按加载顺序后加载者生效）",
            $"Conflict Check: {Conflicts.Count} overlaps (later load order wins)");

    public string WorkspaceRoot { get => _workspaceRoot; set => SetProperty(ref _workspaceRoot, value); }
    public string InstallDir { get => _installDir; set => SetProperty(ref _installDir, value); }
    public string GameDir { get => _gameDir; set => SetProperty(ref _gameDir, value); }
    public string PicsDir { get => _picsDir; set => SetProperty(ref _picsDir, value); }
    public string BuildOnlyDir { get => _buildOnlyDir; set => SetProperty(ref _buildOnlyDir, value); }
    public string Repo { get => _repo; set => SetProperty(ref _repo, value); }

    public string GameVersionBadge { get => _gameVersionBadge; private set => SetProperty(ref _gameVersionBadge, value); }
    public IBrush GameVersionBrush { get => _gameVersionBrush; private set => SetProperty(ref _gameVersionBrush, value); }
    public string SyncStatusText { get => _syncStatusText; private set => SetProperty(ref _syncStatusText, value); }
    public IBrush SyncStatusBrush { get => _syncStatusBrush; private set => SetProperty(ref _syncStatusBrush, value); }
    public string ModCountText { get => _modCountText; private set => SetProperty(ref _modCountText, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                NotifyCommandStates();
            }
        }
    }

    public bool IsIdle => !IsBusy;
    public bool IsProgressIndeterminate { get => _isProgressIndeterminate; private set => SetProperty(ref _isProgressIndeterminate, value); }
    public double ProgressValue { get => _progressValue; private set => SetProperty(ref _progressValue, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public RelayCommand ToggleLanguageCommand { get; }
    public AsyncRelayCommand ApplyCommand { get; }
    public AsyncRelayCommand ApplyAndLaunchCommand { get; }
    public AsyncRelayCommand RestoreVanillaCommand { get; }
    public RelayCommand LaunchGameCommand { get; }
    public AsyncRelayCommand UpdateGameCommand { get; }
    public AsyncRelayCommand ExportBuildOnlyCommand { get; }
    public RelayCommand CancelCommand { get; }

    public AsyncRelayCommand NewWorkspaceCommand { get; }
    public AsyncRelayCommand RemoveWorkspaceCommand { get; }
    public RelayCommand OpenWorkspaceFolderCommand { get; }

    public AsyncRelayCommand ToggleHotReloadCommand { get; }
    public RelayCommand OpenLivePreviewCommand { get; }
    public AsyncRelayCommand InjectHotReloadBridgeCommand { get; }
    public AsyncRelayCommand TriggerHotReloadNowCommand { get; }

    public RelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ImportZipCommand { get; }
    public AsyncRelayCommand ImportFolderCommand { get; }
    public AsyncRelayCommand CreateTemplateCommand { get; }
    public AsyncRelayCommand PackWorkspaceCommand { get; }
    public RelayCommand OpenModsFolderCommand { get; }
    public RelayCommand EditSelectedModInStudioCommand { get; }
    public RelayCommand EnableAllCommand { get; }
    public RelayCommand DisableAllCommand { get; }

    public AsyncRelayCommand BrowseWorkspaceCommand { get; }
    public AsyncRelayCommand BrowseInstallDirCommand { get; }
    public AsyncRelayCommand BrowseGameDirCommand { get; }
    public AsyncRelayCommand BrowsePicsDirCommand { get; }
    public AsyncRelayCommand BrowseBuildOnlyDirCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand ClearLogsCommand { get; }
    public AsyncRelayCommand CopyLogsCommand { get; }

    private void LoadWorkspaceProfiles(string ensureCurrentPath)
    {
        _suppressStateSave = true;
        try
        {
            var list = _workspaceRegistry.ListWorkspaces(ensureCurrentPath);
            Workspaces.Clear();
            foreach (var p in list) Workspaces.Add(p);
            SelectedWorkspace = Workspaces.FirstOrDefault(w =>
                string.Equals(Path.GetFullPath(w.Path), Path.GetFullPath(ensureCurrentPath), StringComparison.OrdinalIgnoreCase))
                ?? Workspaces.FirstOrDefault();
            RemoveWorkspaceCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            _suppressStateSave = false;
        }
    }

    private void SwitchToWorkspaceProfile(WorkspaceProfile profile)
    {
        StopHotReloadIfRunning();
        _workspaceRegistry.SetActiveWorkspace(profile.Id);
        _engine = new ModInjectionEngine(profile.Path);
        LoadWorkspaceProfiles(profile.Path);
        ReloadAll();
        AppendLog(LogLevel.Ok, Loc.T(
            $"已切换工作区：{profile.Name} -> {profile.Path}",
            $"Switched workspace: {profile.Name} -> {profile.Path}"));
    }

    private async Task RunNewWorkspaceAsync()
    {
        var defaultParent = Path.GetDirectoryName(_engine.Repository.WorkspaceRoot) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var input = await _dialogService.PromptNewWorkspaceAsync(Loc.IsEnglish, defaultParent);
        if (input is null) return;

        try
        {
            var created = _workspaceRegistry.CreateWorkspace(input.Name, input.Path, input.Description, setActive: true);
            SwitchToWorkspaceProfile(created);
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Error, ex.Message);
        }
    }

    private async Task RunRemoveWorkspaceAsync()
    {
        if (SelectedWorkspace is null || Workspaces.Count <= 1) return;
        var current = SelectedWorkspace;
        var confirmed = await _dialogService.ConfirmAsync(
            Loc.T("移除工作区记录", "Remove Workspace Profile"),
            Loc.T(
                $"确定从工作区快捷列表中移除「{current.Name}」({current.Path}) 吗？\n（仅移除列表记录，不会删除磁盘上的任何 MOD 文件）",
                $"Remove '{current.Name}' ({current.Path}) from the workspace switcher?\n(Files on disk will NOT be deleted.)"),
            Loc.T("确认移除", "Remove"),
            Loc.T("取消", "Cancel"));
        if (!confirmed) return;

        _workspaceRegistry.RemoveWorkspace(current.Id);
        var next = _workspaceRegistry.GetActiveWorkspace();
        if (next is not null)
        {
            SwitchToWorkspaceProfile(next);
        }
    }

    private async Task RunToggleHotReloadAsync()
    {
        if (IsHotReloadRunning)
        {
            StopHotReloadIfRunning();
            return;
        }

        try
        {
            SavePathsSilently();
            _hotReloadServer = new ModHotReloadServer(_engine, AppendLogUiSafe);
            _hotReloadServer.Reloaded += OnHotReloadEvent;
            await _hotReloadServer.StartAsync(ModHotReloadServer.DefaultPort);
            IsHotReloadRunning = true;
            HotReloadBadgeText = $"LIVE · :{_hotReloadServer.Port} · Rev #{_hotReloadServer.Revision}";
            StatusText = Loc.T(
                $"热加载运行中：{_hotReloadServer.BaseUrl}（监听 mods\\ 自动刷新）",
                $"Hot-Reload live at {_hotReloadServer.BaseUrl} (watching mods\\)");
        }
        catch (Exception ex)
        {
            StopHotReloadIfRunning();
            AppendLog(LogLevel.Error, ex.Message);
        }
    }

    private void OnHotReloadEvent(HotReloadEventInfo info)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_hotReloadServer is null) return;
            HotReloadBadgeText = string.IsNullOrWhiteSpace(info.Error)
                ? $"LIVE · :{_hotReloadServer.Port} · Rev #{info.Revision}"
                : $"ERR · :{_hotReloadServer.Port} · Rev #{info.Revision}";
        });
    }

    private void StopHotReloadIfRunning()
    {
        if (_hotReloadServer is not null)
        {
            _hotReloadServer.Reloaded -= OnHotReloadEvent;
            _hotReloadServer.Dispose();
            _hotReloadServer = null;
        }

        IsHotReloadRunning = false;
        HotReloadBadgeText = string.Empty;
    }

    private void RunOpenLivePreview()
    {
        if (_hotReloadServer is { IsRunning: true })
        {
            _dialogService.OpenUrlInBrowser(_hotReloadServer.BaseUrl);
        }
    }

    private async Task RunTriggerHotReloadNowAsync()
    {
        if (_hotReloadServer is { IsRunning: true })
        {
            await _hotReloadServer.TriggerManualReloadAsync("(manual-ui)");
        }
    }

    private async Task RunInjectHotReloadBridgeAsync()
    {
        if (!IsHotReloadRunning)
        {
            await RunToggleHotReloadAsync();
        }

        if (_hotReloadServer is not { IsRunning: true }) return;
        var port = _hotReloadServer.Port;
        SavePathsSilently();

        await ExecuteBusyAsync(
            Loc.T($"注入热加载桥 (端口 {port})", $"Inject Hot-Reload Bridge (:{port})"),
            async (interaction, ct) =>
            {
                await _engine.ApplyAsync(new ApplyModsOptions
                {
                    ModeOverride = InjectionMode.VirtualBundle,
                    DeveloperModeOverride = true,
                    HotReloadPort = port
                }, interaction, ct);
            });
    }

    private async Task OnStudioFileSavedAsync(string? changedFile)
    {
        ReloadAll();
        if (_hotReloadServer is { IsRunning: true })
        {
            await _hotReloadServer.TriggerManualReloadAsync(changedFile);
        }
    }

    private void OpenSelectedModInStudio()
    {
        if (SelectedMod is null) return;
        Studio.SelectModById(SelectedMod.Id);
        SelectedMainTabIndex = 1;
    }

    private void ToggleLanguage()
    {
        Loc.Toggle();
        var state = _engine.Repository.LoadState();
        state.Language = Loc.LanguageCode;
        _engine.Repository.SaveState(state);

        _suppressStateSave = true;
        try
        {
            RebuildInjectionModeOptions(SelectedInjectionMode.Mode);
            OnPropertyChanged(nameof(HotReloadToggleLabel));
            foreach (var mod in Mods) mod.RefreshLocalization();
            if (_lastStatus is not null) ApplyStatusSnapshot(_lastStatus);
            if (!IsBusy) StatusText = Loc.T("就绪", "Ready");
        }
        finally
        {
            _suppressStateSave = false;
        }
    }

    private void RebuildInjectionModeOptions(InjectionMode activeMode)
    {
        InjectionModeOptions =
        [
            new InjectionModeOption(
                InjectionMode.VirtualBundle,
                Loc.T("零侵入虚拟版本包（推荐）", "Zero-Touch Virtual Bundle (Recommended)"),
                Loc.T(
                    "完全不修改游戏 EXE / DLL 和原版 web-*.zip，在 Game\\900.x.y 隔离注入，支持热加载与一键还原。",
                    "Never modifies game EXE / DLL or vanilla web-*.zip. Isolates modded assets in Game\\900.x.y with hot-reload & 1-click restore.")),
            new InjectionModeOption(
                InjectionMode.LauncherManifest,
                Loc.T("启动器清单注入（兼容模式）", "Launcher Manifest Patch (Legacy)"),
                Loc.T(
                    "修改 Game\\<版本> 并同步更新启动器 DLL 哈希清单（自动备份），保留游戏原版版本号，仅支持覆盖既有文件。",
                    "Patches Game\\<ver> and updates StartupHashes.json in the launcher DLL (auto-backed up). Existing files only."))
        ];
        SelectedInjectionMode = InjectionModeOptions.FirstOrDefault(o => o.Mode == activeMode) ?? InjectionModeOptions[0];
    }

    private void NotifyCommandStates()
    {
        ApplyCommand.NotifyCanExecuteChanged();
        ApplyAndLaunchCommand.NotifyCanExecuteChanged();
        RestoreVanillaCommand.NotifyCanExecuteChanged();
        LaunchGameCommand.NotifyCanExecuteChanged();
        UpdateGameCommand.NotifyCanExecuteChanged();
        ExportBuildOnlyCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        NewWorkspaceCommand.NotifyCanExecuteChanged();
        RemoveWorkspaceCommand.NotifyCanExecuteChanged();
        ToggleHotReloadCommand.NotifyCanExecuteChanged();
        OpenLivePreviewCommand.NotifyCanExecuteChanged();
        InjectHotReloadBridgeCommand.NotifyCanExecuteChanged();
        TriggerHotReloadNowCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        ImportZipCommand.NotifyCanExecuteChanged();
        ImportFolderCommand.NotifyCanExecuteChanged();
        CreateTemplateCommand.NotifyCanExecuteChanged();
        PackWorkspaceCommand.NotifyCanExecuteChanged();
        EditSelectedModInStudioCommand.NotifyCanExecuteChanged();
        EnableAllCommand.NotifyCanExecuteChanged();
        DisableAllCommand.NotifyCanExecuteChanged();
        BrowseWorkspaceCommand.NotifyCanExecuteChanged();
        BrowseInstallDirCommand.NotifyCanExecuteChanged();
        BrowseGameDirCommand.NotifyCanExecuteChanged();
        BrowsePicsDirCommand.NotifyCanExecuteChanged();
        BrowseBuildOnlyDirCommand.NotifyCanExecuteChanged();
        SaveSettingsCommand.NotifyCanExecuteChanged();

        foreach (var mod in Mods) mod.NotifyIdleChanged();
    }

    public void ReloadAll()
    {
        var previousSelectedId = SelectedMod?.Id;
        var state = _engine.Repository.LoadState();
        var status = _engine.GetStatus();

        _suppressStateSave = true;
        try
        {
            WorkspaceRoot = status.WorkspaceRoot;
            InstallDir = status.InstallDir ?? string.Empty;
            GameDir = status.GameBaseDir ?? string.Empty;
            PicsDir = state.PicsDir ?? GameLocator.FindPicsDir() ?? string.Empty;
            Repo = string.IsNullOrWhiteSpace(state.Repo) ? InstallOptions.DefaultRepo : state.Repo;
            DeveloperMode = status.DeveloperMode;
            SelectedInjectionMode = InjectionModeOptions.FirstOrDefault(o => o.Mode == status.Mode) ?? InjectionModeOptions[0];

            Mods.Clear();
            foreach (var mod in status.Mods)
            {
                Mods.Add(new ModItemViewModel(
                    mod,
                    Loc,
                    onToggled: OnModListMutated,
                    onMoveUp: MoveModUp,
                    onMoveDown: MoveModDown,
                    onDelete: DeleteModAsync,
                    onOpenFolder: m => _dialogService.OpenFolderInExplorer(m.DirectoryPath),
                    onPack: PackSingleModAsync,
                    isIdle: () => IsIdle));
            }

            SelectedMod = Mods.FirstOrDefault(m => string.Equals(m.Id, previousSelectedId, StringComparison.OrdinalIgnoreCase))
                          ?? Mods.FirstOrDefault();

            Studio.SyncMods(status.Mods, Studio.SelectedMod?.Id ?? SelectedMod?.Id);
            ApplyStatusSnapshot(status);
        }
        finally
        {
            _suppressStateSave = false;
            NotifyCommandStates();
        }
    }

    private void RefreshStatusOnly()
    {
        var status = _engine.GetStatus();
        ApplyStatusSnapshot(status);
    }

    private void ApplyStatusSnapshot(ModManagerStatusSnapshot status)
    {
        _lastStatus = status;

        if (!string.IsNullOrWhiteSpace(status.ActiveBaseGameVersion))
        {
            var buildTag = status.LauncherMeta is { LauncherBuild: > 0 }
                ? Loc.T($" · 启动器 Build {status.LauncherMeta.LauncherBuild}", $" · Launcher Build {status.LauncherMeta.LauncherBuild}")
                : string.Empty;
            GameVersionBadge = Loc.T($"原版 v{status.ActiveBaseGameVersion}{buildTag}", $"Vanilla v{status.ActiveBaseGameVersion}{buildTag}");
            GameVersionBrush = GoodBrush;
        }
        else if (!string.IsNullOrWhiteSpace(status.InstallDir))
        {
            GameVersionBadge = Loc.T("已检测安装目录（未找到 web-*.zip）", "Install dir found (missing web-*.zip)");
            GameVersionBrush = WarnBrush;
        }
        else
        {
            GameVersionBadge = Loc.T("未检测到游戏（可点击更新自动下载）", "Game not detected (click Update to download)");
            GameVersionBrush = WarnBrush;
        }

        SyncStatusText = Loc.IsEnglish ? FormatEnglishSyncSummary(status) : status.SyncSummary;
        SyncStatusBrush = status.SyncState switch
        {
            InjectionSyncState.Synced => GoodBrush,
            InjectionSyncState.OutOfDate => WarnBrush,
            InjectionSyncState.GameUpdated => AlertBrush,
            _ => MutedBrush
        };

        var enabledCount = status.Mods.Count(m => m.Enabled);
        ModCountText = Loc.T(
            $"已启用 {enabledCount} / 共 {status.Mods.Count} 个 MOD",
            $"{enabledCount} enabled / {status.Mods.Count} total MODs");

        Conflicts.Clear();
        var conflictByMod = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in status.Conflicts)
        {
            Conflicts.Add(new ConflictItemViewModel(c, Loc));
            foreach (var id in c.ModIds)
            {
                if (!conflictByMod.TryGetValue(id, out var list))
                {
                    list = [];
                    conflictByMod[id] = list;
                }

                list.Add($"[{c.Category}] {c.ResourceKey}");
            }
        }

        VersionIssues.Clear();
        var issueByMod = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var issue in status.VersionIssues)
        {
            VersionIssues.Add(new VersionIssueItemViewModel(issue));
            if (!issueByMod.TryGetValue(issue.ModId, out var list))
            {
                list = [];
                issueByMod[issue.ModId] = list;
            }

            list.Add(issue.Message);
        }

        foreach (var vm in Mods)
        {
            if (conflictByMod.TryGetValue(vm.Id, out var items))
            {
                vm.HasConflict = true;
                vm.ConflictTooltip = Loc.T(
                    $"与其它已启用 MOD 存在 {items.Count} 处重叠：{string.Join("、", items.Take(5))}",
                    $"{items.Count} overlaps with other enabled MODs: {string.Join(", ", items.Take(5))}");
            }
            else
            {
                vm.HasConflict = false;
                vm.ConflictTooltip = string.Empty;
            }

            if (issueByMod.TryGetValue(vm.Id, out var vIssues))
            {
                vm.HasVersionIssue = true;
                vm.VersionIssueTooltip = string.Join(Environment.NewLine, vIssues);
            }
            else
            {
                vm.HasVersionIssue = false;
                vm.VersionIssueTooltip = string.Empty;
            }
        }

        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(ConflictsHeader));
        OnPropertyChanged(nameof(HasVersionIssues));
    }

    private static string FormatEnglishSyncSummary(ModManagerStatusSnapshot status) => status.SyncState switch
    {
        InjectionSyncState.Synced => $"Synced ({status.Mode}, base v{status.ActiveBaseGameVersion})",
        InjectionSyncState.OutOfDate => "Changes pending — click 'Apply MODs' to inject",
        InjectionSyncState.GameUpdated => $"Official game updated to v{status.ActiveBaseGameVersion} — click 'Apply MODs' to re-inject",
        _ => "Vanilla (no MODs injected)"
    };

    private void OnModListMutated()
    {
        if (_suppressStateSave) return;
        SaveCurrentModOrderAndFlags();
    }

    private void SaveCurrentModOrderAndFlags()
    {
        var state = _engine.Repository.LoadState();
        var models = Mods.Select(m => m.Model).ToList();
        for (var i = 0; i < Mods.Count; i++) Mods[i].Order = i;

        _engine.Repository.SaveModOrderAndFlags(models, state);
        RefreshStatusOnly();

        if (_hotReloadServer is { IsRunning: true })
        {
            _ = _hotReloadServer.TriggerManualReloadAsync("(order/toggle)");
        }
    }

    private void MoveModUp(ModItemViewModel item)
    {
        var idx = Mods.IndexOf(item);
        if (idx <= 0) return;
        Mods.Move(idx, idx - 1);
        SelectedMod = item;
        SaveCurrentModOrderAndFlags();
    }

    private void MoveModDown(ModItemViewModel item)
    {
        var idx = Mods.IndexOf(item);
        if (idx < 0 || idx >= Mods.Count - 1) return;
        Mods.Move(idx, idx + 1);
        SelectedMod = item;
        SaveCurrentModOrderAndFlags();
    }

    private void SetAllEnabled(bool enable)
    {
        _suppressStateSave = true;
        try
        {
            foreach (var m in Mods) m.SetEnabledSilent(enable);
        }
        finally
        {
            _suppressStateSave = false;
        }

        SaveCurrentModOrderAndFlags();
    }

    private async Task DeleteModAsync(ModItemViewModel item)
    {
        var confirmed = await _dialogService.ConfirmAsync(
            Loc.T("确认删除 MOD", "Delete MOD"),
            Loc.T(
                $"确定要从 mods\\ 目录中永久删除「{item.Name}」({item.Id}) 吗？\n（不影响原版游戏；删除后请点击「应用注入」更新游戏状态）",
                $"Permanently delete '{item.Name}' ({item.Id}) from mods\\?\n(Does not affect vanilla game files.)"),
            Loc.T("确认删除", "Delete"),
            Loc.T("取消", "Cancel"));

        if (!confirmed) return;

        try
        {
            _engine.Repository.DeleteMod(item.Id);
            AppendLog(LogLevel.Ok, Loc.T($"已删除 MOD：{item.Name} ({item.Id})", $"Deleted MOD: {item.Name} ({item.Id})"));
            ReloadAll();
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Error, ex.Message);
        }
    }

    private async Task PackSingleModAsync(ModItemViewModel item)
    {
        var distDir = Path.Combine(_engine.Repository.WorkspaceRoot, "dist");
        var suggested = $"{item.Id}-v{item.Version}.zip";
        var dest = await _dialogService.PickSaveZipFileAsync(
            Loc.T($"打包 MOD：{item.Name}", $"Pack MOD: {item.Name}"),
            suggested,
            distDir);

        if (string.IsNullOrWhiteSpace(dest)) return;

        try
        {
            var zipPath = ModPackager.PackSingleMod(_engine.Repository, item.Id, dest);
            AppendLog(LogLevel.Ok, Loc.T($"已打包 MOD -> {zipPath}", $"Packed MOD -> {zipPath}"));
            StatusText = Loc.T("MOD 打包完成", "MOD packed");
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Error, ex.Message);
        }
    }

    private async Task RunPackWorkspaceAsync()
    {
        var distDir = Path.Combine(_engine.Repository.WorkspaceRoot, "dist");
        var dest = await _dialogService.PickSaveZipFileAsync(
            Loc.T("打包整个 MOD 工作区为整合包 (.zip)", "Pack MOD Workspace as Modpack (.zip)"),
            "YGO-Rogue-Modpack-v1.0.0.zip",
            distDir);

        if (string.IsNullOrWhiteSpace(dest)) return;

        try
        {
            var (zipPath, count, minVer) = ModPackager.PackWorkspace(
                _engine.Repository,
                outputZipPath: dest,
                packName: Path.GetFileNameWithoutExtension(dest));
            var minText = string.IsNullOrWhiteSpace(minVer) ? string.Empty : $" (minGameVersion: v{minVer})";
            AppendLog(LogLevel.Ok, Loc.T(
                $"已打包工作区整合包（共 {count} 个 MOD{minText}）-> {zipPath}",
                $"Packed workspace modpack ({count} MODs{minText}) -> {zipPath}"));
            StatusText = Loc.T("工作区打包完成", "Workspace packed");
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Error, ex.Message);
        }
    }

    private async Task RunImportZipAsync()
    {
        var zipPath = await _dialogService.PickZipFileAsync(
            Loc.T("选择要导入的 MOD 压缩包或工作区整合包 (.zip)", "Select MOD Archive or Workspace Modpack (.zip)"));
        if (!string.IsNullOrWhiteSpace(zipPath)) await ImportFromPathAsync(zipPath);
    }

    private async Task RunImportFolderAsync()
    {
        var folder = await _dialogService.PickFolderAsync(
            Loc.T("选择要导入的 MOD 文件夹", "Select MOD Folder to Import"));
        if (!string.IsNullOrWhiteSpace(folder)) await ImportFromPathAsync(folder);
    }

    private async Task ImportFromPathAsync(string path)
    {
        try
        {
            if (ModPackager.IsWorkspacePackZip(path))
            {
                var confirmed = await _dialogService.ConfirmAsync(
                    Loc.T("导入工作区整合包", "Import Workspace Modpack"),
                    Loc.T(
                        "检测到这是一个 MOD 工作区整合包（含 workspace-pack.json）。\n导入后将自动解压全部 MOD 并恢复加载顺序与启用状态，是否继续？",
                        "Detected a Workspace Modpack (workspace-pack.json).\nImport all contained MODs and restore their load order & enabled states?"),
                    Loc.T("导入整合包", "Import Modpack"),
                    Loc.T("取消", "Cancel"));
                if (!confirmed) return;

                var (manifest, count) = ModPackager.ImportWorkspacePack(_engine.Repository, path, overwrite: true);
                AppendLog(LogLevel.Ok, Loc.T(
                    $"已导入工作区整合包：{manifest.Name} v{manifest.Version}（共 {count} 个 MOD）",
                    $"Imported workspace modpack: {manifest.Name} v{manifest.Version} ({count} MODs)"));
                ReloadAll();
                return;
            }

            var imported = _engine.Repository.ImportMod(path, overwrite: false);
            AppendLog(LogLevel.Ok, Loc.T($"已导入 MOD：{imported.Manifest.Name} ({imported.Id})", $"Imported MOD: {imported.Manifest.Name} ({imported.Id})"));
            ReloadAll();
            SelectedMod = Mods.FirstOrDefault(m => string.Equals(m.Id, imported.Id, StringComparison.OrdinalIgnoreCase));
        }
        catch (ModOperationException ex) when (ex.Message.Contains("已存在", StringComparison.Ordinal))
        {
            var overwrite = await _dialogService.ConfirmAsync(
                Loc.T("覆盖已有 MOD", "Overwrite Existing MOD"),
                ex.Message + Loc.T("\n\n是否覆盖现有版本？", "\n\nOverwrite existing version?"),
                Loc.T("覆盖导入", "Overwrite"),
                Loc.T("取消", "Cancel"));
            if (!overwrite) return;
            var imported = _engine.Repository.ImportMod(path, overwrite: true);
            AppendLog(LogLevel.Ok, Loc.T($"已覆盖导入 MOD：{imported.Manifest.Name} ({imported.Id})", $"Overwrote MOD: {imported.Manifest.Name} ({imported.Id})"));
            ReloadAll();
            SelectedMod = Mods.FirstOrDefault(m => string.Equals(m.Id, imported.Id, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Error, ex.Message);
        }
    }

    private async Task RunCreateTemplateAsync()
    {
        var status = _engine.GetStatus();
        var input = await _dialogService.PromptCreateModAsync(Loc.IsEnglish, status.ActiveBaseGameVersion);
        if (input is null) return;

        try
        {
            var created = _engine.Repository.CreateTemplateMod(
                input.Id, input.Name, input.Author, input.Description,
                status.ActiveBaseGameVersion, input.MinGameVersion);
            AppendLog(LogLevel.Ok, Loc.T($"已创建 MOD 模板：{created.DirectoryPath}", $"Created MOD template: {created.DirectoryPath}"));
            ReloadAll();
            SelectedMod = Mods.FirstOrDefault(m => string.Equals(m.Id, created.Id, StringComparison.OrdinalIgnoreCase));
            Studio.SelectModById(created.Id);
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Error, ex.Message);
        }
    }

    private void PersistPathsAndRefreshStatus()
    {
        SavePathsSilently();
        RefreshStatusOnly();
        AppendLog(LogLevel.Ok, Loc.T("路径与注入设置已保存。", "Paths and injection settings saved."));
    }

    private async Task RunApplyAsync(bool launchAfter)
    {
        SavePathsSilently();
        int? hrPort = _hotReloadServer is { IsRunning: true } ? _hotReloadServer.Port : null;
        await ExecuteBusyAsync(
            launchAfter ? Loc.T("应用注入并启动游戏", "Apply MODs & Launch Game") : Loc.T("应用 MOD 注入", "Apply MODs"),
            async (interaction, ct) =>
            {
                await _engine.ApplyAsync(new ApplyModsOptions
                {
                    ModeOverride = SelectedInjectionMode.Mode,
                    LaunchGameAfter = launchAfter,
                    DeveloperModeOverride = DeveloperMode,
                    HotReloadPort = hrPort
                }, interaction, ct);
            });
    }

    private async Task RunExportBuildOnlyAsync()
    {
        if (string.IsNullOrWhiteSpace(BuildOnlyDir))
        {
            AppendLog(LogLevel.Error, Loc.T("请先在设置中指定 BuildOnly 导出目录。", "Please specify a BuildOnly export directory first."));
            return;
        }

        SavePathsSilently();
        await ExecuteBusyAsync(
            Loc.T("合成导出测试文件 (BuildOnly)", "BuildOnly Export"),
            async (interaction, ct) =>
            {
                await _engine.ApplyAsync(new ApplyModsOptions
                {
                    BuildOnlyDir = BuildOnlyDir.Trim(),
                    DeveloperModeOverride = DeveloperMode
                }, interaction, ct);
            });
    }

    private async Task RunRestoreAsync()
    {
        var confirmed = await _dialogService.ConfirmAsync(
            Loc.T("还原纯净原版", "Restore Vanilla Game"),
            Loc.T(
                "将移除所有隔离虚拟 MOD 包（Game\\900+.*）、还原可能修改过的启动器 DLL 备份，并清理游戏网页缓存。\n已管理的 mods\\ 目录不会被删除。确定继续吗？",
                "Removes all isolated virtual MOD bundles (Game\\900+.*), restores any backed-up launcher DLL, and clears web caches.\nYour mods\\ folder is kept untouched. Continue?"),
            Loc.T("确认还原", "Restore"),
            Loc.T("取消", "Cancel"));

        if (!confirmed) return;

        SavePathsSilently();
        await ExecuteBusyAsync(
            Loc.T("还原纯净原版", "Restore Vanilla"),
            async (interaction, ct) => await _engine.RestoreVanillaAsync(interaction, cancellationToken: ct));
    }

    private async Task RunUpdateGameAsync()
    {
        SavePathsSilently();
        await ExecuteBusyAsync(
            Loc.T("检查并更新原版游戏", "Check & Update Official Game"),
            async (interaction, ct) => await _engine.UpdateOfficialGameAsync(interaction, ct));
    }

    private void RunLaunchGame()
    {
        try
        {
            ModInjectionEngine.LaunchGame(NullIfBlank(InstallDir));
            AppendLog(LogLevel.Ok, Loc.T("已启动游戏。", "Launched game."));
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Error, ex.Message);
        }
    }

    private async Task ExecuteBusyAsync(string title, Func<IInstallerInteraction, CancellationToken, Task> action)
    {
        AppendLog(LogLevel.Info, $"=== {title} ===");
        IsBusy = true;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        StatusText = $"{title}...";
        _cts = new CancellationTokenSource();

        var interaction = new AvaloniaInstallerInteraction(AppendLog, UpdateProgress, _dialogService);

        try
        {
            await action(interaction, _cts.Token);
            IsProgressIndeterminate = false;
            ProgressValue = 100;
            StatusText = Loc.T($"{title}完成", $"{title} completed");
            ReloadAll();
        }
        catch (OperationCanceledException)
        {
            AppendLog(LogLevel.Warn, Loc.T("操作已取消。", "Operation canceled."));
            StatusText = Loc.T("已取消", "Canceled");
            IsProgressIndeterminate = false;
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Error, ex.Message);
            StatusText = Loc.T("操作失败", "Operation failed");
            IsProgressIndeterminate = false;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    private void SavePathsSilently()
    {
        var state = _engine.Repository.LoadState();
        state.InstallDir = NullIfBlank(InstallDir);
        state.GameDir = NullIfBlank(GameDir);
        state.PicsDir = NullIfBlank(PicsDir);
        state.Repo = string.IsNullOrWhiteSpace(Repo) ? InstallOptions.DefaultRepo : Repo.Trim();
        state.InjectionMode = SelectedInjectionMode.Mode;
        state.DeveloperMode = DeveloperMode;
        state.Language = Loc.LanguageCode;
        _engine.Repository.SaveState(state);
    }

    private void CancelOperation()
    {
        _cts?.Cancel();
        StatusText = Loc.T("正在取消...", "Canceling...");
    }

    private async Task CopyLogsAsync()
    {
        if (Logs.Count == 0) return;
        await _dialogService.CopyTextAsync(string.Join(Environment.NewLine, Logs.Select(l => l.FullText)));
        StatusText = Loc.T("日志已复制到剪贴板", "Logs copied to clipboard");
    }

    private void AppendLog(LogLevel level, string message) => Logs.Add(new LogItem(level, message));

    private void AppendLogUiSafe(LogLevel level, string message)
    {
        if (Dispatcher.UIThread.CheckAccess()) AppendLog(level, message);
        else Dispatcher.UIThread.Post(() => AppendLog(level, message));
    }

    private void UpdateProgress(string status, double? percent)
    {
        StatusText = status;
        if (percent is { } value)
        {
            IsProgressIndeterminate = false;
            ProgressValue = value;
        }
        else
        {
            IsProgressIndeterminate = true;
        }
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Dispose()
    {
        StopHotReloadIfRunning();
        _cts?.Dispose();
    }
}
