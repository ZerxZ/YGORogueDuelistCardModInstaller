using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using YGORogueDuelistCardModInstaller.Avalonia.Services;
using YGORogueDuelistCardModInstaller.Core;
using YGORogueDuelistCardModInstaller.Core.ModManager;

namespace YGORogueDuelistCardModInstaller.Avalonia.ViewModels;

public sealed class VanillaTileItem : ViewModelBase
{
    private Bitmap? _thumbnail;

    public required VanillaCategory Category { get; init; }
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string Badge { get; init; }
    public string? ImageZipPath { get; init; }
    public object? RawModel { get; init; }

    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }
}

public sealed class VanillaMiniCardItem : ViewModelBase
{
    private Bitmap? _thumbnail;

    public required int Id { get; init; }
    public required string DisplayName { get; init; }
    public string? ImageZipPath { get; init; }

    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }
}

/// <summary>
/// Offline visual reference gallery for vanilla Yu-Gi-Oh! Rogue Duelist Cards, Characters,
/// Relics, Booster Packs, and Preconstructed Decks with real artwork thumbnails and 1-click Clone to MOD Studio.
/// </summary>
public sealed class VanillaBrowserViewModel : ViewModelBase, IDisposable
{
    private const int PageSize = 60;

    private readonly Func<ModInjectionEngine> _getEngine;
    private readonly ModStudioViewModel _studio;
    private readonly Action<int> _switchToMainTab;
    private readonly Action<LogLevel, string> _log;
    private readonly Func<string?, Task> _onModMutated;
    private readonly ConcurrentDictionary<string, Bitmap> _bitmapCache = new(StringComparer.OrdinalIgnoreCase);

    private VanillaCatalogSnapshot? _catalog;
    private VanillaCategory _activeCategory = VanillaCategory.Cards;
    private string _searchQuery = string.Empty;
    private int _visibleLimit = PageSize;
    private bool _isLoading;
    private string _statusMessage = string.Empty;
    private VanillaTileItem? _selectedItem;
    private Bitmap? _selectedDetailImage;
    private string _detailTitle = string.Empty;
    private string _detailSubtitle = string.Empty;
    private string _detailDescription = string.Empty;
    private string _detailMeta = string.Empty;
    private string _detailLuaScript = string.Empty;
    private bool _hasDetailLua;
    private bool _hasDetailSubCards;
    private string _detailSubCardsHeader = string.Empty;
    private bool _keepVanillaIdentity;
    private int _totalMatches;

    public VanillaBrowserViewModel(
        LocalizationService loc,
        Func<ModInjectionEngine> getEngine,
        ModStudioViewModel studio,
        Action<int> switchToMainTab,
        Action<LogLevel, string> log,
        Func<string?, Task> onModMutated)
    {
        Loc = loc;
        _getEngine = getEngine;
        _studio = studio;
        _switchToMainTab = switchToMainTab;
        _log = log;
        _onModMutated = onModMutated;

        LoadCatalogCommand = new AsyncRelayCommand(LoadCatalogAsync, () => !IsLoading);
        SelectCardsCommand = new RelayCommand(() => SwitchCategory(VanillaCategory.Cards));
        SelectCharactersCommand = new RelayCommand(() => SwitchCategory(VanillaCategory.Characters));
        SelectRelicsCommand = new RelayCommand(() => SwitchCategory(VanillaCategory.Relics));
        SelectPacksCommand = new RelayCommand(() => SwitchCategory(VanillaCategory.Packs));
        SelectDecksCommand = new RelayCommand(() => SwitchCategory(VanillaCategory.PreconstructedDecks));
        LoadMoreCommand = new RelayCommand(LoadMore, () => HasMoreItems);
        CloneToStudioCommand = new AsyncRelayCommand(CloneSelectedToStudioAsync, () => SelectedItem is not null);
        QuickCloneToModCommand = new AsyncRelayCommand(QuickCloneSelectedToModAsync, () => SelectedItem is not null && _studio.SelectedMod is not null);
    }

