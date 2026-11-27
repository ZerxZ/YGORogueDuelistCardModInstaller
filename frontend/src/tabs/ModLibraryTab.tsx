import { useEffect, useMemo, useRef, useState } from 'react';
import {
  Alert,
  Button,
  Card,
  Checkbox,
  Chip,
  Input,
  Label,
  ListBox,
  Modal,
  Select,
  Separator,
  Surface,
  Switch,
  TextArea,
  TextField,
} from '@heroui/react';
import {
  Archive,
  ArrowDown,
  ArrowUp,
  BookOpen,
  Bot,
  CheckCircle2,
  CheckSquare,
  ChevronDown,
  ChevronRight,
  Code2,
  Copy,
  FolderInput,
  FolderOpen,
  FolderPlus,
  GripVertical,
  ImagePlus,
  Info,
  Layers,
  ListChecks,
  ListOrdered,
  Package,
  Palette,
  Pencil,
  Plus,
  RefreshCw,
  Save,
  Search,
  Sliders,
  Sparkles,
  Square,
  Tag,
  Trash2,
  Upload,
  User,
  Wand2,
  X,
} from 'lucide-react';
import { apiPost, apiUploadBinary, resolveApiUrl } from '../api';
import { fmt, type I18nDict } from '../i18n';
import type { Lang, ManagerStatus, ModCategoryKey, ModEntry } from '../types';

interface OrganizePlanItem {
  modId: string;
  modName: string;
  oldCategory: string;
  newCategory: string;
  oldOrder: number;
  newOrder: number;
  reason: string;
}

interface OrganizePlanResp {
  ok: boolean;
  applied: boolean;
  mode: string;
  summary: string;
  items: OrganizePlanItem[];
  suggestedCustomCategories?: string[];
}

interface ModLibraryTabProps {
  status: ManagerStatus;
  t: I18nDict;
  lang: Lang;
  selectedModId: string;
  inspectorOpen: boolean;
  onSelectMod: (id: string) => void;
  onRefresh: () => Promise<void>;
  onOpenInStudio: (modId: string) => void;
  onOpenNewModModal: () => void;
  onOpenModGuide: () => void;
  onCopyAiPrompt: () => void;
  onCreateExampleMod: () => Promise<void>;
  onNotify: (msg: string, kind?: 'ok' | 'err' | 'info') => void;
}

export const MOD_CATEGORY_LIST: ModCategoryKey[] = [
  'all',
  'cards',
  'characters',
  'relics',
  'packs_decks',
  'ui_theme',
  'script_plugin',
  'trainer',
  'overhaul',
  'other',
];

export function getModCategoryLabel(cat: string | null | undefined, t: I18nDict): string {
  switch ((cat ?? '').toLowerCase()) {
    case 'all':
      return t.modCatAll;
    case 'cards':
      return t.modCatCards;
    case 'characters':
      return t.modCatCharacters;
    case 'relics':
      return t.modCatRelics;
    case 'packs_decks':
      return t.modCatPacksDecks;
    case 'ui_theme':
      return t.modCatUiTheme;
    case 'script_plugin':
      return t.modCatScriptPlugin;
    case 'trainer':
      return t.modCatTrainer;
    case 'overhaul':
      return t.modCatOverhaul;
    case 'other':
      return t.modCatOther;
    default:
      return cat ? cat : t.modCatAuto;
  }
}

function getCategoryIcon(cat: string): React.ReactNode {
  switch (cat) {
    case 'cards':
      return <Sparkles className="size-3.5" />;
    case 'characters':
      return <User className="size-3.5" />;
    case 'relics':
      return <Wand2 className="size-3.5" />;
    case 'packs_decks':
      return <Package className="size-3.5" />;
    case 'ui_theme':
      return <Palette className="size-3.5" />;
    case 'script_plugin':
      return <Code2 className="size-3.5" />;
    case 'trainer':
      return <Sliders className="size-3.5" />;
    case 'overhaul':
      return <Layers className="size-3.5" />;
    default:
      return <Package className="size-3.5" />;
  }
}

function getCategoryAccent(cat: string): 'accent' | 'warning' | 'success' | 'danger' | 'default' {
  switch (cat) {
    case 'overhaul':
    case 'cards':
      return 'accent';
    case 'relics':
    case 'trainer':
      return 'warning';
    case 'characters':
    case 'packs_decks':
      return 'success';
    case 'ui_theme':
    case 'script_plugin':
      return 'default';
    default:
      return 'default';
  }
}

function isImageFileName(name: string): boolean {
  return /\.(png|webp|jpg|jpeg|svg|gif)$/i.test(name);
}

function isArchiveFileName(name: string): boolean {
  return /\.(yrdpk|zip|ypk)$/i.test(name);
}

function summarizeModStats(m: ModEntry, t: I18nDict): string {
  const s = m.stats;
  const parts: string[] = [];
  if (s.cardCount > 0) parts.push(fmt(t.statCards, { n: s.cardCount }));
  if (s.characterCount > 0) parts.push(fmt(t.statChars, { n: s.characterCount }));
  if (s.relicCount > 0) parts.push(fmt(t.statRelics, { n: s.relicCount }));
  if (s.packCount > 0) parts.push(fmt(t.statPacks, { n: s.packCount }));
  if (s.preconstructedDeckCount + s.deckCount > 0)
    parts.push(fmt(t.statDecks, { n: s.preconstructedDeckCount + s.deckCount }));
  if (s.scriptCount > 0) parts.push(`${s.scriptCount} Lua`);
  if (s.luaLibCount > 0) parts.push(`${s.luaLibCount} Lib`);
  if (s.artCount > 0) parts.push(fmt(t.statArt, { n: s.artCount }));
  if (s.hasTrainer) parts.push(t.statTrainer);
  if (s.hasLuaDsl) parts.push('mod.lua');
  if (s.hasJsDsl) parts.push('mod.js');
  if (s.jsPluginCount > 0) parts.push(`${s.jsPluginCount} JS`);
  if (s.cssStyleCount > 0) parts.push(`${s.cssStyleCount} CSS`);
  return parts.length > 0 ? parts.join(' · ') : t.statEmpty;
}

