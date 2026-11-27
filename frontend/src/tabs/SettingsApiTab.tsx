import { useCallback, useEffect, useState } from 'react';
import { Button, Card, Chip, Input, Label, ListBox, ProgressBar, Select, Surface, Switch, TextField } from '@heroui/react';
import {
  Activity,
  Bot,
  Check,
  ChevronDown,
  ChevronUp,
  Copy,
  Cpu,
  Download,
  ExternalLink,
  FileText,
  FolderOpen,
  Globe,
  MonitorSmartphone,
  Package,
  Pause,
  Play,
  RefreshCw,
  Save,
  Server,
  ShieldCheck,
  Zap,
} from 'lucide-react';
import { buildAiPrompt } from '../aiPrompts';
import { apiGet, apiPost } from '../api';
import { fmt, type I18nDict } from '../i18n';
import type {
  ApiEndpointDoc,
  Lang,
  ManagerStatus,
  UpdateCheckPayload,
  VanillaDllInfo,
} from '../types';

interface SettingsApiTabProps {
  status: ManagerStatus;
  t: I18nDict;
  lang: Lang;
  initialUpdateInfo?: UpdateCheckPayload | null;
  progressText?: string;
  progressPct?: number | null;
  isDownloadingLive?: boolean;
  onRefresh: () => Promise<void>;
  onOpenConsole?: () => void;
  onNotify: (msg: string, kind?: 'ok' | 'err' | 'info') => void;
}

const DEFAULT_PRESETS = [
  { id: 'official', nameZh: '官方 GitHub 直连（默认）', nameEn: 'Official GitHub Direct (Default)', prefix: '' },
  { id: 'ghfast', nameZh: 'ghfast 代理加速 (ghfast.top)', nameEn: 'ghfast Mirror (ghfast.top)', prefix: 'https://ghfast.top/' },
  { id: 'gh-proxy', nameZh: 'gh-proxy 代理加速 (gh-proxy.com)', nameEn: 'gh-proxy Mirror (gh-proxy.com)', prefix: 'https://gh-proxy.com/' },
  { id: 'ghproxy-net', nameZh: 'ghproxy.net 代理加速', nameEn: 'ghproxy.net Mirror', prefix: 'https://ghproxy.net/' },
];

