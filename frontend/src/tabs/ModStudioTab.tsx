import { useCallback, useEffect, useRef, useState } from 'react';
import {
  Button,
  Card,
  Chip,
  Input,
  Label,
  ListBox,
  SearchField,
  Select,
  Spinner,
  Switch,
  TextArea,
  TextField,
} from '@heroui/react';
import {
  CheckCircle2,
  Code2,
  FileCode2,
  FilePlus2,
  FolderOpen,
  Image as ImageIcon,
  Save,
  Sliders,
  Sparkles,
  Trash2,
  Upload,
  Wand2,
} from 'lucide-react';
import { apiGet, apiPost, resolveApiUrl } from '../api';
import { CodeEditor, detectCodeMode } from '../components/CodeEditor';
import { useInfiniteScroll } from '../hooks/useInfiniteScroll';
import { fmt, type I18nDict } from '../i18n';
import type {
  StudioCardItem,
  StudioCatalogResponse,
  StudioCategoryKey,
  StudioCharacterItem,
  StudioCodeItem,
  StudioDeckItem,
  StudioDraftFromVanilla,
  StudioPackItem,
  StudioPresetsResponse,
  StudioRelicItem,
  StudioSyncEvent,
  StudioTrainerState,
  SubCardThumb,
} from '../types';

interface ModStudioTabProps {
  t: I18nDict;
  activeModId: string;
  activeModName?: string;
  mcpUrl?: string;
  mcpSyncEvent?: StudioSyncEvent | null;
  category: StudioCategoryKey;
  inspectorOpen: boolean;
  onCategoryChange: (cat: StudioCategoryKey) => void;
  onCountsChange: (counts: Record<StudioCategoryKey, number>) => void;
  onSelectActiveMod: (modId: string) => void;
  onRefreshStatus: () => Promise<void>;
  incomingDraft: StudioDraftFromVanilla | null;
  onClearIncomingDraft: () => void;
  focusTarget: { modId: string; category: StudioCategoryKey; key: string } | null;
  onClearFocusTarget: () => void;
  onNotify: (msg: string, kind?: 'ok' | 'err' | 'info') => void;
}

const PAGE_SIZE = 60;