export function ModLibraryTab({
  status,
  t,
  lang,
  selectedModId,
  inspectorOpen: _inspectorOpen,
  onSelectMod,
  onRefresh,
  onOpenInStudio,
  onOpenNewModModal,
  onOpenModGuide,
  onCopyAiPrompt,
  onCreateExampleMod,
  onNotify,
}: ModLibraryTabProps) {
  const isDev = Boolean(status.developerMode);
  const [busyAction, setBusyAction] = useState(false);
  const [activeCategory, setActiveCategory] = useState<string>('all');
  const [searchQuery, setSearchQuery] = useState('');
  const [viewMode, setViewMode] = useState<'grouped' | 'flat'>('grouped');
  const [collapsedCats, setCollapsedCats] = useState<Record<string, boolean>>({});

  // Custom Category Creator
  const [showAddCat, setShowAddCat] = useState(false);
  const [newCatName, setNewCatName] = useState('');

  // AI / Smart Organize Modal
  const [organizeOpen, setOrganizeOpen] = useState(false);
  const [organizeTab, setOrganizeTab] = useState<'smart' | 'ai'>('smart');
  const [preserveCustomCats, setPreserveCustomCats] = useState(true);
  const [aiBaseUrl, setAiBaseUrl] = useState(status.aiApiBaseUrl || 'https://api.deepseek.com/v1');
  const [aiModel, setAiModel] = useState(status.aiApiModel || 'deepseek-chat');
  const [aiApiKey, setAiApiKey] = useState(status.hasAiApiKey ? '__KEEP__' : '');
  const [aiPrompt, setAiPrompt] = useState('');
  const [organizeBusy, setOrganizeBusy] = useState(false);
  const [organizePlan, setOrganizePlan] = useState<OrganizePlanResp | null>(null);

  // Drag & Drop state (Reorder MODs, Categorize MODs, Drop external .zip / cover image)
  const [draggingModId, setDraggingModId] = useState<string | null>(null);
  const [dropTargetModId, setDropTargetModId] = useState<string | null>(null);
  const [dropPosition, setDropPosition] = useState<'before' | 'after'>('before');
  const [dropTargetCategory, setDropTargetCategory] = useState<string | null>(null);
  const [isDraggingExternalFile, setIsDraggingExternalFile] = useState(false);
  const [coverDropHover, setCoverDropHover] = useState(false);
  const externalDragDepthRef = useRef(0);

  useEffect(() => {
    if (status.aiApiBaseUrl) setAiBaseUrl(status.aiApiBaseUrl);
    if (status.aiApiModel) setAiModel(status.aiApiModel);
    if (status.hasAiApiKey && !aiApiKey) setAiApiKey('__KEEP__');
  }, [status.aiApiBaseUrl, status.aiApiModel, status.hasAiApiKey]);

  // Modal Window Detail / Metadata Editor (replaces cramped right sidebar!)
  const [detailModalOpen, setDetailModalOpen] = useState(false);
  const [editingMeta, setEditingMeta] = useState(false);
  const [metaForm, setMetaForm] = useState({
    name: '',
    version: '',
    author: '',
    category: 'auto',
    minGameVersion: '',
    tagsCsv: '',
    description: '',
    autoDiscoverEntries: true,
  });

  const selectedMod = useMemo(
    () => status.mods.find((m) => m.id === selectedModId) ?? status.mods[0] ?? null,
    [status.mods, selectedModId],
  );

  useEffect(() => {
    if (!selectedMod) return;
    setMetaForm({
      name: selectedMod.name || '',
      version: selectedMod.version || '1.0.0',
      author: selectedMod.author || '',
      category: selectedMod.category || 'auto',
      minGameVersion: selectedMod.minGameVersion || '',
      tagsCsv: (selectedMod.tags ?? []).join(', '),
      description: selectedMod.description || '',
      autoDiscoverEntries: selectedMod.autoDiscoverEntries !== false,
    });
    setEditingMeta(false);
  }, [selectedMod?.id, selectedMod?.name, selectedMod?.category, selectedMod?.coverUrl, selectedMod?.autoDiscoverEntries]);

  const allCategorySlugs = useMemo(() => {
    const list: string[] = [...MOD_CATEGORY_LIST];
    const seen = new Set(list.map((c) => c.toLowerCase()));
    for (const c of status.customCategories ?? []) {
      const clean = c.trim();
      if (clean && !seen.has(clean.toLowerCase())) {
        seen.add(clean.toLowerCase());
        list.push(clean);
      }
    }
    for (const m of status.mods) {
      const eff = (m.effectiveCategory || 'other').trim();
      if (eff && !seen.has(eff.toLowerCase())) {
        seen.add(eff.toLowerCase());
        list.push(eff);
      }
    }
    return list;
  }, [status.customCategories, status.mods]);

  const categoryCounts = useMemo(() => {
    const counts: Record<string, number> = { all: status.mods.length };
    for (const m of status.mods) {
      const cat = (m.effectiveCategory || 'other').trim();
      counts[cat] = (counts[cat] ?? 0) + 1;
      const lower = cat.toLowerCase();
      if (lower !== cat) counts[lower] = (counts[lower] ?? 0) + 1;
    }
    return counts;
  }, [status.mods]);

  const filteredMods = useMemo(() => {
    const q = searchQuery.trim().toLowerCase();
    return status.mods.filter((m) => {
      const eff = (m.effectiveCategory || 'other').trim();
      if (activeCategory !== 'all' && eff.toLowerCase() !== activeCategory.toLowerCase()) return false;
      if (!q) return true;
      return (
        m.name.toLowerCase().includes(q) ||
        m.id.toLowerCase().includes(q) ||
        (m.author || '').toLowerCase().includes(q) ||
        (m.description || '').toLowerCase().includes(q) ||
        eff.toLowerCase().includes(q) ||
        getModCategoryLabel(eff, t).toLowerCase().includes(q) ||
        (m.tags ?? []).some((tag) => tag.toLowerCase().includes(q))
      );
    });
  }, [status.mods, activeCategory, searchQuery, t]);

  const groupedChecklist = useMemo(() => {
    const map = new Map<string, ModEntry[]>();
    for (const m of filteredMods) {
      const cat = (m.effectiveCategory || 'other').trim();
      if (!map.has(cat)) map.set(cat, []);
      map.get(cat)!.push(m);
    }
    return Array.from(map.entries());
  }, [filteredMods]);

  const conflictModIds = useMemo(() => {
    const set = new Set<string>();
    for (const c of status.conflicts) {
      for (const id of c.modIds) set.add(id.toLowerCase());
    }
    return set;
  }, [status.conflicts]);

  const versionIssueMap = useMemo(() => {
    const map = new Map<string, string>();
    for (const v of status.versionIssues) {
      map.set(v.modId.toLowerCase(), v.message);
    }
    return map;
  }, [status.versionIssues]);

  const runAction = async (fn: () => Promise<void>) => {
    setBusyAction(true);
    try {
      await fn();
      await onRefresh();
    } catch (e) {
      onNotify((e as Error).message, 'err');
    } finally {
      setBusyAction(false);
    }
  };

  const handleImportZip = () =>
    runAction(async () => {
      const pick = await apiPost<{ path: string | null }>('/api/dialog/pick-file', {
        title: t.importZip,
        kind: 'zip',
      });
      if (!pick.path) return;
      const res = await apiPost<{
        isWorkspacePack: boolean;
        isYpk?: boolean;
        name: string;
        count?: number;
        cardCount?: number;
        scriptCount?: number;
        artCount?: number;
      }>('/api/mods/import', {
        sourcePath: pick.path,
        force: true,
      });
      onNotify(
        res.isYpk
          ? fmt(t.toastImportedYpk, {
              name: res.name,
              cards: res.cardCount ?? 0,
              scripts: res.scriptCount ?? 0,
              art: res.artCount ?? 0,
            })
          : res.isWorkspacePack
            ? fmt(t.toastImportedPack, { name: res.name, count: res.count ?? 0 })
            : fmt(t.toastImportedMod, { name: res.name }),
        'ok',
      );
    });

  const handleImportFolder = () =>
    runAction(async () => {
      const pick = await apiPost<{ path: string | null }>('/api/dialog/pick-folder', {
        title: t.importFolder,
      });
      if (!pick.path) return;
      const res = await apiPost<{ name: string }>('/api/mods/import', {
        sourcePath: pick.path,
        force: true,
      });
      onNotify(fmt(t.toastImportedMod, { name: res.name }), 'ok');
    });

  const handlePackWorkspace = (format: 'yrdpk' | 'zip' = 'yrdpk') =>
    runAction(async () => {
      const save = await apiPost<{ path: string | null }>('/api/dialog/save-zip', {
        title: t.packWorkspace,
        suggestedName: `ygo-modpack-v${status.activeBaseGameVersion ?? '1.0.0'}.${format}`,
      });
      if (!save.path) return;
      const res = await apiPost<{ zipPath: string; outputDir?: string; modCount: number }>('/api/workspace/pack', {
        outputPath: save.path,
        format,
        enabledOnly: false,
      });
      onNotify(fmt(t.toastPackedWs, { count: res.modCount, path: res.zipPath }), 'ok');
      if (res.outputDir) {
        apiPost('/api/shell/open-folder', { path: res.outputDir }).catch(() => {});
      }
    });

  const handlePackSingle = (mod: ModEntry, format: 'yrdpk' | 'zip' = 'yrdpk') =>
    runAction(async () => {
      const save = await apiPost<{ path: string | null }>('/api/dialog/save-zip', {
        title: `${t.packZip} (.${format})`,
        suggestedName: `${mod.id}-v${mod.version}.${format}`,
      });
      if (!save.path) return;
      const res = await apiPost<{ zipPath: string; outputDir?: string }>('/api/mods/pack', {
        modId: mod.id,
        outputPath: save.path,
        format,
      });
      onNotify(fmt(t.toastPackedMod, { name: mod.name, path: res.zipPath }), 'ok');
      if (res.outputDir) {
        apiPost('/api/shell/open-folder', { path: res.outputDir }).catch(() => {});
      }
    });

  const handleToggle = (modId: string, enabled: boolean) =>
    runAction(async () => {
      await apiPost('/api/mods/toggle', { modId, enabled });
    });

  const handleReorderByIds = (orderedIds: string[]) =>
    runAction(async () => {
      await apiPost('/api/mods/reorder', { orderedIds });
      onNotify(t.toastReordered, 'ok');
    });

  const handleMove = (modId: string, delta: number) => {
    const index = status.mods.findIndex((m) => m.id === modId);
    const next = index + delta;
    if (index < 0 || next < 0 || next >= status.mods.length) return;
    const ids = status.mods.map((m) => m.id);
    const [moved] = ids.splice(index, 1);
    ids.splice(next, 0, moved);
    handleReorderByIds(ids);
  };

  const handleDelete = (mod: ModEntry) => {
    if (!window.confirm(fmt(t.confirmDeleteMod, { name: mod.name, id: mod.id }))) return;
    runAction(async () => {
      await apiPost('/api/mods/delete', { modId: mod.id });
      onNotify(fmt(t.toastDeletedMod, { id: mod.id }), 'info');
    });
  };

  const handleSaveMetadata = () => {
    if (!selectedMod) return;
    runAction(async () => {
      await apiPost('/api/mods/metadata', {
        modId: selectedMod.id,
        name: metaForm.name,
        version: metaForm.version,
        author: metaForm.author,
        category: metaForm.category,
        minGameVersion: metaForm.minGameVersion,
        tagsCsv: metaForm.tagsCsv,
        description: metaForm.description,
        autoDiscoverEntries: metaForm.autoDiscoverEntries,
      });
      setEditingMeta(false);
      onNotify(fmt(t.toastMetadataSaved, { id: selectedMod.id }), 'ok');
    });
  };

  const handlePickCover = (mod: ModEntry) =>
    runAction(async () => {
      const pick = await apiPost<{ path: string | null }>('/api/dialog/pick-file', {
        title: t.changeCover,
        kind: 'image',
      });
      if (!pick.path) return;
      await apiPost('/api/mods/metadata', {
        modId: mod.id,
        coverSourcePath: pick.path,
      });
      onNotify(fmt(t.toastCoverUpdated, { id: mod.id }), 'ok');
    });

  const handleClearCover = (mod: ModEntry) =>
    runAction(async () => {
      await apiPost('/api/mods/metadata', {
        modId: mod.id,
        clearCover: true,
      });
      onNotify(fmt(t.toastMetadataSaved, { id: mod.id }), 'info');
    });

  const handleUploadDroppedCover = (modId: string, file: File) =>
    runAction(async () => {
      await apiUploadBinary(
        `/api/mods/cover-upload?modId=${encodeURIComponent(modId)}&fileName=${encodeURIComponent(file.name)}`,
        file,
      );
      onSelectMod(modId);
      onNotify(fmt(t.toastCoverUpdated, { id: modId }), 'ok');
    });

  const handleUploadDroppedArchives = (files: File[]) =>
    runAction(async () => {
      for (const file of files) {
        if (!isArchiveFileName(file.name)) continue;
        const res = await apiUploadBinary<{
          isWorkspacePack: boolean;
          isYpk?: boolean;
          name: string;
          count?: number;
          modId?: string;
          cardCount?: number;
          scriptCount?: number;
          artCount?: number;
        }>(`/api/mods/import-upload?fileName=${encodeURIComponent(file.name)}`, file);
        if (res.modId) onSelectMod(res.modId);
        onNotify(
          res.isYpk
            ? fmt(t.toastImportedYpk, {
                name: res.name,
                cards: res.cardCount ?? 0,
                scripts: res.scriptCount ?? 0,
                art: res.artCount ?? 0,
              })
            : res.isWorkspacePack
              ? fmt(t.toastImportedPack, { name: res.name, count: res.count ?? 0 })
              : fmt(t.toastImportedMod, { name: res.name }),
          'ok',
        );
      }
    });

  // ---- Drag & Drop Handlers ----
  const handleExternalDragEnter = (e: React.DragEvent) => {
    if (!e.dataTransfer.types.includes('Files')) return;
    e.preventDefault();
    externalDragDepthRef.current += 1;
    setIsDraggingExternalFile(true);
  };

  const handleExternalDragOver = (e: React.DragEvent) => {
    if (!e.dataTransfer.types.includes('Files')) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'copy';
  };

  const handleExternalDragLeave = (e: React.DragEvent) => {
    if (!e.dataTransfer.types.includes('Files')) return;
    e.preventDefault();
    externalDragDepthRef.current = Math.max(0, externalDragDepthRef.current - 1);
    if (externalDragDepthRef.current === 0) {
      setIsDraggingExternalFile(false);
    }
  };

  const handleExternalDropOnLibrary = (e: React.DragEvent) => {
    if (!e.dataTransfer.types.includes('Files')) return;
    e.preventDefault();
    externalDragDepthRef.current = 0;
    setIsDraggingExternalFile(false);

    const files = Array.from(e.dataTransfer.files || []);
    const archives = files.filter((f) => isArchiveFileName(f.name));
    if (archives.length > 0) {
      handleUploadDroppedArchives(archives);
      return;
    }

    const firstImg = files.find((f) => isImageFileName(f.name));
    if (firstImg && selectedMod) {
      handleUploadDroppedCover(selectedMod.id, firstImg);
    }
  };

  const handleRowDragStart = (e: React.DragEvent, modId: string) => {
    setDraggingModId(modId);
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData('application/x-ygo-mod-id', modId);
    e.dataTransfer.setData('text/plain', modId);
  };

  const handleRowDragOver = (e: React.DragEvent, targetModId: string) => {
    const isFiles = e.dataTransfer.types.includes('Files');
    if (isFiles) {
      e.preventDefault();
      e.dataTransfer.dropEffect = 'copy';
      setDropTargetModId(targetModId);
      return;
    }
    if (!draggingModId || draggingModId === targetModId) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    const rect = (e.currentTarget as HTMLElement).getBoundingClientRect();
    const pos = e.clientY - rect.top < rect.height / 2 ? 'before' : 'after';
    setDropTargetModId(targetModId);
    setDropPosition(pos);
  };

  const handleRowDrop = (e: React.DragEvent, targetModId: string) => {
    e.preventDefault();
    e.stopPropagation();

    // Check if user dropped an external image or zip onto a specific MOD row
    if (e.dataTransfer.files && e.dataTransfer.files.length > 0) {
      externalDragDepthRef.current = 0;
      setIsDraggingExternalFile(false);
      setDropTargetModId(null);
      const files = Array.from(e.dataTransfer.files);
      const img = files.find((f) => isImageFileName(f.name));
      if (img) {
        handleUploadDroppedCover(targetModId, img);
        return;
      }
      const archives = files.filter((f) => isArchiveFileName(f.name));
      if (archives.length > 0) {
        handleUploadDroppedArchives(archives);
      }
      return;
    }

    const sourceId = draggingModId || e.dataTransfer.getData('application/x-ygo-mod-id');
    setDraggingModId(null);
    setDropTargetModId(null);
    if (!sourceId || sourceId === targetModId) return;

    const ids = status.mods.map((m) => m.id);
    const fromIdx = ids.indexOf(sourceId);
    const toIdxOriginal = ids.indexOf(targetModId);
    if (fromIdx < 0 || toIdxOriginal < 0) return;

    ids.splice(fromIdx, 1);
    const adjustedTargetIdx = ids.indexOf(targetModId);
    const insertAt = dropPosition === 'after' ? adjustedTargetIdx + 1 : adjustedTargetIdx;
    ids.splice(insertAt, 0, sourceId);
    handleReorderByIds(ids);
  };

  const handleCategoryChipDrop = (e: React.DragEvent, cat: string) => {
    e.preventDefault();
    e.stopPropagation();
    setDropTargetCategory(null);
    if (cat === 'all') return;
    const sourceId = draggingModId || e.dataTransfer.getData('application/x-ygo-mod-id');
    setDraggingModId(null);
    if (!sourceId) return;
    const mod = status.mods.find((m) => m.id === sourceId);
    if (!mod) return;

    runAction(async () => {
      await apiPost('/api/mods/metadata', {
        modId: mod.id,
        category: cat,
      });
      onNotify(
        fmt(t.toastCategoryMoved, { name: mod.name, cat: getModCategoryLabel(cat, t) }),
        'ok',
      );
    });
  };

  const handleToggleCategory = (cat: string, enabled: boolean) =>
    runAction(async () => {
      await apiPost('/api/mods/toggle-category', { category: cat, enabled });
    });

  const handleQuickChangeCategory = (mod: ModEntry, cat: string) =>
    runAction(async () => {
      await apiPost('/api/mods/metadata', { modId: mod.id, category: cat });
      onNotify(fmt(t.toastCategoryMoved, { name: mod.name, cat: getModCategoryLabel(cat, t) }), 'ok');
    });

  const handleAddCustomCategory = () => {
    const clean = newCatName.trim();
    if (!clean) return;
    runAction(async () => {
      await apiPost('/api/mods/categories', { action: 'add', name: clean });
      setNewCatName('');
      setShowAddCat(false);
      onNotify(fmt(t.toastCategoryAdded, { name: clean }), 'ok');
    });
  };

  const handlePreviewOrganize = async (mode: 'smart' | 'ai') => {
    setOrganizeBusy(true);
    try {
      const payload =
        mode === 'ai'
          ? {
              mode: 'ai',
              apply: false,
              apiBaseUrl: aiBaseUrl,
              model: aiModel,
              apiKey: aiApiKey,
              userPrompt: aiPrompt,
              saveAiConfig: true,
            }
          : {
              mode: 'smart',
              apply: false,
              preserveCustom: preserveCustomCats,
            };
      const res = await apiPost<OrganizePlanResp>('/api/mods/organize', payload);
      setOrganizePlan(res);
    } catch (e) {
      onNotify((e as Error).message, 'err');
    } finally {
      setOrganizeBusy(false);
    }
  };

  const handleApplyOrganizePlan = async () => {
    if (!organizePlan || organizePlan.items.length === 0) return;
    setOrganizeBusy(true);
    try {
      await apiPost('/api/mods/organize', {
        applyItems: organizePlan.items.map((it) => ({
          modId: it.modId,
          category: it.newCategory,
          reason: it.reason,
        })),
      });
      await onRefresh();
      setOrganizeOpen(false);
      setOrganizePlan(null);
      onNotify(t.toastOrganizeApplied, 'ok');
    } catch (e) {
      onNotify((e as Error).message, 'err');
    } finally {
      setOrganizeBusy(false);
    }
  };

  const handleCopyOrganizeMcpPrompt = () => {
    const modSummary = status.mods
      .map(
        (m, i) =>
          `${i + 1}. id="${m.id}" name="${m.name}" category="${m.effectiveCategory}" enabled=${m.enabled} stats=(cards:${m.stats.cardCount}, chars:${m.stats.characterCount}, relics:${m.stats.relicCount}, packs:${m.stats.packCount}, lua:${m.stats.scriptCount}, js:${m.stats.jsPluginCount}, css:${m.stats.cssStyleCount}, trainer:${m.stats.hasTrainer})`,
      )
      .join('\n');
    const prompt =
      lang === 'zh'
        ? `请帮我整理《Yu-Gi-Oh! Rogue Duelist》的 MOD 清单列表（分类 + 加载顺序），如果已连接 MCP (${status.mcpUrl}) 请直接调用 organize_mods 工具应用：\n\n当前 MOD 列表：\n${modSummary}\n\n要求：基础大型规则/综合包在前，卡牌/卡包/角色/遗物在中，脚本与美化在后，修改器放最后。你可以根据主题创建清晰的中文分类名。${aiPrompt ? `\n补充要求：${aiPrompt}` : ''}`
        : `Please organize my Yu-Gi-Oh! Rogue Duelist MOD checklist (categories + load order). If connected via MCP (${status.mcpUrl}), call the organize_mods tool directly:\n\nCurrent MODs:\n${modSummary}\n\nGoal: Overhauls/rules first, cards/packs/characters/relics in middle, scripts/UI themes next, trainers last.${aiPrompt ? `\nExtra preference: ${aiPrompt}` : ''}`;
    navigator.clipboard.writeText(prompt).catch(() => {});
    onNotify(t.toastAiSuperPromptCopied, 'ok');
  };

  const handleCoverBannerDrop = (e: React.DragEvent, mod: ModEntry) => {
    e.preventDefault();
    e.stopPropagation();
    setCoverDropHover(false);
    externalDragDepthRef.current = 0;
    setIsDraggingExternalFile(false);
    const file = Array.from(e.dataTransfer.files || []).find((f) => isImageFileName(f.name));
    if (file) {
      handleUploadDroppedCover(mod.id, file);
    }
  };

  return (
    <div
      className="relative flex h-full min-h-0 flex-1 overflow-hidden"
      onDragEnter={handleExternalDragEnter}
      onDragOver={handleExternalDragOver}
      onDragLeave={handleExternalDragLeave}
      onDrop={handleExternalDropOnLibrary}
    >
      {/* External File Drag-and-Drop Overlay */}
      {isDraggingExternalFile && (
        <div className="pointer-events-none absolute inset-2 z-30 flex flex-col items-center justify-center rounded-2xl border-2 border-dashed border-accent bg-background/85 backdrop-blur-md">
          <Upload className="mb-2 size-10 text-accent" />
          <p className="text-sm font-semibold text-foreground">{t.dragDropOverlayTitle}</p>
          <p className="mt-1 text-xs text-muted">{t.dragDropOverlayHint}</p>
        </div>
      )}

      {/* Center Pane */}
      <div className="flex min-w-0 flex-1 flex-col">
        {/* Responsive Wrapping Toolbar (Player Mode hides MOD Editor actions; keeps Import, Pack, Organize & Checklist!) */}
        <div className="flex min-h-11 shrink-0 flex-wrap items-center gap-1.5 border-b border-border bg-background px-3 py-1.5">
          <Button size="sm" variant="primary" isDisabled={busyAction} onPress={handleImportZip} className="h-7 shrink-0 text-xs">
            <Upload className="size-3.5" />
            {t.importZip}
          </Button>
          <Button size="sm" variant="secondary" isDisabled={busyAction} onPress={handleImportFolder} className="h-7 shrink-0 text-xs">
            <FolderInput className="size-3.5 text-accent" />
            {t.importFolder}
          </Button>
          <Button
            size="sm"
            variant="secondary"
            isDisabled={busyAction || status.mods.length === 0}
            onPress={() => {
              setOrganizePlan(null);
              setOrganizeOpen(true);
            }}
            className="h-7 shrink-0 text-xs"
          >
            <Sparkles className="size-3.5 text-warning" />
            {t.aiOrganizeBtn}
          </Button>
          <Button
            size="sm"
            variant="secondary"
            onPress={() => setShowAddCat((v) => !v)}
            className="h-7 shrink-0 text-xs"
          >
            <FolderPlus className="size-3.5 text-accent" />
            {t.addCategoryBtn}
          </Button>
          <Button
            size="sm"
            variant="secondary"
            isDisabled={busyAction || status.mods.length === 0}
            onPress={() => handlePackWorkspace('yrdpk')}
            className="h-7 shrink-0 text-xs"
          >
            <Archive className="size-3.5 text-warning" />
            {t.packWorkspace}
          </Button>
          {isDev && (
            <>
              <Separator orientation="vertical" className="mx-0.5 h-4" />
              <Button size="sm" variant="secondary" onPress={onOpenNewModModal} className="h-7 shrink-0 text-xs">
                <Plus className="size-3.5 text-accent" />
                {t.newMod}
              </Button>
              <Button
                size="sm"
                variant="ghost"
                isDisabled={busyAction}
                onPress={() => runAction(onCreateExampleMod)}
                className="h-7 shrink-0 text-xs text-warning"
              >
                <Sparkles className="size-3.5" />
                {t.createExampleMod}
              </Button>
              <Button
                size="sm"
                variant="ghost"
                onPress={onOpenModGuide}
                className="h-7 shrink-0 text-xs text-accent hover:bg-accent/10"
              >
                <BookOpen className="size-3.5" />
                {t.modGuideBtn}
              </Button>
              <Button
                size="sm"
                variant="ghost"
                onPress={onCopyAiPrompt}
                className="h-7 shrink-0 text-xs text-warning hover:bg-warning/10"
              >
                <Bot className="size-3.5" />
                {t.copyAiSuperPromptBtn}
              </Button>
            </>
          )}
          <Button
            size="sm"
            variant="ghost"
            onPress={() => apiPost('/api/shell/open-folder', { path: status.modsRoot })}
            className="h-7 shrink-0 text-xs text-muted hover:text-foreground"
          >
            <FolderOpen className="size-3.5" />
            {t.openModsDir}
          </Button>

          <Separator orientation="vertical" className="mx-0.5 h-4" />

          <Button
            size="sm"
            variant={viewMode === 'grouped' ? 'secondary' : 'ghost'}
            onPress={() => setViewMode(viewMode === 'grouped' ? 'flat' : 'grouped')}
            className="h-7 shrink-0 text-xs"
          >
            {viewMode === 'grouped' ? (
              <>
                <ListChecks className="size-3.5 text-accent" />
                {t.checklistViewGrouped}
              </>
            ) : (
              <>
                <ListOrdered className="size-3.5 text-warning" />
                {t.checklistViewFlat}
              </>
            )}
          </Button>
          <Button size="sm" variant="ghost" isDisabled={busyAction} onPress={() => handleToggle('all', true)} className="h-7 shrink-0 text-xs">
            <CheckSquare className="size-3.5 text-success" />
            {t.enableAll}
          </Button>
          <Button size="sm" variant="ghost" isDisabled={busyAction} onPress={() => handleToggle('all', false)} className="h-7 shrink-0 text-xs">
            <Square className="size-3.5 text-muted" />
            {t.disableAll}
          </Button>
          <Button isIconOnly size="sm" variant="ghost" aria-label={t.refresh} onPress={() => onRefresh()} className="ml-auto size-7 shrink-0">
            <RefreshCw className="size-3.5" />
          </Button>
        </div>

        {/* Inline Quick Add Custom Category Bar */}
        {showAddCat && (
          <div className="flex shrink-0 items-center gap-2 border-b border-border/80 bg-accent-soft/15 px-3 py-1.5">
            <Tag className="size-3.5 text-accent shrink-0" />
            <input
              type="text"
              value={newCatName}
              onChange={(e) => setNewCatName(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') handleAddCustomCategory();
              }}
              placeholder={t.aiOrganizeCustomCatPlaceholder}
              className="h-7 flex-1 rounded-lg border border-border bg-background px-2.5 text-xs text-foreground focus:border-accent focus:outline-none"
            />
            <Button size="sm" variant="primary" isDisabled={!newCatName.trim() || busyAction} onPress={handleAddCustomCategory} className="h-7 text-xs">
              {t.confirm}
            </Button>
            <Button size="sm" variant="ghost" onPress={() => setShowAddCat(false)} className="h-7 text-xs">
              {t.cancel}
            </Button>
          </div>
        )}

        {/* Category Filter & Search Strip (supports Drag MOD onto any Standard or Custom Category Chip!) */}
        <div className="shrink-0 space-y-2 border-b border-border/80 bg-surface/35 px-3 py-2">
          <div className="flex flex-wrap items-center gap-1.5">
            {allCategorySlugs.map((cat) => {
              const count = categoryCounts[cat] ?? categoryCounts[cat.toLowerCase()] ?? 0;
              const isCustom = !MOD_CATEGORY_LIST.includes(cat as ModCategoryKey);
              if (cat !== 'all' && !isCustom && count === 0 && !draggingModId && activeCategory !== cat) {
                return null;
              }
              const isActive = activeCategory.toLowerCase() === cat.toLowerCase();
              const isDropHover = dropTargetCategory === cat && cat !== 'all';
              return (
                <button
                  key={cat}
                  type="button"
                  onClick={() => setActiveCategory(cat)}
                  onDragOver={(e) => {
                    if (cat === 'all' || !draggingModId) return;
                    e.preventDefault();
                    e.dataTransfer.dropEffect = 'move';
                    setDropTargetCategory(cat);
                  }}
                  onDragLeave={() => {
                    if (dropTargetCategory === cat) setDropTargetCategory(null);
                  }}
                  onDrop={(e) => handleCategoryChipDrop(e, cat)}
                  className={`inline-flex items-center gap-1 rounded-lg border px-2 py-1 text-[11px] font-medium transition-all ${
                    isDropHover
                      ? 'scale-105 border-warning bg-warning/20 text-warning shadow-sm'
                      : isActive
                        ? 'border-accent/75 bg-accent/15 text-accent'
                        : 'border-border/70 bg-surface text-muted hover:border-border hover:text-foreground'
                  }`}
                >
                  {cat !== 'all' && (isCustom ? <Tag className="size-3" /> : getCategoryIcon(cat))}
                  <span>{getModCategoryLabel(cat, t)}</span>
                  <span className="font-mono text-[10px] opacity-75">{count}</span>
                </button>
              );
            })}

            <div className="relative ml-auto min-w-[180px] flex-1 sm:max-w-[260px]">
              <Search className="pointer-events-none absolute top-1/2 left-2.5 size-3.5 -translate-y-1/2 text-muted" />
              <input
                type="text"
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.target.value)}
                placeholder={t.searchModsPlaceholder}
                className="h-7 w-full rounded-lg border border-border/80 bg-background pr-7 pl-8 text-xs text-foreground placeholder:text-muted/70 focus:border-accent focus:outline-none"
              />
              {searchQuery && (
                <button
                  type="button"
                  onClick={() => setSearchQuery('')}
                  className="absolute top-1/2 right-2 -translate-y-1/2 text-muted hover:text-foreground"
                >
                  <X className="size-3" />
                </button>
              )}
            </div>
          </div>
        </div>

        {/* Load Order & Draggable List */}
        <div className="min-h-0 flex-1 overflow-y-auto p-3 space-y-2">
          <div className="flex items-center justify-between gap-2 px-1 pb-0.5 text-[11px] text-muted">
            <span className="truncate">{t.dragOrderHint}</span>
            <span className="shrink-0 font-mono">
              {status.mods.filter((m) => m.enabled).length} / {status.mods.length}
            </span>
          </div>

          {filteredMods.length === 0 ? (
            <div className="flex h-64 flex-col items-center justify-center text-center">
              <Package className="mb-3 size-9 text-muted/35" />
              <p className="text-sm font-semibold text-foreground">{t.noModsTitle}</p>
              <p className="mt-1 text-xs text-muted">{t.noModsHint}</p>
              {status.mods.length === 0 && isDev && (
                <Button
                  size="sm"
                  variant="secondary"
                  className="mt-3"
                  onPress={() => runAction(onCreateExampleMod)}
                >
                  <Sparkles className="size-3.5 text-warning" />
                  {t.createExampleMod}
                </Button>
              )}
            </div>
          ) : (() => {
            const renderModChecklistRow = (mod: ModEntry) => {
              const globalIdx = status.mods.findIndex((m) => m.id === mod.id);
              const isSelected = selectedMod?.id === mod.id;
              const hasConflict = conflictModIds.has(mod.id.toLowerCase());
              const verWarn = versionIssueMap.get(mod.id.toLowerCase());
              const isDragging = draggingModId === mod.id;
              const isDropTarget = dropTargetModId === mod.id && draggingModId !== mod.id;
              const cat = mod.effectiveCategory || 'other';

              return (
                <div
                  key={mod.id}
                  draggable={!busyAction}
                  onDragStart={(e) => handleRowDragStart(e, mod.id)}
                  onDragOver={(e) => handleRowDragOver(e, mod.id)}
                  onDragLeave={() => {
                    if (dropTargetModId === mod.id) setDropTargetModId(null);
                  }}
                  onDrop={(e) => handleRowDrop(e, mod.id)}
                  onDragEnd={() => {
                    setDraggingModId(null);
                    setDropTargetModId(null);
                    setDropTargetCategory(null);
                  }}
                  onClick={() => {
                    onSelectMod(mod.id);
                    setDetailModalOpen(true);
                  }}
                  className={`group relative flex cursor-pointer items-center gap-2.5 rounded-xl border px-2.5 py-2 transition-all ${
                    isDragging ? 'opacity-45 scale-[0.99]' : ''
                  } ${
                    !mod.enabled ? 'opacity-65' : ''
                  } ${
                    isSelected
                      ? 'border-accent/80 bg-accent-soft/40'
                      : 'border-border/75 bg-surface hover:border-border hover:bg-surface-secondary/70'
                  } ${
                    isDropTarget && dropPosition === 'before'
                      ? 'ring-2 ring-accent/70 border-t-accent'
                      : isDropTarget && dropPosition === 'after'
                        ? 'ring-2 ring-warning/70 border-b-warning'
                        : ''
                  }`}
                >
                  {/* Drag Handle */}
                  <div
                    className="flex shrink-0 cursor-grab items-center text-muted/60 hover:text-foreground active:cursor-grabbing"
                    title={t.dragOrderHint}
                  >
                    <GripVertical className="size-4" />
                  </div>

                  {/* Enable Checkbox */}
                  <div onClick={(e) => e.stopPropagation()}>
                    <Checkbox
                      aria-label={`Enable ${mod.name}`}
                      isSelected={mod.enabled}
                      onChange={(checked) => handleToggle(mod.id, checked)}
                    >
                      <Checkbox.Content>
                        <Checkbox.Control>
                          <Checkbox.Indicator />
                        </Checkbox.Control>
                      </Checkbox.Content>
                    </Checkbox>
                  </div>

                  {/* Order Badge */}
                  <span className="inline-flex h-5 min-w-6 shrink-0 items-center justify-center rounded-md bg-surface-secondary px-1.5 font-mono text-[11px] font-semibold text-muted">
                    #{globalIdx + 1}
                  </span>

                  {/* 40x40 Cover Art Thumbnail */}
                  <div className="relative size-10 shrink-0 overflow-hidden rounded-lg border border-border/80 bg-surface-secondary flex items-center justify-center">
                    {mod.coverUrl ? (
                      <img
                        src={resolveApiUrl(mod.coverUrl)}
                        alt={mod.name}
                        className="h-full w-full object-cover"
                        loading="lazy"
                      />
                    ) : (
                      <div className="flex flex-col items-center justify-center text-muted/60">
                        {getCategoryIcon(cat)}
                      </div>
                    )}
                  </div>

                  {/* Main MOD Info */}
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-1.5">
                      <span className="truncate text-[13px] font-semibold text-foreground">{mod.name}</span>
                      <span className="font-mono text-[11px] text-muted">v{mod.version}</span>
                      {/* Inline Quick Category Switcher */}
                      <select
                        aria-label={t.modCategoryLabel}
                        value={cat}
                        onClick={(e) => e.stopPropagation()}
                        onChange={(e) => handleQuickChangeCategory(mod, e.target.value)}
                        className="h-5 max-w-[140px] cursor-pointer rounded-md border border-border/70 bg-surface-secondary px-1.5 text-[10px] font-medium text-foreground/90 focus:border-accent focus:outline-none"
                      >
                        {allCategorySlugs
                          .filter((c) => c !== 'all')
                          .map((c) => (
                            <option key={c} value={c}>
                              {getModCategoryLabel(c, t)}
                            </option>
                          ))}
                      </select>
                      {mod.settingsCount > 0 && (
                        <Chip size="sm" color="success" variant="soft" className="h-4">
                          <Chip.Label className="text-[10px]">
                            F9 · {fmt(t.badgeSettings, { n: mod.settingsCount })}
                          </Chip.Label>
                        </Chip>
                      )}
                      {hasConflict && (
                        <Chip size="sm" color="warning" variant="primary" className="h-4">
                          {t.badgeConflict}
                        </Chip>
                      )}
                      {verWarn && (
                        <Chip size="sm" color="danger" variant="primary" className="h-4">
                          {t.badgeVersionWarn}
                        </Chip>
                      )}
                    </div>
                    <p className="mt-0.5 truncate text-[11px] text-muted">{summarizeModStats(mod, t)}</p>
                  </div>

                  {/* Row Actions */}
                  <div
                    className="flex shrink-0 items-center gap-1 opacity-85 group-hover:opacity-100"
                    onClick={(e) => e.stopPropagation()}
                  >
                    <Button
                      size="sm"
                      variant="secondary"
                      onPress={() => {
                        onSelectMod(mod.id);
                        setDetailModalOpen(true);
                      }}
                      className="h-7 px-2 text-[11px]"
                    >
                      <Info className="size-3 text-accent" />
                      <span className="hidden sm:inline">{t.editMetadata}</span>
                    </Button>
                    {isDev && (
                      <Button
                        size="sm"
                        variant="secondary"
                        onPress={() => onOpenInStudio(mod.id)}
                        className="h-7 px-2 text-[11px]"
                      >
                        <Palette className="size-3 text-accent" />
                        <span className="hidden md:inline">{t.editInStudio}</span>
                      </Button>
                    )}
                    <Button
                      size="sm"
                      variant="secondary"
                      isDisabled={busyAction}
                      onPress={() => handlePackSingle(mod, 'yrdpk')}
                      className="h-7 px-2 text-[11px]"
                    >
                      <Archive className="size-3 text-warning" />
                      <span className="hidden lg:inline">.yrdpk</span>
                    </Button>
                    <Button
                      isIconOnly
                      size="sm"
                      variant="ghost"
                      aria-label={t.moveUp}
                      isDisabled={globalIdx <= 0 || busyAction}
                      onPress={() => handleMove(mod.id, -1)}
                      className="size-7"
                    >
                      <ArrowUp className="size-3.5" />
                    </Button>
                    <Button
                      isIconOnly
                      size="sm"
                      variant="ghost"
                      aria-label={t.moveDown}
                      isDisabled={globalIdx >= status.mods.length - 1 || busyAction}
                      onPress={() => handleMove(mod.id, 1)}
                      className="size-7"
                    >
                      <ArrowDown className="size-3.5" />
                    </Button>
                    <Button
                      isIconOnly
                      size="sm"
                      variant="danger-soft"
                      aria-label={t.deleteMod}
                      isDisabled={busyAction}
                      onPress={() => handleDelete(mod)}
                      className="size-7"
                    >
                      <Trash2 className="size-3.5" />
                    </Button>
                  </div>
                </div>
              );
            };

            if (viewMode === 'flat') {
              return <div className="space-y-1.5">{filteredMods.map(renderModChecklistRow)}</div>;
            }

            return (
              <div className="space-y-3">
                {groupedChecklist.map(([cat, items]) => {
                  const enabledInCat = items.filter((x) => x.enabled).length;
                  const allEnabledInCat = enabledInCat === items.length && items.length > 0;
                  const isCollapsed = Boolean(collapsedCats[cat]);
                  const isCatHover = dropTargetCategory === cat;
                  const isCustom = !MOD_CATEGORY_LIST.includes(cat as ModCategoryKey);

                  return (
                    <div
                      key={cat}
                      onDragOver={(e) => {
                        if (!draggingModId) return;
                        e.preventDefault();
                        e.dataTransfer.dropEffect = 'move';
                        setDropTargetCategory(cat);
                      }}
                      onDragLeave={() => {
                        if (dropTargetCategory === cat) setDropTargetCategory(null);
                      }}
                      onDrop={(e) => handleCategoryChipDrop(e, cat)}
                      className={`overflow-hidden rounded-xl border transition-all ${
                        isCatHover
                          ? 'border-warning bg-warning/10 ring-1 ring-warning/60'
                          : 'border-border/80 bg-surface/40'
                      }`}
                    >
                      <div
                        onClick={() => setCollapsedCats((prev) => ({ ...prev, [cat]: !prev[cat] }))}
                        className="flex cursor-pointer items-center justify-between gap-2 border-b border-border/60 bg-surface-secondary/60 px-3 py-2 select-none hover:bg-surface-secondary"
                      >
                        <div className="flex min-w-0 items-center gap-2">
                          <div onClick={(e) => e.stopPropagation()}>
                            <Checkbox
                              aria-label={`Toggle category ${cat}`}
                              isSelected={allEnabledInCat}
                              isIndeterminate={enabledInCat > 0 && enabledInCat < items.length}
                              onChange={(checked) => handleToggleCategory(cat, checked)}
                            >
                              <Checkbox.Content>
                                <Checkbox.Control>
                                  <Checkbox.Indicator />
                                </Checkbox.Control>
                              </Checkbox.Content>
                            </Checkbox>
                          </div>
                          {isCollapsed ? (
                            <ChevronRight className="size-3.5 text-muted" />
                          ) : (
                            <ChevronDown className="size-3.5 text-muted" />
                          )}
                          <span className="text-accent">
                            {isCustom ? <Tag className="size-3.5" /> : getCategoryIcon(cat)}
                          </span>
                          <span className="truncate text-xs font-bold text-foreground">
                            {getModCategoryLabel(cat, t)}
                          </span>
                        </div>
                        <div className="flex shrink-0 items-center gap-1.5">
                          <Chip size="sm" color={enabledInCat > 0 ? 'accent' : 'default'} variant="soft" className="h-5">
                            <Chip.Label className="font-mono text-[10px]">
                              {enabledInCat} / {items.length}
                            </Chip.Label>
                          </Chip>
                        </div>
                      </div>

                      {!isCollapsed && (
                        <div className="space-y-1.5 p-2">
                          {items.map(renderModChecklistRow)}
                        </div>
                      )}
                    </div>
                  );
                })}
              </div>
            );
          })()}

          {/* Compact Conflict & Version Status Footer inside Main Full-Width List */}
          {(status.versionIssues.length > 0 || status.conflicts.length > 0) && (
            <div className="mt-3 space-y-2">
              {status.versionIssues.length > 0 && (
                <Alert status="danger">
                  <Alert.Indicator />
                  <Alert.Content>
                    <Alert.Title className="text-xs">
                      {t.versionIssuesTitle} ({status.versionIssues.length})
                    </Alert.Title>
                    <Alert.Description>
                      <ul className="mt-1 space-y-1 text-[11px]">
                        {status.versionIssues.map((v, i) => (
                          <li key={`${v.modId}-${i}`}>
                            <span className="font-semibold">{v.modName}:</span> {v.message}
                          </li>
                        ))}
                      </ul>
                    </Alert.Description>
                  </Alert.Content>
                </Alert>
              )}
              {status.conflicts.length > 0 && (
                <Alert status="warning">
                  <Alert.Indicator />
                  <Alert.Content>
                    <Alert.Title className="text-xs">
                      {t.conflictsTitle} ({status.conflicts.length})
                    </Alert.Title>
                    <Alert.Description>
                      <div className="mt-1.5 max-h-40 overflow-y-auto space-y-1 pr-1">
                        {status.conflicts.map((c, i) => (
                          <div
                            key={`${c.category}-${c.resourceKey}-${i}`}
                            className="rounded-lg border border-warning/25 bg-surface/60 px-2 py-1 text-[11px]"
                          >
                            <span className="font-mono font-semibold text-foreground">
                              [{c.category}] {c.resourceKey}
                            </span>{' '}
                            <span className="text-muted">
                              ({c.modIds.join(' → ')} · {t.conflictWinner}: <b className="text-success">{c.winnerModId}</b>)
                            </span>
                          </div>
                        ))}
                      </div>
                    </Alert.Description>
                  </Alert.Content>
                </Alert>
              )}
            </div>
          )}
        </div>
      </div>

      {/* SPACIOUS 900px MODAL WINDOW: MOD Details, Cover, Metadata Editor, Pack (.yrdpk/.zip) & Entries */}
      <Modal.Backdrop isOpen={detailModalOpen && Boolean(selectedMod)} onOpenChange={setDetailModalOpen}>
        <Modal.Container placement="center" size="lg">
          <Modal.Dialog className="sm:max-w-[900px] max-h-[88vh] flex flex-col">
            <Modal.CloseTrigger />
            {selectedMod && (
              <>
                <Modal.Header className="pb-2">
                  <div className="min-w-0 flex-1 space-y-1">
                    <div className="flex flex-wrap items-center gap-2 pr-6">
                      <Modal.Heading className="truncate text-base">{selectedMod.name}</Modal.Heading>
                      <Chip size="sm" color="accent" variant="soft" className="h-5">
                        <Chip.Label className="font-mono text-[10px]">ID: {selectedMod.id}</Chip.Label>
                      </Chip>
                      <Chip size="sm" variant="soft" className="h-5">
                        <Chip.Label className="font-mono text-[10px]">v{selectedMod.version}</Chip.Label>
                      </Chip>
                      {selectedMod.gameVersion && (
                        <Chip size="sm" color="success" variant="soft" className="h-5">
                          {t.badgeGameVer} {selectedMod.gameVersion}
                        </Chip>
                      )}
                      {selectedMod.minGameVersion && (
                        <Chip size="sm" color="warning" variant="soft" className="h-5">
                          {t.badgeMinVer} {selectedMod.minGameVersion}
                        </Chip>
                      )}
                    </div>
                    <p className="text-xs text-muted">
                      {t.authorLabel}: {selectedMod.author || t.authorCommunity}
                    </p>
                  </div>
                </Modal.Header>

                <Modal.Body className="min-h-0 flex-1 overflow-y-auto py-3">
                  <div className="grid grid-cols-1 gap-4 md:grid-cols-[310px_1fr]">
                    {/* LEFT COLUMN: Cover Banner + Stats + Always-Visible Actions */}
                    <div className="space-y-3">
                      <div
                        onClick={() => handlePickCover(selectedMod)}
                        onDragOver={(e) => {
                          if (!e.dataTransfer.types.includes('Files')) return;
                          e.preventDefault();
                          e.stopPropagation();
                          setCoverDropHover(true);
                        }}
                        onDragLeave={() => setCoverDropHover(false)}
                        onDrop={(e) => handleCoverBannerDrop(e, selectedMod)}
                        className={`group/cover relative h-48 w-full cursor-pointer overflow-hidden rounded-xl border border-border/80 bg-gradient-to-br from-surface-secondary via-background to-accent-soft/25 transition-all ${
                          coverDropHover ? 'ring-2 ring-inset ring-accent' : ''
                        }`}
                      >
                        {selectedMod.coverUrl ? (
                          <img
                            src={resolveApiUrl(selectedMod.coverUrl)}
                            alt={selectedMod.name}
                            className="h-full w-full object-cover transition-transform duration-300 group-hover/cover:scale-[1.03]"
                          />
                        ) : (
                          <div className="flex h-full w-full flex-col items-center justify-center gap-1.5 text-muted/70">
                            <ImagePlus className="size-8 text-accent/70" />
                            <span className="text-xs">{t.dropCoverHint}</span>
                          </div>
                        )}
                        <div
                          className="absolute inset-x-0 bottom-0 flex items-center justify-between bg-gradient-to-t from-black/80 via-black/45 to-transparent px-3 py-2"
                          onClick={(e) => e.stopPropagation()}
                        >
                          <Chip size="sm" color={getCategoryAccent(selectedMod.effectiveCategory)} variant="primary" className="h-5">
                            <Chip.Label className="text-[10px]">
                              {getModCategoryLabel(selectedMod.effectiveCategory, t)}
                            </Chip.Label>
                          </Chip>
                          <div className="flex items-center gap-1">
                            <Button
                              size="sm"
                              variant="secondary"
                              isDisabled={busyAction}
                              onPress={() => handlePickCover(selectedMod)}
                              className="h-6 px-2 text-[11px]"
                            >
                              <ImagePlus className="size-3" />
                              {t.changeCover}
                            </Button>
                            {selectedMod.cover && (
                              <Button
                                size="sm"
                                variant="danger-soft"
                                isDisabled={busyAction}
                                onPress={() => handleClearCover(selectedMod)}
                                className="h-6 px-1.5 text-[11px]"
                              >
                                <X className="size-3" />
                              </Button>
                            )}
                          </div>
                        </div>
                      </div>

                      <Surface
                        variant="secondary"
                        className="rounded-lg border border-border/60 px-2.5 py-2 font-mono text-[11px] text-success-soft-foreground"
                      >
                        {summarizeModStats(selectedMod, t)}
                      </Surface>

                      <div className="grid grid-cols-2 gap-2">
                        {isDev && (
                          <Button
                            size="sm"
                            variant="primary"
                            onPress={() => {
                              setDetailModalOpen(false);
                              onOpenInStudio(selectedMod.id);
                            }}
                            className="h-8 text-xs"
                          >
                            <Palette className="size-3.5" />
                            {t.editInStudio}
                          </Button>
                        )}
                        <Button
                          size="sm"
                          variant="secondary"
                          onPress={() => apiPost('/api/shell/open-folder', { path: selectedMod.directoryPath })}
                          className={`h-8 text-xs ${!isDev ? 'col-span-2' : ''}`}
                        >
                          <FolderOpen className="size-3.5" />
                          {t.openFolder}
                        </Button>
                        <Button
                          size="sm"
                          variant="secondary"
                          isDisabled={busyAction}
                          onPress={() => handlePackSingle(selectedMod, 'yrdpk')}
                          className="h-8 text-xs"
                        >
                          <Archive className="size-3.5 text-warning" />
                          打包 .yrdpk
                        </Button>
                        <Button
                          size="sm"
                          variant="secondary"
                          isDisabled={busyAction}
                          onPress={() => handlePackSingle(selectedMod, 'zip')}
                          className="h-8 text-xs"
                        >
                          <Archive className="size-3.5 text-accent" />
                          打包 .zip
                        </Button>
                      </div>
                    </div>

                    {/* RIGHT COLUMN: Full Scrollable Description / Metadata Editor / Auto Entries / Hints */}
                    <div className="min-w-0 space-y-3">
                      <Card variant="default" className="p-3.5 space-y-2.5">
                        <div className="flex items-center justify-between gap-2">
                          <span className="text-xs font-semibold text-foreground">{t.modalDescription}</span>
                          {!editingMeta && (
                            <Button
                              size="sm"
                              variant="ghost"
                              onPress={() => setEditingMeta(true)}
                              className="h-6 px-2 text-[11px] text-accent"
                            >
                              <Pencil className="size-3" />
                              {t.editMetadata}
                            </Button>
                          )}
                        </div>

                        {!editingMeta ? (
                          <>
                            <p className="whitespace-pre-wrap break-words text-xs leading-relaxed text-foreground/90">
                              {selectedMod.description || '—'}
                            </p>
                            {selectedMod.tags.length > 0 && (
                              <div className="flex flex-wrap gap-1 pt-1">
                                {selectedMod.tags.map((tag) => (
                                  <Chip key={tag} size="sm" variant="soft" className="h-4">
                                    <Chip.Label className="font-mono text-[10px]">#{tag}</Chip.Label>
                                  </Chip>
                                ))}
                              </div>
                            )}
                          </>
                        ) : (
                          <div className="space-y-2.5">
                            <TextField
                              fullWidth
                              variant="secondary"
                              value={metaForm.name}
                              onChange={(v) => setMetaForm((f) => ({ ...f, name: v }))}
                            >
                              <Label className="text-[11px]">{t.modalDisplayName}</Label>
                              <Input className="h-8 text-xs" />
                            </TextField>

                            <div className="grid grid-cols-2 gap-2">
                              <div>
                                <div className="mb-1 text-[11px] font-medium text-foreground/85">{t.modCategoryLabel}</div>
                                <Select
                                  aria-label={t.modCategoryLabel}
                                  fullWidth
                                  variant="secondary"
                                  value={metaForm.category}
                                  onChange={(k) => k && setMetaForm((f) => ({ ...f, category: String(k) }))}
                                >
                                  <Select.Trigger className="h-8 text-xs">
                                    <Select.Value />
                                    <Select.Indicator />
                                  </Select.Trigger>
                                  <Select.Popover>
                                    <ListBox>
                                      <ListBox.Item id="auto" textValue={t.modCatAuto}>
                                        {t.modCatAuto}
                                        <ListBox.ItemIndicator />
                                      </ListBox.Item>
                                      {allCategorySlugs.filter((c) => c !== 'all').map((cat) => (
                                        <ListBox.Item key={cat} id={cat} textValue={getModCategoryLabel(cat, t)}>
                                          {getModCategoryLabel(cat, t)}
                                          <ListBox.ItemIndicator />
                                        </ListBox.Item>
                                      ))}
                                    </ListBox>
                                  </Select.Popover>
                                </Select>
                              </div>
                              <TextField
                                fullWidth
                                variant="secondary"
                                value={metaForm.version}
                                onChange={(v) => setMetaForm((f) => ({ ...f, version: v }))}
                              >
                                <Label className="text-[11px]">Version</Label>
                                <Input className="h-8 font-mono text-xs" />
                              </TextField>
                            </div>

                            <div className="grid grid-cols-2 gap-2">
                              <TextField
                                fullWidth
                                variant="secondary"
                                value={metaForm.author}
                                onChange={(v) => setMetaForm((f) => ({ ...f, author: v }))}
                              >
                                <Label className="text-[11px]">{t.modalAuthor}</Label>
                                <Input className="h-8 text-xs" />
                              </TextField>
                              <TextField
                                fullWidth
                                variant="secondary"
                                value={metaForm.minGameVersion}
                                onChange={(v) => setMetaForm((f) => ({ ...f, minGameVersion: v }))}
                              >
                                <Label className="text-[11px]">{t.modalMinGameVer}</Label>
                                <Input className="h-8 font-mono text-xs" placeholder="0.2.47" />
                              </TextField>
                            </div>

                            <TextField
                              fullWidth
                              variant="secondary"
                              value={metaForm.tagsCsv}
                              onChange={(v) => setMetaForm((f) => ({ ...f, tagsCsv: v }))}
                            >
                              <Label className="text-[11px]">{t.modTagsLabel}</Label>
                              <Input className="h-8 font-mono text-xs" placeholder="cards, lua, settings" />
                            </TextField>

                            <TextField
                              fullWidth
                              variant="secondary"
                              value={metaForm.description}
                              onChange={(v) => setMetaForm((f) => ({ ...f, description: v }))}
                            >
                              <Label className="text-[11px]">{t.modalDescription}</Label>
                              <TextArea rows={4} className="text-xs" />
                            </TextField>

                            <Switch
                              size="sm"
                              isSelected={metaForm.autoDiscoverEntries}
                              onChange={(checked) => setMetaForm((f) => ({ ...f, autoDiscoverEntries: checked }))}
                            >
                              <Switch.Content>
                                <Switch.Control><Switch.Thumb /></Switch.Control>
                                <div className="min-w-0">
                                  <span className="block text-[11px] font-medium text-foreground">{t.autoEntriesLabel}</span>
                                  <span className="block text-[10px] text-muted">{t.autoEntriesHint}</span>
                                </div>
                              </Switch.Content>
                            </Switch>

                            <div className="flex items-center justify-end gap-1.5 pt-1">
                              <Button size="sm" variant="secondary" onPress={() => setEditingMeta(false)} className="h-7 text-xs">
                                {t.cancel}
                              </Button>
                              <Button size="sm" variant="primary" isDisabled={busyAction} onPress={handleSaveMetadata} className="h-7 text-xs">
                                <Save className="size-3.5" />
                                {t.saveMetadata}
                              </Button>
                            </div>
                          </div>
                        )}
                      </Card>

                      {/* Auto-Imported Entries */}
                      <Card variant="default" className="p-3 gap-2">
                        <div className="flex items-center justify-between gap-2">
                          <span className="flex items-center gap-1.5 text-xs font-semibold text-foreground">
                            <Code2 className="size-3.5 text-accent" />
                            {t.autoEntriesTitle}
                          </span>
                          <Chip
                            size="sm"
                            color={selectedMod.autoDiscoverEntries ? 'success' : 'default'}
                            variant="soft"
                            className="h-4"
                          >
                            <Chip.Label className="text-[10px]">
                              {selectedMod.autoDiscoverEntries ? 'AUTO' : 'MANUAL'}
                            </Chip.Label>
                          </Chip>
                        </div>

                        {selectedMod.resolvedJsEntries.length === 0 &&
                        selectedMod.resolvedCssEntries.length === 0 &&
                        selectedMod.resolvedLuaLibs.length === 0 ? (
                          <p className="text-[11px] text-muted">{t.autoEntriesEmpty}</p>
                        ) : (
                          <div className="space-y-1 text-[11px]">
                            {selectedMod.resolvedJsEntries.map((entry) => (
                              <div key={`js-${entry}`} className="flex items-center gap-1.5 rounded-md bg-surface-secondary/70 px-2 py-1 font-mono">
                                <Chip size="sm" color="warning" variant="soft" className="h-4 shrink-0">JS</Chip>
                                <span className="truncate text-foreground/90">{entry}</span>
                              </div>
                            ))}
                            {selectedMod.resolvedCssEntries.map((entry) => (
                              <div key={`css-${entry}`} className="flex items-center gap-1.5 rounded-md bg-surface-secondary/70 px-2 py-1 font-mono">
                                <Chip size="sm" color="accent" variant="soft" className="h-4 shrink-0">CSS</Chip>
                                <span className="truncate text-foreground/90">{entry}</span>
                              </div>
                            ))}
                            {selectedMod.resolvedLuaLibs.map((entry) => (
                              <div key={`lua-${entry}`} className="flex items-center gap-1.5 rounded-md bg-surface-secondary/70 px-2 py-1 font-mono">
                                <Chip size="sm" color="success" variant="soft" className="h-4 shrink-0">Lua Lib</Chip>
                                <span className="truncate text-foreground/90">{entry}</span>
                              </div>
                            ))}
                          </div>
                        )}
                      </Card>

                      {/* MOD Hints & Spec Checklist */}
                      <Card variant="default" className="p-3 gap-2">
                        <div className="flex items-center justify-between gap-2">
                          <span className="flex items-center gap-1.5 text-xs font-semibold text-foreground">
                            <Info className="size-3.5 text-accent" />
                            {t.modHintsTitle}
                          </span>
                          <Button size="sm" variant="ghost" onPress={onOpenModGuide} className="h-6 px-2 text-[11px] text-accent">
                            <BookOpen className="size-3" />
                            {t.modGuideBtn}
                          </Button>
                        </div>
                        <ul className="space-y-1.5 text-[11px] leading-relaxed">
                          <li className="flex items-start gap-1.5 text-foreground/85">
                            <CheckCircle2 className={`mt-0.5 size-3.5 shrink-0 ${selectedMod.coverUrl ? 'text-success' : 'text-muted/50'}`} />
                            <span>{selectedMod.coverUrl ? t.hintHasCover : t.hintNoCover}</span>
                          </li>
                          <li className="flex items-start gap-1.5 text-foreground/85">
                            <CheckCircle2 className={`mt-0.5 size-3.5 shrink-0 ${selectedMod.autoDiscoverEntries ? 'text-success' : 'text-warning'}`} />
                            <span>
                              {selectedMod.autoDiscoverEntries
                                ? fmt(t.hintAutoEntriesOn, {
                                    js: selectedMod.resolvedJsEntries.length,
                                    css: selectedMod.resolvedCssEntries.length,
                                    lua: selectedMod.resolvedLuaLibs.length,
                                  })
                                : t.hintAutoEntriesOff}
                            </span>
                          </li>
                          <li className="flex items-start gap-1.5 text-foreground/85">
                            <CheckCircle2 className={`mt-0.5 size-3.5 shrink-0 ${selectedMod.settingsCount > 0 ? 'text-success' : 'text-muted/50'}`} />
                            <span>
                              {selectedMod.settingsCount > 0
                                ? fmt(t.hintHasSettings, { n: selectedMod.settingsCount })
                                : t.hintNoSettings}
                            </span>
                          </li>
                          {selectedMod.stats.cardCount > 0 && selectedMod.stats.scriptCount < selectedMod.stats.cardCount && (
                            <li className="flex items-start gap-1.5 text-warning">
                              <Info className="mt-0.5 size-3.5 shrink-0" />
                              <span>
                                {fmt(t.hintMissingLua, {
                                  cards: selectedMod.stats.cardCount,
                                  scripts: selectedMod.stats.scriptCount,
                                })}
                              </span>
                            </li>
                          )}
                        </ul>
                      </Card>
                    </div>
                  </div>
                </Modal.Body>

                <Modal.Footer className="flex items-center justify-end gap-2 pt-2">
                  <Button size="sm" variant="secondary" onPress={() => setDetailModalOpen(false)}>
                    {t.closeBtn}
                  </Button>
                </Modal.Footer>
              </>
            )}
          </Modal.Dialog>
        </Modal.Container>
      </Modal.Backdrop>

      {/* AI & SMART MOD CHECKLIST ORGANIZER MODAL */}
      <Modal.Backdrop isOpen={organizeOpen} onOpenChange={setOrganizeOpen}>
        <Modal.Container placement="center" size="lg">
          <Modal.Dialog className="sm:max-w-[780px] max-h-[86vh] flex flex-col">
            <Modal.CloseTrigger />
            <Modal.Header>
              <Modal.Icon className="bg-warning/15 text-warning">
                <Sparkles className="size-5" />
              </Modal.Icon>
              <Modal.Heading>{t.aiOrganizeTitle}</Modal.Heading>
            </Modal.Header>

            <Modal.Body className="min-h-0 flex-1 overflow-y-auto space-y-3 py-2">
              <div className="flex flex-wrap items-center gap-2">
                <Button
                  size="sm"
                  variant={organizeTab === 'smart' ? 'primary' : 'secondary'}
                  onPress={() => setOrganizeTab('smart')}
                  className="h-8 text-xs"
                >
                  <Sparkles className="size-3.5" />
                  {t.aiOrganizeSmartTab}
                </Button>
                <Button
                  size="sm"
                  variant={organizeTab === 'ai' ? 'primary' : 'secondary'}
                  onPress={() => setOrganizeTab('ai')}
                  className="h-8 text-xs"
                >
                  <Bot className="size-3.5" />
                  {t.aiOrganizeLlmTab}
                </Button>
                <Button
                  size="sm"
                  variant="ghost"
                  onPress={handleCopyOrganizeMcpPrompt}
                  className="ml-auto h-8 text-xs text-accent"
                >
                  <Copy className="size-3.5" />
                  {t.aiOrganizeCopyMcpBtn}
                </Button>
              </div>

              {organizeTab === 'smart' ? (
                <Card variant="default" className="p-3.5 space-y-3">
                  <p className="text-xs leading-relaxed text-muted">
                    {lang === 'zh'
                      ? '本地智能规则引擎会分析每个 MOD 的卡牌、Lua、遗物、角色、UI 样式与修改器内容，自动归类并按最佳层级排好加载顺序（大型综合 → 卡牌 → 卡包卡组 → 角色 → 遗物 → 脚本 → 界面美化 → 修改器）。无需 API Key！'
                      : 'The local smart rule engine analyzes every MOD’s content and sorts them into clean categories and optimal load order (Overhaul → Cards → Packs/Decks → Characters → Relics → Scripts → UI → Trainer). Zero API key needed!'}
                  </p>
                  <Switch size="sm" isSelected={preserveCustomCats} onChange={setPreserveCustomCats}>
                    <Switch.Content>
                      <Switch.Control><Switch.Thumb /></Switch.Control>
                      <span className="text-xs text-foreground">
                        {lang === 'zh' ? '保留我手动创建的自定义分类名称' : 'Preserve my custom category names'}
                      </span>
                    </Switch.Content>
                  </Switch>
                  <Button
                    size="sm"
                    variant="primary"
                    isDisabled={organizeBusy}
                    onPress={() => handlePreviewOrganize('smart')}
                    className="h-8 text-xs"
                  >
                    <Sparkles className="size-3.5" />
                    {t.aiOrganizePreviewBtn}
                  </Button>
                </Card>
              ) : (
                <Card variant="default" className="p-3.5 space-y-2.5">
                  <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
                    <TextField fullWidth variant="secondary" value={aiBaseUrl} onChange={setAiBaseUrl}>
                      <Label className="text-[11px]">OpenAI 兼容接口 (Base URL)</Label>
                      <Input className="h-8 font-mono text-xs" placeholder="https://api.deepseek.com/v1" />
                    </TextField>
                    <TextField fullWidth variant="secondary" value={aiModel} onChange={setAiModel}>
                      <Label className="text-[11px]">Model</Label>
                      <Input className="h-8 font-mono text-xs" placeholder="deepseek-chat / gpt-4o-mini / qwen2.5" />
                    </TextField>
                  </div>
                  <TextField fullWidth variant="secondary" value={aiApiKey} onChange={setAiApiKey}>
                    <Label className="text-[11px]">API Key（本地 Ollama 可留空）</Label>
                    <Input type="password" className="h-8 font-mono text-xs" placeholder="sk-..." />
                  </TextField>
                  <TextField fullWidth variant="secondary" value={aiPrompt} onChange={setAiPrompt}>
                    <Label className="text-[11px]">{lang === 'zh' ? '整理偏好（可选）' : 'Custom Preference (Optional)'}</Label>
                    <TextArea rows={2} className="text-xs" placeholder={t.aiOrganizePromptPlaceholder} />
                  </TextField>
                  <Button
                    size="sm"
                    variant="primary"
                    isDisabled={organizeBusy}
                    onPress={() => handlePreviewOrganize('ai')}
                    className="h-8 text-xs"
                  >
                    <Bot className="size-3.5" />
                    {organizeBusy ? (lang === 'zh' ? 'AI 正在思考整理方案...' : 'AI organizing...') : t.aiOrganizePreviewBtn}
                  </Button>
                </Card>
              )}

              {organizePlan && (
                <Card variant="default" className="p-3 space-y-2 border-accent/50">
                  <div className="flex items-center justify-between gap-2">
                    <span className="text-xs font-semibold text-accent">{organizePlan.summary}</span>
                  </div>
                  <div className="max-h-64 overflow-y-auto space-y-1.5 pr-1">
                    {organizePlan.items.map((it) => {
                      const catChanged = it.oldCategory.toLowerCase() !== it.newCategory.toLowerCase();
                      const orderChanged = it.oldOrder !== it.newOrder;
                      return (
                        <div
                          key={it.modId}
                          className="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-border/70 bg-background px-2.5 py-1.5 text-xs"
                        >
                          <div className="min-w-0 flex-1">
                            <div className="flex flex-wrap items-center gap-1.5">
                              <span className={`font-mono text-[11px] ${orderChanged ? 'text-warning font-bold' : 'text-muted'}`}>
                                #{it.oldOrder + 1} → #{it.newOrder + 1}
                              </span>
                              <span className="font-semibold text-foreground">{it.modName}</span>
                              <Chip size="sm" color={catChanged ? 'accent' : 'default'} variant="soft" className="h-4">
                                <Chip.Label className="text-[10px]">
                                  {catChanged
                                    ? `${getModCategoryLabel(it.oldCategory, t)} → ${getModCategoryLabel(it.newCategory, t)}`
                                    : getModCategoryLabel(it.newCategory, t)}
                                </Chip.Label>
                              </Chip>
                            </div>
                            <p className="mt-0.5 text-[11px] text-muted">{it.reason}</p>
                          </div>
                        </div>
                      );
                    })}
                  </div>
                </Card>
              )}
            </Modal.Body>

            <Modal.Footer className="flex items-center justify-end gap-2 pt-2">
              <Button size="sm" variant="secondary" onPress={() => setOrganizeOpen(false)}>
                {t.cancel}
              </Button>
              <Button
                size="sm"
                variant="primary"
                isDisabled={!organizePlan || organizeBusy}
                onPress={handleApplyOrganizePlan}
              >
                <CheckCircle2 className="size-3.5" />
                {t.aiOrganizeApplyBtn}
              </Button>
            </Modal.Footer>
          </Modal.Dialog>
        </Modal.Container>
      </Modal.Backdrop>
    </div>
  );
}
