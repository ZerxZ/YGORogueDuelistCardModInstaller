using System.Collections.ObjectModel;
using System.Globalization;
using YGORogueDuelistCardModInstaller.Avalonia.Services;
using YGORogueDuelistCardModInstaller.Core;
using YGORogueDuelistCardModInstaller.Core.ModManager;

namespace YGORogueDuelistCardModInstaller.Avalonia.ViewModels;

public sealed record LuaTemplateOption(string Key, string DisplayName);

/// <summary>
/// ViewModel for the beginner-friendly Visual MOD Studio workbench tab.
/// Supports Card/Character/Relic/Pack/Preconstructed Deck/.ydk/Trainer visual builders + Xmake DSL / Lua / JS code editor.
/// </summary>
public sealed class ModStudioViewModel : ViewModelBase
{
    private readonly IDialogService _dialogService;
    private readonly Func<ModInjectionEngine> _getEngine;
    private readonly Action<LogLevel, string> _log;
    private readonly Func<string?, Task> _onSavedAndReload;

    private ManagedMod? _selectedMod;
    private string? _selectedFile;
    private int _selectedSubTabIndex;

    // Card Builder fields
    private string _cardId = "190010001";
    private string _cardName = "Astral Draw";
    private string _cardZhName = "星界抽卡";
    private string _cardDesc = "Draw 2 cards.";
    private string _cardZhDesc = "从卡组抽2张卡。";
    private CardPresetInfo _selectedCardPreset;
    private string _cardRace = "Normal";
    private string _cardAttribute = "DARK";
    private string _cardLevel = "4";
    private string _cardAtk = "1800";
    private string _cardDef = "1500";
    private string _cardPackId = string.Empty;
    private string _cardArtPath = string.Empty;
    private LuaTemplateOption _selectedCardLuaTemplate;
    private string _cardLuaScript = string.Empty;

    // Character Builder fields
    private string _charName = "Custom Duelist";
    private string _charZhName = "自定义决斗者";
    private string _charSprite = "dm01.png";
    private string _charRace = "Spellcaster";
    private string _charAttribute = "DARK";
    private string _charColor = "#38bdf8";
    private string _charStartingRelic = "mage";
    private string _charStartingPacks = string.Empty;
    private string _charDeckCards = "Dark Magician, Dark Magician Girl, Pot of Greed, Monster Reborn";
    private bool _charUnlocked = true;

    // Relic Builder fields
    private string _relicKey = "starlight_charm";
    private string _relicName = "Starlight Charm";
    private string _relicZhName = "星光护符";
    private string _relicCost = "80";
    private string _relicTag = "utility";
    private string _relicDesc = "At the start of the duel, gain 1000 LP.";
    private string _relicZhDesc = "决斗开始时，回复1000基本分。";
    private string _relicArt = "Merchant Urn.png";
    private LuaTemplateOption _selectedRelicLuaTemplate;
    private string _relicLuaEffect = "Duel.Recover(0, 1000, REASON_EFFECT)";

    // Pack & Preconstructed Deck Builder fields
    private string _packId = "EN-CUSTOM";
    private string _packName = "Custom Expansion Pack";
    private string _packZhName = "自定义扩展卡包";
    private string _packCode = "CST";
    private string _packSize = "9";
    private string _packCommonIds = "46986414, 38033121, 55144522";
    private string _packRareIds = "83764718, 5318639";
    private string _packSuperIds = "44095762";
    private string _packUltraIds = "190010001, 89631139";

    private string _deckId = "CUSTOM-STARTER";
    private string _deckName = "Structure Deck: Custom";
    private string _deckZhName = "预组卡组：自定义";
    private string _deckArt = "SDK";
    private string _deckCardIds = "190010001, 46986414, 38033121, 55144522, 83764718, 5318639";

