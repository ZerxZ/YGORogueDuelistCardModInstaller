using YGORogueDuelistCardModInstaller.Avalonia.Services;
using YGORogueDuelistCardModInstaller.Core.ModManager;

namespace YGORogueDuelistCardModInstaller.Avalonia.ViewModels;

public sealed class ModItemViewModel : ViewModelBase
{
    private readonly Action _onToggled;
    private readonly LocalizationService _loc;
    private bool _enabled;
    private int _order;
    private bool _hasConflict;
    private string _conflictTooltip = string.Empty;
    private bool _hasVersionIssue;
    private string _versionIssueTooltip = string.Empty;

    public ModItemViewModel(
        ManagedMod model,
        LocalizationService loc,
        Action onToggled,
        Action<ModItemViewModel> onMoveUp,
        Action<ModItemViewModel> onMoveDown,
        Func<ModItemViewModel, Task> onDelete,
        Action<ModItemViewModel> onOpenFolder,
        Func<ModItemViewModel, Task> onPack,
        Func<bool> isIdle)
    {
        Model = model;
        _loc = loc;
        _onToggled = onToggled;
        _enabled = model.Enabled;
        _order = model.Order;
        TagsText = model.Manifest.Tags.Count > 0 ? string.Join(" · ", model.Manifest.Tags) : "mod";

        MoveUpCommand = new RelayCommand(() => onMoveUp(this), isIdle);
        MoveDownCommand = new RelayCommand(() => onMoveDown(this), isIdle);
        DeleteCommand = new AsyncRelayCommand(() => onDelete(this), isIdle);
        OpenFolderCommand = new RelayCommand(() => onOpenFolder(this));
        PackCommand = new AsyncRelayCommand(() => onPack(this), isIdle);
    }

    public ManagedMod Model { get; }

    public string Id => Model.Id;
    public string Name => Model.Manifest.Name;
    public string Version => Model.Manifest.Version;
    public string Author => string.IsNullOrWhiteSpace(Model.Manifest.Author) ? _loc.T("未知", "Unknown") : Model.Manifest.Author;
    public string AuthorDisplay => _loc.T($"作者：{Author}", $"Author: {Author}");
    public string Description => string.IsNullOrWhiteSpace(Model.Manifest.Description)
        ? _loc.T("（暂无说明）", "(No description)")
        : Model.Manifest.Description;