export function ModStudioTab({
  t,
  activeModId,
  activeModName,
  mcpUrl,
  mcpSyncEvent,
  category,
  inspectorOpen,
  onCategoryChange,
  onCountsChange,
  onSelectActiveMod,
  onRefreshStatus,
  incomingDraft,
  onClearIncomingDraft,
  focusTarget,
  onClearFocusTarget,
  onNotify,
}: ModStudioTabProps) {
  const [query, setQuery] = useState('');
  const [debouncedQuery, setDebouncedQuery] = useState('');
  const [presets, setPresets] = useState<StudioPresetsResponse | null>(null);

  const [items, setItems] = useState<Record<string, unknown>[]>([]);
  const [total, setTotal] = useState(0);
  const [hasMore, setHasMore] = useState(false);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [selectedSourceFilePath, setSelectedSourceFilePath] = useState<string | null>(null);
  const [selectedPreviewImg, setSelectedPreviewImg] = useState<string | null>(null);
  const [selectedSubCards, setSelectedSubCards] = useState<SubCardThumb[]>([]);

  const [cardForm, setCardForm] = useState({
    id: '190010001',
    name: '',
    zhName: '',
    desc: '',
    zhDesc: '',
    typeMask: 2,
    race: 'Normal',
    attribute: 'DARK',
    level: '4',
    atk: '1800',
    def: '1200',
    packId: '',
    artPath: '',
    luaScript: '',
  });

  const [charForm, setCharForm] = useState({
    name: '',
    zhName: '',
    sprite: 'dm01.png',
    race: 'Spellcaster',
    attribute: 'DARK',
    color: '#38bdf8',
    startingRelic: 'mage',
    startingPacks: 'EN-LOB',
    deckCards: 'Dark Magician, Dark Magician Girl, Kuriboh',
    unlockedByDefault: true,
  });

  const [relicForm, setRelicForm] = useState({
    key: '',
    name: '',
    zhName: '',
    cost: '80',
    tag: 'utility',
    description: '',
    zhDescription: '',
    art: '',
    luaEffect: '',
  });

  const [packForm, setPackForm] = useState({
    id: '',
    name: '',
    zhName: '',
    code: '',
    size: '9',
    commonIds: '',
    rareIds: '',
    superIds: '',
    ultraIds: '',
  });

  const [deckForm, setDeckForm] = useState({
    id: '',
    name: '',
    zhName: '',
    art: 'SDK',
    cardIds: '',
  });

  const [trainerForm, setTrainerForm] = useState({
    unlockAllCharacters: false,
    unlockAllVariants: false,
    startingGold: '',
    startingGoldBonus: '',
    startingLp: '',
    freeShop: false,
    shopPriceMultiplier: '',
    rewardGoldMultiplier: '',
    startingRelics: '',
    inGameOverlay: true,
  });

  const [codePath, setCodePath] = useState('mod.lua');
  const [codeContent, setCodeContent] = useState('');
  const [loadingCode, setLoadingCode] = useState(false);

  const scrollRef = useRef<HTMLDivElement | null>(null);
  const reqIdRef = useRef(0);
  const pendingSelectKeyRef = useRef<string | null>(null);

  useEffect(() => {
    apiGet<StudioPresetsResponse>('/api/studio/presets')
      .then(setPresets)
      .catch(() => {});
  }, []);

  useEffect(() => {
    const timer = setTimeout(() => setDebouncedQuery(query.trim()), 180);
    return () => clearTimeout(timer);
  }, [query]);

  // Publish live Studio editing focus to built-in MCP server (get_studio_context)
  const currentScriptPath =
    category === 'code'
      ? codePath
      : category === 'cards' && cardForm.id
        ? `lua/cards/c${cardForm.id}.lua`
        : selectedSourceFilePath;

  const currentItemName =
    category === 'cards'
      ? cardForm.zhName || cardForm.name
      : category === 'characters'
        ? charForm.zhName || charForm.name
        : category === 'relics'
          ? relicForm.zhName || relicForm.name
          : category === 'packs'
            ? packForm.zhName || packForm.name
            : category === 'decks'
              ? deckForm.zhName || deckForm.name
              : codePath;

  useEffect(() => {
    if (!activeModId) return;
    const timer = setTimeout(() => {
      apiPost('/api/studio/context', {
        modId: activeModId,
        modName: activeModName || activeModId,
        category,
        itemKey: selectedKey || cardForm.id || relicForm.key || codePath || '',
        itemName: currentItemName || '',
        scriptPath: currentScriptPath || '',
      }).catch(() => {});
    }, 150);
    return () => clearTimeout(timer);
  }, [activeModId, activeModName, category, selectedKey, cardForm.id, relicForm.key, codePath, currentItemName, currentScriptPath]);

  const copyAiScriptPrompt = useCallback(
    (scriptRelPath: string, itemDesc: string) => {
      const prompt = [
        `请使用内置 MCP 服务 ygo-rogue-mod (${mcpUrl || 'http://127.0.0.1:17850/mcp'}) 帮我编辑当前脚本：`,
        `- MOD ID: ${activeModId}`,
        `- 目标条目: ${itemDesc}`,
        `- 脚本路径: ${scriptRelPath}`,
        '',
        '请先调用 get_studio_context 获取当前脚本完整内容（如需参考原版效果可调用 get_vanilla_card_script），然后用 patch_mod_file 或 write_mod_file 直接修改保存，最后调用 validate_mod 与 trigger_hot_reload。',
        '我希望实现的效果是：',
      ].join('\n');
      navigator.clipboard.writeText(prompt).catch(() => {});
      onNotify(t.toastAiPromptCopied, 'ok');
    },
    [activeModId, mcpUrl, onNotify, t.toastAiPromptCopied],
  );

  const hydrateTrainer = (tr: StudioTrainerState) => {
    setTrainerForm({
      unlockAllCharacters: tr.unlockAllCharacters,
      unlockAllVariants: tr.unlockAllVariants,
      startingGold: tr.startingGold != null ? String(tr.startingGold) : '',
      startingGoldBonus: tr.startingGoldBonus != null ? String(tr.startingGoldBonus) : '',
      startingLp: tr.startingLp != null ? String(tr.startingLp) : '',
      freeShop: tr.freeShop,
      shopPriceMultiplier: tr.shopPriceMultiplier != null ? String(tr.shopPriceMultiplier) : '',
      rewardGoldMultiplier: tr.rewardGoldMultiplier != null ? String(tr.rewardGoldMultiplier) : '',
      startingRelics: tr.startingRelics.join(', '),
      inGameOverlay: tr.inGameOverlay,
    });
  };

  const selectItem = useCallback(
    (cat: StudioCategoryKey, raw: Record<string, unknown>, modIdOverride?: string) => {
      const mId = modIdOverride ?? activeModId;
      const key = String(raw.key ?? '');
      setSelectedKey(key);
      setSelectedSourceFilePath((raw.sourceFilePath as string | null) ?? null);
      setSelectedPreviewImg((raw.imageUrl as string | null) ?? null);
      setSelectedSubCards((raw.subCards as SubCardThumb[] | undefined) ?? []);

      if (cat === 'cards') {
        const c = raw as unknown as StudioCardItem;
        setCardForm({
          id: String(c.id),
          name: c.name || '',
          zhName: c.zhName || '',
          desc: c.desc || '',
          zhDesc: c.zhDesc || '',
          typeMask: c.typeMask || 2,
          race: c.race || 'Normal',
          attribute: c.attribute || 'DARK',
          level: String(c.level ?? 0),
          atk: String(c.atk ?? 0),
          def: String(c.def ?? 0),
          packId: c.packId || '',
          artPath: c.artLocalPath || '',
          luaScript: c.luaScript || '',
        });
      } else if (cat === 'characters') {
        const ch = raw as unknown as StudioCharacterItem;
        setCharForm({
          name: ch.name || '',
          zhName: ch.zhName || '',
          sprite: ch.sprite || 'dm01.png',
          race: ch.race || 'Spellcaster',
          attribute: ch.attribute || 'DARK',
          color: ch.color || '#38bdf8',
          startingRelic: ch.startingRelic || 'mage',
          startingPacks: (ch.startingPacks || []).join(', '),
          deckCards: (ch.deckCards || []).join(', '),
          unlockedByDefault: ch.unlockedByDefault ?? true,
        });
      } else if (cat === 'relics') {
        const r = raw as unknown as StudioRelicItem;
        setRelicForm({
          key: r.key || '',
          name: r.name || '',
          zhName: r.zhName || '',
          cost: String(r.cost ?? 80),
          tag: r.tag || 'utility',
          description: r.description || '',
          zhDescription: r.zhDescription || '',
          art: r.art || '',
          luaEffect: r.luaEffect || '',
        });
      } else if (cat === 'packs') {
        const p = raw as unknown as StudioPackItem;
        setPackForm({
          id: p.id || '',
          name: p.name || '',
          zhName: p.zhName || '',
          code: p.code || '',
          size: String(p.size ?? 9),
          commonIds: (p.commonIds || []).join(', '),
          rareIds: (p.rareIds || []).join(', '),
          superIds: (p.superIds || []).join(', '),
          ultraIds: (p.ultraIds || []).join(', '),
        });
      } else if (cat === 'decks') {
        const d = raw as unknown as StudioDeckItem;
        setDeckForm({
          id: d.id || '',
          name: d.name || '',
          zhName: d.zhName || '',
          art: d.art || 'SDK',
          cardIds: (d.cardIds || []).join(', '),
        });
      } else if (cat === 'code') {
        const f = raw as unknown as StudioCodeItem;
        setCodePath(f.path);
        if (mId) {
          setLoadingCode(true);
          const qs = new URLSearchParams({ modId: mId, path: f.path });
          apiGet<{ content: string }>(`/api/studio/file?${qs.toString()}`)
            .then((res) => setCodeContent(res.content))
            .catch((e) => onNotify((e as Error).message, 'err'))
            .finally(() => setLoadingCode(false));
        }
      }
    },
    [activeModId, onNotify],
  );

  const fetchCatalog = useCallback(
    async (modId: string, cat: StudioCategoryKey, q: string, offset: number, append: boolean, refresh = false) => {
      if (!modId) {
        setItems([]);
        setTotal(0);
        setHasMore(false);
        return;
      }
      const myReq = ++reqIdRef.current;
      setLoading(true);
      try {
        const qs = new URLSearchParams({
          modId,
          category: cat,
          q,
          offset: String(offset),
          limit: String(PAGE_SIZE),
          ...(refresh ? { refresh: '1' } : {}),
        });
        const data = await apiGet<StudioCatalogResponse>(`/api/studio/catalog?${qs.toString()}`);
        if (myReq !== reqIdRef.current) return;
        onCountsChange(data.counts);
        setTotal(data.total);
        setHasMore(data.hasMore);
        hydrateTrainer(data.trainer);
        setItems((prev) => (append ? [...prev, ...data.items] : data.items));

        if (!append) {
          const wanted = pendingSelectKeyRef.current;
          pendingSelectKeyRef.current = null;
          if (wanted) {
            const match = data.items.find((it) => String(it.key).toLowerCase() === wanted.toLowerCase());
            if (match) {
              selectItem(cat, match, modId);
              return;
            }
          }
          if (data.items.length > 0 && !incomingDraft) {
            selectItem(cat, data.items[0], modId);
          } else if (data.items.length === 0) {
            setSelectedKey(null);
            setSelectedSourceFilePath(null);
            setSelectedPreviewImg(null);
            setSelectedSubCards([]);
          }
        }
      } catch (e) {
        if (myReq === reqIdRef.current) onNotify((e as Error).message, 'err');
      } finally {
        if (myReq === reqIdRef.current) setLoading(false);
      }
    },
    [incomingDraft, onCountsChange, onNotify, selectItem],
  );

  useEffect(() => {
    if (!focusTarget) return;
    if (focusTarget.modId && focusTarget.modId !== activeModId) {
      onSelectActiveMod(focusTarget.modId);
    }
    pendingSelectKeyRef.current = focusTarget.key;
    onCategoryChange(focusTarget.category);
    setQuery('');
    fetchCatalog(focusTarget.modId || activeModId, focusTarget.category, '', 0, false, true);
    onClearFocusTarget();
  }, [focusTarget, activeModId, onSelectActiveMod, onCategoryChange, fetchCatalog, onClearFocusTarget]);

  useEffect(() => {
    if (!mcpSyncEvent || !activeModId) return;
    if (mcpSyncEvent.modId.toLowerCase() !== activeModId.toLowerCase()) return;
    pendingSelectKeyRef.current = selectedKey;
    fetchCatalog(activeModId, category, debouncedQuery, 0, false, true);
    if (category === 'code' && codePath) {
      apiGet<{ content: string }>(
        `/api/studio/file?modId=${encodeURIComponent(activeModId)}&path=${encodeURIComponent(codePath)}`,
      )
        .then((res) => setCodeContent(res.content ?? ''))
        .catch(() => {});
    }
    onNotify(fmt(t.toastMcpSynced, { path: mcpSyncEvent.path || mcpSyncEvent.tool }), 'info');
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mcpSyncEvent]);

  useEffect(() => {
    if (!incomingDraft) return;
    const { category: vCat, keepVanillaIdentity, detail } = incomingDraft;
    const raw = (detail.raw ?? {}) as Record<string, unknown>;
    onCategoryChange(vCat);
    setSelectedKey(null);
    setSelectedSourceFilePath(null);
    setSelectedPreviewImg(detail.imageUrl);
    setSelectedSubCards(detail.subCards ?? []);

    if (vCat === 'cards') {
      const baseId = Number(raw.id ?? 190010001);
      const baseName = String(raw.name ?? '');
      const baseZh = String(raw.zhName ?? '');
      const applyCardDraft = (targetId: number) => {
        setCardForm({
          id: String(targetId),
          name: keepVanillaIdentity ? baseName : `${baseName} (Custom)`,
          zhName: baseZh ? (keepVanillaIdentity ? baseZh : `${baseZh}（改）`) : '',
          desc: String(raw.desc ?? ''),
          zhDesc: String(raw.zhDesc ?? ''),
          typeMask: Number(raw.typeMask ?? 2),
          race: String(raw.race ?? 'Normal'),
          attribute: String(raw.attribute ?? 'DARK'),
          level: String(raw.level ?? 0),
          atk: String(raw.atk ?? 0),
          def: String(raw.def ?? 0),
          packId: '',
          artPath: '',
          luaScript: detail.luaScript || '',
        });
      };
      if (keepVanillaIdentity || !activeModId) {
        applyCardDraft(baseId);
      } else {
        apiGet<{ nextId: number }>(`/api/studio/suggest-card-id?modId=${encodeURIComponent(activeModId)}`)
          .then((r) => applyCardDraft(r.nextId))
          .catch(() => applyCardDraft(190010001));
      }
    } else if (vCat === 'characters') {
      const nm = String(raw.name ?? '');
      const zh = String(raw.zhName ?? '');
      setCharForm({
        name: keepVanillaIdentity ? nm : `${nm} Remix`,
        zhName: zh ? (keepVanillaIdentity ? zh : `${zh}（改）`) : '',
        sprite: String(raw.sprite ?? 'dm01.png'),
        race: String(raw.race ?? 'Spellcaster'),
        attribute: String(raw.attribute ?? 'DARK'),
        color: String(raw.color ?? '#38bdf8'),
        startingRelic: String(raw.startingRelic ?? 'mage'),
        startingPacks: ((raw.startingPacks as string[]) ?? []).join(', '),
        deckCards: ((raw.deckCards as string[]) ?? []).join(', '),
        unlockedByDefault: true,
      });
    } else if (vCat === 'relics') {
      const rk = String(raw.key ?? 'custom_relic');
      const nm = String(raw.name ?? '');
      const zh = String(raw.zhName ?? '');
      setRelicForm({
        key: keepVanillaIdentity ? rk : `${rk}_custom`,
        name: keepVanillaIdentity ? nm : `${nm} (Custom)`,
        zhName: zh ? (keepVanillaIdentity ? zh : `${zh}（改）`) : '',
        cost: String(raw.cost ?? 80),
        tag: String(raw.tag ?? 'utility'),
        description: String(raw.description ?? ''),
        zhDescription: String(raw.zhDescription ?? ''),
        art: String(raw.art ?? ''),
        luaEffect: presets?.luaRelicTemplates?.[0]?.code ?? 'Duel.Recover(tp, 1000, REASON_RULE)',
      });
    } else if (vCat === 'packs') {
      const pid = String(raw.id ?? 'MOD-PACK');
      const nm = String(raw.name ?? '');
      const zh = String(raw.zhName ?? '');
      setPackForm({
        id: keepVanillaIdentity ? pid : `${pid}-MOD`,
        name: keepVanillaIdentity ? nm : `${nm} (Custom)`,
        zhName: zh ? (keepVanillaIdentity ? zh : `${zh}（改）`) : '',
        code: String(raw.code ?? 'MOD'),
        size: String(raw.size ?? 9),
        commonIds: ((raw.commonIds as number[]) ?? []).join(', '),
        rareIds: ((raw.rareIds as number[]) ?? []).join(', '),
        superIds: ((raw.superIds as number[]) ?? []).join(', '),
        ultraIds: ((raw.ultraIds as number[]) ?? []).join(', '),
      });
    } else if (vCat === 'decks') {
      const did = String(raw.id ?? 'MOD-DECK');
      const nm = String(raw.name ?? '');
      const zh = String(raw.zhName ?? '');
      setDeckForm({
        id: keepVanillaIdentity ? did : `${did}-MOD`,
        name: keepVanillaIdentity ? nm : `${nm} (Custom)`,
        zhName: zh ? (keepVanillaIdentity ? zh : `${zh}（改）`) : '',
        art: String(raw.art ?? 'SDK'),
        cardIds: ((raw.cardIds as number[]) ?? []).join(', '),
      });
    }

    onClearIncomingDraft();
    onNotify(fmt(t.toastLoadedDraft, { title: detail.title }), 'info');
  }, [incomingDraft, activeModId, presets, onCategoryChange, onClearIncomingDraft, onNotify, t.toastLoadedDraft]);

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: 0 });
    fetchCatalog(activeModId, category, debouncedQuery, 0, false);
  }, [activeModId, category, debouncedQuery, fetchCatalog]);

  const loadMore = useCallback(() => {
    if (loading || !hasMore || !activeModId) return;
    fetchCatalog(activeModId, category, debouncedQuery, items.length, true);
  }, [loading, hasMore, activeModId, fetchCatalog, category, debouncedQuery, items.length]);

  const sentinelRef = useInfiniteScroll({
    hasMore,
    loading,
    onLoadMore: loadMore,
    rootRef: scrollRef,
  });

  const handleNewItem = async () => {
    setSelectedKey(null);
    setSelectedSourceFilePath(null);
    setSelectedPreviewImg(null);
    setSelectedSubCards([]);

    if (category === 'cards') {
      let nextId = 190010001;
      if (activeModId) {
        try {
          const r = await apiGet<{ nextId: number }>(`/api/studio/suggest-card-id?modId=${encodeURIComponent(activeModId)}`);
          nextId = r.nextId;
        } catch {
          // ignore
        }
      }
      setCardForm({
        id: String(nextId),
        name: 'Astral Draw',
        zhName: '星界抽卡',
        desc: 'Draw 2 cards.',
        zhDesc: '从卡组抽2张卡。',
        typeMask: 2,
        race: 'Normal',
        attribute: 'DARK',
        level: '0',
        atk: '0',
        def: '0',
        packId: '',
        artPath: '',
        luaScript: presets?.luaCardTemplates?.[1]?.code ?? '',
      });
    } else if (category === 'characters') {
      setCharForm({
        name: 'Custom Duelist',
        zhName: '自定义决斗者',
        sprite: 'dm01.png',
        race: 'Spellcaster',
        attribute: 'DARK',
        color: '#38bdf8',
        startingRelic: 'mage',
        startingPacks: 'EN-LOB',
        deckCards: 'Dark Magician, Dark Magician Girl, Kuriboh',
        unlockedByDefault: true,
      });
    } else if (category === 'relics') {
      setRelicForm({
        key: 'astral_blessing',
        name: 'Astral Blessing',
        zhName: '星界祝福',
        cost: '80',
        tag: 'utility',
        description: 'At the start of each duel, gain 1000 LP.',
        zhDescription: '决斗开始时，回复 1000 点基本分。',
        art: '',
        luaEffect: presets?.luaRelicTemplates?.[0]?.code ?? '',
      });
    } else if (category === 'packs') {
      setPackForm({
        id: 'MOD-PACK-01',
        name: 'Astral Expansion',
        zhName: '星界扩展包',
        code: 'AST1',
        size: '9',
        commonIds: '46986414, 38033121',
        rareIds: '89631139',
        superIds: '',
        ultraIds: '',
      });
    } else if (category === 'decks') {
      setDeckForm({
        id: 'MOD-DECK-01',
        name: 'Astral Starter',
        zhName: '星界预组',
        art: 'SDK',
        cardIds: '46986414, 46986414, 38033121, 40640057',
      });
    } else if (category === 'code') {
      setCodePath('scripts/custom-plugin.js');
      setCodeContent(
        `// Runs inside Yu-Gi-Oh! Rogue Duelist via window.YgoModAPI\nwindow.YgoModAPI?.registerPlugin?.({\n  id: 'custom-plugin',\n  onLoad(ctx) {\n    ctx.log('Plugin loaded!');\n    ctx.showToast('Custom MOD Plugin Active!', 'success');\n  }\n});\n`,
      );
    }
  };

  const getCategoryLabel = (cat: StudioCategoryKey) => {
    switch (cat) {
      case 'cards': return t.catCards;
      case 'characters': return t.catCharacters;
      case 'relics': return t.catRelics;
      case 'packs': return t.catPacks;
      case 'decks': return t.catDecks;
      case 'trainer': return t.catTrainer;
      case 'code': return t.catCode;
    }
  };

  const handleSaveCurrent = async () => {
    if (!activeModId) {
      onNotify(t.noModSelected, 'err');
      return;
    }
    setSaving(true);
    try {
      if (category === 'cards') {
        await apiPost('/api/studio/card/save', {
          modId: activeModId,
          ...cardForm,
          id: Number(cardForm.id),
          level: Number(cardForm.level) || 0,
          atk: Number(cardForm.atk) || 0,
          def: Number(cardForm.def) || 0,
        });
        pendingSelectKeyRef.current = cardForm.id;
      } else if (category === 'characters') {
        await apiPost('/api/studio/character/save', { modId: activeModId, ...charForm });
      } else if (category === 'relics') {
        await apiPost('/api/studio/relic/save', {
          modId: activeModId,
          ...relicForm,
          cost: Number(relicForm.cost) || 80,
        });
        pendingSelectKeyRef.current = relicForm.key;
      } else if (category === 'packs') {
        await apiPost('/api/studio/pack/save', {
          modId: activeModId,
          ...packForm,
          size: Number(packForm.size) || 9,
        });
        pendingSelectKeyRef.current = packForm.id;
      } else if (category === 'decks') {
        await apiPost('/api/studio/deck/save', { modId: activeModId, ...deckForm });
        pendingSelectKeyRef.current = deckForm.id;
      } else if (category === 'trainer') {
        await apiPost('/api/studio/trainer/save', {
          modId: activeModId,
          unlockAllCharacters: trainerForm.unlockAllCharacters,
          unlockAllVariants: trainerForm.unlockAllVariants,
          startingGold: trainerForm.startingGold ? Number(trainerForm.startingGold) : null,
          startingGoldBonus: trainerForm.startingGoldBonus ? Number(trainerForm.startingGoldBonus) : null,
          startingLp: trainerForm.startingLp ? Number(trainerForm.startingLp) : null,
          freeShop: trainerForm.freeShop,
          shopPriceMultiplier: trainerForm.shopPriceMultiplier ? Number(trainerForm.shopPriceMultiplier) : null,
          rewardGoldMultiplier: trainerForm.rewardGoldMultiplier ? Number(trainerForm.rewardGoldMultiplier) : null,
          startingRelics: trainerForm.startingRelics,
          inGameOverlay: trainerForm.inGameOverlay,
        });
      } else if (category === 'code') {
        await apiPost('/api/studio/file', {
          modId: activeModId,
          path: codePath,
          content: codeContent,
        });
        pendingSelectKeyRef.current = codePath;
      }
      onNotify(fmt(t.toastSavedStudio, { cat: getCategoryLabel(category) }), 'ok');
      await fetchCatalog(activeModId, category, debouncedQuery, 0, false, true);
      await onRefreshStatus();
    } catch (e) {
      onNotify((e as Error).message, 'err');
    } finally {
      setSaving(false);
    }
  };

  const handleDeleteCurrent = async () => {
    if (!activeModId || !selectedKey) return;
    if (!window.confirm(fmt(t.confirmDeleteItem, { cat: getCategoryLabel(category), key: selectedKey }))) return;
    try {
      await apiPost('/api/studio/item/delete', {
        modId: activeModId,
        category,
        key: selectedKey,
        sourceFilePath: selectedSourceFilePath,
      });
      onNotify(fmt(t.toastDeletedItem, { key: selectedKey }), 'info');
      await fetchCatalog(activeModId, category, debouncedQuery, 0, false, true);
      await onRefreshStatus();
    } catch (e) {
      onNotify((e as Error).message, 'err');
    }
  };

  const handlePickImage = async (target: 'card' | 'relic') => {
    try {
      const pick = await apiPost<{ path: string | null }>('/api/dialog/pick-file', {
        title: t.browseImg,
        kind: 'image',
      });
      if (!pick.path) return;
      if (target === 'card') {
        setCardForm((f) => ({ ...f, artPath: pick.path! }));
      } else {
        setRelicForm((f) => ({ ...f, art: pick.path! }));
      }
      setSelectedPreviewImg(`/api/studio/image?absPath=${encodeURIComponent(pick.path)}`);
    } catch (e) {
      onNotify((e as Error).message, 'err');
    }
  };

  const handleImportYdk = async () => {
    if (!activeModId) return;
    try {
      const pick = await apiPost<{ path: string | null }>('/api/dialog/pick-file', {
        title: t.importYdk,
        kind: 'ydk',
      });
      if (!pick.path) return;
      await apiPost('/api/studio/ydk/import', { modId: activeModId, sourcePath: pick.path });
      onNotify(t.toastImportedYdk, 'ok');
      await fetchCatalog(activeModId, 'decks', debouncedQuery, 0, false, true);
      await onRefreshStatus();
    } catch (e) {
      onNotify((e as Error).message, 'err');
    }
  };

  const handleScaffoldDsl = async () => {
    if (!activeModId) return;
    try {
      await apiPost('/api/studio/dsl/scaffold', { modId: activeModId });
      onNotify(t.toastScaffoldedDsl, 'ok');
      onCategoryChange('code');
      pendingSelectKeyRef.current = 'mod.lua';
      await fetchCatalog(activeModId, 'code', '', 0, false, true);
      await onRefreshStatus();
    } catch (e) {
      onNotify((e as Error).message, 'err');
    }
  };

  const handleValidate = async () => {
    if (!activeModId) return;
    try {
      const res = await apiGet<{ isValid: boolean; errors: string[]; warnings: string[] }>(
        `/api/studio/validate?modId=${encodeURIComponent(activeModId)}`,
      );
      if (res.isValid && res.warnings.length === 0) {
        onNotify(fmt(t.toastValidOk, { id: activeModId }), 'ok');
      } else if (res.isValid) {
        onNotify(fmt(t.toastValidWarn, { count: res.warnings.length, first: res.warnings[0] }), 'info');
      } else {
        onNotify(fmt(t.toastValidErr, { errors: res.errors.join('; ') }), 'err');
      }
    } catch (e) {
      onNotify((e as Error).message, 'err');
    }
  };

  const currentCardPreset = presets?.cardPresets.find((p) => p.typeMask === cardForm.typeMask);
  const isMonsterCard = currentCardPreset ? currentCardPreset.isMonster : (cardForm.typeMask & 1) !== 0;

  return (
    <div className="flex h-full min-h-0 flex-1 overflow-hidden">
      {/* CENTER PANE: Slim Single-Line Toolbar + Visual Infinite-Scroll Gallery (or File Explorer) */}
      <div
        className={`flex min-w-0 flex-col ${
          category === 'code' ? 'w-[260px] shrink-0 border-r border-border' : 'flex-1'
        }`}
      >
        <div className="flex h-11 shrink-0 items-center gap-1.5 border-b border-border bg-background px-3">
          <SearchField
            aria-label="Filter MOD items"
            variant="secondary"
            value={query}
            onChange={setQuery}
            className="min-w-0 flex-1 max-w-sm"
          >
            <SearchField.Group className="h-7">
              <SearchField.SearchIcon />
              <SearchField.Input placeholder={t.searchStudio} className="w-full text-xs" />
              <SearchField.ClearButton />
            </SearchField.Group>
          </SearchField>

          {category !== 'code' && (
            <div className="ml-auto flex shrink-0 items-center gap-1.5">
              {category !== 'trainer' && (
                <Button size="sm" variant="primary" onPress={handleNewItem} className="h-7 text-xs">
                  <FilePlus2 className="size-3.5" />
                  {t.newItem}
                </Button>
              )}
              <Button size="sm" variant="secondary" isDisabled={!activeModId} onPress={handleScaffoldDsl} className="h-7 text-xs">
                <Code2 className="size-3.5 text-accent" />
                <span className="hidden xl:inline">{t.scaffoldDsl}</span>
                <span className="xl:hidden">DSL</span>
              </Button>
              <Button size="sm" variant="secondary" isDisabled={!activeModId} onPress={handleValidate} className="h-7 text-xs">
                <CheckCircle2 className="size-3.5 text-success" />
                <span className="hidden xl:inline">{t.validateMod}</span>
              </Button>
            </div>
          )}
          {category === 'code' && (
            <Button isIconOnly size="sm" variant="primary" aria-label={t.newItem} onPress={handleNewItem} className="size-7 shrink-0">
              <FilePlus2 className="size-3.5" />
            </Button>
          )}
        </div>

        {/* Scrollable Gallery / File Tree */}
        <div ref={scrollRef} className="min-h-0 flex-1 overflow-y-auto p-3">
          {!activeModId ? (
            <div className="flex h-full flex-col items-center justify-center p-8 text-center text-xs text-muted">
              <p>{t.noModSelected}</p>
            </div>
          ) : category === 'trainer' ? (
            <div className="flex h-full flex-col items-center justify-center p-6 text-center space-y-3">
              <div className="flex size-14 items-center justify-center rounded-2xl border border-danger/30 bg-danger-soft">
                <Sliders className="size-7 text-danger" />
              </div>
              <h3 className="text-sm font-bold text-foreground">{t.trainerHeroTitle}</h3>
              <p className="max-w-sm text-xs leading-relaxed text-muted">{t.trainerHeroDesc}</p>
            </div>
          ) : category === 'code' ? (
            <div className="space-y-1">
              {items.map((raw) => {
                const f = raw as unknown as StudioCodeItem;
                const isSel = selectedKey === f.key || codePath === f.path;
                return (
                  <button
                    key={f.path}
                    type="button"
                    onClick={() => selectItem('code', raw)}
                    className={`flex w-full items-center gap-2 rounded-lg border px-2.5 py-1.5 text-left text-xs transition-colors ${
                      isSel
                        ? 'border-accent/60 bg-accent-soft/50 text-foreground font-medium'
                        : 'border-transparent text-muted hover:bg-surface-secondary hover:text-foreground'
                    }`}
                  >
                    <FileCode2 className="size-3.5 shrink-0 text-accent" />
                    <span className="min-w-0 flex-1 truncate font-mono text-[11px]">{f.path}</span>
                    <span className="rounded bg-surface-secondary px-1 py-0.5 font-mono text-[9px] text-muted">
                      {f.ext || 'TXT'}
                    </span>
                  </button>
                );
              })}
              {items.length === 0 && !loading && (
                <div className="py-10 text-center text-xs text-muted">{t.emptyGallery}</div>
              )}
            </div>
          ) : (
            <>
              {items.length === 0 && !loading ? (
                <div className="flex h-full flex-col items-center justify-center p-10 text-center text-xs text-muted">
                  <Sparkles className="mb-2 size-8 text-muted/35" />
                  <p>{t.emptyGallery}</p>
                </div>
              ) : (
                <div className="grid grid-cols-2 gap-2.5 sm:grid-cols-3 md:grid-cols-4 xl:grid-cols-4 2xl:grid-cols-5">
                  {items.map((raw) => {
                    const key = String(raw.key ?? '');
                    const isSel = selectedKey === key;
                    const zhName = String(raw.zhName ?? '');
                    const enName = String(raw.name ?? key);
                    const title = zhName || enName;
                    const imgUrl = raw.imageUrl as string | null;
                    const sourceTag = String(raw.sourceTag ?? '');
                    let subtitle = enName;
                    let badge = sourceTag || category;

                    if (category === 'cards') {
                      const c = raw as unknown as StudioCardItem;
                      subtitle = `#${c.id} · ${c.name}`;
                      badge = c.level > 0 ? `Lv${c.level} ${c.atk}/${c.def}` : c.displayType;
                    } else if (category === 'characters') {
                      const ch = raw as unknown as StudioCharacterItem;
                      subtitle = `${ch.race} / ${ch.attribute}`;
                      badge = fmt(t.cardsCountUnit, { n: ch.deckCards?.length ?? 0 });
                    } else if (category === 'relics') {
                      const r = raw as unknown as StudioRelicItem;
                      subtitle = r.key;
                      badge = `${r.cost}G · ${r.tag}`;
                    } else if (category === 'packs') {
                      const p = raw as unknown as StudioPackItem;
                      subtitle = p.id;
                      badge = fmt(t.cardsCountUnit, { n: p.totalCards });
                    } else if (category === 'decks') {
                      const d = raw as unknown as StudioDeckItem;
                      subtitle = d.id;
                      badge = fmt(t.cardsCountUnit, { n: d.cardIds?.length ?? 0 }) + (d.isYdk ? ' · YDK' : '');
                    }

                    return (
                      <button
                        key={`${category}-${key}`}
                        type="button"
                        onClick={() => selectItem(category, raw)}
                        className={`group flex flex-col overflow-hidden rounded-xl border text-left transition-all duration-150 ${
                          isSel
                            ? 'border-accent bg-accent-soft/40 ring-1 ring-accent/40'
                            : 'border-border/80 bg-surface hover:-translate-y-0.5 hover:border-foreground/25'
                        }`}
                      >
                        <div className="relative flex aspect-[3/4] w-full items-center justify-center overflow-hidden bg-background p-1.5">
                          {imgUrl ? (
                            <img
                              src={resolveApiUrl(imgUrl)}
                              alt={title}
                              loading="lazy"
                              decoding="async"
                              className="h-full w-full object-contain transition-transform duration-200 group-hover:scale-[1.03]"
                            />
                          ) : (
                            <ImageIcon className="size-7 text-muted/35" />
                          )}
                          {sourceTag && (
                            <span className="absolute right-1.5 top-1.5 rounded bg-background/90 px-1.5 py-0.5 font-mono text-[9px] text-muted border border-border/70">
                              {sourceTag}
                            </span>
                          )}
                        </div>
                        <div className="flex flex-1 flex-col justify-between gap-1 p-2">
                          <div>
                            <div className="truncate text-xs font-semibold text-foreground">{title}</div>
                            <div className="truncate text-[10px] text-muted">{subtitle}</div>
                          </div>
                          <span className="w-fit max-w-full truncate rounded-md bg-surface-secondary px-1.5 py-0.5 font-mono text-[10px] text-accent">
                            {badge}
                          </span>
                        </div>
                      </button>
                    );
                  })}
                </div>
              )}

              <div ref={sentinelRef} className="flex items-center justify-center py-4 text-xs text-muted">
                {loading ? (
                  <span className="inline-flex items-center gap-2 text-accent">
                    <Spinner size="sm" />
                    {t.loadingMore}
                  </span>
                ) : hasMore ? (
                  <span>{fmt(t.loadedProgress, { loaded: items.length, total })}</span>
                ) : items.length > 0 ? (
                  <span>
                    {t.allLoaded} ({items.length})
                  </span>
                ) : null}
              </div>
            </>
          )}
        </div>
      </div>

      {/* RIGHT INSPECTOR / CODE EDITOR PANE (Pinned on desktop >= 1024px; slide-over sheet on narrow screens) */}
      {(inspectorOpen || category === 'code' || category === 'trainer') && (
        <aside
          className={`flex min-h-0 flex-col overflow-hidden border-l border-border bg-background lg:bg-surface/40 ${
            category === 'code'
              ? 'flex-1'
              : 'fixed inset-y-0 top-10 right-0 z-20 w-full max-w-[460px] shadow-2xl lg:static lg:z-auto lg:w-[440px] lg:max-w-none lg:shrink-0 lg:shadow-none xl:w-[500px]'
          }`}
        >
          {/* Sticky Inspector Header */}
          <div className="flex h-11 shrink-0 items-center justify-between gap-2 border-b border-border bg-background px-3.5">
            <div className="min-w-0 truncate font-mono text-xs font-semibold text-foreground">
              {category === 'code'
                ? codePath
                : selectedKey
                  ? fmt(t.editingHeader, { cat: getCategoryLabel(category), key: selectedKey })
                  : fmt(t.creatingHeader, { cat: getCategoryLabel(category) })}
            </div>
            <div className="flex shrink-0 items-center gap-1.5">
              {selectedKey && category !== 'trainer' && category !== 'code' && (
                <Button size="sm" variant="danger-soft" onPress={handleDeleteCurrent} className="h-7 px-2.5 text-xs">
                  <Trash2 className="size-3.5" />
                  {t.deleteItem}
                </Button>
              )}
              <Button
                size="sm"
                isPending={saving}
                isDisabled={!activeModId}
                onPress={handleSaveCurrent}
                className="button--success h-7 px-2.5 text-xs font-semibold"
              >
                {({ isPending }) => (
                  <>
                    {isPending ? <Spinner color="current" size="sm" /> : <Save className="size-3.5" />}
                    {t.saveHotReload}
                  </>
                )}
              </Button>
            </div>
          </div>

          {/* Scrollable Editor Body */}
          <div className="min-h-0 flex-1 overflow-y-auto p-3.5 space-y-3">
            {/* CODE / DSL FULL-HEIGHT CODEMIRROR 6 */}
            {category === 'code' && (
              <div className="flex h-full flex-col gap-2.5">
                <div className="flex items-end gap-2">
                  <TextField fullWidth variant="secondary" value={codePath} onChange={setCodePath}>
                    <Label className="text-[11px] text-muted">{t.relativeFilePath}</Label>
                    <Input className="h-8 font-mono text-xs" placeholder="mod.lua" />
                  </TextField>
                  <Button size="sm" variant="secondary" isDisabled={!activeModId} onPress={handleScaffoldDsl} className="h-8 shrink-0 text-xs">
                    <Code2 className="size-3.5 text-accent" />
                    {t.scaffoldDsl}
                  </Button>
                  <Button size="sm" variant="secondary" isDisabled={!activeModId} onPress={handleValidate} className="h-8 shrink-0 text-xs">
                    <CheckCircle2 className="size-3.5 text-success" />
                    {t.validateMod}
                  </Button>
                </div>
                <div className="min-h-0 flex-1">
                  {loadingCode ? (
                    <div className="flex h-64 items-center justify-center">
                      <Spinner size="md" />
                    </div>
                  ) : (
                    <CodeEditor
                      value={codeContent}
                      onChange={setCodeContent}
                      mode={detectCodeMode(codePath)}
                      filePath={codePath}
                      height="calc(100vh - 210px)"
                      onSave={handleSaveCurrent}
                      title={codePath}
                      hint={t.ctrlSHint}
                      copyLabel={t.copy}
                      copiedLabel={t.copied}
                      expandLabel={t.expandEditor}
                      exitExpandLabel={t.exitExpandEditor}
                      aiPromptLabel={t.aiEditScriptBtn}
                      onCopyAiPrompt={() => copyAiScriptPrompt(codePath, codePath)}
                    />
                  )}
                </div>
              </div>
            )}

            {/* CARD VISUAL EDITOR + CODEMIRROR 6 LUA */}
            {category === 'cards' && (
              <div className="space-y-3">
                <Card variant="default" className="p-3">
                  <div className="flex gap-3">
                    <div className="flex h-32 w-24 shrink-0 items-center justify-center overflow-hidden rounded-lg border border-border bg-background p-1">
                      {selectedPreviewImg ? (
                        <img src={resolveApiUrl(selectedPreviewImg)} alt="" className="h-full w-full object-contain" />
                      ) : (
                        <ImageIcon className="size-6 text-muted/40" />
                      )}
                    </div>
                    <div className="min-w-0 flex-1 space-y-2">
                      <div className="grid grid-cols-2 gap-2">
                        <div className="flex items-end gap-1">
                          <StudioField
                            label={t.cardIdLabel}
                            mono
                            value={cardForm.id}
                            onChange={(v) => setCardForm({ ...cardForm, id: v })}
                          />
                          <Button
                            size="sm"
                            variant="tertiary"
                            onPress={async () => {
                              if (!activeModId) return;
                              const r = await apiGet<{ nextId: number }>(
                                `/api/studio/suggest-card-id?modId=${encodeURIComponent(activeModId)}`,
                              );
                              setCardForm((f) => ({ ...f, id: String(r.nextId) }));
                            }}
                            className="h-8 px-2 text-[11px]"
                          >
                            {t.autoId}
                          </Button>
                        </div>

                        <Select
                          aria-label={t.cardTypeLabel}
                          fullWidth
                          variant="secondary"
                          value={String(cardForm.typeMask)}
                          onChange={(k) => {
                            if (!k) return;
                            const mask = Number(k);
                            const preset = presets?.cardPresets.find((p) => p.typeMask === mask);
                            setCardForm((f) => ({
                              ...f,
                              typeMask: mask,
                              race: preset?.defaultRace || f.race,
                            }));
                          }}
                        >
                          <Label className="text-[11px] text-muted">{t.cardTypeLabel}</Label>
                          <Select.Trigger className="h-8 text-xs">
                            <Select.Value />
                            <Select.Indicator />
                          </Select.Trigger>
                          <Select.Popover>
                            <ListBox>
                              {(presets?.cardPresets ?? []).map((p) => (
                                <ListBox.Item key={p.typeMask} id={String(p.typeMask)} textValue={p.label}>
                                  {p.label}
                                  <ListBox.ItemIndicator />
                                </ListBox.Item>
                              ))}
                            </ListBox>
                          </Select.Popover>
                        </Select>
                      </div>

                      <div className="flex items-end gap-1.5">
                        <StudioField
                          label={t.artworkFileLabel}
                          value={cardForm.artPath}
                          onChange={(v) => setCardForm({ ...cardForm, artPath: v })}
                        />
                        <Button size="sm" variant="secondary" onPress={() => handlePickImage('card')} className="h-8 shrink-0 text-xs">
                          <FolderOpen className="size-3.5 text-accent" />
                          {t.browseImg}
                        </Button>
                      </div>
                    </div>
                  </div>
                </Card>

                <div className="grid grid-cols-2 gap-2.5">
                  <StudioField label={t.enNameLabel} value={cardForm.name} onChange={(v) => setCardForm({ ...cardForm, name: v })} />
                  <StudioField label={t.zhNameLabel} value={cardForm.zhName} onChange={(v) => setCardForm({ ...cardForm, zhName: v })} />
                </div>

                {isMonsterCard && (
                  <div className="grid grid-cols-5 gap-2">
                    <Select
                      aria-label={t.raceLabel}
                      fullWidth
                      variant="secondary"
                      value={cardForm.race}
                      onChange={(k) => k && setCardForm({ ...cardForm, race: String(k) })}
                    >
                      <Label className="text-[11px] text-muted">{t.raceLabel}</Label>
                      <Select.Trigger className="h-8 text-xs">
                        <Select.Value />
                        <Select.Indicator />
                      </Select.Trigger>
                      <Select.Popover>
                        <ListBox>
                          {Object.keys(presets?.monsterRaces ?? { Spellcaster: '0x2' }).map((r) => (
                            <ListBox.Item key={r} id={r} textValue={r}>
                              {r}
                              <ListBox.ItemIndicator />
                            </ListBox.Item>
                          ))}
                        </ListBox>
                      </Select.Popover>
                    </Select>

                    <Select
                      aria-label={t.attrLabel}
                      fullWidth
                      variant="secondary"
                      value={cardForm.attribute}
                      onChange={(k) => k && setCardForm({ ...cardForm, attribute: String(k) })}
                    >
                      <Label className="text-[11px] text-muted">{t.attrLabel}</Label>
                      <Select.Trigger className="h-8 text-xs">
                        <Select.Value />
                        <Select.Indicator />
                      </Select.Trigger>
                      <Select.Popover>
                        <ListBox>
                          {Object.keys(presets?.attributes ?? { DARK: 32 })
                            .filter((a) => a.trim().length > 0)
                            .map((a) => (
                              <ListBox.Item key={a} id={a} textValue={a}>
                                {a}
                                <ListBox.ItemIndicator />
                              </ListBox.Item>
                            ))}
                        </ListBox>
                      </Select.Popover>
                    </Select>

                    <StudioField label={t.levelLabel} value={cardForm.level} onChange={(v) => setCardForm({ ...cardForm, level: v })} />
                    <StudioField label={t.atkLabel} value={cardForm.atk} onChange={(v) => setCardForm({ ...cardForm, atk: v })} />
                    <StudioField label={t.defLabel} value={cardForm.def} onChange={(v) => setCardForm({ ...cardForm, def: v })} />
                  </div>
                )}

                <div className="grid grid-cols-2 gap-2.5">
                  <StudioTextArea label={t.enDescLabel} value={cardForm.desc} onChange={(v) => setCardForm({ ...cardForm, desc: v })} />
                  <StudioTextArea label={t.zhDescLabel} value={cardForm.zhDesc} onChange={(v) => setCardForm({ ...cardForm, zhDesc: v })} />
                </div>

                <StudioField
                  label={t.addToPackLabel}
                  value={cardForm.packId}
                  onChange={(v) => setCardForm({ ...cardForm, packId: v })}
                />

                <div className="space-y-1.5">
                  <div className="flex items-center justify-between gap-2">
                    <span className="text-xs font-semibold text-accent">{t.luaScriptLabel}</span>
                    <Select
                      aria-label={t.insertLuaTemplate}
                      variant="secondary"
                      placeholder={t.insertLuaTemplate}
                      className="w-52"
                      value={null}
                      onChange={(k) => {
                        const tpl = presets?.luaCardTemplates.find((x) => x.key === String(k));
                        if (tpl) setCardForm((f) => ({ ...f, luaScript: tpl.code }));
                      }}
                    >
                      <Select.Trigger className="h-7 text-[11px]">
                        <Select.Value />
                        <Select.Indicator />
                      </Select.Trigger>
                      <Select.Popover>
                        <ListBox>
                          {(presets?.luaCardTemplates ?? []).map((tpl) => (
                            <ListBox.Item key={tpl.key} id={tpl.key} textValue={tpl.label}>
                              {tpl.label}
                              <ListBox.ItemIndicator />
                            </ListBox.Item>
                          ))}
                        </ListBox>
                      </Select.Popover>
                    </Select>
                  </div>
                  <CodeEditor
                    value={cardForm.luaScript}
                    onChange={(val) => setCardForm((f) => ({ ...f, luaScript: val }))}
                    mode="lua"
                    height="250px"
                    onSave={handleSaveCurrent}
                    title={`lua/cards/c${cardForm.id || '0'}.lua`}
                    hint={t.ctrlSHint}
                    copyLabel={t.copy}
                    copiedLabel={t.copied}
                    expandLabel={t.expandEditor}
                    exitExpandLabel={t.exitExpandEditor}
                    aiPromptLabel={t.aiEditScriptBtn}
                    onCopyAiPrompt={() =>
                      copyAiScriptPrompt(
                        `lua/cards/c${cardForm.id || '0'}.lua`,
                        `#${cardForm.id} ${cardForm.zhName || cardForm.name}`,
                      )
                    }
                  />
                </div>
              </div>
            )}

            {/* CHARACTER VISUAL EDITOR */}
            {category === 'characters' && (
              <div className="space-y-3">
                <PreviewWithSubCards imageUrl={selectedPreviewImg} subCards={selectedSubCards} label={t.subCardsLabel} />
                <div className="grid grid-cols-2 gap-2.5">
                  <StudioField label={t.charEnNameLabel} value={charForm.name} onChange={(v) => setCharForm({ ...charForm, name: v })} />
                  <StudioField label={t.charZhNameLabel} value={charForm.zhName} onChange={(v) => setCharForm({ ...charForm, zhName: v })} />
                  <StudioField label={t.charSpriteLabel} value={charForm.sprite} onChange={(v) => setCharForm({ ...charForm, sprite: v })} />
                  <StudioField label={t.charColorLabel} value={charForm.color} onChange={(v) => setCharForm({ ...charForm, color: v })} />
                  <StudioField label={t.charRaceLabel} value={charForm.race} onChange={(v) => setCharForm({ ...charForm, race: v })} />
                  <StudioField label={t.charAttrLabel} value={charForm.attribute} onChange={(v) => setCharForm({ ...charForm, attribute: v })} />
                  <StudioField label={t.charRelicLabel} value={charForm.startingRelic} onChange={(v) => setCharForm({ ...charForm, startingRelic: v })} />
                  <StudioField label={t.charPacksLabel} value={charForm.startingPacks} onChange={(v) => setCharForm({ ...charForm, startingPacks: v })} />
                </div>
                <StudioTextArea
                  label={t.charDeckLabel}
                  rows={4}
                  value={charForm.deckCards}
                  onChange={(v) => setCharForm({ ...charForm, deckCards: v })}
                />
                <Switch
                  size="sm"
                  isSelected={charForm.unlockedByDefault}
                  onChange={(v) => setCharForm({ ...charForm, unlockedByDefault: v })}
                >
                  <Switch.Content>
                    <Switch.Control>
                      <Switch.Thumb />
                    </Switch.Control>
                    <span className="text-xs text-foreground">{t.charUnlockedLabel}</span>
                  </Switch.Content>
                </Switch>
              </div>
            )}

            {/* RELIC VISUAL EDITOR + CODEMIRROR 6 LUA */}
            {category === 'relics' && (
              <div className="space-y-3">
                <Card variant="default" className="p-3">
                  <div className="flex gap-3">
                    <div className="flex size-24 shrink-0 items-center justify-center overflow-hidden rounded-lg border border-border bg-background p-1">
                      {selectedPreviewImg ? (
                        <img src={resolveApiUrl(selectedPreviewImg)} alt="" className="h-full w-full object-contain" />
                      ) : (
                        <Wand2 className="size-6 text-muted/40" />
                      )}
                    </div>
                    <div className="min-w-0 flex-1 space-y-2">
                      <div className="grid grid-cols-3 gap-2">
                        <StudioField label={t.relicKeyLabel} mono value={relicForm.key} onChange={(v) => setRelicForm({ ...relicForm, key: v })} />
                        <StudioField label={t.relicCostLabel} value={relicForm.cost} onChange={(v) => setRelicForm({ ...relicForm, cost: v })} />
                        <StudioField label={t.relicTagLabel} value={relicForm.tag} onChange={(v) => setRelicForm({ ...relicForm, tag: v })} />
                      </div>
                      <div className="flex items-end gap-1.5">
                        <StudioField
                          label={t.relicArtLabel}
                          value={relicForm.art}
                          onChange={(v) => setRelicForm({ ...relicForm, art: v })}
                        />
                        <Button size="sm" variant="secondary" onPress={() => handlePickImage('relic')} className="h-8 shrink-0 text-xs">
                          <FolderOpen className="size-3.5 text-warning" />
                          {t.browseImg}
                        </Button>
                      </div>
                    </div>
                  </div>
                </Card>

                <div className="grid grid-cols-2 gap-2.5">
                  <StudioField label={t.relicEnNameLabel} value={relicForm.name} onChange={(v) => setRelicForm({ ...relicForm, name: v })} />
                  <StudioField label={t.relicZhNameLabel} value={relicForm.zhName} onChange={(v) => setRelicForm({ ...relicForm, zhName: v })} />
                </div>
                <div className="grid grid-cols-2 gap-2.5">
                  <StudioTextArea label={t.relicEnDescLabel} value={relicForm.description} onChange={(v) => setRelicForm({ ...relicForm, description: v })} />
                  <StudioTextArea label={t.relicZhDescLabel} value={relicForm.zhDescription} onChange={(v) => setRelicForm({ ...relicForm, zhDescription: v })} />
                </div>

                <div className="space-y-1.5">
                  <div className="flex items-center justify-between gap-2">
                    <span className="text-xs font-semibold text-warning">{t.relicLuaLabel}</span>
                    <Select
                      aria-label={t.insertRelicTemplate}
                      variant="secondary"
                      placeholder={t.insertRelicTemplate}
                      className="w-52"
                      value={null}
                      onChange={(k) => {
                        const tpl = presets?.luaRelicTemplates.find((x) => x.key === String(k));
                        if (tpl) setRelicForm((f) => ({ ...f, luaEffect: tpl.code }));
                      }}
                    >
                      <Select.Trigger className="h-7 text-[11px]">
                        <Select.Value />
                        <Select.Indicator />
                      </Select.Trigger>
                      <Select.Popover>
                        <ListBox>
                          {(presets?.luaRelicTemplates ?? []).map((tpl) => (
                            <ListBox.Item key={tpl.key} id={tpl.key} textValue={tpl.label}>
                              {tpl.label}
                              <ListBox.ItemIndicator />
                            </ListBox.Item>
                          ))}
                        </ListBox>
                      </Select.Popover>
                    </Select>
                  </div>
                  <CodeEditor
                    value={relicForm.luaEffect}
                    onChange={(val) => setRelicForm((f) => ({ ...f, luaEffect: val }))}
                    mode="lua"
                    height="230px"
                    onSave={handleSaveCurrent}
                    title={`relic:${relicForm.key || 'custom'}.lua`}
                    hint={t.ctrlSHint}
                    copyLabel={t.copy}
                    copiedLabel={t.copied}
                    expandLabel={t.expandEditor}
                    exitExpandLabel={t.exitExpandEditor}
                    aiPromptLabel={t.aiEditScriptBtn}
                    onCopyAiPrompt={() =>
                      copyAiScriptPrompt(
                        selectedSourceFilePath || `relics/custom-relics.json`,
                        `Relic ${relicForm.key} (${relicForm.zhName || relicForm.name})`,
                      )
                    }
                  />
                </div>
              </div>
            )}

            {/* PACK VISUAL EDITOR */}
            {category === 'packs' && (
              <div className="space-y-3">
                <PreviewWithSubCards imageUrl={selectedPreviewImg} subCards={selectedSubCards} label={t.subCardsLabel} />
                <div className="grid grid-cols-3 gap-2">
                  <StudioField label={t.packIdLabel} mono value={packForm.id} onChange={(v) => setPackForm({ ...packForm, id: v })} />
                  <StudioField label={t.packCodeLabel} mono value={packForm.code} onChange={(v) => setPackForm({ ...packForm, code: v })} />
                  <StudioField label={t.packSizeLabel} value={packForm.size} onChange={(v) => setPackForm({ ...packForm, size: v })} />
                </div>
                <div className="grid grid-cols-2 gap-2.5">
                  <StudioField label={t.packEnNameLabel} value={packForm.name} onChange={(v) => setPackForm({ ...packForm, name: v })} />
                  <StudioField label={t.packZhNameLabel} value={packForm.zhName} onChange={(v) => setPackForm({ ...packForm, zhName: v })} />
                </div>
                <StudioTextArea label={t.packCommonLabel} rows={2} value={packForm.commonIds} onChange={(v) => setPackForm({ ...packForm, commonIds: v })} />
                <StudioTextArea label={t.packRareLabel} rows={2} value={packForm.rareIds} onChange={(v) => setPackForm({ ...packForm, rareIds: v })} />
                <StudioTextArea label={t.packSuperLabel} rows={2} value={packForm.superIds} onChange={(v) => setPackForm({ ...packForm, superIds: v })} />
                <StudioTextArea label={t.packUltraLabel} rows={2} value={packForm.ultraIds} onChange={(v) => setPackForm({ ...packForm, ultraIds: v })} />
              </div>
            )}

            {/* PRECONSTRUCTED DECK EDITOR */}
            {category === 'decks' && (
              <div className="space-y-3">
                <PreviewWithSubCards imageUrl={selectedPreviewImg} subCards={selectedSubCards} label={t.subCardsLabel} />
                <div className="flex justify-end">
                  <Button size="sm" variant="secondary" isDisabled={!activeModId} onPress={handleImportYdk} className="h-7 text-xs">
                    <Upload className="size-3.5 text-accent" />
                    {t.importYdk}
                  </Button>
                </div>
                <div className="grid grid-cols-2 gap-2.5">
                  <StudioField label={t.deckIdLabel} mono value={deckForm.id} onChange={(v) => setDeckForm({ ...deckForm, id: v })} />
                  <StudioField label={t.deckArtLabel} mono value={deckForm.art} onChange={(v) => setDeckForm({ ...deckForm, art: v })} />
                  <StudioField label={t.deckEnNameLabel} value={deckForm.name} onChange={(v) => setDeckForm({ ...deckForm, name: v })} />
                  <StudioField label={t.deckZhNameLabel} value={deckForm.zhName} onChange={(v) => setDeckForm({ ...deckForm, zhName: v })} />
                </div>
                <StudioTextArea
                  label={t.deckCardIdsLabel}
                  rows={6}
                  value={deckForm.cardIds}
                  onChange={(v) => setDeckForm({ ...deckForm, cardIds: v })}
                />
              </div>
            )}

            {/* TRAINER VISUAL EDITOR */}
            {category === 'trainer' && (
              <div className="space-y-3.5">
                <Card variant="default" className="p-3.5">
                  <div className="grid grid-cols-2 gap-3">
                    <Switch
                      size="sm"
                      isSelected={trainerForm.unlockAllCharacters}
                      onChange={(v) => setTrainerForm({ ...trainerForm, unlockAllCharacters: v })}
                    >
                      <Switch.Content>
                        <Switch.Control><Switch.Thumb /></Switch.Control>
                        <span className="text-xs">{t.trainerUnlockChars}</span>
                      </Switch.Content>
                    </Switch>
                    <Switch
                      size="sm"
                      isSelected={trainerForm.unlockAllVariants}
                      onChange={(v) => setTrainerForm({ ...trainerForm, unlockAllVariants: v })}
                    >
                      <Switch.Content>
                        <Switch.Control><Switch.Thumb /></Switch.Control>
                        <span className="text-xs">{t.trainerUnlockVariants}</span>
                      </Switch.Content>
                    </Switch>
                    <Switch
                      size="sm"
                      isSelected={trainerForm.freeShop}
                      onChange={(v) => setTrainerForm({ ...trainerForm, freeShop: v })}
                    >
                      <Switch.Content>
                        <Switch.Control><Switch.Thumb /></Switch.Control>
                        <span className="text-xs">{t.trainerFreeShop}</span>
                      </Switch.Content>
                    </Switch>
                    <Switch
                      size="sm"
                      isSelected={trainerForm.inGameOverlay}
                      onChange={(v) => setTrainerForm({ ...trainerForm, inGameOverlay: v })}
                    >
                      <Switch.Content>
                        <Switch.Control><Switch.Thumb /></Switch.Control>
                        <span className="text-xs">{t.trainerF8Overlay}</span>
                      </Switch.Content>
                    </Switch>
                  </div>
                </Card>

                <div className="grid grid-cols-2 gap-2.5">
                  <StudioField
                    label={t.trainerStartGold}
                    value={trainerForm.startingGold}
                    onChange={(v) => setTrainerForm({ ...trainerForm, startingGold: v })}
                  />
                  <StudioField
                    label={t.trainerGoldBonus}
                    value={trainerForm.startingGoldBonus}
                    onChange={(v) => setTrainerForm({ ...trainerForm, startingGoldBonus: v })}
                  />
                  <StudioField
                    label={t.trainerStartLp}
                    value={trainerForm.startingLp}
                    onChange={(v) => setTrainerForm({ ...trainerForm, startingLp: v })}
                  />
                  <StudioField
                    label={t.trainerShopMult}
                    value={trainerForm.shopPriceMultiplier}
                    onChange={(v) => setTrainerForm({ ...trainerForm, shopPriceMultiplier: v })}
                  />
                  <StudioField
                    label={t.trainerRewardMult}
                    value={trainerForm.rewardGoldMultiplier}
                    onChange={(v) => setTrainerForm({ ...trainerForm, rewardGoldMultiplier: v })}
                  />
                  <StudioField
                    label={t.trainerStartRelics}
                    value={trainerForm.startingRelics}
                    onChange={(v) => setTrainerForm({ ...trainerForm, startingRelics: v })}
                  />
                </div>
              </div>
            )}
          </div>
        </aside>
      )}
    </div>
  );
}

