import { useState } from 'react';
import { Button, Chip } from '@heroui/react';
import { Download, ExternalLink, Flame, Play, RefreshCw, Syringe } from 'lucide-react';
import { apiPost } from '../api';
import type { I18nDict } from '../i18n';
import type { HotReloadStatus } from '../types';

interface LivePreviewTabProps {
  t: I18nDict;
  hotReload: HotReloadStatus;
  hasGameExe?: boolean;
  hasWebBundle?: boolean;
  busy?: boolean;
  onStartHotReload: () => Promise<void>;
  onInjectHotReload: () => Promise<void>;
  onDownloadWebBundleAndPlay?: () => Promise<void>;
}

export function LivePreviewTab({
  t,
  hotReload,
  hasGameExe,
  hasWebBundle,
  busy,
  onStartHotReload,
  onInjectHotReload,
  onDownloadWebBundleAndPlay,
}: LivePreviewTabProps) {
  const [frameKey, setFrameKey] = useState(0);

  return (
    <div className="flex h-full min-h-0 flex-1 flex-col overflow-hidden">
      <div className="flex min-h-11 shrink-0 flex-wrap items-center justify-between gap-2 border-b border-border bg-background px-3.5 py-1.5">
        <div className="flex flex-wrap items-center gap-2 text-xs">
          <Flame className={`size-4 ${hotReload.running ? 'text-warning animate-pulse' : 'text-muted'}`} />
          <span className="font-semibold text-foreground">{t.previewTitle}</span>
          {!hasGameExe && hasWebBundle && (
            <Chip size="sm" color="accent" variant="soft" className="h-5">
              <Chip.Label className="text-[10px]">{t.standaloneWebBadge}</Chip.Label>
            </Chip>
          )}
          {hotReload.running && hotReload.url && (
            <Chip size="sm" color="success" variant="soft" className="h-5">
              <Chip.Label className="font-mono text-[10px]">
                {hotReload.url} · rev #{hotReload.revision}
              </Chip.Label>
            </Chip>
          )}
        </div>
        <div className="flex items-center gap-1.5">
          {hotReload.running && hotReload.url && (
            <>
              <Button size="sm" variant="secondary" onPress={() => setFrameKey((k) => k + 1)} className="h-7 text-xs">
                <RefreshCw className="size-3.5" />
                {t.reloadFrame}
              </Button>
              <Button
                size="sm"
                variant="secondary"
                onPress={() => apiPost('/api/shell/open-url', { url: hotReload.url })}
                className="h-7 text-xs"
              >
                <ExternalLink className="size-3.5" />
                {t.popOutBrowser}
              </Button>
            </>
          )}
          {hasGameExe && (
            <Button size="sm" className="button--warning-soft h-7 text-xs" onPress={onInjectHotReload}>
              <Syringe className="size-3.5" />
              {t.hotReloadInject}
            </Button>
          )}
        </div>
      </div>

      <div className="min-h-0 flex-1 bg-black">
        {hotReload.running && hotReload.url ? (
          <iframe
            key={`${hotReload.url}-${frameKey}`}
            src={hotReload.url}
            title="Yu-Gi-Oh! Rogue Duelist Hot-Reload Preview"
            className="h-full w-full border-0"
            allow="autoplay; fullscreen"
          />
        ) : (
          <div className="flex h-full flex-col items-center justify-center bg-background p-8 text-center space-y-4">
            <div className="flex size-16 items-center justify-center rounded-2xl border border-warning/30 bg-warning-soft">
              <Flame className="size-8 text-warning" />
            </div>
            <div className="space-y-1.5">
              <h3 className="text-base font-bold text-foreground">{t.previewOffline}</h3>
              <p className="max-w-md text-xs leading-relaxed text-muted">{t.previewOfflineHint}</p>
            </div>
            <div className="flex flex-wrap items-center justify-center gap-2">
              {hasWebBundle ? (
                <Button size="md" variant="primary" isDisabled={busy} onPress={onStartHotReload}>
                  <Play className="size-4" />
                  {t.startAndPreview}
                </Button>
              ) : (
                onDownloadWebBundleAndPlay && (
                  <Button size="md" variant="primary" isDisabled={busy} onPress={onDownloadWebBundleAndPlay}>
                    <Download className="size-4" />
                    {t.standaloneWebPlayBtn}
                  </Button>
                )
              )}
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