    public string GameVersionDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Model.Manifest.MinGameVersion))
            {
                return !string.IsNullOrWhiteSpace(Model.Manifest.MaxGameVersion)
                    ? $"v{Model.Manifest.MinGameVersion}..v{Model.Manifest.MaxGameVersion}"
                    : $"≥ v{Model.Manifest.MinGameVersion}";
            }

            return string.IsNullOrWhiteSpace(Model.Manifest.GameVersion)
                ? _loc.T("通用", "Any")
                : $"v{Model.Manifest.GameVersion}";
        }
    }

    public string VersionBadgeText => _loc.T($"版本: v{Version}", $"Ver: v{Version}");
    public string CompatBadgeText => _loc.T($"适配: {GameVersionDisplay}", $"Game: {GameVersionDisplay}");
    public string DirectoryPath => Model.DirectoryPath;
    public string SummaryText => BuildSummary(Model.Stats, _loc);
    public string TagsText { get; }
    public IReadOnlyList<string> OverridePaths => Model.Stats.OverrideRelativePaths;
    public bool HasOverrides => Model.Stats.OverrideFileCount > 0;

    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public AsyncRelayCommand PackCommand { get; }

    public void NotifyIdleChanged()
    {
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        PackCommand.NotifyCanExecuteChanged();
    }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Author));
        OnPropertyChanged(nameof(AuthorDisplay));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(GameVersionDisplay));
        OnPropertyChanged(nameof(VersionBadgeText));
        OnPropertyChanged(nameof(CompatBadgeText));
        OnPropertyChanged(nameof(SummaryText));
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                Model.Enabled = value;
                _onToggled();
            }
        }
    }

    public int Order
    {
        get => _order;
        set
        {
            if (SetProperty(ref _order, value))
            {
                Model.Order = value;
                OnPropertyChanged(nameof(OrderBadge));
            }
        }
    }

    public string OrderBadge => $"#{Order + 1}";

    public bool HasConflict
    {
        get => _hasConflict;
        set => SetProperty(ref _hasConflict, value);
    }

    public string ConflictTooltip
    {
        get => _conflictTooltip;
        set => SetProperty(ref _conflictTooltip, value);
    }

    public bool HasVersionIssue
    {
        get => _hasVersionIssue;
        set => SetProperty(ref _hasVersionIssue, value);
    }

    public string VersionIssueTooltip
    {
        get => _versionIssueTooltip;
        set => SetProperty(ref _versionIssueTooltip, value);
    }

    public void SetEnabledSilent(bool value)
    {
        if (SetProperty(ref _enabled, value, nameof(Enabled)))
        {
            Model.Enabled = value;
        }
    }

    private static string BuildSummary(ModContentStats s, LocalizationService loc)
    {
        var parts = new List<string>();
        if (s.HasLuaDsl) parts.Add("mod.lua");
        if (s.HasJsDsl) parts.Add("mod.js");
        if (s.CardCount > 0) parts.Add(loc.T($"{s.CardCount} 张卡", $"{s.CardCount} cards"));
        if (s.CharacterCount > 0) parts.Add(loc.T($"{s.CharacterCount} 角色", $"{s.CharacterCount} chars"));
        if (s.RelicCount > 0) parts.Add(loc.T($"{s.RelicCount} 遗物", $"{s.RelicCount} relics"));
        if (s.PackCount > 0) parts.Add(loc.T($"{s.PackCount} 卡包", $"{s.PackCount} packs"));
        if (s.PreconstructedDeckCount > 0) parts.Add(loc.T($"{s.PreconstructedDeckCount} 预组", $"{s.PreconstructedDeckCount} precon"));
        if (s.ScriptCount > 0) parts.Add(loc.T($"{s.ScriptCount} Lua脚本", $"{s.ScriptCount} Lua"));
        if (s.LuaLibCount > 0) parts.Add(loc.T($"{s.LuaLibCount} Lua库", $"{s.LuaLibCount} Lua lib"));
        if (s.JsPluginCount > 0) parts.Add(loc.T($"{s.JsPluginCount} JS插件", $"{s.JsPluginCount} JS"));
        if (s.CssStyleCount > 0) parts.Add(loc.T($"{s.CssStyleCount} CSS", $"{s.CssStyleCount} CSS"));
        if (s.DeckCount > 0) parts.Add(loc.T($"{s.DeckCount} 起始卡组", $"{s.DeckCount} decks"));
        if (s.ArtCount > 0) parts.Add(loc.T($"{s.ArtCount} 卡图", $"{s.ArtCount} art"));
        if (s.CampaignFileCount > 0) parts.Add(loc.T($"{s.CampaignFileCount} 战役", $"{s.CampaignFileCount} campaign"));
        if (s.HasTrainer) parts.Add(loc.T("修改器", "Trainer"));
        if (s.OverrideFileCount > 0) parts.Add(loc.T($"{s.OverrideFileCount} 覆盖", $"{s.OverrideFileCount} overrides"));
        return parts.Count > 0 ? string.Join(" · ", parts) : loc.T("空模板（可放入文件）", "Empty template");
    }
}

public sealed class ConflictItemViewModel(ModConflict conflict, LocalizationService loc)
{
    public string Category => conflict.Category;
    public string ResourceKey => conflict.ResourceKey;
    public string ChainText => string.Join(" → ", conflict.ModIds);
    public string WinnerText => loc.T($"生效：{conflict.WinnerModId}", $"Winner: {conflict.WinnerModId}");
}

public sealed class VersionIssueItemViewModel(ModVersionIssue issue)
{
    public string ModId => issue.ModId;
    public string ModName => issue.ModName;
    public string Message => issue.Message;
}

public sealed record InjectionModeOption(InjectionMode Mode, string DisplayName, string Description);
