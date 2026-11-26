using System.ComponentModel;

namespace YGORogueDuelistCardModInstaller.Avalonia.Services;

/// <summary>
/// AOT-safe bilingual (Chinese / English) localization service bound by Avalonia compiled bindings.
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    private bool _isEnglish;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsEnglish
    {
        get => _isEnglish;
        set
        {
            if (_isEnglish == value) return;
            _isEnglish = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }

    public string LanguageCode => _isEnglish ? "en" : "zh";

    public void SetLanguage(string? code) =>
        IsEnglish = string.Equals(code, "en", StringComparison.OrdinalIgnoreCase);

    public void Toggle() => IsEnglish = !IsEnglish;

    public string T(string zh, string en) => _isEnglish ? en : zh;

    // Header & primary actions
    public string WindowTitle => T("Yu-Gi-Oh! Rogue Duelist MOD 管理器", "Yu-Gi-Oh! Rogue Duelist MOD Manager");
    public string LangToggleText => _isEnglish ? "中文" : "EN";
    public string LangToggleTooltip => T("切换界面语言为英文 (Switch to English)", "Switch UI language to Chinese (切换中文)");
    public string ApplyButton => T("应用注入", "Apply MODs");
    public string ApplyAndLaunchButton => T("注入并启动游戏", "Apply & Launch");
    public string RestoreButton => T("还原纯净版", "Restore Vanilla");
    public string InjectionModeLabel => T("注入方案：", "Injection Mode:");
    public string DeveloperModeLabel => T("开发者模式 (F10)", "Dev Mode (F10)");
    public string DeveloperModeTooltip => T(
        "开启后注入内置 F10 开发者调试面板、window.YgoModDev 控制台工具，并自动解锁沙盒自由决斗与形态切换。",
        "Enables in-game F10 Dev Inspector, window.YgoModDev API, and unlocks Sandbox Free Duel & character variants.");

    // Multi-Workspace bar
    public string WorkspaceLabel => T("工作区：", "Workspace:");
    public string NewWorkspaceButton => T("+ 工作区", "+ Workspace");
    public string RemoveWorkspaceTooltip => T("从多工作区列表中移除当前项（不删除磁盘文件）", "Remove from workspace list (keeps files on disk)");
    public string OpenWorkspaceTooltip => T("在资源管理器中打开当前工作区", "Open current workspace in Explorer");

    // Hot-Reload bar
    public string StartHotReloadButton => T("🔥 启动热加载", "🔥 Start Hot-Reload");
    public string StopHotReloadButton => T("■ 停止热加载", "■ Stop Hot-Reload");
    public string InjectHotReloadButton => T("注入热加载桥", "Inject Hot-Reload Bridge");
    public string InjectHotReloadTooltip => T(
        "将热加载桥接脚本写入游戏虚拟包，之后在桌面游戏内无需重启即可实时刷新 MOD 改动",
        "Injects the hot-reload bridge into the game virtual bundle so the desktop game live-reloads on every save.");
    public string OpenLivePreviewButton => T("浏览器热预览", "Live Browser Preview");
    public string OpenLivePreviewTooltip => T(
        "直接在浏览器中打开实时热加载预览（http://127.0.0.1:<port>/），改完即看效果",
        "Open live hot-reload preview directly in your browser (http://127.0.0.1:<port>/).");
    public string TriggerHotReloadButton => T("立即刷新", "Reload Now");

    // Tabs
    public string TabMods => T("MOD 列表与加载顺序", "MOD Library & Load Order");
    public string TabStudio => T("可视化 MOD 创作工作台", "Visual MOD Studio");
    public string TabSettings => T("游戏路径与更新设置", "Paths, Updates & Export");

    // Toolbar
    public string ImportZipButton => T("+ 导入 ZIP / 整合包", "+ Import ZIP / Pack");
    public string ImportFolderButton => T("+ 导入文件夹", "+ Import Folder");
    public string CreateTemplateButton => T("新建 MOD 模板", "New MOD Template");
    public string PackWorkspaceButton => T("打包工作区", "Pack Workspace");
    public string OpenModsFolderButton => T("打开 mods 目录", "Open mods/");
    public string EnableAllButton => T("全选", "Enable All");
    public string DisableAllButton => T("全不选", "Disable All");
    public string RefreshButton => T("刷新", "Refresh");

    // MOD list & details
    public string LoadOrderHint => T(
        "加载顺序（从上到下依次叠加，靠后的 MOD 优先级更高）",
        "Load order (applied top to bottom; lower items take higher priority)");
    public string ApplyHint => T(
        "勾选即启用 · 需点「应用注入」或开启「热加载」",
        "Check to enable · Click 'Apply MODs' or start Hot-Reload");
    public string ContentsLabel => T("包含内容", "Contents");
    public string OverridesLabel => T("覆盖/新增文件 (overrides/)", "Overrides / Added Files (overrides/)");
    public string OpenModFolderButton => T("打开目录", "Open Folder");
    public string PackThisModButton => T("打包为 .zip", "Pack .zip");
    public string EditInStudioButton => T("在工作台编辑", "Edit in Studio");
    public string VersionIssuesHeader => T("版本兼容性告警", "Version Compatibility Warnings");

    // Visual MOD Studio labels
    public string StudioTargetModLabel => T("当前编辑 MOD：", "Active MOD:");
    public string StudioQuickNewMod => T("+ 新建 MOD", "+ New MOD");
    public string StudioValidateButton => T("校验 MOD", "Validate");
    public string StudioScaffoldDslButton => T("生成 mod.lua/js", "Scaffold DSL");
    public string StudioFilesHeader => T("MOD 文件浏览（点击编辑）", "MOD Files (Click to Edit)");
    public string StudioNoModHint => T(
        "请先在上方选择一个 MOD，或点击「+ 新建 MOD」开始零基础可视化创作！",
        "Select a MOD above or click '+ New MOD' to start visual MOD authoring!");

    public string StudioTabCard => T("卡牌工坊", "Card Builder");
    public string StudioTabCharacter => T("角色工坊", "Character");
    public string StudioTabRelic => T("遗物工坊", "Relic");
    public string StudioTabPackDeck => T("卡包与构筑", "Packs & Decks");
    public string StudioTabTrainer => T("修改器", "Trainer");
    public string StudioTabCode => T("代码与 DSL", "Code & DSL");

    public string StudioAutoId => T("自动分配 ID", "Auto ID");
    public string StudioPickArt => T("选择图片...", "Pick Art...");
    public string StudioFillLua => T("填充 Lua 模板", "Fill Lua Template");
    public string StudioSaveCard => T("保存卡牌 & Lua 脚本", "Save Card & Lua");
    public string StudioSaveCharacter => T("保存角色", "Save Character");
    public string StudioSaveRelic => T("保存遗物", "Save Relic");
    public string StudioSavePack => T("保存卡包", "Save Pack");
    public string StudioSaveDeck => T("保存预组卡组", "Save Precon Deck");
    public string StudioImportYdk => T("导入 YGOPro .ydk 文件", "Import .ydk Deck");
    public string StudioSaveTrainer => T("保存 trainer.json", "Save trainer.json");
    public string StudioSaveCode => T("保存文件（自动触发热加载）", "Save File (Triggers Hot-Reload)");

    // Settings tab
    public string WorkspaceDirLabel => T("当前工作区目录", "Active Workspace Path");
    public string SwitchDirButton => T("切换目录...", "Switch...");
    public string InstallDirLabel => T("游戏安装目录", "Game Install Dir");
    public string InstallDirWatermark => T("含 Yu-Gi-Oh! Rogue Duelist.dll 与 web-X.Y.Z.zip", "Contains Yu-Gi-Oh! Rogue Duelist.dll & web-X.Y.Z.zip");
    public string GameDirLabel => T("游戏数据根目录", "Game Data Dir");
    public string PicsDirLabel => T("备用卡图目录 (可选)", "Fallback Art Dir (Opt.)");
    public string PicsDirWatermark => T("YGOPro\\pics（MOD 未自带卡图时的后备来源）", "YGOPro\\pics (fallback if MOD has no inline art)");
    public string RepoLabel => T("官方 GitHub 仓库", "Official GitHub Repo");
    public string BrowseButton => T("浏览...", "Browse...");
    public string SaveSettingsButton => T("保存设置", "Save");
    public string UpdateGameButton => T("检查并更新原版游戏", "Check & Update Game");
    public string LaunchGameButton => T("直接启动游戏", "Launch Game");
    public string BuildOnlyDesc => T(
        "测试导出（BuildOnly）：将当前已启用的全部 MOD 合并导出到指定目录，不修改游戏任何文件",
        "BuildOnly Export: Compose all enabled MODs into a standalone output folder without touching game files.");
    public string BuildOnlyDirLabel => T("导出目标目录", "Export Output Dir");
    public string ExportBuildOnlyButton => T("导出测试文件", "Export BuildOnly");

    // Footer & logs
    public string CancelButton => T("取消", "Cancel");
    public string LogsHeader => T("运行日志", "Console Log");
    public string CopyLogsButton => T("复制日志", "Copy Logs");
    public string ClearLogsButton => T("清空", "Clear");
}
