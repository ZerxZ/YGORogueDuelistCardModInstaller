import { useEffect, useState } from 'react';
import { Button, Chip, Spinner } from '@heroui/react';
import {
  Copy,
  GraduationCap,
  Minus,
  PanelLeft,
  PanelRight,
  Play,
  RotateCcw,
  Sparkles,
  Square,
  Terminal,
  X,
} from 'lucide-react';
import type { I18nDict } from '../i18n';

declare global {
  interface External {
    sendMessage?: (message: string) => void;
    receiveMessage?: (callback: (message: string) => void) => void;
  }
}

export function isDesktopHost(): boolean {
  return typeof window !== 'undefined' && typeof window.external?.sendMessage === 'function';
}

export function sendDesktopCommand(cmd: string): void {
  try {
    window.external?.sendMessage?.(cmd);
  } catch {
    // Running inside standard web browser — ignore native window IPC
  }
}

interface TitleBarProps {
  t: I18nDict;
  workspaceName: string;
  sectionTitle: string;
  vanillaVersion: string | null;
  enabledCount: number;
  totalMods: number;
  busyOp: boolean;
  sidebarOpen: boolean;
  inspectorOpen: boolean;
  consoleOpen: boolean;
  onToggleSidebar: () => void;
  onToggleInspector: () => void;
  onToggleConsole: () => void;
  onOpenOnboarding: () => void;
  onApply: () => void;
  onApplyLaunch: () => void;
  onRestore: () => void;
}