function fmtBytes(bytes?: number | null): string {
  if (!bytes || bytes <= 0) return '0 B';
  if (bytes >= 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${bytes} B`;
}

export function SettingsApiTab({
  status,
  t,
  lang,
  initialUpdateInfo,
  progressText,
  progressPct,
  isDownloadingLive,
  onRefresh,
  onOpenConsole,
  onNotify,
}: SettingsApiTabProps) {
  const [installDir, setInstallDir] = useState(status.installDir);
  const [gameBaseDir, setGameBaseDir] = useState(status.gameBaseDir);
  const [picsDir, setPicsDir] = useState(status.picsDir);
  const [repo, setRepo] = useState(status.repo);
  const [githubProxy, setGithubProxy] = useState(status.githubProxy || '');
  const [managerRepo, setManagerRepo] = useState(status.managerRepo || 'ZerxZ/YGORogueDuelistCardModInstaller');
  const [endpoints, setEndpoints] = useState<ApiEndpointDoc[]>([]);
  const [copiedPath, setCopiedPath] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Update center & proxy states
  const [checkingUpdates, setCheckingUpdates] = useState(false);
  const [testingProxy, setTestingProxy] = useState(false);
  const [proxyTestMsg, setProxyTestMsg] = useState<{ ok: boolean; text: string } | null>(null);
  const [updateInfo, setUpdateInfo] = useState<UpdateCheckPayload | null>(initialUpdateInfo ?? null);
  const [downloading, setDownloading] = useState(false);
  const [showGameNotes, setShowGameNotes] = useState(false);
  const [showMgrNotes, setShowMgrNotes] = useState(false);
  const [exportingPlatform, setExportingPlatform] = useState(false);

  useEffect(() => {
    if (initialUpdateInfo && !updateInfo) {
      setUpdateInfo(initialUpdateInfo);
    }
  }, [initialUpdateInfo, updateInfo]);

  // Vanilla DLL inspector state
  const [dllInfo, setDllInfo] = useState<VanillaDllInfo | null>(null);
  const [showBootScript, setShowBootScript] = useState(false);

  useEffect(() => {
    setInstallDir(status.installDir);
    setGameBaseDir(status.gameBaseDir);
    setPicsDir(status.picsDir);
    setRepo(status.repo);
    setGithubProxy(status.githubProxy || '');
    setManagerRepo(status.managerRepo || 'ZerxZ/YGORogueDuelistCardModInstaller');
  }, [status.installDir, status.gameBaseDir, status.picsDir, status.repo, status.githubProxy, status.managerRepo]);

  useEffect(() => {
    apiGet<{ endpoints: ApiEndpointDoc[] }>(`/api/docs?lang=${encodeURIComponent(lang)}`)
      .then((res) => setEndpoints(res.endpoints || []))
      .catch(() => {});
  }, [lang]);

  const loadDllInfo = useCallback(async () => {
    try {
      const res = await apiGet<VanillaDllInfo>('/api/vanilla/dll-info');
      setDllInfo(res);
    } catch {
      // ignore
    }
  }, []);

  useEffect(() => {
    loadDllInfo();
  }, [loadDllInfo, status.installDir]);

  const pickFolder = async (title: string, initialPath: string, setter: (v: string) => void) => {
    try {
      const res = await apiPost<{ path: string | null }>('/api/dialog/pick-folder', { title, initialPath });
      if (res.path) setter(res.path);
    } catch (e) {
      onNotify((e as Error).message, 'err');
    }
  };

  const handleSave = async () => {
    setBusy(true);
    try {
      await apiPost('/api/settings', {
        installDir,
        gameDir: gameBaseDir,
        picsDir,
        repo,
        githubProxy,
        managerRepo,
      });
      await onRefresh();
      await loadDllInfo();
      onNotify(t.toastSettingsSaved, 'ok');
    } catch (e) {
      onNotify((e as Error).message, 'err');
    } finally {
      setBusy(false);
    }
  };

  const handleProxyPresetSelect = async (presetId: string) => {
    const presets = updateInfo?.proxyPresets ?? DEFAULT_PRESETS;
    const found = presets.find((p) => p.id === presetId);
    if (!found) return;
    setGithubProxy(found.prefix);
    setProxyTestMsg(null);
    try {
      await apiPost('/api/settings', { githubProxy: found.prefix });
      await onRefresh();
    } catch {
      // ignore
    }
  };

  const handleTestProxy = async () => {
    setTestingProxy(true);
    setProxyTestMsg(null);
    try {
      const res = await apiPost<{ ok: boolean; statusCode: number; latencyMs: number; error?: string | null }>(
        '/api/updates/test-proxy',
        { proxy: githubProxy, repo },
      );
      if (res.ok) {
        setProxyTestMsg({ ok: true, text: fmt(t.proxyTestOk, { code: res.statusCode, ms: res.latencyMs }) });
      } else {
        setProxyTestMsg({ ok: false, text: fmt(t.proxyTestFail, { err: res.error || `HTTP ${res.statusCode}` }) });
      }
    } catch (e) {
      setProxyTestMsg({ ok: false, text: fmt(t.proxyTestFail, { err: (e as Error).message }) });
    } finally {
      setTestingProxy(false);
    }
  };

  const handleCheckUpdates = async () => {
    setCheckingUpdates(true);
    try {
      await apiPost('/api/settings', { githubProxy, repo, managerRepo }).catch(() => {});
      const res = await apiGet<UpdateCheckPayload>(`/api/updates/check?proxy=${encodeURIComponent(githubProxy)}`);
      setUpdateInfo(res);
      onNotify(t.toastGameUpdated, 'ok');
    } catch (e) {
      onNotify((e as Error).message, 'err');
    } finally {
      setCheckingUpdates(false);
    }
  };

  const handleDownloadGame = async (mode: 'web-bundle' | 'installer') => {
    setDownloading(true);
    onOpenConsole?.();
    try {
      await apiPost('/api/settings', { githubProxy, repo }).catch(() => {});
      await apiPost('/api/updates/download-game', { mode, force: true });
      await onRefresh();
      await loadDllInfo();
      onNotify(mode === 'web-bundle' ? t.toastWebBundleDone : t.toastGameUpdated, 'ok');
    } catch (e) {
      onNotify((e as Error).message, 'err');
    } finally {
      setDownloading(false);
      apiGet<UpdateCheckPayload>(`/api/updates/check?proxy=${encodeURIComponent(githubProxy)}`)
        .then(setUpdateInfo)
        .catch(() => {});
    }
  };

  const handleDownloadManager = async () => {
    setDownloading(true);
    onOpenConsole?.();
    try {
      await apiPost('/api/settings', { githubProxy, managerRepo }).catch(() => {});
      const res = await apiPost<{ savedPath: string }>('/api/updates/download-manager', {});
      onNotify(fmt(t.toastManagerDownloaded, { path: res.savedPath }), 'ok');
    } catch (e) {
      onNotify((e as Error).message, 'err');
    } finally {
      setDownloading(false);
    }
  };

  const handlePauseDownload = async () => {
    try {
      await apiPost('/api/updates/cancel', {});
      onNotify(t.downloadPausedToast, 'info');
    } catch (e) {
      onNotify((e as Error).message, 'err');
    }
  };

  const handleBuildOnly = async () => {
    try {
      const picked = await apiPost<{ path: string | null }>('/api/dialog/pick-folder', {
        title: t.exportBuildOnly,
        initialPath: status.workspaceRoot,
      });
      if (!picked.path) return;
      setBusy(true);
      await apiPost('/api/apply', { buildOnlyDir: picked.path });
      onNotify(fmt(t.toastBuildOnlyDone, { path: picked.path }), 'ok');
    } catch (e) {
      onNotify((e as Error).message, 'err');
    } finally {
      setBusy(false);
    }
  };

  const handleExportLinux = async () => {
    setExportingPlatform(true);
    onOpenConsole?.();
    try {
      const res = await apiPost<{ outputPath: string; outputDir: string }>('/api/export/linux-bundle', {});
      onNotify(`Linux MOD 包已导出：${res.outputPath}`, 'ok');
      await apiPost('/api/shell/open-folder', { path: res.outputDir }).catch(() => {});
    } catch (e) {
      onNotify((e as Error).message, 'err');
    } finally {
      setExportingPlatform(false);
    }
  };

  const handleExportAndroid = async (pickApk: boolean) => {
    let sourceApkPath = '';
    if (pickApk) {
      const pick = await apiPost<{ path: string | null }>('/api/dialog/pick-file', {
        title: lang === 'zh' ? '选择官方原版 YGO-Rogue-Android.apk' : 'Select official YGO-Rogue-Android.apk',
        kind: 'all',
      });
      if (!pick.path) return;
      sourceApkPath = pick.path;
    }
    setExportingPlatform(true);
    onOpenConsole?.();
    try {
      const res = await apiPost<{ outputPath: string; outputDir: string }>('/api/export/android-bundle', {
        sourceApkPath: sourceApkPath || undefined,
      });
      onNotify(`Android MOD 包已导出：${res.outputPath}`, 'ok');
      await apiPost('/api/shell/open-folder', { path: res.outputDir }).catch(() => {});
    } catch (e) {
      onNotify((e as Error).message, 'err');
    } finally {
      setExportingPlatform(false);
    }
  };

  const handleLaunch = async () => {
    try {
      await apiPost('/api/launch');
      onNotify(t.toastGameLaunched, 'ok');
    } catch (e) {
      onNotify((e as Error).message, 'err');
    }
  };

  const copyEndpoint = (path: string) => {
    const full = `${status.apiBaseUrl.replace(/\/$/, '')}${path}`;
    navigator.clipboard.writeText(full).catch(() => {});
    setCopiedPath(path);
    setTimeout(() => setCopiedPath(null), 1400);
  };

  const presets = updateInfo?.proxyPresets ?? DEFAULT_PRESETS;
  const activePresetId = presets.find((p) => p.prefix === githubProxy)?.id ?? 'custom';

  return (
    <div className="grid h-full min-h-0 flex-1 grid-cols-1 gap-3 overflow-y-auto bg-background p-4 lg:grid-cols-12">
      {/* LEFT COLUMN: Built-in Trainer & Online Guard + Update Center + Proxy + Vanilla DLL Inspector + Paths */}
      <div className="space-y-3 lg:col-span-7">
        {/* 0. Built-in Trainer, Online Multiplayer Guard & Developer Mode Switches */}
        <Card variant="default" className="p-4 gap-3">
          <Card.Header className="p-0">
            <Card.Title className="flex items-center gap-2 text-sm">
              <ShieldCheck className="size-4 text-success" />
              {t.gameplayGuardTitle}
            </Card.Title>
          </Card.Header>
          <Card.Content className="p-0 space-y-2.5">
            <Switch
              size="sm"
              isSelected={status.hideOnlineWhenModded !== false}
              onChange={async (checked) => {
                await apiPost('/api/settings', { hideOnlineWhenModded: checked });
                await onRefresh();
              }}
            >
              <Switch.Content>
                <Switch.Control><Switch.Thumb /></Switch.Control>
                <div className="min-w-0">
                  <span className="block text-xs font-medium text-foreground">{t.hideOnlineSwitchLabel}</span>
                  <span className="block text-[11px] text-muted">{t.hideOnlineSwitchHint}</span>
                </div>
              </Switch.Content>
            </Switch>

            <Switch
              size="sm"
              isSelected={status.enableBuiltInTrainer !== false}
              onChange={async (checked) => {
                await apiPost('/api/settings', { enableBuiltInTrainer: checked });
                await onRefresh();
              }}
            >
              <Switch.Content>
                <Switch.Control><Switch.Thumb /></Switch.Control>
                <div className="min-w-0">
                  <span className="block text-xs font-medium text-foreground">{t.trainerSwitchLabel}</span>
                  <span className="block text-[11px] text-muted">{t.trainerSwitchHint}</span>
                </div>
              </Switch.Content>
            </Switch>

            <Switch
              size="sm"
              isSelected={status.developerMode}
              onChange={async (checked) => {
                await apiPost('/api/settings', { developerMode: checked });
                await onRefresh();
              }}
            >
              <Switch.Content>
                <Switch.Control><Switch.Thumb /></Switch.Control>
                <div className="min-w-0">
                  <span className="block text-xs font-medium text-foreground">{t.devMode}</span>
                  <span className="block text-[11px] text-muted">{t.devModeSwitchHint}</span>
                </div>
              </Switch.Content>
            </Switch>
          </Card.Content>
        </Card>

        {/* 1. Built-in Update Center & GitHub Mirror Proxy (with Resumable Downloads!) */}
        <Card variant="default" className="p-4 gap-3">
          <Card.Header className="p-0 flex flex-row items-start justify-between gap-2">
            <div>
              <Card.Title className="flex items-center gap-2 text-sm">
                <Globe className="size-4 text-accent" />
                {t.updateCenterTitle}
              </Card.Title>
              <p className="mt-0.5 text-[11px] text-muted">{t.updateCenterHint}</p>
            </div>
            <div className="flex shrink-0 items-center gap-1.5">
              {(downloading || status.isDownloading) && (
                <Button size="sm" variant="danger-soft" onPress={handlePauseDownload} className="h-7 text-xs">
                  <Pause className="size-3" />
                  {t.pauseDownload}
                </Button>
              )}
              <Button
                size="sm"
                variant="primary"
                isDisabled={checkingUpdates || downloading}
                onPress={handleCheckUpdates}
                className="h-7 text-xs"
              >
                <RefreshCw className={`size-3 ${checkingUpdates ? 'animate-spin' : ''}`} />
                {checkingUpdates ? t.checkingUpdates : t.checkUpdatesNow}
              </Button>
            </div>
          </Card.Header>

          <Card.Content className="p-0 space-y-3">
            {/* Prominent Live Download / Install Progress Bar */}
            {(downloading || status.isDownloading || isDownloadingLive) && (
              <div className="rounded-xl border border-accent/45 bg-accent/10 p-3 space-y-2">
                <div className="flex items-center justify-between gap-2 text-xs">
                  <div className="flex min-w-0 items-center gap-2 font-semibold text-foreground">
                    <Download className="size-3.5 shrink-0 animate-bounce text-accent" />
                    <span className="truncate">
                      {status.activeDownloadLabel || status.downloadProgress?.label || t.downloadProgressTitle}
                    </span>
                  </div>
                  <div className="flex shrink-0 items-center gap-2">
                    <Chip size="sm" color="accent" variant="primary" className="h-5">
                      <Chip.Label className="font-mono text-[10px]">
                        {typeof progressPct === 'number' && progressPct >= 0
                          ? `${Math.round(progressPct)}%`
                          : typeof status.downloadProgress?.percent === 'number'
                            ? `${Math.round(status.downloadProgress.percent)}%`
                            : '...'}
                      </Chip.Label>
                    </Chip>
                    <Button size="sm" variant="danger-soft" onPress={handlePauseDownload} className="h-6 px-2 text-[11px]">
                      <Pause className="size-3" />
                      {t.pauseDownload}
                    </Button>
                  </div>
                </div>

                {typeof (progressPct ?? status.downloadProgress?.percent) === 'number' ? (
                  <ProgressBar
                    aria-label={t.downloadProgressTitle}
                    size="md"
                    color="accent"
                    value={Math.max(0, Math.min(100, Number(progressPct ?? status.downloadProgress?.percent ?? 0)))}
                  >
                    <ProgressBar.Track className="h-2.5">
                      <ProgressBar.Fill />
                    </ProgressBar.Track>
                  </ProgressBar>
                ) : (
                  <div className="h-2.5 w-full overflow-hidden rounded-full bg-surface-secondary">
                    <div className="h-full w-1/3 animate-pulse rounded-full bg-accent" />
                  </div>
                )}

                <p className="truncate font-mono text-[11px] text-foreground/85">
                  {progressText || status.downloadProgress?.status || t.downloadConnecting}
                </p>
              </div>
            )}

            {/* GitHub Proxy Preset + Custom Prefix + Test Latency */}
            <div className="rounded-xl border border-border/80 bg-background p-3 space-y-2.5">
              <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
                <Select
                  aria-label={t.proxyPresetLabel}
                  fullWidth
                  variant="secondary"
                  value={activePresetId}
                  onChange={(k) => k && handleProxyPresetSelect(String(k))}
                >
                  <Label className="text-[11px] text-muted">{t.proxyPresetLabel}</Label>
                  <Select.Trigger className="h-8 text-xs">
                    <Select.Value />
                    <Select.Indicator />
                  </Select.Trigger>
                  <Select.Popover>
                    <ListBox>
                      {presets.map((p) => (
                        <ListBox.Item key={p.id} id={p.id} textValue={lang === 'zh' ? p.nameZh : p.nameEn}>
                          {lang === 'zh' ? p.nameZh : p.nameEn}
                          <ListBox.ItemIndicator />
                        </ListBox.Item>
                      ))}
                      {activePresetId === 'custom' && (
                        <ListBox.Item id="custom" textValue={githubProxy}>
                          {githubProxy}
                          <ListBox.ItemIndicator />
                        </ListBox.Item>
                      )}
                    </ListBox>
                  </Select.Popover>
                </Select>

                <div className="flex items-end gap-1.5">
                  <TextField fullWidth variant="secondary" value={githubProxy} onChange={setGithubProxy}>
                    <Label className="text-[11px] text-muted">{t.proxyCustomLabel}</Label>
                    <Input className="h-8 font-mono text-xs" placeholder={t.proxyCustomPh} />
                  </TextField>
                  <Button
                    size="sm"
                    variant="secondary"
                    isDisabled={testingProxy}
                    onPress={handleTestProxy}
                    className="h-8 shrink-0 text-xs"
                  >
                    <Activity className="size-3 text-accent" />
                    {t.proxyTestBtn}
                  </Button>
                </div>
              </div>

              <div className="flex flex-wrap items-center justify-between gap-2 text-[11px]">
                <span className="text-muted">{t.proxyChinaNote}</span>
                {proxyTestMsg && (
                  <Chip size="sm" color={proxyTestMsg.ok ? 'success' : 'danger'} variant="soft" className="h-5">
                    <Chip.Label className="font-mono text-[10px]">{proxyTestMsg.text}</Chip.Label>
                  </Chip>
                )}
              </div>
            </div>

            {/* Resumable Partial Downloads (.part) indicator */}
            {updateInfo && updateInfo.partialDownloads.length > 0 && (
              <div className="rounded-lg border border-warning/35 bg-warning-soft/25 px-3 py-2 text-[11px]">
                <div className="font-semibold text-warning">{t.partialDownloadsHint}</div>
                <div className="mt-1 flex flex-wrap gap-1.5 font-mono text-[10.5px]">
                  {updateInfo.partialDownloads.map((pd) => (
                    <Chip key={pd.partPath} size="sm" color="warning" variant="soft" className="h-5">
                      <Chip.Label>
                        {pd.fileName} ({fmtBytes(pd.downloadedBytes)})
                      </Chip.Label>
                    </Chip>
                  ))}
                </div>
              </div>
            )}

            {/* Official Game Update Row */}
            <div className="rounded-xl border border-border/80 bg-background p-3 space-y-2">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <div className="text-xs font-semibold text-foreground">{t.gameUpdateCardTitle}</div>
                <div className="flex items-center gap-1.5 font-mono text-[11px]">
                  <Chip size="sm" variant="soft" className="h-5">
                    <Chip.Label>
                      {t.localVersionLabel}: {status.activeBaseGameVersion ? `v${status.activeBaseGameVersion}` : t.statusUnknownVer}
                      {status.launcherBuild > 0 ? ` · Build ${status.launcherBuild}` : ''}
                    </Chip.Label>
                  </Chip>
                  {updateInfo?.game.latestTag && (
                    <Chip size="sm" color={updateInfo.game.hasUpdate ? 'warning' : 'success'} variant="soft" className="h-5">
                      <Chip.Label>
                        {t.latestVersionLabel}: {updateInfo.game.latestTag} (
                        {updateInfo.game.hasUpdate ? t.statusHasUpdate : t.statusUpToDate})
                      </Chip.Label>
                    </Chip>
                  )}
                </div>
              </div>

              <div className="flex flex-wrap gap-2">
                <Button
                  size="sm"
                  variant="primary"
                  isDisabled={downloading || busy}
                  onPress={() => handleDownloadGame('web-bundle')}
                  className="h-7 text-xs"
                >
                  <Zap className="size-3.5" />
                  {updateInfo?.game.hasDeltaPatch && updateInfo.game.deltaAsset
                    ? `${t.deltaFastTag} (${fmtBytes(updateInfo.game.deltaAsset.size)})`
                    : `${t.fastWebBundleBtn}${updateInfo?.game.webBundleAsset?.size ? ` (${fmtBytes(updateInfo.game.webBundleAsset.size)})` : ''}`}
                </Button>
                {status.hasWebBundle && (
                  <Button
                    size="sm"
                    variant="secondary"
                    isDisabled={downloading || busy}
                    onPress={async () => {
                      try {
                        await apiPost('/api/launch');
                        await onRefresh();
                      } catch (e) {
                        onNotify((e as Error).message, 'err');
                      }
                    }}
                    className="h-7 text-xs"
                  >
                    <Play className="size-3.5 text-success" />
                    {t.standaloneLaunchWebBtn}
                  </Button>
                )}
                <Button
                  size="sm"
                  variant="secondary"
                  isDisabled={downloading || busy}
                  onPress={() => handleDownloadGame('installer')}
                  className="h-7 text-xs"
                >
                  <Download className="size-3.5 text-warning" />
                  {t.fullInstallerBtn}
                  {updateInfo?.game.installerAsset?.size ? ` (${fmtBytes(updateInfo.game.installerAsset.size)})` : ''}
                </Button>
                {updateInfo?.game.body && (
                  <Button
                    size="sm"
                    variant="secondary"
                    onPress={() => setShowGameNotes((v) => !v)}
                    className="h-7 text-xs"
                  >
                    <FileText className="size-3.5 text-accent" />
                    {t.viewChangelogBtn}
                  </Button>
                )}
                <Button
                  size="sm"
                  variant="ghost"
                  onPress={() => apiPost('/api/shell/open-folder', { path: `${status.workspaceRoot}\\downloads` })}
                  className="h-7 text-xs"
                >
                  <FolderOpen className="size-3.5" />
                  {t.openDownloadsDir}
                </Button>
              </div>
              {showGameNotes && updateInfo?.game.body && (
                <pre className="max-h-48 overflow-auto whitespace-pre-wrap rounded-lg border border-border/75 bg-surface/60 p-2.5 font-sans text-[11px] leading-relaxed text-foreground/90">
                  {updateInfo.game.body}
                </pre>
              )}
            </div>

            {/* MOD Manager Self-Update Row */}
            <div className="rounded-xl border border-border/80 bg-background p-3 space-y-2">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <div className="text-xs font-semibold text-foreground">{t.managerUpdateCardTitle}</div>
                <div className="flex items-center gap-1.5 font-mono text-[11px]">
                  <Chip size="sm" variant="soft" className="h-5">
                    <Chip.Label>
                      {t.localVersionLabel}: v{status.managerVersion}
                    </Chip.Label>
                  </Chip>
                  {updateInfo?.manager.latestTag && (
                    <Chip size="sm" color={updateInfo.manager.hasUpdate ? 'warning' : 'success'} variant="soft" className="h-5">
                      <Chip.Label>
                        {t.latestVersionLabel}: {updateInfo.manager.latestTag} (
                        {updateInfo.manager.hasUpdate ? t.statusHasUpdate : t.statusUpToDate})
                      </Chip.Label>
                    </Chip>
                  )}
                </div>
              </div>
              <div className="flex flex-wrap gap-2">
                <Button
                  size="sm"
                  variant="secondary"
                  isDisabled={downloading || busy}
                  onPress={handleDownloadManager}
                  className="h-7 text-xs"
                >
                  <Download className="size-3.5 text-accent" />
                  {t.downloadManagerBtn}
                </Button>
                {updateInfo?.manager.body && (
                  <Button
                    size="sm"
                    variant="secondary"
                    onPress={() => setShowMgrNotes((v) => !v)}
                    className="h-7 text-xs"
                  >
                    <FileText className="size-3.5 text-accent" />
                    {t.viewChangelogBtn}
                  </Button>
                )}
                {updateInfo?.manager.htmlUrl && (
                  <Button
                    size="sm"
                    variant="ghost"
                    onPress={() => apiPost('/api/shell/open-url', { url: updateInfo.manager.htmlUrl })}
                    className="h-7 text-xs"
                  >
                    <ExternalLink className="size-3.5" />
                    GitHub Releases
                  </Button>
                )}
              </div>
              {showMgrNotes && updateInfo?.manager.body && (
                <pre className="max-h-48 overflow-auto whitespace-pre-wrap rounded-lg border border-border/75 bg-surface/60 p-2.5 font-sans text-[11px] leading-relaxed text-foreground/90">
                  {updateInfo.manager.body}
                </pre>
              )}
            </div>
          </Card.Content>
        </Card>

        {/* 1.5 Cross-Platform Export: Linux / Steam Deck & Android */}
        <Card variant="default" className="p-4 gap-2.5">
          <Card.Header className="p-0">
            <Card.Title className="flex items-center gap-2 text-xs">
              <MonitorSmartphone className="size-4 text-accent" />
              {t.crossPlatformTitle}
            </Card.Title>
            <p className="mt-0.5 text-[11px] text-muted">{t.crossPlatformHint}</p>
          </Card.Header>
          <Card.Content className="p-0 space-y-2.5">
            <div className="flex flex-wrap gap-2">
              <Button
                size="sm"
                variant="secondary"
                isDisabled={exportingPlatform || busy}
                onPress={handleExportLinux}
                className="h-7 text-xs"
              >
                <Package className="size-3.5 text-success" />
                {t.exportLinuxBtn}
              </Button>
              <Button
                size="sm"
                variant="secondary"
                isDisabled={exportingPlatform || busy}
                onPress={() => handleExportAndroid(true)}
                className="h-7 text-xs"
              >
                <MonitorSmartphone className="size-3.5 text-warning" />
                {t.exportAndroidApkBtn}
              </Button>
              <Button
                size="sm"
                variant="secondary"
                isDisabled={exportingPlatform || busy}
                onPress={() => handleExportAndroid(false)}
                className="h-7 text-xs"
              >
                <Download className="size-3.5 text-accent" />
                {t.exportAndroidOverlayBtn}
              </Button>
            </div>

            <div className="rounded-lg border border-border/75 bg-background p-2.5 text-[11px] space-y-1.5">
              <div className="text-muted">{t.androidLanHint}</div>
              {status.hotReload.running && status.hotReload.lanUrls && status.hotReload.lanUrls.length > 0 ? (
                <div className="flex flex-wrap items-center gap-1.5">
                  {status.hotReload.lanUrls.map((u) => (
                    <Button
                      key={u}
                      size="sm"
                      variant="secondary"
                      onPress={() => {
                        navigator.clipboard.writeText(u).catch(() => {});
                        onNotify(`已复制局域网地址：${u}`, 'ok');
                      }}
                      className="h-6 font-mono text-[11px]"
                    >
                      <Copy className="size-3 text-success" />
                      {u}
                    </Button>
                  ))}
                </div>
              ) : (
                <div className="font-mono text-[10.5px] text-muted">
                  {status.hotReload.running
                    ? `${status.hotReload.url} (${lang === 'zh' ? '仅本机回环；以管理员运行或放行端口可启用局域网访问' : 'Loopback only; allow LAN port for phone access'})`
                    : lang === 'zh'
                      ? '（未启动热加载 — 点击左下角「启动热加载」即可获取局域网手机试玩地址）'
                      : '(Hot-Reload stopped — click Start Hot-Reload in the bottom-left sidebar)'}
                </div>
              )}
            </div>
          </Card.Content>
        </Card>

        {/* 2. Vanilla Launcher DLL Inspector */}
        <Card variant="default" className="p-4 gap-2.5">
          <Card.Header className="p-0 flex flex-row items-center justify-between gap-2">
            <Card.Title className="flex items-center gap-2 text-xs">
              <Cpu className="size-4 text-warning" />
              {t.dllInspectorTitle}
            </Card.Title>
            <Button size="sm" variant="ghost" onPress={loadDllInfo} className="h-6 px-2 text-[11px]">
              <RefreshCw className="size-3" />
              {t.dllInspectorRefresh}
            </Button>
          </Card.Header>

          {!dllInfo || !dllInfo.found ? (
            <p className="text-[11px] text-muted">{t.dllNotFound}</p>
          ) : (
            <Card.Content className="p-0 space-y-2 text-[11px]">
              <div className="truncate rounded-md border border-border/70 bg-background px-2.5 py-1 font-mono text-[10.5px] text-foreground/90" title={dllInfo.fullPath}>
                {dllInfo.fullPath}
              </div>

              <div className="grid grid-cols-2 gap-x-3 gap-y-1.5 font-mono sm:grid-cols-3">
                <div className="rounded-lg border border-border/70 bg-background px-2.5 py-1.5">
                  <div className="text-[10px] text-muted">{t.dllFileSize}</div>
                  <div className="font-semibold text-foreground">{fmtBytes(dllInfo.fileSizeBytes)}</div>
                </div>
                <div className="rounded-lg border border-border/70 bg-background px-2.5 py-1.5">
                  <div className="text-[10px] text-muted">{t.dllLauncherBuild}</div>
                  <div className="font-semibold text-accent">
                    Build {dllInfo.launcherBuild} ({dllInfo.assemblyVersion})
                  </div>
                </div>
                <div className="rounded-lg border border-border/70 bg-background px-2.5 py-1.5">
                  <div className="text-[10px] text-muted">{t.dllInitBundle}</div>
                  <div className="font-semibold text-success">{dllInfo.initialBundleFileName}</div>
                </div>
                <div className="rounded-lg border border-border/70 bg-background px-2.5 py-1.5">
                  <div className="text-[10px] text-muted">{t.dllStartupHashes}</div>
                  <div className="font-semibold text-foreground">
                    {dllInfo.hasStartupHashesResource ? `${t.dllExists} (${fmtBytes(dllInfo.startupHashesSizeBytes)})` : t.dllMissing}
                  </div>
                </div>
                <div className="rounded-lg border border-border/70 bg-background px-2.5 py-1.5">
                  <div className="text-[10px] text-muted">{t.dllBackupStatus}</div>
                  <div className="font-semibold text-foreground">{dllInfo.hasBackupOrig ? t.dllExists : t.dllMissing}</div>
                </div>
                <div className="rounded-lg border border-border/70 bg-background px-2.5 py-1.5">
                  <div className="text-[10px] text-muted">DLL SHA-256</div>
                  <div className="truncate font-semibold text-muted" title={dllInfo.sha256}>
                    {dllInfo.sha256?.slice(0, 16)}…
                  </div>
                </div>
              </div>

              {dllInfo.updateEndpoints && dllInfo.updateEndpoints.length > 0 && (
                <div className="rounded-lg border border-border/70 bg-background p-2.5">
                  <div className="mb-1 flex items-center gap-1.5 text-[10.5px] font-semibold text-muted">
                    <ShieldCheck className="size-3 text-success" />
                    {t.dllUpdateEndpoints}
                  </div>
                  <div className="space-y-0.5 font-mono text-[10.5px] text-sky-300">
                    {dllInfo.updateEndpoints.map((ep) => (
                      <div key={ep} className="truncate">
                        {ep}
                      </div>
                    ))}
                  </div>
                </div>
              )}

              {dllInfo.desktopBootScript && (
                <div>
                  <button
                    type="button"
                    onClick={() => setShowBootScript((v) => !v)}
                    className="flex items-center gap-1 text-[11px] font-medium text-accent hover:underline"
                  >
                    {showBootScript ? <ChevronUp className="size-3.5" /> : <ChevronDown className="size-3.5" />}
                    {t.dllShowBootScript}
                  </button>
                  {showBootScript && (
                    <pre className="mt-1.5 max-h-48 overflow-auto rounded-lg border border-border/80 bg-[#080a0e] p-2.5 font-mono text-[10.5px] text-emerald-300">
                      {dllInfo.desktopBootScript}
                    </pre>
                  )}
                </div>
              )}
            </Card.Content>
          )}
        </Card>

        {/* 3. Paths & Repositories */}
        <Card variant="default" className="p-4 gap-3">
          <Card.Header className="p-0">
            <Card.Title className="text-sm">{t.tabSettings}</Card.Title>
          </Card.Header>

          <Card.Content className="p-0 space-y-2.5">
            <PathRow
              label={t.pathInstallRoot}
              value={installDir}
              onChange={setInstallDir}
              browseLabel={t.browse}
              onBrowse={() => pickFolder(t.selectGameInstallTitle, installDir, setInstallDir)}
            />
            <PathRow
              label={t.pathGameBase}
              value={gameBaseDir}
              onChange={setGameBaseDir}
              browseLabel={t.browse}
              onBrowse={() => pickFolder(t.selectGameBaseTitle, gameBaseDir, setGameBaseDir)}
            />
            <PathRow
              label={t.pathPicsDir}
              value={picsDir}
              onChange={setPicsDir}
              browseLabel={t.browse}
              onBrowse={() => pickFolder(t.selectPicsDirTitle, picsDir, setPicsDir)}
            />
            <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
              <TextField fullWidth variant="secondary" value={repo} onChange={setRepo}>
                <Label className="text-[11px] text-muted">{t.pathRepo}</Label>
                <Input className="h-8 font-mono text-xs" />
              </TextField>
              <TextField fullWidth variant="secondary" value={managerRepo} onChange={setManagerRepo}>
                <Label className="text-[11px] text-muted">{t.managerUpdateCardTitle}</Label>
                <Input className="h-8 font-mono text-xs" />
              </TextField>
            </div>
          </Card.Content>

          <Card.Footer className="p-0 pt-2 flex flex-wrap gap-2">
            <Button size="sm" variant="primary" isDisabled={busy} onPress={handleSave} className="h-8 text-xs">
              <Save className="size-3.5" />
              {t.saveSettings}
            </Button>
            <Button size="sm" variant="secondary" isDisabled={busy} onPress={handleLaunch} className="h-8 text-xs">
              <Play className="size-3.5 text-accent" />
              {t.launchGame}
            </Button>
            <Button size="sm" variant="secondary" isDisabled={busy} onPress={handleBuildOnly} className="h-8 text-xs">
              <Package className="size-3.5 text-warning" />
              {t.exportBuildOnly}
            </Button>
          </Card.Footer>
        </Card>
      </div>

      {/* RIGHT COLUMN: Environment Info + Open Local REST, SSE & MCP API Explorer */}
      <div className="flex min-h-0 flex-col gap-3 lg:col-span-5">
        <Card variant="default" className="p-4 gap-2">
          <Card.Header className="p-0">
            <Card.Title className="text-xs">{t.envInfoTitle}</Card.Title>
          </Card.Header>
          <Card.Content className="p-0 grid grid-cols-2 gap-1.5 font-mono text-[11px] text-muted">
            <span>{t.envManagerVer}:</span>
            <span className="text-accent font-semibold">v{status.managerVersion}</span>
            <span>{t.envActiveWorkspace}:</span>
            <span className="truncate text-foreground" title={status.workspaceRoot}>
              {status.workspaceRoot}
            </span>
            <span>{t.envVirtualBundles}:</span>
            <span className="text-success">
              {status.installedVirtualVersions.length > 0 ? status.installedVirtualVersions.join(', ') : t.envPureVanilla}
            </span>
            <span>Built-in MCP:</span>
            <span className="truncate text-accent" title={status.mcpUrl}>
              {status.mcpUrl}
            </span>
          </Card.Content>
        </Card>

        <Surface className="flex min-h-[320px] flex-1 flex-col rounded-xl border border-border p-4" variant="default">
          <div className="mb-3 flex items-center justify-between gap-2">
            <div className="flex items-center gap-2">
              <Server className="size-4 text-accent" />
              <div>
                <h3 className="text-sm font-bold text-foreground">{t.apiTitle}</h3>
                <p className="text-[11px] text-muted">{t.apiHint}</p>
              </div>
            </div>
            <Chip size="sm" color="success" variant="soft">
              <Chip.Label className="font-mono text-[10px]">{status.apiBaseUrl}</Chip.Label>
            </Chip>
            <Button
              size="sm"
              variant="secondary"
              onPress={() => {
                const prompt = buildAiPrompt('rest_api', {
                  lang,
                  apiBaseUrl: status.apiBaseUrl,
                  mcpUrl: status.mcpUrl,
                  mcpSseUrl: status.mcpSseUrl,
                  workspaceRoot: status.workspaceRoot,
                  gameVersion: status.activeBaseGameVersion,
                  endpoints,
                });
                navigator.clipboard.writeText(prompt).catch(() => {});
                onNotify(t.toastAiSuperPromptCopied, 'ok');
              }}
              className="h-6 px-2 text-[11px]"
            >
              <Bot className="size-3 text-accent" />
              {t.copyAiApiPromptBtn}
            </Button>
          </div>

          <div className="min-h-0 flex-1 overflow-y-auto space-y-1.5 pr-1">
            {endpoints.map((ep) => (
              <div
                key={`${ep.method}-${ep.path}`}
                className="flex items-center justify-between gap-2 rounded-lg border border-border/75 bg-background px-2.5 py-2 text-xs"
              >
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2">
                    <Chip size="sm" color={ep.method === 'GET' ? 'accent' : 'success'} variant="soft" className="h-4">
                      <Chip.Label className="font-mono text-[9px] font-bold">{ep.method}</Chip.Label>
                    </Chip>
                    <span className="truncate font-mono text-[11px] text-foreground">{ep.path}</span>
                  </div>
                  <p className="mt-0.5 text-[11px] text-muted">{ep.description}</p>
                </div>
                <Button
                  isIconOnly
                  size="sm"
                  variant="ghost"
                  aria-label={t.copyFullUrl}
                  onPress={() => copyEndpoint(ep.path)}
                  className="size-7 shrink-0"
                >
                  {copiedPath === ep.path ? <Check className="size-3.5 text-success" /> : <Copy className="size-3.5" />}
                </Button>
              </div>
            ))}
          </div>
        </Surface>
      </div>
    </div>
  );
}

function PathRow({
  label,
  value,
  onChange,
  browseLabel,
  onBrowse,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  browseLabel: string;
  onBrowse: () => void;
}) {
  return (
    <div className="flex items-end gap-2">
      <TextField fullWidth variant="secondary" value={value} onChange={onChange}>
        <Label className="text-[11px] text-muted">{label}</Label>
        <Input className="h-8 font-mono text-xs" />
      </TextField>
      <Button size="sm" variant="secondary" onPress={onBrowse} className="h-8 shrink-0 text-xs">
        <FolderOpen className="size-3.5 text-accent" />
        {browseLabel}
      </Button>
    </div>
  );
}