    public LocalizationService Loc { get; }
    public ObservableCollection<VanillaTileItem> Items { get; } = [];
    public ObservableCollection<VanillaMiniCardItem> DetailSubCards { get; } = [];

    public bool IsCatalogLoaded => _catalog is not null;
    public bool IsCatalogNotLoaded => _catalog is null;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value)) LoadCatalogCommand.NotifyCanExecuteChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                _visibleLimit = PageSize;
                RebuildFilteredItems();
            }
        }
    }

    public bool KeepVanillaIdentity
    {
        get => _keepVanillaIdentity;
        set => SetProperty(ref _keepVanillaIdentity, value);
    }

    public VanillaTileItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                OnPropertyChanged(nameof(HasSelectedItem));
                CloneToStudioCommand.NotifyCanExecuteChanged();
                QuickCloneToModCommand.NotifyCanExecuteChanged();
                _ = PopulateDetailAsync(value);
            }
        }
    }

    public bool HasSelectedItem => SelectedItem is not null;
    public bool HasMoreItems => Items.Count < _totalMatches;
    public string ResultCountText => _catalog is null
        ? string.Empty
        : Loc.T($"显示 {Items.Count} / 共 {_totalMatches} 项（原版 v{_catalog.Version}）", $"Showing {Items.Count} / {_totalMatches} (Vanilla v{_catalog.Version})");

    public string CardsCategoryLabel => _catalog is null ? Loc.T("🃏 卡牌", "🃏 Cards") : Loc.T($"🃏 卡牌 ({_catalog.Cards.Count})", $"🃏 Cards ({_catalog.Cards.Count})");
    public string CharactersCategoryLabel => _catalog is null ? Loc.T("🧙 角色", "🧙 Characters") : Loc.T($"🧙 角色 ({_catalog.Characters.Count})", $"🧙 Characters ({_catalog.Characters.Count})");
    public string RelicsCategoryLabel => _catalog is null ? Loc.T("🏺 遗物", "🏺 Relics") : Loc.T($"🏺 遗物 ({_catalog.Relics.Count})", $"🏺 Relics ({_catalog.Relics.Count})");
    public string PacksCategoryLabel => _catalog is null ? Loc.T("📦 卡包", "📦 Packs") : Loc.T($"📦 卡包 ({_catalog.Packs.Count})", $"📦 Packs ({_catalog.Packs.Count})");
    public string DecksCategoryLabel => _catalog is null ? Loc.T("🎴 预组卡组", "🎴 Decks") : Loc.T($"🎴 预组 ({_catalog.PreconstructedDecks.Count})", $"🎴 Decks ({_catalog.PreconstructedDecks.Count})");

    public Bitmap? SelectedDetailImage { get => _selectedDetailImage; private set => SetProperty(ref _selectedDetailImage, value); }
    public string DetailTitle { get => _detailTitle; private set => SetProperty(ref _detailTitle, value); }
    public string DetailSubtitle { get => _detailSubtitle; private set => SetProperty(ref _detailSubtitle, value); }
    public string DetailMeta { get => _detailMeta; private set => SetProperty(ref _detailMeta, value); }
    public string DetailDescription { get => _detailDescription; private set => SetProperty(ref _detailDescription, value); }
    public string DetailLuaScript { get => _detailLuaScript; private set => SetProperty(ref _detailLuaScript, value); }
    public bool HasDetailLua { get => _hasDetailLua; private set => SetProperty(ref _hasDetailLua, value); }
    public bool HasDetailSubCards { get => _hasDetailSubCards; private set => SetProperty(ref _hasDetailSubCards, value); }
    public string DetailSubCardsHeader { get => _detailSubCardsHeader; private set => SetProperty(ref _detailSubCardsHeader, value); }

    public AsyncRelayCommand LoadCatalogCommand { get; }
    public RelayCommand SelectCardsCommand { get; }
    public RelayCommand SelectCharactersCommand { get; }
    public RelayCommand SelectRelicsCommand { get; }
    public RelayCommand SelectPacksCommand { get; }
    public RelayCommand SelectDecksCommand { get; }
    public RelayCommand LoadMoreCommand { get; }
    public AsyncRelayCommand CloneToStudioCommand { get; }
    public AsyncRelayCommand QuickCloneToModCommand { get; }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(CardsCategoryLabel));
        OnPropertyChanged(nameof(CharactersCategoryLabel));
        OnPropertyChanged(nameof(RelicsCategoryLabel));
        OnPropertyChanged(nameof(PacksCategoryLabel));
        OnPropertyChanged(nameof(DecksCategoryLabel));
        OnPropertyChanged(nameof(ResultCountText));
    }

    public void NotifyActiveModChanged() => QuickCloneToModCommand.NotifyCanExecuteChanged();

    public async Task EnsureLoadedAsync()
    {
        if (_catalog is null && !IsLoading)
        {
            await LoadCatalogAsync();
        }
    }

    public async Task LoadCatalogAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        StatusMessage = Loc.T("正在从原版游戏数据包加载图鉴与资源索引...", "Loading vanilla catalog & artwork index...");

        try
        {
            var engine = _getEngine();
            var status = engine.GetStatus();
            var vanilla = ModInjectionEngine.FindBestVanillaSource(status.InstallDir, status.GameBaseDir, status.LauncherMeta)
                ?? throw new ModOperationException(Loc.T(
                    "未检测到原版游戏 web-X.Y.Z.zip，请先在「游戏路径与更新设置」中指定游戏安装目录。",
                    "Could not find vanilla web-X.Y.Z.zip. Please set Game Install Dir in Settings first."));

            var snapshot = await Task.Run(() => VanillaContentCatalog.Load(vanilla.ZipPath, vanilla.Version));
            _catalog = snapshot;
            OnPropertyChanged(nameof(IsCatalogLoaded));
            OnPropertyChanged(nameof(IsCatalogNotLoaded));
            RefreshLocalization();
            _visibleLimit = PageSize;
            RebuildFilteredItems();
            StatusMessage = Loc.T(
                $"已加载原版 v{snapshot.Version}：{snapshot.Cards.Count} 卡 / {snapshot.Characters.Count} 角色 / {snapshot.Relics.Count} 遗物 / {snapshot.Packs.Count} 卡包 / {snapshot.PreconstructedDecks.Count} 预组",
                $"Loaded Vanilla v{snapshot.Version}: {snapshot.Cards.Count} cards / {snapshot.Characters.Count} chars / {snapshot.Relics.Count} relics / {snapshot.Packs.Count} packs / {snapshot.PreconstructedDecks.Count} decks");
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            _log(LogLevel.Error, ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void SwitchCategory(VanillaCategory cat)
    {
        _activeCategory = cat;
        _visibleLimit = PageSize;
        RebuildFilteredItems();
    }

    private void LoadMore()
    {
        _visibleLimit += PageSize;
        RebuildFilteredItems();
    }

    private void RebuildFilteredItems()
    {
        Items.Clear();
        if (_catalog is null) return;

        var q = _searchQuery.Trim();
        var matches = new List<VanillaTileItem>();

        switch (_activeCategory)
        {
            case VanillaCategory.Cards:
                foreach (var c in _catalog.Cards)
                {
                    if (!Matches(q, c.Name, c.ZhName, c.Id.ToString(CultureInfo.InvariantCulture), c.DisplayType, c.Race, c.Attribute)) continue;
                    matches.Add(new VanillaTileItem
                    {
                        Category = VanillaCategory.Cards,
                        Key = c.Id.ToString(CultureInfo.InvariantCulture),
                        Title = string.IsNullOrWhiteSpace(c.ZhName) ? c.Name : c.ZhName,
                        Subtitle = c.Name,
                        Badge = c.Level > 0 ? $"Lv{c.Level} {c.Atk}/{c.Def}" : c.DisplayType,
                        ImageZipPath = c.ThumbZipPath ?? c.ImageZipPath,
                        RawModel = c
                    });
                }
                break;

            case VanillaCategory.Characters:
                foreach (var ch in _catalog.Characters)
                {
                    if (!Matches(q, ch.Name, ch.ZhName, ch.Index.ToString(CultureInfo.InvariantCulture), ch.Race, ch.Attribute, ch.StartingRelic)) continue;
                    matches.Add(new VanillaTileItem
                    {
                        Category = VanillaCategory.Characters,
                        Key = ch.Index.ToString(CultureInfo.InvariantCulture),
                        Title = string.IsNullOrWhiteSpace(ch.ZhName) ? ch.Name : ch.ZhName,
                        Subtitle = $"#{ch.Index} · {ch.Name}",
                        Badge = ch.IsPlayable ? Loc.T($"可选 · {ch.Cards.Count}卡", $"Playable · {ch.Cards.Count}") : $"NPC #{ch.Index}",
                        ImageZipPath = ch.ImageZipPath,
                        RawModel = ch
                    });
                }
                break;

            case VanillaCategory.Relics:
                foreach (var r in _catalog.Relics)
                {
                    if (!Matches(q, r.Key, r.Name, r.ZhName, r.Tag, r.Description, r.ZhDescription)) continue;
                    matches.Add(new VanillaTileItem
                    {
                        Category = VanillaCategory.Relics,
                        Key = r.Key,
                        Title = string.IsNullOrWhiteSpace(r.ZhName) ? r.Name : r.ZhName,
                        Subtitle = r.Key,
                        Badge = $"{r.Cost}G · {r.Tag}",
                        ImageZipPath = r.ImageZipPath,
                        RawModel = r
                    });
                }
                break;

            case VanillaCategory.Packs:
                foreach (var p in _catalog.Packs)
                {
                    if (!Matches(q, p.Id, p.Name, p.ZhName, p.Code, p.Region)) continue;
                    matches.Add(new VanillaTileItem
                    {
                        Category = VanillaCategory.Packs,
                        Key = p.Id,
                        Title = string.IsNullOrWhiteSpace(p.ZhName) ? p.Name : p.ZhName,
                        Subtitle = p.Id,
                        Badge = Loc.T($"{p.TotalCardCount} 张卡", $"{p.TotalCardCount} cards"),
                        ImageZipPath = p.ImageZipPath,
                        RawModel = p
                    });
                }
                break;

            case VanillaCategory.PreconstructedDecks:
                foreach (var d in _catalog.PreconstructedDecks)
                {
                    if (!Matches(q, d.Id, d.Name, d.ZhName, d.Art)) continue;
                    matches.Add(new VanillaTileItem
                    {
                        Category = VanillaCategory.PreconstructedDecks,
                        Key = d.Id,
                        Title = string.IsNullOrWhiteSpace(d.ZhName) ? d.Name : d.ZhName,
                        Subtitle = $"{d.Id} ({d.Art})",
                        Badge = Loc.T($"{d.CardIds.Count} 张卡", $"{d.CardIds.Count} cards"),
                        ImageZipPath = d.ImageZipPath,
                        RawModel = d
                    });
                }
                break;
        }

        _totalMatches = matches.Count;
        var batch = matches.Take(_visibleLimit).ToList();
        foreach (var item in batch) Items.Add(item);

        SelectedItem = Items.FirstOrDefault();
        OnPropertyChanged(nameof(HasMoreItems));
        OnPropertyChanged(nameof(ResultCountText));
        LoadMoreCommand.NotifyCanExecuteChanged();

        _ = LoadThumbnailsBatchAsync(batch, _catalog.ZipPath);
    }

    private async Task LoadThumbnailsBatchAsync(List<VanillaTileItem> tiles, string zipPath)
    {
        await Task.Run(() =>
        {
            foreach (var tile in tiles)
            {
                if (string.IsNullOrWhiteSpace(tile.ImageZipPath)) continue;
                var bmp = GetOrDecodeBitmap(zipPath, tile.ImageZipPath, decodeWidth: 160);
                if (bmp is not null)
                {
                    Dispatcher.UIThread.Post(() => tile.Thumbnail = bmp, DispatcherPriority.Background);
                }
            }
        });
    }

    private async Task PopulateDetailAsync(VanillaTileItem? item)
    {
        DetailSubCards.Clear();
        HasDetailSubCards = false;
        HasDetailLua = false;
        DetailLuaScript = string.Empty;
        SelectedDetailImage = null;

        if (item is null || _catalog is null)
        {
            DetailTitle = string.Empty;
            DetailSubtitle = string.Empty;
            DetailMeta = string.Empty;
            DetailDescription = string.Empty;
            return;
        }

        var zipPath = _catalog.ZipPath;
        string? hiResPath = item.ImageZipPath;
        var miniCards = new List<VanillaMiniCardItem>();

        switch (item.RawModel)
        {
            case VanillaCardItem c:
                hiResPath = c.ImageZipPath ?? c.ThumbZipPath;
                DetailTitle = string.IsNullOrWhiteSpace(c.ZhName) ? c.Name : $"{c.ZhName}  /  {c.Name}";
                DetailSubtitle = $"ID: {c.Id}  ·  {c.DisplayType}  ·  {c.Race}" + (string.IsNullOrWhiteSpace(c.Attribute) ? "" : $"  ·  {c.Attribute}");
                DetailMeta = c.Level > 0
                    ? $"Level {c.Level}  |  ATK {c.Atk} / DEF {c.Def}  |  mask(type={c.TypeMask}, race=\"{c.RaceMask}\", attr={c.AttributeMask})"
                    : $"mask(type={c.TypeMask}, race=\"{c.RaceMask}\", attr={c.AttributeMask})";
                DetailDescription = string.IsNullOrWhiteSpace(c.ZhDesc) ? c.Desc : $"{c.ZhDesc}\n\n{c.Desc}";

                var lua = await Task.Run(() => VanillaContentCatalog.ReadCardLuaScript(zipPath, c.Id));
                if (!string.IsNullOrWhiteSpace(lua))
                {
                    DetailLuaScript = lua;
                    HasDetailLua = true;
                }
                break;

            case VanillaCharacterItem ch:
                DetailTitle = string.IsNullOrWhiteSpace(ch.ZhName) ? ch.Name : $"{ch.ZhName}  /  {ch.Name}";
                DetailSubtitle = $"Index #{ch.Index}  ·  Sprite: {ch.Sprite}  ·  {ch.Race} / {ch.Attribute}";
                DetailMeta = $"Starting Relic: {ch.StartingRelic}  |  Packs: {string.Join(", ", ch.StartingPacks)}";
                DetailDescription = $"Theme: {ch.Theme}\nCards ({ch.Cards.Count}): {string.Join(", ", ch.Cards)}";
                DetailSubCardsHeader = Loc.T($"角色起始卡组预览（共 {ch.Cards.Count} 张）", $"Character Deck Preview ({ch.Cards.Count} cards)");
                foreach (var cname in ch.Cards.Take(24))
                {
                    if (_catalog.CardByName.TryGetValue(cname, out var found))
                    {
                        miniCards.Add(new VanillaMiniCardItem
                        {
                            Id = found.Id,
                            DisplayName = string.IsNullOrWhiteSpace(found.ZhName) ? found.Name : found.ZhName,
                            ImageZipPath = found.ThumbZipPath ?? found.ImageZipPath
                        });
                    }
                }
                break;

            case VanillaRelicItem r:
                DetailTitle = string.IsNullOrWhiteSpace(r.ZhName) ? r.Name : $"{r.ZhName}  /  {r.Name}";
                DetailSubtitle = $"Key: {r.Key}  ·  Cost: {r.Cost}G  ·  Tag: {r.Tag}";
                DetailMeta = $"Art: {r.Art}  |  Effect: {r.Effect}";
                DetailDescription = string.IsNullOrWhiteSpace(r.ZhDescription) ? r.Description : $"{r.ZhDescription}\n\n{r.Description}";
                break;

            case VanillaPackItem p:
                DetailTitle = string.IsNullOrWhiteSpace(p.ZhName) ? p.Name : $"{p.ZhName}  /  {p.Name}";
                DetailSubtitle = $"ID: {p.Id}  ·  Code: {p.Code}  ·  Region: {p.Region}  ·  Size: {p.Size}";
                DetailMeta = $"Common: {p.CommonIds.Count}  |  Rare: {p.RareIds.Count}  |  Super: {p.SuperIds.Count}  |  Ultra: {p.UltraIds.Count}";
                DetailDescription = $"Release Date: {p.Date}\nTotal Pool: {p.TotalCardCount} cards";
                DetailSubCardsHeader = Loc.T($"卡包内含卡牌预览（前 24 张 / 共 {p.TotalCardCount} 张）", $"Pack Cards Preview (first 24 / {p.TotalCardCount})");
                foreach (var cid in p.UltraIds.Concat(p.SuperIds).Concat(p.RareIds).Concat(p.CommonIds).Distinct().Take(24))
                {
                    if (_catalog.CardById.TryGetValue(cid, out var found))
                    {
                        miniCards.Add(new VanillaMiniCardItem
                        {
                            Id = found.Id,
                            DisplayName = string.IsNullOrWhiteSpace(found.ZhName) ? found.Name : found.ZhName,
                            ImageZipPath = found.ThumbZipPath ?? found.ImageZipPath
                        });
                    }
                }
                break;

            case VanillaPreconstructedDeckItem d:
                DetailTitle = string.IsNullOrWhiteSpace(d.ZhName) ? d.Name : $"{d.ZhName}  /  {d.Name}";
                DetailSubtitle = $"ID: {d.Id}  ·  Art: {d.Art}  ·  Date: {d.Date}";
                DetailMeta = $"Cards: {d.CardIds.Count}";
                DetailDescription = string.Join(", ", d.CardIds.Take(40));
                DetailSubCardsHeader = Loc.T($"预组卡组卡牌预览（前 24 张 / 共 {d.CardIds.Count} 张）", $"Preconstructed Deck Cards (first 24 / {d.CardIds.Count})");
                foreach (var cid in d.CardIds.Take(24))
                {
                    if (_catalog.CardById.TryGetValue(cid, out var found))
                    {
                        miniCards.Add(new VanillaMiniCardItem
                        {
                            Id = found.Id,
                            DisplayName = string.IsNullOrWhiteSpace(found.ZhName) ? found.Name : found.ZhName,
                            ImageZipPath = found.ThumbZipPath ?? found.ImageZipPath
                        });
                    }
                }
                break;
        }

        if (miniCards.Count > 0)
        {
            foreach (var mc in miniCards) DetailSubCards.Add(mc);
            HasDetailSubCards = true;
        }

        if (!string.IsNullOrWhiteSpace(hiResPath))
        {
            var hiRes = await Task.Run(() => GetOrDecodeBitmap(zipPath, hiResPath, decodeWidth: 340));
            if (SelectedItem == item) SelectedDetailImage = hiRes;
        }

        if (miniCards.Count > 0)
        {
            _ = Task.Run(() =>
            {
                foreach (var mc in miniCards)
                {
                    if (string.IsNullOrWhiteSpace(mc.ImageZipPath)) continue;
                    var bmp = GetOrDecodeBitmap(zipPath, mc.ImageZipPath, decodeWidth: 110);
                    if (bmp is not null)
                    {
                        Dispatcher.UIThread.Post(() => mc.Thumbnail = bmp, DispatcherPriority.Background);
                    }
                }
            });
        }
    }

    private async Task CloneSelectedToStudioAsync()
    {
        if (SelectedItem is null || _catalog is null) return;
        var mod = _studio.SelectedMod;
        var zipPath = _catalog.ZipPath;

        switch (SelectedItem.RawModel)
        {
            case VanillaCardItem c:
                var targetId = KeepVanillaIdentity
                    ? c.Id
                    : (mod is not null ? ModStudioService.SuggestNextCardId(mod.DirectoryPath) : 190010001);
                var lua = await Task.Run(() => VanillaContentCatalog.ReadCardLuaScript(zipPath, c.Id));
                string? copiedArt = mod is not null
                    ? VanillaContentCatalog.ExportZipImageToModArt(zipPath, c.ImageZipPath ?? c.ThumbZipPath, mod.DirectoryPath, targetId.ToString(CultureInfo.InvariantCulture))
                    : null;
                _studio.LoadFromVanillaCard(c, targetId, lua, copiedArt, KeepVanillaIdentity);
                break;

            case VanillaCharacterItem ch:
                _studio.LoadFromVanillaCharacter(ch, KeepVanillaIdentity);
                break;

            case VanillaRelicItem r:
                var newKey = KeepVanillaIdentity ? r.Key : $"{r.Key}_custom";
                string? copiedRelicArt = mod is not null && !string.IsNullOrWhiteSpace(r.ImageZipPath)
                    ? VanillaContentCatalog.ExportZipImageToModArt(zipPath, r.ImageZipPath, mod.DirectoryPath, $"relic_{newKey}")
                    : null;
                _studio.LoadFromVanillaRelic(r, newKey, copiedRelicArt, KeepVanillaIdentity);
                break;

            case VanillaPackItem p:
                _studio.LoadFromVanillaPack(p, KeepVanillaIdentity);
                break;

            case VanillaPreconstructedDeckItem d:
                _studio.LoadFromVanillaPreconstructedDeck(d, KeepVanillaIdentity);
                break;
        }

        _switchToMainTab(1); // Jump to Visual MOD Studio!
        _log(LogLevel.Ok, Loc.T(
            $"已将原版「{SelectedItem.Title}」载入可视化创作工作台，修改后点击保存即可！",
            $"Cloned vanilla '{SelectedItem.Title}' into Visual MOD Studio — edit and click Save!"));
    }

    private async Task QuickCloneSelectedToModAsync()
    {
        if (SelectedItem is null || _catalog is null || _studio.SelectedMod is null) return;
        await CloneSelectedToStudioAsync();

        switch (SelectedItem.RawModel)
        {
            case VanillaCardItem:
                await _studio.SaveCardCommand.ExecuteAsyncInternal();
                break;
            case VanillaCharacterItem:
                await _studio.SaveCharacterCommand.ExecuteAsyncInternal();
                break;
            case VanillaRelicItem:
                await _studio.SaveRelicCommand.ExecuteAsyncInternal();
                break;
            case VanillaPackItem:
                await _studio.SavePackCommand.ExecuteAsyncInternal();
                break;
            case VanillaPreconstructedDeckItem:
                await _studio.SaveDeckCommand.ExecuteAsyncInternal();
                break;
        }

        await _onModMutated(SelectedItem.Key);
    }

    private Bitmap? GetOrDecodeBitmap(string zipPath, string entryPath, int decodeWidth)
    {
        var cacheKey = $"{zipPath}|{entryPath}|{decodeWidth}";
        if (_bitmapCache.TryGetValue(cacheKey, out var cached)) return cached;

        var bytes = VanillaContentCatalog.ReadZipEntryBytes(zipPath, entryPath);
        if (bytes is null || bytes.Length == 0) return null;

        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var bmp = Bitmap.DecodeToWidth(ms, decodeWidth);
            _bitmapCache.TryAdd(cacheKey, bmp);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static bool Matches(string query, params string?[] fields)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        foreach (var f in fields)
        {
            if (!string.IsNullOrWhiteSpace(f) && f.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        foreach (var kv in _bitmapCache)
        {
            try { kv.Value.Dispose(); } catch { }
        }
        _bitmapCache.Clear();
    }
}
