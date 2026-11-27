import { useCallback, useEffect, useRef, useState } from 'react';
import {
  Button,
  Input,
  Label,
  ListBox,
  Modal,
  ProgressBar,
  Select,
  Spinner,
  Switch,
  TextField,
  Toast,
  toast,
} from '@heroui/react';
import {
  BookOpen,
  Bot,
  Check,
  ChevronDown,
  ChevronUp,
  Code2,
  Copy,
  ExternalLink,
  Flame,
  FolderOpen,
  FolderPlus,
  Globe,
  Layers,
  MonitorPlay,
  Moon,
  Package,
  Palette,
  Plus,
  RefreshCw,
  Settings,
  Sliders,
  Sparkles,
  Square,
  Sun,
  Syringe,
  Terminal,
  Trash2,
  User,
  Wand2,
  X,
} from 'lucide-react';
import { buildAiPrompt } from './aiPrompts';
import { apiGet, apiPost, subscribeEvents } from './api';
import { ModGuideModal } from './components/ModGuideModal';
import { TitleBar, WindowResizeHandles } from './components/WindowChrome';
import { fmt, getDict, normalizeLang } from './i18n';
import { LivePreviewTab } from './tabs/LivePreviewTab';
import { McpHubTab } from './tabs/McpHubTab';
import { getModCategoryLabel, MOD_CATEGORY_LIST, ModLibraryTab } from './tabs/ModLibraryTab';
import { ModStudioTab } from './tabs/ModStudioTab';
import { SettingsApiTab } from './tabs/SettingsApiTab';
import { VanillaCatalogTab } from './tabs/VanillaCatalogTab';
import type {
  Lang,
  LogLine,
  ManagerStatus,
  McpCallRecord,
  StudioCategoryKey,
  StudioDraftFromVanilla,
  StudioSyncEvent,
  VanillaCategoryKey,
  WorkspaceProfile,
} from './types';

type TabKey = 'library' | 'studio' | 'vanilla' | 'preview' | 'mcp' | 'settings';