    // Trainer Builder fields
    private bool _trainerUnlockChars = true;
    private bool _trainerUnlockVariants = true;
    private string _trainerStartingGold = "9999";
    private string _trainerGoldBonus = string.Empty;
    private string _trainerStartingLp = "12000";
    private bool _trainerFreeShop;
    private string _trainerShopMult = "0.5";
    private string _trainerRewardMult = "2.0";
    private string _trainerStartingRelics = "mage";
    private bool _trainerOverlay = true;

    // Code & DSL Editor fields
    private string _codeRelativePath = "mod.lua";
    private string _codeContent = string.Empty;
    private string _studioStatusMessage = string.Empty;

    public ModStudioViewModel(
        IDialogService dialogService,
        LocalizationService loc,
        Func<ModInjectionEngine> getEngine,
        Action<LogLevel, string> log,
        Func<string?, Task> onSavedAndReload)
    {
        _dialogService = dialogService;
        Loc = loc;
        _getEngine = getEngine;
        _log = log;
        _onSavedAndReload = onSavedAndReload;

        CardPresets = ModStudioService.CardTypePresets;
        _selectedCardPreset = CardPresets[4]; // Normal Spell default (beginner-friendly)
        AttributeChoices = ["DARK", "LIGHT", "EARTH", "WATER", "FIRE", "WIND", "DIVINE", ""];
        MonsterRaceChoices = ModStudioService.MonsterRaceMasks.Keys.ToList();
        CardLuaTemplates =
        [
            new("draw1", "抽 1 张卡 / Draw 1 Card"),
            new("draw2", "抽 2 张卡 / Draw 2 Cards"),
            new("gain_lp", "回复 1000 LP / Gain 1000 LP"),
            new("destroy_monster", "破坏对方 1 只怪兽 / Destroy 1 Opp Monster"),
            new("monster_summon_draw", "召唤/特召成功抽 1 / On Summon Draw 1"),
            new("blank", "空白骨架 / Blank Skeleton")
        ];
        _selectedCardLuaTemplate = CardLuaTemplates[1];
        RelicLuaTemplates =
        [
            new("gain_lp", "决斗开始回复 1000 LP / Gain 1000 LP"),
            new("draw_extra", "决斗开始额外抽 1 / Extra Draw 1"),
            new("damage_opp", "决斗开始给对方 800 伤害 / Burn 800"),
            new("blank", "自定义 Lua / Custom Lua")
        ];
        _selectedRelicLuaTemplate = RelicLuaTemplates[0];
        _cardLuaScript = ModStudioService.BuildLuaCardTemplate("draw2", 190010001, _cardName);

        AutoSuggestCardIdCommand = new RelayCommand(AutoSuggestCardId, () => HasSelectedMod);
        PickCardArtCommand = new AsyncRelayCommand(PickCardArtAsync, () => HasSelectedMod);
        FillCardLuaTemplateCommand = new RelayCommand(FillCardLuaTemplate);
        SaveCardCommand = new AsyncRelayCommand(SaveCardAsync, () => HasSelectedMod);

        SaveCharacterCommand = new AsyncRelayCommand(SaveCharacterAsync, () => HasSelectedMod);

        PickRelicArtCommand = new AsyncRelayCommand(PickRelicArtAsync, () => HasSelectedMod);
        FillRelicLuaTemplateCommand = new RelayCommand(() =>
            RelicLuaEffect = ModStudioService.BuildLuaRelicTemplate(SelectedRelicLuaTemplate.Key));
        SaveRelicCommand = new AsyncRelayCommand(SaveRelicAsync, () => HasSelectedMod);

        SavePackCommand = new AsyncRelayCommand(SavePackAsync, () => HasSelectedMod);
        SaveDeckCommand = new AsyncRelayCommand(SaveDeckAsync, () => HasSelectedMod);
        ImportYdkCommand = new AsyncRelayCommand(ImportYdkAsync, () => HasSelectedMod);

        SaveTrainerCommand = new AsyncRelayCommand(SaveTrainerAsync, () => HasSelectedMod);

        ScaffoldDslCommand = new AsyncRelayCommand(ScaffoldDslAsync, () => HasSelectedMod);
        ValidateModCommand = new RelayCommand(ValidateSelectedMod, () => HasSelectedMod);
        SaveCodeFileCommand = new AsyncRelayCommand(SaveCodeFileAsync, () => HasSelectedMod);
        OpenFileInCodeCommand = new RelayCommand<string>(OpenRelativeFile);
    }