function StudioField({
  label,
  value,
  onChange,
  mono = false,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  mono?: boolean;
}) {
  return (
    <TextField fullWidth variant="secondary" value={value} onChange={onChange}>
      <Label className="text-[11px] text-muted">{label}</Label>
      <Input className={`h-8 text-xs ${mono ? 'font-mono' : ''}`} />
    </TextField>
  );
}

function StudioTextArea({
  label,
  value,
  onChange,
  rows = 3,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  rows?: number;
}) {
  return (
    <TextField fullWidth variant="secondary" value={value} onChange={onChange}>
      <Label className="text-[11px] text-muted">{label}</Label>
      <TextArea rows={rows} className="text-xs" />
    </TextField>
  );
}

function PreviewWithSubCards({
  imageUrl,
  subCards,
  label,
}: {
  imageUrl: string | null;
  subCards: SubCardThumb[];
  label: string;
}) {
  if (!imageUrl && subCards.length === 0) return null;
  return (
    <Card variant="default" className="p-3 gap-2">
      {imageUrl && (
        <div className="flex justify-center">
          <img src={resolveApiUrl(imageUrl)} alt="" className="h-28 rounded-lg object-contain" />
        </div>
      )}
      {subCards.length > 0 && (
        <div>
          <div className="mb-1.5 flex items-center justify-between text-[11px] font-semibold text-warning">
            <span>{label}</span>
            <Chip size="sm" variant="soft">{subCards.length}</Chip>
          </div>
          <div className="flex gap-1.5 overflow-x-auto pb-1">
            {subCards.map((sc, i) => (
              <div
                key={`${sc.id}-${i}`}
                className="flex w-14 shrink-0 flex-col items-center rounded-lg border border-border/70 bg-background p-0.5 text-center"
                title={`#${sc.id} ${sc.name}`}
              >
                {sc.imageUrl ? (
                  <img src={resolveApiUrl(sc.imageUrl)} alt={sc.name} loading="lazy" className="h-16 w-full object-contain" />
                ) : (
                  <div className="flex h-16 w-full items-center justify-center">
                    <ImageIcon className="size-4 text-muted/40" />
                  </div>
                )}
                <span className="w-full truncate text-[9px] text-foreground/85">{sc.name}</span>
              </div>
            ))}
          </div>
        </div>
      )}
    </Card>
  );
}