export function TitleBar({
  t,
  workspaceName,
  sectionTitle,
  vanillaVersion,
  enabledCount,
  totalMods,
  busyOp,
  sidebarOpen,
  inspectorOpen,
  consoleOpen,
  onToggleSidebar,
  onToggleInspector,
  onToggleConsole,
  onOpenOnboarding,
  onApply,
  onApplyLaunch,
  onRestore,
}: TitleBarProps) {
  const [maximized, setMaximized] = useState(false);

  useEffect(() => {
    try {
      window.external?.receiveMessage?.((msg) => {
        if (msg === 'win:state:maximized') setMaximized(true);
        else if (msg === 'win:state:normal') setMaximized(false);
      });
    } catch {
      // ignore
    }
  }, []);

  const handleDragMouseDown = (e: React.MouseEvent<HTMLDivElement>) => {
    if (e.button !== 0) return;
    // Ignore clicks on buttons, inputs, or interactive elements
    const target = e.target as HTMLElement;
    if (target.closest('button, a, input, select, [role="button"]')) return;
    if (e.detail === 2) {
      sendDesktopCommand('win:maximize');
    } else {
      sendDesktopCommand('win:drag');
    }
  };

  return (
    <header
      onMouseDown={handleDragMouseDown}
      className="flex h-10 shrink-0 select-none items-center justify-between border-b border-border bg-background px-2 text-xs"
    >
      {/* Left: Sidebar Toggle + Compact Brand */}
      <div className="flex items-center gap-1.5 min-w-0">
        <button
          type="button"
          onClick={onToggleSidebar}
          aria-label={t.toggleSidebar}
          title={t.toggleSidebar}
          className={`flex size-7 items-center justify-center rounded-md transition-colors ${
            sidebarOpen
              ? 'text-foreground hover:bg-surface-secondary'
              : 'text-muted hover:bg-surface-secondary hover:text-foreground'
          }`}
        >
          <PanelLeft className="size-4" />
        </button>

        <div className="flex items-center gap-1.5 pr-1">
          <div className="flex size-5 items-center justify-center rounded-md bg-accent/15 text-accent border border-accent/30">
            <Sparkles className="size-3" />
          </div>
          <span className="font-semibold tracking-tight text-foreground whitespace-nowrap">
            {t.brandShort}
          </span>
        </div>

        <span className="hidden text-border sm:inline">/</span>

        {/* Breadcrumb (Matches T3 Code top bar style!) */}
        <div className="hidden min-w-0 items-center gap-1.5 sm:flex">
          <span className="max-w-[160px] truncate text-muted">{workspaceName}</span>
          <span className="text-border">/</span>
          <span className="max-w-[200px] truncate font-medium text-foreground">{sectionTitle}</span>
        </div>

        {vanillaVersion && (
          <Chip size="sm" color="success" variant="soft" className="ml-1 hidden h-5 lg:inline-flex">
            <Chip.Label className="font-mono text-[10px]">
              v{vanillaVersion} · {enabledCount}/{totalMods}
            </Chip.Label>
          </Chip>
        )}
      </div>

      {/* Right: Sleek Action Strip + Pane Toggles + Native Dark Window Controls */}
      <div className="flex shrink-0 items-center gap-1">
        <div className="flex items-center gap-1 pr-1">
          <Button
            size="sm"
            variant="secondary"
            isPending={busyOp}
            onPress={onApply}
            className="hidden h-7 px-2.5 text-[11px] font-medium sm:inline-flex"
          >
            {({ isPending }) => (
              <>
                {isPending && <Spinner color="current" size="sm" />}
                {t.apply}
              </>
            )}
          </Button>
          <Button
            size="sm"
            isDisabled={busyOp}
            onPress={onApplyLaunch}
            className="button--success h-7 px-2 text-[11px] font-semibold sm:px-2.5"
          >
            <Play className="size-3" />
            <span className="hidden sm:inline">{t.applyLaunch}</span>
            <span className="sm:hidden">{t.apply}</span>
          </Button>
          <Button
            size="sm"
            variant="ghost"
            isDisabled={busyOp}
            onPress={onRestore}
            className="h-7 px-1.5 text-[11px] text-muted hover:text-danger sm:px-2"
          >
            <RotateCcw className="size-3" />
            <span className="hidden md:inline">{t.restore}</span>
          </Button>
        </div>

        <div className="mx-0.5 h-4 w-px bg-border" />

        {/* Beginner Onboarding Tutorial Button */}
        <button
          type="button"
          onClick={onOpenOnboarding}
          title={t.onboardingBtn}
          className="flex h-7 items-center gap-1 rounded-md px-2 text-[11px] font-medium text-accent transition-colors hover:bg-accent/15"
        >
          <GraduationCap className="size-3.5" />
          <span className="hidden xl:inline">{t.onboardingBtn}</span>
        </button>

        {/* IDE Pane Toggles (like T3 Code top-right!) */}
        <button
          type="button"
          onClick={onToggleConsole}
          title={t.consoleTitle}
          className={`flex size-7 items-center justify-center rounded-md transition-colors ${
            consoleOpen
              ? 'bg-surface-secondary text-accent'
              : 'text-muted hover:bg-surface-secondary hover:text-foreground'
          }`}
        >
          <Terminal className="size-3.5" />
        </button>
        <button
          type="button"
          onClick={onToggleInspector}
          title={t.toggleInspector}
          aria-label={t.toggleInspector}
          className={`flex size-7 items-center justify-center rounded-md transition-colors ${
            inspectorOpen
              ? 'bg-surface-secondary text-accent'
              : 'text-muted hover:bg-surface-secondary hover:text-foreground'
          }`}
        >
          <PanelRight className="size-3.5" />
        </button>

        {/* Native Dark Window Controls (Minimize / Maximize / Close) */}
        {isDesktopHost() && (
          <>
            <div className="mx-0.5 h-4 w-px bg-border" />
            <div className="flex items-center">
              <button
                type="button"
                aria-label={t.winMinimize}
                title={t.winMinimize}
                onClick={() => sendDesktopCommand('win:minimize')}
                className="flex h-7 w-9 items-center justify-center rounded-md text-muted transition-colors hover:bg-surface-secondary hover:text-foreground"
              >
                <Minus className="size-3.5" />
              </button>
              <button
                type="button"
                aria-label={maximized ? t.winRestore : t.winMaximize}
                title={maximized ? t.winRestore : t.winMaximize}
                onClick={() => sendDesktopCommand('win:maximize')}
                className="flex h-7 w-9 items-center justify-center rounded-md text-muted transition-colors hover:bg-surface-secondary hover:text-foreground"
              >
                {maximized ? <Copy className="size-3" /> : <Square className="size-3" />}
              </button>
              <button
                type="button"
                aria-label={t.winClose}
                title={t.winClose}
                onClick={() => sendDesktopCommand('win:close')}
                className="flex h-7 w-9 items-center justify-center rounded-md text-muted transition-colors hover:bg-danger hover:text-white"
              >
                <X className="size-3.5" />
              </button>
            </div>
          </>
        )}
      </div>
    </header>
  );
}

/**
 * 8-way native Win32 border resize hit strips for the chromeless Photino window.
 */
export function WindowResizeHandles() {
  if (!isDesktopHost()) return null;

  const start = (dir: string) => (e: React.MouseEvent) => {
    if (e.button !== 0) return;
    e.preventDefault();
    sendDesktopCommand(`win:resize-${dir}`);
  };

  return (
    <>
      <div onMouseDown={start('n')} className="fixed inset-x-2 top-0 z-50 h-1 cursor-n-resize" />
      <div onMouseDown={start('s')} className="fixed inset-x-2 bottom-0 z-50 h-1 cursor-s-resize" />
      <div onMouseDown={start('w')} className="fixed inset-y-2 left-0 z-50 w-1 cursor-w-resize" />
      <div onMouseDown={start('e')} className="fixed inset-y-2 right-0 z-50 w-1 cursor-e-resize" />
      <div onMouseDown={start('nw')} className="fixed left-0 top-0 z-50 size-2.5 cursor-nw-resize" />
      <div onMouseDown={start('ne')} className="fixed right-0 top-0 z-50 size-2.5 cursor-ne-resize" />
      <div onMouseDown={start('sw')} className="fixed bottom-0 left-0 z-50 size-2.5 cursor-sw-resize" />
      <div onMouseDown={start('se')} className="fixed bottom-0 right-0 z-50 size-2.5 cursor-se-resize" />
    </>
  );
}