    public LocalizationService Loc { get; }
    public ObservableCollection<ManagedMod> AvailableMods { get; } = [];
    public ObservableCollection<string> ModFiles { get; } = [];

    public IReadOnlyList<CardPresetInfo> CardPresets { get; }
    public IReadOnlyList<string> AttributeChoices { get; }
    public IReadOnlyList<string> MonsterRaceChoices { get; }
    public IReadOnlyList<LuaTemplateOption> CardLuaTemplates { get; }
    public IReadOnlyList<LuaTemplateOption> RelicLuaTemplates { get; }

    public ManagedMod? SelectedMod
    {
        get => _selectedMod;
        set
        {
            if (SetProperty(ref _selectedMod, value))
            {
                OnPropertyChanged(nameof(HasSelectedMod));
                OnPropertyChanged(nameof(HasNoMod));
                RefreshModFiles();
                NotifyCommands();
                if (value is not null)
                {
                    AutoSuggestCardId();
                }
            }
        }
    }

    public bool HasSelectedMod => SelectedMod is not null;
    public bool HasNoMod => SelectedMod is null;

    public int SelectedSubTabIndex
    {
        get => _selectedSubTabIndex;
        set => SetProperty(ref _selectedSubTabIndex, value);
    }

    public string? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (SetProperty(ref _selectedFile, value) && !string.IsNullOrWhiteSpace(value))
            {
                OpenRelativeFile(value);
                SelectedSubTabIndex = 5; // Jump to Code & DSL tab when user clicks a file
            }
        }
    }

    public string StudioStatusMessage { get => _studioStatusMessage; set => SetProperty(ref _studioStatusMessage, value); }

    // Card Builder bindings
    public string CardId { get => _cardId; set => SetProperty(ref _cardId, value); }
    public string CardName { get => _cardName; set => SetProperty(ref _cardName, value); }
    public string CardZhName { get => _cardZhName; set => SetProperty(ref _cardZhName, value); }
    public string CardDesc { get => _cardDesc; set => SetProperty(ref _cardDesc, value); }
    public string CardZhDesc { get => _cardZhDesc; set => SetProperty(ref _cardZhDesc, value); }

    public CardPresetInfo SelectedCardPreset
    {
        get => _selectedCardPreset;
        set
        {
            if (value is not null && SetProperty(ref _selectedCardPreset, value))
            {
                CardRace = value.DefaultRace;
                OnPropertyChanged(nameof(IsMonsterCard));
                OnPropertyChanged(nameof(CardBitmaskPreview));
            }
        }
    }

    public bool IsMonsterCard => SelectedCardPreset.IsMonster;

    public string CardBitmaskPreview
    {
        get
        {
            var attr = IsMonsterCard && ModStudioService.AttributeMasks.TryGetValue(CardAttribute, out var a) ? a : 0;
            var race = IsMonsterCard && ModStudioService.MonsterRaceMasks.TryGetValue(CardRace, out var r) ? r : "0";
            return $"data.type={SelectedCardPreset.TypeMask}  ·  data.race=\"{race}\"  ·  data.attribute={attr}";
        }
    }

    public string CardRace
    {
        get => _cardRace;
        set { if (SetProperty(ref _cardRace, value)) OnPropertyChanged(nameof(CardBitmaskPreview)); }
    }

    public string CardAttribute
    {
        get => _cardAttribute;
        set { if (SetProperty(ref _cardAttribute, value)) OnPropertyChanged(nameof(CardBitmaskPreview)); }
    }

    public string CardLevel { get => _cardLevel; set => SetProperty(ref _cardLevel, value); }
    public string CardAtk { get => _cardAtk; set => SetProperty(ref _cardAtk, value); }
    public string CardDef { get => _cardDef; set => SetProperty(ref _cardDef, value); }
    public string CardPackId { get => _cardPackId; set => SetProperty(ref _cardPackId, value); }
    public string CardArtPath { get => _cardArtPath; set => SetProperty(ref _cardArtPath, value); }
    public LuaTemplateOption SelectedCardLuaTemplate { get => _selectedCardLuaTemplate; set => SetProperty(ref _selectedCardLuaTemplate, value); }
    public string CardLuaScript { get => _cardLuaScript; set => SetProperty(ref _cardLuaScript, value); }

    // Character bindings
    public string CharName { get => _charName; set => SetProperty(ref _charName, value); }
    public string CharZhName { get => _charZhName; set => SetProperty(ref _charZhName, value); }
    public string CharSprite { get => _charSprite; set => SetProperty(ref _charSprite, value); }
    public string CharRace { get => _charRace; set => SetProperty(ref _charRace, value); }
    public string CharAttribute { get => _charAttribute; set => SetProperty(ref _charAttribute, value); }
    public string CharColor { get => _charColor; set => SetProperty(ref _charColor, value); }
    public string CharStartingRelic { get => _charStartingRelic; set => SetProperty(ref _charStartingRelic, value); }
    public string CharStartingPacks { get => _charStartingPacks; set => SetProperty(ref _charStartingPacks, value); }
    public string CharDeckCards { get => _charDeckCards; set => SetProperty(ref _charDeckCards, value); }
    public bool CharUnlocked { get => _charUnlocked; set => SetProperty(ref _charUnlocked, value); }

    // Relic bindings
    public string RelicKey { get => _relicKey; set => SetProperty(ref _relicKey, value); }
    public string RelicName { get => _relicName; set => SetProperty(ref _relicName, value); }
    public string RelicZhName { get => _relicZhName; set => SetProperty(ref _relicZhName, value); }
    public string RelicCost { get => _relicCost; set => SetProperty(ref _relicCost, value); }
    public string RelicTag { get => _relicTag; set => SetProperty(ref _relicTag, value); }
    public string RelicDesc { get => _relicDesc; set => SetProperty(ref _relicDesc, value); }
    public string RelicZhDesc { get => _relicZhDesc; set => SetProperty(ref _relicZhDesc, value); }
    public string RelicArt { get => _relicArt; set => SetProperty(ref _relicArt, value); }
    public LuaTemplateOption SelectedRelicLuaTemplate { get => _selectedRelicLuaTemplate; set => SetProperty(ref _selectedRelicLuaTemplate, value); }
    public string RelicLuaEffect { get => _relicLuaEffect; set => SetProperty(ref _relicLuaEffect, value); }

    // Pack & Deck bindings
    public string PackId { get => _packId; set => SetProperty(ref _packId, value); }
    public string PackName { get => _packName; set => SetProperty(ref _packName, value); }
    public string PackZhName { get => _packZhName; set => SetProperty(ref _packZhName, value); }
    public string PackCode { get => _packCode; set => SetProperty(ref _packCode, value); }
    public string PackSize { get => _packSize; set => SetProperty(ref _packSize, value); }
    public string PackCommonIds { get => _packCommonIds; set => SetProperty(ref _packCommonIds, value); }
    public string PackRareIds { get => _packRareIds; set => SetProperty(ref _packRareIds, value); }
    public string PackSuperIds { get => _packSuperIds; set => SetProperty(ref _packSuperIds, value); }
    public string PackUltraIds { get => _packUltraIds; set => SetProperty(ref _packUltraIds, value); }

    public string DeckId { get => _deckId; set => SetProperty(ref _deckId, value); }
    public string DeckName { get => _deckName; set => SetProperty(ref _deckName, value); }
    public string DeckZhName { get => _deckZhName; set => SetProperty(ref _deckZhName, value); }
    public string DeckArt { get => _deckArt; set => SetProperty(ref _deckArt, value); }
    public string DeckCardIds { get => _deckCardIds; set => SetProperty(ref _deckCardIds, value); }

    // Trainer bindings
    public bool TrainerUnlockChars { get => _trainerUnlockChars; set => SetProperty(ref _trainerUnlockChars, value); }
    public bool TrainerUnlockVariants { get => _trainerUnlockVariants; set => SetProperty(ref _trainerUnlockVariants, value); }
    public string TrainerStartingGold { get => _trainerStartingGold; set => SetProperty(ref _trainerStartingGold, value); }
    public string TrainerGoldBonus { get => _trainerGoldBonus; set => SetProperty(ref _trainerGoldBonus, value); }
    public string TrainerStartingLp { get => _trainerStartingLp; set => SetProperty(ref _trainerStartingLp, value); }
    public bool TrainerFreeShop { get => _trainerFreeShop; set => SetProperty(ref _trainerFreeShop, value); }
    public string TrainerShopMult { get => _trainerShopMult; set => SetProperty(ref _trainerShopMult, value); }
    public string TrainerRewardMult { get => _trainerRewardMult; set => SetProperty(ref _trainerRewardMult, value); }
    public string TrainerStartingRelics { get => _trainerStartingRelics; set => SetProperty(ref _trainerStartingRelics, value); }
    public bool TrainerOverlay { get => _trainerOverlay; set => SetProperty(ref _trainerOverlay, value); }

    // Code Editor bindings
    public string CodeRelativePath { get => _codeRelativePath; set => SetProperty(ref _codeRelativePath, value); }
    public string CodeContent { get => _codeContent; set => SetProperty(ref _codeContent, value); }

    public RelayCommand AutoSuggestCardIdCommand { get; }
    public AsyncRelayCommand PickCardArtCommand { get; }
    public RelayCommand FillCardLuaTemplateCommand { get; }
    public AsyncRelayCommand SaveCardCommand { get; }
    public AsyncRelayCommand SaveCharacterCommand { get; }
    public AsyncRelayCommand PickRelicArtCommand { get; }
    public RelayCommand FillRelicLuaTemplateCommand { get; }
    public AsyncRelayCommand SaveRelicCommand { get; }
    public AsyncRelayCommand SavePackCommand { get; }
    public AsyncRelayCommand SaveDeckCommand { get; }
    public AsyncRelayCommand ImportYdkCommand { get; }
    public AsyncRelayCommand SaveTrainerCommand { get; }
    public AsyncRelayCommand ScaffoldDslCommand { get; }
    public RelayCommand ValidateModCommand { get; }
    public AsyncRelayCommand SaveCodeFileCommand { get; }
    public RelayCommand<string> OpenFileInCodeCommand { get; }

    public void SyncMods(IReadOnlyList<ManagedMod> mods, string? preferredModId = null)
    {
        var keepId = preferredModId ?? SelectedMod?.Id;
        AvailableMods.Clear();
        foreach (var m in mods) AvailableMods.Add(m);
        SelectedMod = AvailableMods.FirstOrDefault(m => string.Equals(m.Id, keepId, StringComparison.OrdinalIgnoreCase))
                      ?? AvailableMods.FirstOrDefault();
    }

    public void SelectModById(string modId)
    {
        var match = AvailableMods.FirstOrDefault(m => string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase));
        if (match is not null) SelectedMod = match;
    }

    private void RefreshModFiles()
    {
        ModFiles.Clear();
        if (SelectedMod is null) return;
        foreach (var rel in ModStudioService.ListModTextFiles(SelectedMod.DirectoryPath))
        {
            ModFiles.Add(rel);
        }

        if (ModFiles.Count > 0 && string.IsNullOrEmpty(CodeContent))
        {
            var first = ModFiles.FirstOrDefault(f => f is "mod.lua" or "mod.js" or "mod-manifest.json") ?? ModFiles[0];
            OpenRelativeFile(first);
        }
    }

    private void OpenRelativeFile(string? relPath)
    {
        if (SelectedMod is null || string.IsNullOrWhiteSpace(relPath)) return;
        try
        {
            CodeRelativePath = relPath;
            CodeContent = ModStudioService.ReadModTextFile(SelectedMod.DirectoryPath, relPath);
        }
        catch (Exception ex)
        {
            _log(LogLevel.Error, ex.Message);
        }
    }

    private void AutoSuggestCardId()
    {
        if (SelectedMod is null) return;
        CardId = ModStudioService.SuggestNextCardId(SelectedMod.DirectoryPath).ToString(CultureInfo.InvariantCulture);
    }

    private void FillCardLuaTemplate()
    {
        var id = int.TryParse(CardId, out var n) ? n : 190010001;
        CardLuaScript = ModStudioService.BuildLuaCardTemplate(SelectedCardLuaTemplate.Key, id, CardName);
    }

    private async Task PickCardArtAsync()
    {
        var picked = await _dialogService.PickImageFileAsync(Loc.T("选择卡牌卡图（自动复制到 art/）", "Select Card Art Image"));
        if (!string.IsNullOrWhiteSpace(picked)) CardArtPath = picked;
    }

    private async Task PickRelicArtAsync()
    {
        var picked = await _dialogService.PickImageFileAsync(Loc.T("选择遗物图标（自动复制到 art/）", "Select Relic Icon Image"));
        if (!string.IsNullOrWhiteSpace(picked)) RelicArt = picked;
    }

    private async Task SaveCardAsync()
    {
        if (SelectedMod is null) return;
        try
        {
            if (!int.TryParse(CardId.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                throw new ModOperationException(Loc.T("卡牌 ID 必须是有效数字。", "Card ID must be a valid integer."));
            var lv = int.TryParse(CardLevel.Trim(), out var l) ? l : 0;
            var atk = int.TryParse(CardAtk.Trim(), out var a) ? a : 0;
            var def = int.TryParse(CardDef.Trim(), out var d) ? d : 0;

            var saved = ModStudioService.SaveVisualCard(
                SelectedMod.DirectoryPath, id, CardName, CardZhName, CardDesc, CardZhDesc,
                SelectedCardPreset, CardRace, CardAttribute, lv, atk, def, CardPackId, CardArtPath, CardLuaScript);

            var msg = Loc.T($"已保存卡牌 #{id}「{CardName}」-> {saved}", $"Saved Card #{id} '{CardName}' -> {saved}");
            StudioStatusMessage = msg;
            _log(LogLevel.Ok, msg);
            RefreshModFiles();
            await _onSavedAndReload($"cards/custom-cards.json");
        }
        catch (Exception ex)
        {
            StudioStatusMessage = ex.Message;
            _log(LogLevel.Error, ex.Message);
        }
    }

    private async Task SaveCharacterAsync()
    {
        if (SelectedMod is null) return;
        try
        {
            var saved = ModStudioService.SaveVisualCharacter(
                SelectedMod.DirectoryPath, CharName, CharZhName, CharSprite, CharRace,
                CharAttribute, CharColor, CharStartingRelic, CharStartingPacks, CharDeckCards, CharUnlocked);

            var msg = Loc.T($"已保存角色「{CharName}」-> {saved}", $"Saved Character '{CharName}' -> {saved}");
            StudioStatusMessage = msg;
            _log(LogLevel.Ok, msg);
            RefreshModFiles();
            await _onSavedAndReload(Path.GetFileName(saved));
        }
        catch (Exception ex)
        {
            StudioStatusMessage = ex.Message;
            _log(LogLevel.Error, ex.Message);
        }
    }

    private async Task SaveRelicAsync()
    {
        if (SelectedMod is null) return;
        try
        {
            var cost = int.TryParse(RelicCost.Trim(), out var c) ? c : 80;
            var saved = ModStudioService.SaveVisualRelic(
                SelectedMod.DirectoryPath, RelicKey, RelicName, RelicZhName, cost,
                RelicTag, RelicDesc, RelicZhDesc, RelicArt, RelicLuaEffect);

            var msg = Loc.T($"已保存遗物「{RelicName}」({RelicKey}) -> {saved}", $"Saved Relic '{RelicName}' ({RelicKey}) -> {saved}");
            StudioStatusMessage = msg;
            _log(LogLevel.Ok, msg);
            RefreshModFiles();
            await _onSavedAndReload(Path.GetFileName(saved));
        }
        catch (Exception ex)
        {
            StudioStatusMessage = ex.Message;
            _log(LogLevel.Error, ex.Message);
        }
    }

    private async Task SavePackAsync()
    {
        if (SelectedMod is null) return;
        try
        {
            var size = int.TryParse(PackSize.Trim(), out var sz) ? sz : 9;
            var saved = ModStudioService.SaveVisualPack(
                SelectedMod.DirectoryPath, PackId, PackName, PackZhName, PackCode, size,
                PackCommonIds, PackRareIds, PackSuperIds, PackUltraIds);

            var msg = Loc.T($"已保存自定义卡包「{PackName}」({PackId}) -> {saved}", $"Saved Pack '{PackName}' ({PackId}) -> {saved}");
            StudioStatusMessage = msg;
            _log(LogLevel.Ok, msg);
            RefreshModFiles();
            await _onSavedAndReload(Path.GetFileName(saved));
        }
        catch (Exception ex)
        {
            StudioStatusMessage = ex.Message;
            _log(LogLevel.Error, ex.Message);
        }
    }

    private async Task SaveDeckAsync()
    {
        if (SelectedMod is null) return;
        try
        {
            var saved = ModStudioService.SaveVisualPreconstructedDeck(
                SelectedMod.DirectoryPath, DeckId, DeckName, DeckZhName, DeckArt, DeckCardIds);

            var msg = Loc.T($"已保存预组卡组「{DeckName}」({DeckId}) -> {saved}", $"Saved Preconstructed Deck '{DeckName}' ({DeckId}) -> {saved}");
            StudioStatusMessage = msg;
            _log(LogLevel.Ok, msg);
            RefreshModFiles();
            await _onSavedAndReload(Path.GetFileName(saved));
        }
        catch (Exception ex)
        {
            StudioStatusMessage = ex.Message;
            _log(LogLevel.Error, ex.Message);
        }
    }

    private async Task ImportYdkAsync()
    {
        if (SelectedMod is null) return;
        var ydk = await _dialogService.PickYdkFileAsync(Loc.T("选择要导入的 YGOPro .ydk 卡组文件", "Select YGOPro .ydk Deck File"));
        if (string.IsNullOrWhiteSpace(ydk)) return;
        try
        {
            var dest = ModStudioService.ImportYdkFile(SelectedMod.DirectoryPath, ydk);
            var msg = Loc.T($"已导入 .ydk 卡组 -> {dest}", $"Imported .ydk deck -> {dest}");
            StudioStatusMessage = msg;
            _log(LogLevel.Ok, msg);
            RefreshModFiles();
            await _onSavedAndReload(Path.GetFileName(dest));
        }
        catch (Exception ex)
        {
            StudioStatusMessage = ex.Message;
            _log(LogLevel.Error, ex.Message);
        }
    }

    private async Task SaveTrainerAsync()
    {
        if (SelectedMod is null) return;
        try
        {
            int? gold = int.TryParse(TrainerStartingGold.Trim(), out var g) ? g : null;
            int? bonus = int.TryParse(TrainerGoldBonus.Trim(), out var b) ? b : null;
            int? lp = int.TryParse(TrainerStartingLp.Trim(), out var l) ? l : null;
            double? shopM = double.TryParse(TrainerShopMult.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sm) ? sm : null;
            double? rewardM = double.TryParse(TrainerRewardMult.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rm) ? rm : null;

            var saved = ModStudioService.SaveVisualTrainer(
                SelectedMod.DirectoryPath, TrainerUnlockChars, TrainerUnlockVariants,
                gold, bonus, lp, TrainerFreeShop, shopM, rewardM, TrainerStartingRelics, TrainerOverlay);

            var msg = Loc.T($"已保存修改器配置 -> {saved}", $"Saved trainer.json -> {saved}");
            StudioStatusMessage = msg;
            _log(LogLevel.Ok, msg);
            RefreshModFiles();
            await _onSavedAndReload("trainer.json");
        }
        catch (Exception ex)
        {
            StudioStatusMessage = ex.Message;
            _log(LogLevel.Error, ex.Message);
        }
    }

    private async Task ScaffoldDslAsync()
    {
        if (SelectedMod is null) return;
        try
        {
            ModStudioService.ScaffoldXmakeDsl(SelectedMod);
            RefreshModFiles();
            OpenRelativeFile("mod.lua");
            SelectedSubTabIndex = 5;
            var msg = Loc.T("已生成 Xmake 风格 mod.lua 与 mod.js 模板！", "Scaffolded Xmake-style mod.lua & mod.js templates!");
            StudioStatusMessage = msg;
            _log(LogLevel.Ok, msg);
            await _onSavedAndReload("mod.lua");
        }
        catch (Exception ex)
        {
            StudioStatusMessage = ex.Message;
            _log(LogLevel.Error, ex.Message);
        }
    }

    private void ValidateSelectedMod()
    {
        if (SelectedMod is null) return;
        var engine = _getEngine();
        var status = engine.GetStatus();
        var report = ModStudioService.ValidateMod(engine.Repository, SelectedMod.Id, status.ActiveBaseGameVersion);
        if (report.IsValid && report.Warnings.Count == 0)
        {
            var ok = Loc.T($"校验通过：「{SelectedMod.Manifest.Name}」未发现语法或配置错误！", $"Validation PASSED: '{SelectedMod.Manifest.Name}' has 0 errors and 0 warnings!");
            StudioStatusMessage = ok;
            _log(LogLevel.Ok, ok);
            return;
        }

        foreach (var err in report.Errors) _log(LogLevel.Error, $"[校验错误] {err}");
        foreach (var warn in report.Warnings) _log(LogLevel.Warn, $"[校验提示] {warn}");
        StudioStatusMessage = Loc.T(
            $"校验结果：{report.Errors.Count} 个错误，{report.Warnings.Count} 条警告（详见下方日志）",
            $"Validation: {report.Errors.Count} errors, {report.Warnings.Count} warnings (see logs)");
    }

    private async Task SaveCodeFileAsync()
    {
        if (SelectedMod is null || string.IsNullOrWhiteSpace(CodeRelativePath)) return;
        try
        {
            var saved = ModStudioService.WriteModTextFile(SelectedMod.DirectoryPath, CodeRelativePath, CodeContent);
            var msg = Loc.T($"已保存 {CodeRelativePath}", $"Saved {CodeRelativePath}");
            StudioStatusMessage = msg;
            _log(LogLevel.Ok, $"{msg} -> {saved}");
            RefreshModFiles();
            await _onSavedAndReload(CodeRelativePath);
        }
        catch (Exception ex)
        {
            StudioStatusMessage = ex.Message;
            _log(LogLevel.Error, ex.Message);
        }
    }

    private void NotifyCommands()
    {
        AutoSuggestCardIdCommand.NotifyCanExecuteChanged();
        PickCardArtCommand.NotifyCanExecuteChanged();
        SaveCardCommand.NotifyCanExecuteChanged();
        SaveCharacterCommand.NotifyCanExecuteChanged();
        PickRelicArtCommand.NotifyCanExecuteChanged();
        SaveRelicCommand.NotifyCanExecuteChanged();
        SavePackCommand.NotifyCanExecuteChanged();
        SaveDeckCommand.NotifyCanExecuteChanged();
        ImportYdkCommand.NotifyCanExecuteChanged();
        SaveTrainerCommand.NotifyCanExecuteChanged();
        ScaffoldDslCommand.NotifyCanExecuteChanged();
        ValidateModCommand.NotifyCanExecuteChanged();
        SaveCodeFileCommand.NotifyCanExecuteChanged();
    }
}