export function App() {
  const [status, setStatus] = useState<ManagerStatus | null>(null);
  const [workspaces, setWorkspaces] = useState<WorkspaceProfile[]>([]);
  const [activeWsId, setActiveWsId] = useState<string>('');
  const [lang, setLang] = useState<Lang>('zh');
  const [theme, setTheme] = useState<'dark' | 'light'>('dark');
  const [tab, setTab] = useState<TabKey>('studio');
  const [activeModId, setActiveModId] = useState<string>('');

  // IDE Pane Toggles (Pinned 3-pane on desktop >= 1024px; slide-over drawers on narrow/mobile!)
  const [sidebarOpen, setSidebarOpen] = useState(() => typeof window === 'undefined' || window.innerWidth >= 1024);
  const [inspectorOpen, setInspectorOpen] = useState(() => typeof window === 'undefined' || window.innerWidth >= 1024);
  const [consoleOpen, setConsoleOpen] = useState(false);

  useEffect(() => {
    const mq = window.matchMedia('(min-width: 1024px)');
    const onChange = (e: MediaQueryListEvent) => {
      setSidebarOpen(e.matches);
      setInspectorOpen(e.matches);
    };
    mq.addEventListener('change', onChange);
    return () => mq.removeEventListener('change', onChange);
  }, []);

  // Synced sidebar sub-navigation states for Studio & Vanilla
  const [studioCategory, setStudioCategory] = useState<StudioCategoryKey>('cards');
  const [studioCounts, setStudioCounts] = useState<Record<StudioCategoryKey, number>>({
    cards: 0,
    characters: 0,
    relics: 0,
    packs: 0,
    decks: 0,
    trainer: 0,
    code: 0,
  });
  const [vanillaCategory, setVanillaCategory] = useState<VanillaCategoryKey>('cards');
  const [vanillaCounts, setVanillaCounts] = useState<Record<VanillaCategoryKey, number>>({
    cards: 2143,
    characters: 116,
    relics: 324,
    packs: 86,
    decks: 22,
  });

  const [incomingDraft, setIncomingDraft] = useState<StudioDraftFromVanilla | null>(null);
  const [studioFocus, setStudioFocus] = useState<{
    modId: string;
    category: StudioCategoryKey;
    key: string;
  } | null>(null);

  const [busyOp, setBusyOp] = useState(false);
  const [progressText, setProgressText] = useState('');
  const [progressPct, setProgressPct] = useState<number>(0);
  const [logs, setLogs] = useState<LogLine[]>([]);
  const [mcpCalls, setMcpCalls] = useState<McpCallRecord[]>([]);
  const [mcpSyncEvent, setMcpSyncEvent] = useState<StudioSyncEvent | null>(null);
  const [copiedLogs, setCopiedLogs] = useState(false);

  // Modals
  const [showModGuide, setShowModGuide] = useState(false);
  const [showNewMod, setShowNewMod] = useState(false);
  const [newModForm, setNewModForm] = useState({
    id: 'my-custom-mod',
    name: 'My Custom MOD',
    author: 'Author',
    category: 'auto',
    coverSourcePath: '',
    minGameVersion: '',
    description: 'Created with Visual MOD Studio.',
    completeExample: false,
  });

  const [showNewWs, setShowNewWs] = useState(false);
  const [newWsForm, setNewWsForm] = useState({
    name: '',
    path: '',
    description: '',
  });

  const logIdRef = useRef(1);
  const logEndRef = useRef<HTMLDivElement | null>(null);

  const t = getDict(lang);

  useEffect(() => {
    const html = document.documentElement;
    html.classList.toggle('dark', theme === 'dark');
    html.classList.toggle('light', theme === 'light');
    html.setAttribute('data-theme', theme);
  }, [theme]);

  const notify = useCallback((msg: string, kind: 'ok' | 'err' | 'info' = 'info') => {
    if (kind === 'ok') toast.success(msg);
    else if (kind === 'err') toast.danger(msg);
    else toast.info(msg);
  }, []);

  const refreshAll = useCallback(async () => {
    const [st, ws] = await Promise.all([
      apiGet<ManagerStatus>('/api/status'),
      apiGet<{ activeId: string; workspaces: WorkspaceProfile[] }>('/api/workspaces'),
    ]);
    setStatus(st);
    setLang(normalizeLang(st.language));
    setWorkspaces(ws.workspaces || []);
    setActiveWsId(ws.activeId || '');
    setActiveModId((prev) => {
      if (prev && st.mods.some((m) => m.id === prev)) return prev;
      return st.mods[0]?.id ?? '';
    });
    if (!newModForm.minGameVersion && st.activeBaseGameVersion) {
      setNewModForm((f) => ({ ...f, minGameVersion: st.activeBaseGameVersion ?? '' }));
    }
  }, [newModForm.minGameVersion]);

  useEffect(() => {
    refreshAll().catch((e) => notify((e as Error).message, 'err'));

    const unsub = subscribeEvents({
      onLog: (ev) => {
        setLogs((prev) => {
          const next = [...prev, { id: logIdRef.current++, ...ev }];
          return next.length > 300 ? next.slice(-300) : next;
        });
      },
      onProgress: (ev) => {
        setProgressText(ev.status || '');
        if (typeof ev.percent === 'number') setProgressPct(ev.percent);
      },
      onHotReload: (ev) => {
        setStatus((prev) =>
          prev
            ? {
                ...prev,
                hotReload: {
                  running: ev.running,
                  port: ev.port,
                  url: ev.url,
                  revision: ev.revision,
                  lastError: ev.error ?? null,
                },
              }
            : prev,
        );
      },
      onMcpCall: (rec) => {
        setMcpCalls((prev) => [rec, ...prev].slice(0, 60));
      },
      onStudioSync: (ev) => {
        setMcpSyncEvent({ ...ev });
      },
    });

    return () => unsub();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    if (consoleOpen) {
      logEndRef.current?.scrollIntoView({ behavior: 'smooth' });
    }
  }, [logs, consoleOpen]);

  const toggleLanguage = async () => {
    const next: Lang = lang === 'zh' ? 'en' : 'zh';
    setLang(next);
    try {
      await apiPost('/api/settings', { language: next });
    } catch {
      // ignore
    }
  };

  const handleApply = async (launchAfter: boolean) => {
    if (!status) return;
    setBusyOp(true);
    setConsoleOpen(true);
    try {
      await apiPost('/api/apply', {
        mode: status.mode,
        developerMode: status.developerMode,
        launchAfter,
      });
      await refreshAll();
      notify(launchAfter ? t.toastAppliedLaunch : t.toastApplied, 'ok');
    } catch (e) {
      notify((e as Error).message, 'err');
    } finally {
      setBusyOp(false);
    }
  };

  const handleRestore = async () => {
    setBusyOp(true);
    setConsoleOpen(true);
    try {
      await apiPost('/api/restore');
      await refreshAll();
      notify(t.toastRestored, 'ok');
    } catch (e) {
      notify((e as Error).message, 'err');
    } finally {
      setBusyOp(false);
    }
  };

  const handleSwitchWorkspace = async (id: string) => {
    try {
      await apiPost('/api/workspaces/switch', { idOrPath: id });
      await refreshAll();
      notify(t.toastSwitchedWs, 'ok');
    } catch (e) {
      notify((e as Error).message, 'err');
    }
  };

  const handleRemoveWorkspace = async () => {
    if (!activeWsId || workspaces.length <= 1) return;
    if (!window.confirm(t.confirmRemoveWorkspace)) return;
    try {
      await apiPost('/api/workspaces/remove', { id: activeWsId });
      await refreshAll();
      notify(t.toastRemovedWs, 'info');
    } catch (e) {
      notify((e as Error).message, 'err');
    }
  };

  const handleStartStopHotReload = async () => {
    if (!status) return;
    try {
      if (status.hotReload.running) {
        await apiPost('/api/hotreload/stop');
        notify(t.toastHotReloadStopped, 'info');
      } else {
        await apiPost('/api/hotreload/start');
        notify(t.toastHotReloadStarted, 'ok');
      }
      await refreshAll();
    } catch (e) {
      notify((e as Error).message, 'err');
    }
  };

  const handleInjectHotReload = async () => {
    setBusyOp(true);
    setConsoleOpen(true);
    try {
      await apiPost('/api/hotreload/inject');
      await refreshAll();
      notify(t.toastHotReloadInjected, 'ok');
    } catch (e) {
      notify((e as Error).message, 'err');
    } finally {
      setBusyOp(false);
    }
  };

  const handleManualReload = async () => {
    try {
      const res = await apiPost<{ revision: number; error: string | null }>('/api/hotreload/reload');
      if (res.error) notify(res.error, 'err');
      else notify(fmt(t.toastHotReloadTriggered, { rev: res.revision }), 'ok');
      await refreshAll();
    } catch (e) {
      notify((e as Error).message, 'err');
    }
  };

  const handleCreateMod = async () => {
    try {
      const payload = {
        ...newModForm,
        category: newModForm.category === 'auto' ? '' : newModForm.category,
      };
      const res = await apiPost<{ modId: string }>('/api/mods/create', payload);
      setShowNewMod(false);
      await refreshAll();
      setActiveModId(res.modId);
      setTab('studio');
      notify(fmt(t.toastCreatedMod, { id: res.modId }), 'ok');
    } catch (e) {
      notify((e as Error).message, 'err');
    }
  };

  const handleCreateExampleMod = async () => {
    const res = await apiPost<{ modId: string; name: string }>('/api/mods/create-example', {});
    await refreshAll();
    setActiveModId(res.modId);
    notify(fmt(t.toastExampleCreated, { name: res.name }), 'ok');
  };

  const handleCopyAiSuperPrompt = () => {
    if (!status) return;
    const mod = status.mods.find((m) => m.id === activeModId);
    const prompt = buildAiPrompt('all_in_one', {
      lang,
      apiBaseUrl: status.apiBaseUrl,
      mcpUrl: status.mcpUrl,
      mcpSseUrl: status.mcpSseUrl,
      workspaceRoot: status.workspaceRoot,
      gameVersion: status.activeBaseGameVersion,
      activeModId: mod?.id,
      activeModName: mod?.name,
    });
    navigator.clipboard.writeText(prompt).catch(() => {});
    notify(t.toastAiSuperPromptCopied, 'ok');
  };

  const handleCreateWorkspace = async () => {
    try {
      await apiPost('/api/workspaces/create', newWsForm);
      setShowNewWs(false);
      setNewWsForm({ name: '', path: '', description: '' });
      await refreshAll();
      notify(t.toastCreatedWs, 'ok');
    } catch (e) {
      notify((e as Error).message, 'err');
    }
  };

  if (!status) {
    return (
      <div className="flex h-full w-full flex-col items-center justify-center gap-3 bg-background text-foreground">
        <Spinner size="lg" />
        <p className="text-xs font-medium tracking-wide text-muted">
          {t.connectingApi}
        </p>
      </div>
    );
  }

  const enabledCount = status.mods.filter((m) => m.enabled).length;
  const activeWsName = workspaces.find((w) => w.id === activeWsId)?.name ?? 'Default';
  const activeModObj = status.mods.find((m) => m.id === activeModId);

  const sectionTitle =
    tab === 'library'
      ? t.tabLibrary
      : tab === 'studio'
        ? activeModObj
          ? `${activeModObj.name}`
          : t.tabStudio
        : tab === 'vanilla'
          ? t.tabVanilla
          : tab === 'preview'
            ? t.tabPreview
            : tab === 'mcp'
              ? t.tabMcp
              : t.tabSettings;

  const studioCategories: { key: StudioCategoryKey; label: string; icon: React.ReactNode }[] = [
    { key: 'cards', label: t.catCards, icon: <Sparkles className="size-3.5" /> },
    { key: 'characters', label: t.catCharacters, icon: <User className="size-3.5" /> },
    { key: 'relics', label: t.catRelics, icon: <Wand2 className="size-3.5" /> },
    { key: 'packs', label: t.catPacks, icon: <Package className="size-3.5" /> },
    { key: 'decks', label: t.catDecks, icon: <Layers className="size-3.5" /> },
    { key: 'trainer', label: t.catTrainer, icon: <Sliders className="size-3.5" /> },
    { key: 'code', label: t.catCode, icon: <Code2 className="size-3.5" /> },
  ];

  const vanillaCategories: { key: VanillaCategoryKey; label: string; icon: React.ReactNode }[] = [
    { key: 'cards', label: t.catCards, icon: <Sparkles className="size-3.5" /> },
    { key: 'characters', label: t.catCharacters, icon: <User className="size-3.5" /> },
    { key: 'relics', label: t.catRelics, icon: <Wand2 className="size-3.5" /> },
    { key: 'packs', label: t.catPacks, icon: <Package className="size-3.5" /> },
    { key: 'decks', label: t.catDecks, icon: <Layers className="size-3.5" /> },
  ];

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background text-foreground">
      <WindowResizeHandles />
      <Toast.Provider placement="bottom end" />

      {/* SINGLE 40px INTEGRATED DARK TITLEBAR (Replaces ugly white OS bar + 3-row header!) */}
      <TitleBar
        t={t}
        workspaceName={activeWsName}
        sectionTitle={sectionTitle}
        vanillaVersion={status.activeBaseGameVersion}
        enabledCount={enabledCount}
        totalMods={status.mods.length}
        busyOp={busyOp}
        sidebarOpen={sidebarOpen}
        inspectorOpen={inspectorOpen}
        consoleOpen={consoleOpen}
        onToggleSidebar={() => setSidebarOpen((o) => !o)}
        onToggleInspector={() => setInspectorOpen((o) => !o)}
        onToggleConsole={() => setConsoleOpen((o) => !o)}
        onApply={() => handleApply(false)}
        onApplyLaunch={() => handleApply(true)}
        onRestore={handleRestore}
      />

      {/* IDE BODY: Left Collapsible Sidebar + Center/Right Stage */}
      <div className="relative flex min-h-0 flex-1 overflow-hidden">
        {/* Mobile Backdrop for Left Sidebar */}
        {sidebarOpen && (
          <div
            onClick={() => setSidebarOpen(false)}
            className="fixed inset-0 top-10 z-30 bg-black/55 backdrop-blur-[1px] lg:hidden"
          />
        )}

        {/* LEFT SIDEBAR (Pinned on desktop >= 1024px; slide-over drawer on mobile!) */}
        {sidebarOpen && (
          <aside className="fixed inset-y-0 top-10 left-0 z-40 flex w-[248px] shrink-0 flex-col border-r border-border bg-surface shadow-2xl lg:static lg:z-auto lg:bg-surface/55 lg:shadow-none">
            {/* Workspace + Active MOD Selectors (Single-line truncated, zero wrapping!) */}
            <div className="space-y-2 border-b border-border/80 p-2.5">
              <div>
                <div className="mb-1 flex items-center justify-between px-0.5 text-[10px] font-medium uppercase tracking-wider text-muted">
                  <span>{t.workspace}</span>
                  <div className="flex items-center gap-0.5">
                    <button
                      type="button"
                      onClick={() => setShowNewWs(true)}
                      title={t.newWorkspace}
                      className="rounded p-0.5 text-muted hover:bg-surface-secondary hover:text-foreground"
                    >
                      <Plus className="size-3" />
                    </button>
                    {workspaces.length > 1 && (
                      <button
                        type="button"
                        onClick={handleRemoveWorkspace}
                        title={t.removeWorkspace}
                        className="rounded p-0.5 text-muted hover:bg-surface-secondary hover:text-danger"
                      >
                        <X className="size-3" />
                      </button>
                    )}
                  </div>
                </div>
                <Select
                  aria-label="Workspace"
                  fullWidth
                  variant="secondary"
                  value={activeWsId || null}
                  onChange={(k) => k && handleSwitchWorkspace(String(k))}
                >
                  <Select.Trigger className="h-7 text-xs">
                    <Select.Value />
                    <Select.Indicator />
                  </Select.Trigger>
                  <Select.Popover>
                    <ListBox>
                      {workspaces.map((w) => (
                        <ListBox.Item key={w.id} id={w.id} textValue={w.name}>
                          {w.name}
                          <ListBox.ItemIndicator />
                        </ListBox.Item>
                      ))}
                    </ListBox>
                  </Select.Popover>
                </Select>
              </div>

              <div>
                <div className="mb-1 flex items-center justify-between px-0.5 text-[10px] font-medium uppercase tracking-wider text-muted">
                  <span>{t.activeMod}</span>
                  <button
                    type="button"
                    onClick={() => setShowNewMod(true)}
                    title={t.newMod}
                    className="rounded p-0.5 text-muted hover:bg-surface-secondary hover:text-foreground"
                  >
                    <Plus className="size-3" />
                  </button>
                </div>
                <Select
                  aria-label={t.activeMod}
                  fullWidth
                  variant="secondary"
                  placeholder={t.noModPlaceholder}
                  value={activeModId || null}
                  onChange={(k) => k && setActiveModId(String(k))}
                >
                  <Select.Trigger className="h-7 text-xs font-medium">
                    <Select.Value />
                    <Select.Indicator />
                  </Select.Trigger>
                  <Select.Popover>
                    <ListBox>
                      {status.mods.map((m) => (
                        <ListBox.Item key={m.id} id={m.id} textValue={m.name}>
                          {m.name}
                          <ListBox.ItemIndicator />
                        </ListBox.Item>
                      ))}
                    </ListBox>
                  </Select.Popover>
                </Select>
              </div>
            </div>

            {/* Vertical Navigation Tree */}
            <div className="min-h-0 flex-1 overflow-y-auto px-2 py-2.5 space-y-1">
              <SidebarNavItem
                active={tab === 'library'}
                onClick={() => setTab('library')}
                icon={<Layers className="size-3.5" />}
                label={t.tabLibrary}
                count={`${enabledCount}/${status.mods.length}`}
              />

              <SidebarNavItem
                active={tab === 'studio'}
                onClick={() => setTab('studio')}
                icon={<Palette className="size-3.5" />}
                label={t.tabStudio}
              />
              {tab === 'studio' && (
                <div className="ml-3.5 border-l border-border/80 pl-2 space-y-0.5 py-0.5">
                  {studioCategories.map((c) => (
                    <button
                      key={c.key}
                      type="button"
                      onClick={() => setStudioCategory(c.key)}
                      className={`flex w-full items-center justify-between rounded-md px-2 py-1 text-xs transition-colors ${
                        studioCategory === c.key
                          ? 'bg-accent/15 text-accent font-medium'
                          : 'text-muted hover:bg-surface-secondary hover:text-foreground'
                      }`}
                    >
                      <span className="flex items-center gap-1.5 truncate">
                        {c.icon}
                        <span className="truncate">{c.label}</span>
                      </span>
                      <span className="font-mono text-[10px] opacity-80">{studioCounts[c.key] ?? 0}</span>
                    </button>
                  ))}
                </div>
              )}

              <SidebarNavItem
                active={tab === 'vanilla'}
                onClick={() => setTab('vanilla')}
                icon={<BookOpen className="size-3.5" />}
                label={t.tabVanilla}
              />
              {tab === 'vanilla' && (
                <div className="ml-3.5 border-l border-border/80 pl-2 space-y-0.5 py-0.5">
                  {vanillaCategories.map((c) => (
                    <button
                      key={c.key}
                      type="button"
                      onClick={() => setVanillaCategory(c.key)}
                      className={`flex w-full items-center justify-between rounded-md px-2 py-1 text-xs transition-colors ${
                        vanillaCategory === c.key
                          ? 'bg-accent/15 text-accent font-medium'
                          : 'text-muted hover:bg-surface-secondary hover:text-foreground'
                      }`}
                    >
                      <span className="flex items-center gap-1.5 truncate">
                        {c.icon}
                        <span className="truncate">{c.label}</span>
                      </span>
                      <span className="font-mono text-[10px] opacity-80">{vanillaCounts[c.key] ?? 0}</span>
                    </button>
                  ))}
                </div>
              )}

              <SidebarNavItem
                active={tab === 'preview'}
                onClick={() => setTab('preview')}
                icon={<MonitorPlay className="size-3.5" />}
                label={t.tabPreview}
                liveDot={status.hotReload.running}
              />

              <SidebarNavItem
                active={tab === 'mcp'}
                onClick={() => setTab('mcp')}
                icon={<Bot className="size-3.5" />}
                label={t.tabMcp}
                count={String(status.mcpToolCount || 21)}
                liveDot={true}
              />

              <SidebarNavItem
                active={tab === 'settings'}
                onClick={() => setTab('settings')}
                icon={<Settings className="size-3.5" />}
                label={t.tabSettings}
              />
            </div>

            {/* Runtime / Hot-Reload Quick Card */}
            <div className="border-t border-border/80 p-2.5 space-y-2 text-xs">
              <div className="flex items-center gap-1">
                <Button
                  size="sm"
                  variant={status.hotReload.running ? 'secondary' : 'tertiary'}
                  onPress={handleStartStopHotReload}
                  className={`h-7 flex-1 justify-start px-2 text-[11px] font-medium ${
                    status.hotReload.running ? 'button--warning-soft' : ''
                  }`}
                >
                  {status.hotReload.running ? (
                    <>
                      <Square className="size-2.5 fill-current" />
                      <span className="truncate">
                        {t.hotReloadStop} #{status.hotReload.revision}
                      </span>
                    </>
                  ) : (
                    <>
                      <Flame className="size-3.5 text-warning" />
                      <span className="truncate">{t.hotReloadStart}</span>
                    </>
                  )}
                </Button>
                <Button
                  isIconOnly
                  size="sm"
                  variant="tertiary"
                  aria-label={t.hotReloadInject}
                  isDisabled={busyOp}
                  onPress={handleInjectHotReload}
                  className="size-7 shrink-0"
                >
                  <Syringe className="size-3.5 text-accent" />
                </Button>
                {status.hotReload.running && status.hotReload.url && (
                  <>
                    <Button
                      isIconOnly
                      size="sm"
                      variant="tertiary"
                      aria-label={t.hotReloadOpen}
                      onPress={() => apiPost('/api/shell/open-url', { url: status.hotReload.url })}
                      className="size-7 shrink-0"
                    >
                      <ExternalLink className="size-3.5 text-success" />
                    </Button>
                    <Button
                      isIconOnly
                      size="sm"
                      variant="tertiary"
                      aria-label={t.hotReloadNow}
                      onPress={handleManualReload}
                      className="size-7 shrink-0"
                    >
                      <RefreshCw className="size-3.5" />
                    </Button>
                  </>
                )}
              </div>

              <Select
                aria-label="Injection Mode"
                fullWidth
                variant="secondary"
                value={status.mode}
                onChange={async (k) => {
                  if (!k) return;
                  await apiPost('/api/settings', { mode: String(k) });
                  await refreshAll();
                }}
              >
                <Select.Trigger className="h-7 text-[11px]">
                  <Select.Value />
                  <Select.Indicator />
                </Select.Trigger>
                <Select.Popover>
                  <ListBox>
                    <ListBox.Item id="VirtualBundle" textValue={t.modeVirtual}>
                      {t.modeVirtual}
                      <ListBox.ItemIndicator />
                    </ListBox.Item>
                    <ListBox.Item id="LauncherManifest" textValue={t.modeManifest}>
                      {t.modeManifest}
                      <ListBox.ItemIndicator />
                    </ListBox.Item>
                  </ListBox>
                </Select.Popover>
              </Select>

              <Switch
                size="sm"
                isSelected={status.developerMode}
                onChange={async (checked) => {
                  await apiPost('/api/settings', { developerMode: checked });
                  await refreshAll();
                }}
              >
                <Switch.Content>
                  <Switch.Control>
                    <Switch.Thumb />
                  </Switch.Control>
                  <span className="text-[11px] text-foreground/90">{t.devMode}</span>
                </Switch.Content>
              </Switch>
            </div>

            {/* Bottom-Left Utility Strip (Matches T3 Code bottom-left icons!) */}
            <div className="flex h-9 shrink-0 items-center justify-between border-t border-border/80 px-2">
              <div className="flex items-center gap-0.5">
                <button
                  type="button"
                  onClick={() => setTab('settings')}
                  title={t.tabSettings}
                  className="flex size-7 items-center justify-center rounded-md text-muted hover:bg-surface-secondary hover:text-foreground"
                >
                  <Settings className="size-3.5" />
                </button>
                <button
                  type="button"
                  onClick={() => setTheme((prev) => (prev === 'dark' ? 'light' : 'dark'))}
                  title={t.toggleTheme}
                  className="flex size-7 items-center justify-center rounded-md text-muted hover:bg-surface-secondary hover:text-foreground"
                >
                  {theme === 'dark' ? <Sun className="size-3.5 text-warning" /> : <Moon className="size-3.5 text-accent" />}
                </button>
                <button
                  type="button"
                  onClick={toggleLanguage}
                  title={t.switchLanguage}
                  className="flex h-7 items-center gap-1 rounded-md px-1.5 text-[11px] font-medium text-muted hover:bg-surface-secondary hover:text-foreground"
                >
                  <Globe className="size-3.5" />
                  <span>{lang === 'zh' ? 'EN' : '中文'}</span>
                </button>
                <button
                  type="button"
                  onClick={() => setShowModGuide(true)}
                  title={t.modGuideBtn}
                  className="flex size-7 items-center justify-center rounded-md text-muted hover:bg-surface-secondary hover:text-accent"
                >
                  <BookOpen className="size-3.5" />
                </button>
              </div>
              <button
                type="button"
                onClick={() => apiPost('/api/shell/open-folder', { path: status.workspaceRoot })}
                title={t.openWorkspace}
                className="flex size-7 items-center justify-center rounded-md text-muted hover:bg-surface-secondary hover:text-foreground"
              >
                <FolderOpen className="size-3.5" />
              </button>
            </div>
          </aside>
        )}

        {/* RIGHT WORKBENCH COLUMN: Main Active Tab + Bottom Collapsible Terminal */}
        <div className="flex min-w-0 flex-1 flex-col overflow-hidden">
          <main className="flex min-h-0 flex-1 overflow-hidden">
            {tab === 'library' && (
              <ModLibraryTab
                status={status}
                t={t}
                selectedModId={activeModId}
                inspectorOpen={inspectorOpen}
                onSelectMod={setActiveModId}
                onRefresh={refreshAll}
                onOpenInStudio={(modId) => {
                  setActiveModId(modId);
                  setTab('studio');
                }}
                onOpenNewModModal={() => setShowNewMod(true)}
                onOpenModGuide={() => setShowModGuide(true)}
                onCopyAiPrompt={handleCopyAiSuperPrompt}
                onCreateExampleMod={handleCreateExampleMod}
                onNotify={notify}
              />
            )}

            {tab === 'studio' && (
              <ModStudioTab
                t={t}
                activeModId={activeModId}
                activeModName={status.mods.find((m) => m.id === activeModId)?.name}
                mcpUrl={status.mcpUrl}
                mcpSyncEvent={mcpSyncEvent}
                category={studioCategory}
                inspectorOpen={inspectorOpen}
                onCategoryChange={setStudioCategory}
                onCountsChange={setStudioCounts}
                onSelectActiveMod={setActiveModId}
                onRefreshStatus={refreshAll}
                incomingDraft={incomingDraft}
                onClearIncomingDraft={() => setIncomingDraft(null)}
                focusTarget={studioFocus}
                onClearFocusTarget={() => setStudioFocus(null)}
                onNotify={notify}
              />
            )}

            {tab === 'vanilla' && (
              <VanillaCatalogTab
                t={t}
                mods={status.mods}
                activeModId={activeModId}
                category={vanillaCategory}
                inspectorOpen={inspectorOpen}
                onCountsChange={setVanillaCounts}
                onSelectActiveMod={setActiveModId}
                onCloneToStudioDraft={(draft) => {
                  setIncomingDraft(draft);
                  setTab('studio');
                }}
                onQuickCloned={(modId, category: VanillaCategoryKey, clonedKey) => {
                  setStudioFocus({ modId, category, key: clonedKey });
                  setTab('studio');
                  refreshAll().catch(() => {});
                }}
                onNotify={notify}
              />
            )}

            {tab === 'preview' && (
              <LivePreviewTab
                t={t}
                hotReload={status.hotReload}
                onStartHotReload={handleStartStopHotReload}
                onInjectHotReload={handleInjectHotReload}
              />
            )}

            {tab === 'mcp' && (
              <McpHubTab
                t={t}
                lang={lang}
                apiBaseUrl={status.apiBaseUrl}
                gameVersion={status.activeBaseGameVersion}
                activeModId={activeModId}
                activeModName={activeModObj?.name}
                liveCalls={mcpCalls}
                onClearLiveCalls={() => setMcpCalls([])}
                onNotify={notify}
              />
            )}

            {tab === 'settings' && (
              <SettingsApiTab
                status={status}
                t={t}
                lang={lang}
                onRefresh={refreshAll}
                onOpenConsole={() => setConsoleOpen(true)}
                onNotify={notify}
              />
            )}
          </main>

          {/* BOTTOM COLLAPSIBLE TERMINAL / SSE CONSOLE (Matches T3 Code bottom terminal!) */}
          <footer className="shrink-0 border-t border-border bg-background">
            <div
              onClick={() => setConsoleOpen((o) => !o)}
              className="flex h-7 cursor-pointer items-center justify-between gap-3 px-3 text-[11px] hover:bg-surface-secondary/60 transition-colors"
            >
              <div className="flex min-w-0 items-center gap-2">
                <Terminal className="size-3 text-accent" />
                <span className="font-medium text-foreground/90">{t.consoleTitle}</span>
                <span className="truncate font-mono text-muted">
                  {progressText || (logs.length > 0 ? logs[logs.length - 1].message : t.ready)}
                </span>
              </div>
              <div className="flex shrink-0 items-center gap-2">
                {progressPct > 0 && progressPct < 100 && (
                  <ProgressBar aria-label="Operation progress" size="sm" color="accent" value={progressPct} className="w-24">
                    <ProgressBar.Track>
                      <ProgressBar.Fill />
                    </ProgressBar.Track>
                  </ProgressBar>
                )}
                <span className="font-mono text-[10px] text-muted">{logs.length}</span>
                {consoleOpen ? <ChevronDown className="size-3.5 text-muted" /> : <ChevronUp className="size-3.5 text-muted" />}
              </div>
            </div>

            {consoleOpen && (
              <div className="border-t border-border/80 bg-[#09090b] px-3 py-2 text-zinc-200">
                <div className="mb-1 flex items-center justify-between text-[10px] text-zinc-500">
                  <span className="font-mono">SSE · {status.apiBaseUrl}</span>
                  <div className="flex items-center gap-1">
                    <Button
                      size="sm"
                      variant="ghost"
                      onPress={() => {
                        const text = logs.map((l) => `[${l.time}] [${l.level.toUpperCase()}] ${l.message}`).join('\n');
                        navigator.clipboard.writeText(text).catch(() => {});
                        setCopiedLogs(true);
                        setTimeout(() => setCopiedLogs(false), 1200);
                      }}
                      className="h-5 px-1.5 text-[10px]"
                    >
                      {copiedLogs ? <Check className="size-3 text-success" /> : <Copy className="size-3" />}
                      {t.copyLogs}
                    </Button>
                    <Button size="sm" variant="ghost" onPress={() => setLogs([])} className="h-5 px-1.5 text-[10px]">
                      <Trash2 className="size-3" />
                      {t.clearLogs}
                    </Button>
                  </div>
                </div>
                <div className="h-36 overflow-y-auto font-mono text-[11px] leading-5">
                  {logs.length === 0 ? (
                    <div className="text-zinc-500">[{t.ready}] {status.syncSummary}</div>
                  ) : (
                    logs.map((l) => (
                      <div
                        key={l.id}
                        className={
                          l.level === 'error'
                            ? 'text-rose-400'
                            : l.level === 'warn'
                              ? 'text-amber-300'
                              : l.level === 'ok'
                                ? 'text-emerald-400'
                                : 'text-zinc-300'
                        }
                      >
                        <span className="text-zinc-600">[{l.time}]</span> {l.message}
                      </div>
                    ))
                  )}
                  <div ref={logEndRef} />
                </div>
              </div>
            )}
          </footer>
        </div>
      </div>

      {/* HEROUI v3 CONTROLLED MODAL: NEW MOD */}
      <Modal.Backdrop isOpen={showNewMod} onOpenChange={setShowNewMod}>
        <Modal.Container placement="center">
          <Modal.Dialog className="sm:max-w-[440px]">
            <Modal.CloseTrigger />
            <Modal.Header>
              <Modal.Icon className="bg-accent-soft text-accent-soft-foreground">
                <Sparkles className="size-5" />
              </Modal.Icon>
              <Modal.Heading>{t.newModModalTitle}</Modal.Heading>
            </Modal.Header>
            <Modal.Body className="space-y-3">
              <Switch
                size="sm"
                isSelected={newModForm.completeExample}
                onChange={(checked) =>
                  setNewModForm((f) => ({
                    ...f,
                    completeExample: checked,
                    id: checked && f.id === 'my-custom-mod' ? 'complete-showcase-example' : f.id,
                  }))
                }
              >
                <Switch.Content>
                  <Switch.Control>
                    <Switch.Thumb />
                  </Switch.Control>
                  <span className="text-xs leading-snug text-foreground/90">{t.modalCompleteExampleSwitch}</span>
                </Switch.Content>
              </Switch>

              <TextField
                fullWidth
                variant="secondary"
                value={newModForm.id}
                onChange={(v) => setNewModForm({ ...newModForm, id: v })}
              >
                <Label>{t.modalModId}</Label>
                <Input className="font-mono text-xs" placeholder="my-card-pack" />
              </TextField>

              {!newModForm.completeExample && (
                <>
                  <TextField
                    fullWidth
                    variant="secondary"
                    value={newModForm.name}
                    onChange={(v) => setNewModForm({ ...newModForm, name: v })}
                  >
                    <Label>{t.modalDisplayName}</Label>
                    <Input className="text-xs" />
                  </TextField>

                  <div className="grid grid-cols-2 gap-2.5">
                    <div>
                      <div className="mb-1 text-xs font-medium text-foreground/90">{t.modalCategory}</div>
                      <Select
                        aria-label={t.modalCategory}
                        fullWidth
                        variant="secondary"
                        value={newModForm.category}
                        onChange={(k) => k && setNewModForm({ ...newModForm, category: String(k) })}
                      >
                        <Select.Trigger className="h-9 text-xs">
                          <Select.Value />
                          <Select.Indicator />
                        </Select.Trigger>
                        <Select.Popover>
                          <ListBox>
                            <ListBox.Item id="auto" textValue={t.modCatAuto}>
                              {t.modCatAuto}
                              <ListBox.ItemIndicator />
                            </ListBox.Item>
                            {MOD_CATEGORY_LIST.filter((c) => c !== 'all').map((cat) => (
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
                      value={newModForm.minGameVersion}
                      onChange={(v) => setNewModForm({ ...newModForm, minGameVersion: v })}
                    >
                      <Label>{t.modalMinGameVer}</Label>
                      <Input className="font-mono text-xs" placeholder="0.2.46" />
                    </TextField>
                  </div>

                  <div className="grid grid-cols-2 gap-2.5">
                    <TextField
                      fullWidth
                      variant="secondary"
                      value={newModForm.author}
                      onChange={(v) => setNewModForm({ ...newModForm, author: v })}
                    >
                      <Label>{t.modalAuthor}</Label>
                      <Input className="text-xs" />
                    </TextField>
                    <div className="flex items-end gap-1">
                      <TextField
                        fullWidth
                        variant="secondary"
                        value={newModForm.coverSourcePath}
                        onChange={(v) => setNewModForm({ ...newModForm, coverSourcePath: v })}
                      >
                        <Label>{t.modalCoverOptional}</Label>
                        <Input className="font-mono text-xs" placeholder="cover.png" />
                      </TextField>
                      <Button
                        size="sm"
                        variant="secondary"
                        onPress={async () => {
                          const pick = await apiPost<{ path: string | null }>('/api/dialog/pick-file', {
                            title: t.changeCover,
                            kind: 'image',
                          });
                          if (pick.path) setNewModForm((f) => ({ ...f, coverSourcePath: pick.path! }));
                        }}
                        className="h-9 shrink-0"
                      >
                        <FolderOpen className="size-3.5" />
                      </Button>
                    </div>
                  </div>

                  <TextField
                    fullWidth
                    variant="secondary"
                    value={newModForm.description}
                    onChange={(v) => setNewModForm({ ...newModForm, description: v })}
                  >
                    <Label>{t.modalDescription}</Label>
                    <Input className="text-xs" />
                  </TextField>
                </>
              )}
            </Modal.Body>
            <Modal.Footer>
              <Button variant="secondary" slot="close">
                {t.cancel}
              </Button>
              <Button variant="primary" onPress={handleCreateMod}>
                <Plus className="size-3.5" />
                {t.confirm}
              </Button>
            </Modal.Footer>
          </Modal.Dialog>
        </Modal.Container>
      </Modal.Backdrop>

      {/* HEROUI v3 CONTROLLED MODAL: NEW WORKSPACE */}
      <Modal.Backdrop isOpen={showNewWs} onOpenChange={setShowNewWs}>
        <Modal.Container placement="center">
          <Modal.Dialog className="sm:max-w-[460px]">
            <Modal.CloseTrigger />
            <Modal.Header>
              <Modal.Icon className="bg-accent-soft text-accent-soft-foreground">
                <FolderPlus className="size-5" />
              </Modal.Icon>
              <Modal.Heading>{t.newWsModalTitle}</Modal.Heading>
            </Modal.Header>
            <Modal.Body className="space-y-3">
              <TextField
                fullWidth
                variant="secondary"
                value={newWsForm.name}
                onChange={(v) => setNewWsForm({ ...newWsForm, name: v })}
              >
                <Label>{t.modalWsName}</Label>
                <Input className="text-xs" placeholder={t.modalWsNamePlaceholder} />
              </TextField>
              <div className="flex items-end gap-1.5">
                <TextField
                  fullWidth
                  variant="secondary"
                  value={newWsForm.path}
                  onChange={(v) => setNewWsForm({ ...newWsForm, path: v })}
                >
                  <Label>{t.modalWsPath}</Label>
                  <Input className="font-mono text-xs" placeholder="C:\Mods\MyWorkspace" />
                </TextField>
                <Button
                  size="sm"
                  variant="secondary"
                  onPress={async () => {
                    const pick = await apiPost<{ path: string | null }>('/api/dialog/pick-folder', {
                      title: t.selectWorkspaceFolderTitle,
                    });
                    if (pick.path) {
                      setNewWsForm((f) => ({
                        ...f,
                        path: pick.path!,
                        name: f.name || pick.path!.split(/[\\/]/).filter(Boolean).pop() || t.workspace,
                      }));
                    }
                  }}
                >
                  <FolderOpen className="size-3.5" />
                  {t.browseFolder}
                </Button>
              </div>
              <TextField
                fullWidth
                variant="secondary"
                value={newWsForm.description}
                onChange={(v) => setNewWsForm({ ...newWsForm, description: v })}
              >
                <Label>{t.modalOptionalDesc}</Label>
                <Input className="text-xs" />
              </TextField>
            </Modal.Body>
            <Modal.Footer>
              <Button variant="secondary" slot="close">
                {t.cancel}
              </Button>
              <Button variant="primary" onPress={handleCreateWorkspace}>
                <Plus className="size-3.5" />
                {t.confirm}
              </Button>
            </Modal.Footer>
          </Modal.Dialog>
        </Modal.Container>
      </Modal.Backdrop>
      {/* COMPLETE MOD AUTHORING GUIDE MODAL */}
      <ModGuideModal
        isOpen={showModGuide}
        onOpenChange={setShowModGuide}
        t={t}
        lang={lang}
        onCreateExample={() => {
          handleCreateExampleMod().catch((e) => notify((e as Error).message, 'err'));
        }}
        onCopyAiPrompt={handleCopyAiSuperPrompt}
      />
    </div>
  );
}

function SidebarNavItem({
  active,
  onClick,
  icon,
  label,
  count,
  liveDot = false,
}: {
  active: boolean;
  onClick: () => void;
  icon: React.ReactNode;
  label: string;
  count?: string;
  liveDot?: boolean;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`flex w-full items-center justify-between gap-2 rounded-lg px-2.5 py-1.5 text-left text-xs font-medium transition-colors ${
        active
          ? 'bg-surface-secondary text-foreground'
          : 'text-muted hover:bg-surface-secondary/60 hover:text-foreground'
      }`}
    >
      <span className="flex min-w-0 items-center gap-2">
        <span className={active ? 'text-accent' : 'text-muted'}>{icon}</span>
        <span className="truncate">{label}</span>
      </span>
      {count && <span className="shrink-0 font-mono text-[10px] text-muted">{count}</span>}
      {liveDot && <span className="size-2 shrink-0 rounded-full bg-success animate-pulse" />}
    </button>
  );
}

export default App;
