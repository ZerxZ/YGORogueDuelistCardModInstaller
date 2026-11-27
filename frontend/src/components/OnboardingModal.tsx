import { useState } from 'react';
import { Button, Checkbox, Chip, Modal } from '@heroui/react';
import {
  AlertTriangle,
  ArrowLeft,
  ArrowRight,
  Bot,
  CheckCircle2,
  Download,
  FileText,
  Flame,
  FolderOpen,
  Globe,
  GraduationCap,
  Layers,
  Palette,
  Play,
  ShieldCheck,
  Sparkles,
} from 'lucide-react';
import { apiPost } from '../api';
import { fmt, type I18nDict } from '../i18n';
import type { Lang, ManagerStatus, UpdateCheckPayload } from '../types';

export type OnboardingJumpTab = 'library' | 'studio' | 'vanilla' | 'preview' | 'mcp' | 'settings';

interface OnboardingModalProps {
  isOpen: boolean;
  onOpenChange: (open: boolean) => void;
  status: ManagerStatus;
  updateInfo?: UpdateCheckPayload | null;
  t: I18nDict;
  lang: Lang;
  onSelectLang: (next: Lang) => void;
  onRefreshStatus: () => Promise<void>;
  onJumpTab: (tab: OnboardingJumpTab) => void;
  onCreateExample?: () => void;
  onDownloadWebBundleAndPlay?: () => void;
  onOpenChangelog?: () => void;
  onComplete: (dontShowAgain: boolean) => void;
}

interface TutorialStep {
  id: string;
  icon: React.ReactNode;
  badgeZh: string;
  badgeEn: string;
  titleZh: string;
  titleEn: string;
  descZh: string;
  descEn: string;
  pointsZh: string[];
  pointsEn: string[];
  jumpTab?: OnboardingJumpTab;
  jumpLabelZh?: string;
  jumpLabelEn?: string;
}

const STEPS: TutorialStep[] = [
  {
    id: 'welcome',
    icon: <ShieldCheck className="size-6 text-success" />,
    badgeZh: '首次启动设置 · 语言与游戏检测',
    badgeEn: 'First-Run Setup · Language & Game Detection',
    titleZh: '欢迎来到 Yu-Gi-Oh! Rogue Duelist MOD 工坊',
    titleEn: 'Welcome to Yu-Gi-Oh! Rogue Duelist MOD Studio',
    descZh:
      '先选择你的界面语言，并确认原版游戏目录。本工坊采用零侵入虚拟版本包（Game\\900.x.y）且自动添加 -mod 识别标记与官方服务器隔离，永不损坏原版文件！',
    descEn:
      'Choose your language and verify your game directory. MODs compile into an isolated Game\\900.x.y virtual bundle with automatic -mod tagging and official server isolation.',
    pointsZh: [
      '支持无限多 MOD 叠加、分类封面、.ypk / .zip 拖拽导入与一键无痕还原纯净原版。',
      '自动标记 MOD 版版本号（如 v0.2.47-mod）并隔离官方统计/回放/联机房，避免与原版混淆。',
      '支持官方 .delta.gz 极速增量更新（仅 ~5.5 MB）、断点续传、Linux / Android 跨平台导出。',
    ],
    pointsEn: [
      'Unlimited stacked MODs, category covers, drag-and-drop .ypk/.zip import & 1-click vanilla restore.',
      'Auto-tags modded builds (-mod) and isolates official stats/replays/rooms from vanilla servers.',
      'Supports ~5.5 MB .delta.gz incremental updates, resumable downloads & Linux/Android export.',
    ],
  },
  {
    id: 'library',
    icon: <Layers className="size-6 text-accent" />,
    badgeZh: 'MOD 管理与整理',
    badgeEn: 'MOD Organization',
    titleZh: '拖拽排序、分类封面与 .zip / .ypk 导入',
    titleEn: 'Drag-Reorder, Categories, Covers & .zip / .ypk Import',
    descZh:
      '在「MOD 列表与加载顺序」中，支持鼠标拖拽排序、拖拽归类、拖拽换封面，以及直接拖入 YGOPro / MDPro3 的 .ypk 扩展卡包。',
    descEn:
      'Organize your entire MOD library with native drag-and-drop, category filters, covers, and .ypk imports.',
    pointsZh: [
      '拖入 .zip MOD / 工作区整合包，或直接拖入 .ypk 卡包即可纯原生解析 .cdb 卡表、Lua 与卡图！',
      '按住 MOD 左侧抓手上下拖拽排序，拖到顶部分类标签即可快速归类。',
      '点击「完整示例 MOD」可一键生成涵盖卡牌/角色/遗物/卡包/卡组/F9 设置的完整参考。',
    ],
    pointsEn: [
      'Drop .zip MODs, workspace modpacks, or YGOPro/MDPro3 .ypk card expansions directly to import!',
      'Drag MOD rows to reorder load priority, or drop onto any category chip to organize.',
      'Click Full Example MOD to scaffold a complete reference with cards, relics, packs, JS/CSS & F9 settings.',
    ],
    jumpTab: 'library',
    jumpLabelZh: '前往 MOD 列表',
    jumpLabelEn: 'Open MOD Library',
  },
  {
    id: 'studio',
    icon: <Palette className="size-6 text-warning" />,
    badgeZh: '弹窗式可视化创作 · 支持同调/超量/灵摆/连接',
    badgeEn: 'Modal Visual Studio · Modern Era Ready',
    titleZh: '全宽图鉴画廊 · 弹窗编辑 · 卡包/预组封面上传',
    titleEn: 'Full-Width Gallery · Modal Editor · Cover Upload',
    descZh:
      '点击任意卡牌、角色、遗物、卡包或预组卡组即可弹出宽敞的独立编辑窗口，支持本地浏览/上传/拖拽更换封面图，并内置同调/超量/灵摆/连接模板！',
    descEn:
      'Click any card, character, relic, pack or deck to open a spacious modal editor with cover upload/drop and Synchro/Xyz/Pendulum/Link Lua templates.',
    pointsZh: [
      '创作台支持直接「导入 .ypk」（或拖入 .ypk），且所有自定义卡图会自动写入 assets/cards/*.jpg 永不裂图！',
      '卡包、预组卡组、卡牌、角色、遗物全部支持浏览选择、文件上传和拖拽更换封面图片。',
      '所有 js/**、styles/**、css/**、lua/lib/** 默认自动导入，并支持现代大师规则 MR2–MR5 自动解锁！',
    ],
    pointsEn: [
      'Import or drop .ypk right inside Studio; custom card art auto-emits to assets/cards/*.jpg cleanly.',
      'Cards, packs, decks, characters & relics all support browse, file upload, and drag-and-drop cover art.',
      'Auto-imports js/**, styles/**, css/**, lua/lib/** and auto-patches MR2–MR5 for Synchro/Xyz/Pendulum/Link.',
    ],
    jumpTab: 'studio',
    jumpLabelZh: '前往创作工作台',
    jumpLabelEn: 'Open Visual Studio',
  },
  {
    id: 'hotreload',
    icon: <Flame className="size-6 text-warning" />,
    badgeZh: '边改边玩 · 局域网手机热加载',
    badgeEn: 'Live Hot-Reload & LAN Android PWA',
    titleZh: '内嵌热加载试玩 & F8 / F9 / F10 游戏内面板',
    titleEn: 'Embedded Preview, Android LAN PWA & F8/F9/F10',
    descZh:
      '开启左侧「启动热加载」后，修改任何卡图、Lua、CSS 或 JSON 都会毫秒级同步，同一 Wi-Fi 下的 Android 手机浏览器也可免重签直连畅玩！',
    descEn:
      'Start Hot-Reload to test edits live in Desktop Preview or directly on Android Chrome over Wi-Fi without re-signing an APK.',
    pointsZh: [
      'F8 实时修改器：直连游戏内核与 SHA-256 存档，随时调金币/LP、发卡/发遗物/开包、全解锁、瞬间胜利。',
      'F9 MOD 列表 & 设置：实时调节各 MOD 提供的难度、倍速、倍率、主题色等设置！',
      'F10 开发者调试：FPS HUD、网络/错误终端、状态检视与沙盒模式。',
    ],
    pointsEn: [
      'F8 Live Trainer: live gold/LP/cards/relics/packs/unlocks/instant-win with valid SHA-256 saves.',
      'F9 MODs & Settings: tune custom in-game settings (difficulty, speed, colors) live.',
      'F10 Dev & Debug: live FPS HUD, fetch/error console, state inspector & sandbox unlock.',
    ],
    jumpTab: 'preview',
    jumpLabelZh: '前往内嵌热加载试玩',
    jumpLabelEn: 'Open Live Preview',
  },
  {
    id: 'mcp',
    icon: <Bot className="size-6 text-accent" />,
    badgeZh: '内置 AI MCP + runtime/script 技能',
    badgeEn: 'Built-in AI MCP + Modding Skill',
    titleZh: '让 AI 帮你写卡写脚本 · 一键复制提示词',
    titleEn: 'AI-Assisted Modding · 1-Click Prompt Copy',
    descZh:
      '内置 23 工具实时 MCP Server 与深度学习整理的 ygo-rogue-modding 技能文档，Cursor / Claude / Codex 直连即可精准编写现代时代 Lua 与 F9 设置。',
    descEn:
      'Embedded 23-tool MCP Server + curated ygo-rogue-modding skill so AI tools write valid ocgcore Lua & F9 settings effortlessly.',
    pointsZh: [
      '在「内置 MCP 与 AI 脚本」页一键写入工作区 .mcp.json，立即接入任意 AI 编程客户端。',
      '内置 7 套「AI 提示词一键复制预设」（含同调/超量/灵摆/连接现代扩展专场提示词）。',
      '完整技能文件位于 .agents/skills/ygo-rogue-modding/SKILL.md，方便作者与 AI 随时查阅。',
    ],
    pointsEn: [
      'Click "Write .mcp.json" in the Built-in MCP tab to connect any AI client in seconds.',
      'Includes 7 1-click AI prompt presets (including Synchro/Xyz/Pendulum/Link modern era).',
      'Full authoring skill saved in .agents/skills/ygo-rogue-modding/SKILL.md.',
    ],
    jumpTab: 'mcp',
    jumpLabelZh: '前往内置 MCP 中心',
    jumpLabelEn: 'Open Built-in MCP Hub',
  },
  {
    id: 'updates',
    icon: <Download className="size-6 text-success" />,
    badgeZh: '增量更新 · 跨平台导出 · 启动开玩',
    badgeEn: 'Delta Updates · Cross-Platform · Play',
    titleZh: 'Delta 极速更新、更新日志与一键启动游戏',
    titleEn: 'Delta Updates, Release Notes & Launch',
    descZh:
      '更新游戏时自动优先使用 ~5.5 MB 的官方 .delta.gz 增量补丁（省 97% 流量！），支持 ghfast.top 代理与断点续传，也可一键导出 Linux / Android MOD 包！',
    descEn:
      'Automatically applies ~5.5 MB official .delta.gz patches (97% smaller!), supports resumable proxy downloads, and exports Linux/Android bundles.',
    pointsZh: [
      '默认官方 GitHub 直连；国内可切换 ghfast.top 等代理加速并实时查看进度条、网速与剩余时间。',
      '随时点击顶部提示条或更新中心查看原版游戏与 MOD 管理器的最新改动日志。',
      '一切就绪后点击右上角绿色「注入并启动游戏」即可进入游戏！',
    ],
    pointsEn: [
      'Official GitHub direct by default; optional mirrors with live progress bar, speed & ETA.',
      'Inspect latest release notes for both the game and MOD Manager anytime.',
      'Click the green "Inject & Play" button in the top-right bar whenever you are ready!',
    ],
    jumpTab: 'settings',
    jumpLabelZh: '前往更新与跨平台导出',
    jumpLabelEn: 'Open Updates & Export',
  },
];

export function OnboardingModal({
  isOpen,
  onOpenChange,
  status,
  updateInfo,
  t,
  lang,
  onSelectLang,
  onRefreshStatus,
  onJumpTab,
  onCreateExample,
  onDownloadWebBundleAndPlay,
  onOpenChangelog,
  onComplete,
}: OnboardingModalProps) {
  const [stepIdx, setStepIdx] = useState(0);
  const [dontShowAgain, setDontShowAgain] = useState(true);
  const [pickingDir, setPickingDir] = useState(false);

  const isZh = lang === 'zh';
  const step = STEPS[stepIdx] ?? STEPS[0];
  const isFirst = stepIdx === 0;
  const isLast = stepIdx === STEPS.length - 1;
  const gameInstalled = Boolean(status.gameInstalled ?? (status.installDir || status.activeBaseGameVersion));

  const handlePickGameDir = async () => {
    setPickingDir(true);
    try {
      const picked = await apiPost<{ path: string | null }>('/api/dialog/pick-folder', {
        title: isZh ? '选择原版游戏安装目录（含 Yu-Gi-Oh! Rogue Duelist.exe）' : 'Select Game Install Folder',
        initialPath: status.installDir || undefined,
      });
      if (picked.path) {
        await apiPost('/api/settings', { installDir: picked.path });
        await onRefreshStatus();
      }
    } catch {
      // ignore
    } finally {
      setPickingDir(false);
    }
  };

  const finish = () => {
    onComplete(dontShowAgain);
    onOpenChange(false);
    setStepIdx(0);
  };

  return (
    <Modal.Backdrop isOpen={isOpen} onOpenChange={onOpenChange}>
      <Modal.Container placement="center" size="lg">
        <Modal.Dialog className="sm:max-w-[700px]">
          <Modal.CloseTrigger />
          <Modal.Header className="pb-2">
            <Modal.Icon className="bg-accent-soft text-accent-soft-foreground">
              <GraduationCap className="size-5" />
            </Modal.Icon>
            <div className="min-w-0 flex-1">
              <div className="flex flex-wrap items-center gap-2">
                <Modal.Heading className="text-base">{t.onboardingTitle}</Modal.Heading>
                <Chip size="sm" color="accent" variant="soft" className="h-5">
                  <Chip.Label className="font-mono text-[10px]">
                    {fmt(t.onboardingStepOf, { cur: stepIdx + 1, total: STEPS.length })}
                  </Chip.Label>
                </Chip>
              </div>
              <div className="mt-2 flex items-center gap-1.5">
                {STEPS.map((s, i) => (
                  <button
                    key={s.id}
                    type="button"
                    onClick={() => setStepIdx(i)}
                    aria-label={`Step ${i + 1}`}
                    className={`h-1.5 rounded-full transition-all ${
                      i === stepIdx
                        ? 'w-7 bg-accent'
                        : i < stepIdx
                          ? 'w-3 bg-success/70'
                          : 'w-3 bg-border hover:bg-muted'
                    }`}
                  />
                ))}
              </div>
            </div>
          </Modal.Header>

          <Modal.Body className="space-y-3 py-3 max-h-[70vh] overflow-y-auto">
            {/* Step 1 Interactive Setup: Language Selector + Game Directory Auto-Detection */}
            {step.id === 'welcome' && (
              <div className="space-y-2.5">
                {/* Language Switcher */}
                <div className="flex flex-wrap items-center justify-between gap-2 rounded-xl border border-border bg-surface p-3">
                  <div className="flex items-center gap-2 text-xs font-semibold text-foreground">
                    <Globe className="size-4 text-accent" />
                    <span>{isZh ? '选择界面语言 / Interface Language' : 'Interface Language / 选择界面语言'}</span>
                  </div>
                  <div className="flex items-center gap-1.5">
                    <Button
                      size="sm"
                      variant={isZh ? 'primary' : 'secondary'}
                      onPress={() => onSelectLang('zh')}
                      className="h-7 px-3 text-xs"
                    >
                      简体中文
                    </Button>
                    <Button
                      size="sm"
                      variant={!isZh ? 'primary' : 'secondary'}
                      onPress={() => onSelectLang('en')}
                      className="h-7 px-3 text-xs"
                    >
                      English
                    </Button>
                  </div>
                </div>

                {/* Game Directory Detection & Quick Setup */}
                <div
                  className={`rounded-xl border p-3 text-xs space-y-2 ${
                    gameInstalled
                      ? 'border-success/40 bg-success-soft/20'
                      : 'border-warning/50 bg-warning-soft/25'
                  }`}
                >
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <div className="flex items-center gap-2 font-semibold">
                      {gameInstalled ? (
                        <CheckCircle2 className="size-4 text-success shrink-0" />
                      ) : (
                        <AlertTriangle className="size-4 text-warning shrink-0" />
                      )}
                      <span className={gameInstalled ? 'text-success' : 'text-warning'}>
                        {gameInstalled
                          ? isZh
                            ? `已检测到原版游戏 (${status.activeBaseGameVersion ? `v${status.activeBaseGameVersion}` : '已定位'})`
                            : `Base Game Detected (${status.activeBaseGameVersion ? `v${status.activeBaseGameVersion}` : 'Ready'})`
                          : isZh
                            ? '尚未检测到原版游戏目录'
                            : 'Base Game Not Detected Yet'}
                      </span>
                    </div>
                    <div className="flex items-center gap-1.5">
                      <Button
                        size="sm"
                        variant="secondary"
                        isDisabled={pickingDir}
                        onPress={handlePickGameDir}
                        className="h-7 text-[11px]"
                      >
                        <FolderOpen className="size-3 text-accent" />
                        {isZh ? '选择游戏目录' : 'Browse Folder'}
                      </Button>
                      {!gameInstalled && (
                        <>
                          {onDownloadWebBundleAndPlay && (
                            <Button
                              size="sm"
                              variant="primary"
                              onPress={() => {
                                onOpenChange(false);
                                onDownloadWebBundleAndPlay();
                              }}
                              className="h-7 text-[11px]"
                            >
                              <Download className="size-3" />
                              {isZh ? '免装 EXE · 下载 web-.zip 试玩' : 'Download web-.zip & Play'}
                            </Button>
                          )}
                          <Button
                            size="sm"
                            variant="secondary"
                            onPress={() => {
                              onJumpTab('settings');
                              onOpenChange(false);
                            }}
                            className="h-7 text-[11px]"
                          >
                            <Download className="size-3" />
                            {isZh ? '下载中心' : 'Downloads'}
                          </Button>
                        </>
                      )}
                    </div>
                  </div>
                  <div className="truncate font-mono text-[10.5px] text-muted" title={status.installDir || status.gameBaseDir}>
                    {status.installDir || status.gameBaseDir || (isZh ? '点击右侧「选择游戏目录」或前往更新中心一键下载原版游戏。' : 'Browse your install folder or open Updates to download automatically.')}
                  </div>
                </div>

                {/* Live Environment Readiness + Latest Release Update Notice */}
                <div className="grid grid-cols-3 gap-2 text-xs">
                  <div className="rounded-lg border border-border/80 bg-background px-3 py-2">
                    <div className="text-[10px] text-muted">{isZh ? '已加载 MOD' : 'Managed MODs'}</div>
                    <div className="mt-0.5 font-mono font-semibold text-foreground">
                      {status.mods.filter((m) => m.enabled).length} / {status.mods.length}
                    </div>
                  </div>
                  <div className="rounded-lg border border-border/80 bg-background px-3 py-2">
                    <div className="text-[10px] text-muted">{isZh ? '管理器 / MCP' : 'Manager / MCP'}</div>
                    <div className="mt-0.5 font-mono font-semibold text-accent">
                      v{status.managerVersion} · {status.mcpToolCount} Tools
                    </div>
                  </div>
                  <div className="rounded-lg border border-border/80 bg-background px-3 py-2 flex items-center justify-between gap-1">
                    <div className="min-w-0">
                      <div className="text-[10px] text-muted">{isZh ? '官方最新版本' : 'Latest Game'}</div>
                      <div className="mt-0.5 truncate font-mono font-semibold text-success">
                        {updateInfo?.game.latestTag || (status.activeBaseGameVersion ? `v${status.activeBaseGameVersion}` : '—')}
                      </div>
                    </div>
                    {onOpenChangelog && updateInfo && (
                      <Button
                        isIconOnly
                        size="sm"
                        variant="ghost"
                        aria-label={t.viewChangelogBtn}
                        onPress={onOpenChangelog}
                        className="size-7 shrink-0"
                      >
                        <FileText className="size-3.5 text-accent" />
                      </Button>
                    )}
                  </div>
                </div>
              </div>
            )}

            <div className="flex items-start gap-3.5 rounded-xl border border-border/80 bg-surface-secondary/50 p-3.5">
              <div className="flex size-11 shrink-0 items-center justify-center rounded-xl border border-border bg-background shadow-sm">
                {step.icon}
              </div>
              <div className="min-w-0 flex-1">
                <Chip size="sm" variant="soft" color="warning" className="mb-1 h-5">
                  <Chip.Label className="text-[10px] font-semibold">
                    {isZh ? step.badgeZh : step.badgeEn}
                  </Chip.Label>
                </Chip>
                <h3 className="text-sm font-bold text-foreground">
                  {isZh ? step.titleZh : step.titleEn}
                </h3>
                <p className="mt-1 text-xs leading-relaxed text-muted">
                  {isZh ? step.descZh : step.descEn}
                </p>
              </div>
            </div>

            {/* Key Highlights */}
            <ul className="space-y-2 rounded-xl border border-border/75 bg-background p-3.5 text-xs text-foreground/90">
              {(isZh ? step.pointsZh : step.pointsEn).map((pt, i) => (
                <li key={i} className="flex items-start gap-2 leading-relaxed">
                  <CheckCircle2 className="mt-0.5 size-3.5 shrink-0 text-success" />
                  <span>{pt}</span>
                </li>
              ))}
            </ul>

            {/* Quick Contextual Action Row */}
            <div className="flex flex-wrap items-center justify-between gap-2 pt-0.5">
              <div className="flex flex-wrap items-center gap-2">
                {step.jumpTab && (
                  <Button
                    size="sm"
                    variant="secondary"
                    onPress={() => {
                      onJumpTab(step.jumpTab!);
                      onOpenChange(false);
                    }}
                    className="h-7 text-xs"
                  >
                    <Play className="size-3 text-accent" />
                    {isZh ? step.jumpLabelZh || t.onboardingGoNow : step.jumpLabelEn || t.onboardingGoNow}
                  </Button>
                )}
                {step.id === 'library' && onCreateExample && (
                  <Button
                    size="sm"
                    variant="secondary"
                    onPress={() => {
                      onJumpTab('library');
                      onOpenChange(false);
                      onCreateExample();
                    }}
                    className="h-7 text-xs"
                  >
                    <Sparkles className="size-3 text-warning" />
                    {t.createExampleMod}
                  </Button>
                )}
                {step.id === 'updates' && onOpenChangelog && (
                  <Button
                    size="sm"
                    variant="secondary"
                    onPress={onOpenChangelog}
                    className="h-7 text-xs"
                  >
                    <FileText className="size-3 text-accent" />
                    {t.viewChangelogBtn}
                  </Button>
                )}
              </div>

              <Checkbox
                isSelected={dontShowAgain}
                onChange={setDontShowAgain}
              >
                <Checkbox.Content>
                  <Checkbox.Control>
                    <Checkbox.Indicator />
                  </Checkbox.Control>
                  <span className="text-[11px] text-muted">{t.onboardingDontShow}</span>
                </Checkbox.Content>
              </Checkbox>
            </div>
          </Modal.Body>

          <Modal.Footer className="flex items-center justify-between gap-2">
            <Button size="sm" variant="ghost" onPress={finish} className="text-xs text-muted">
              {t.onboardingSkip}
            </Button>

            <div className="flex items-center gap-1.5">
              <Button
                size="sm"
                variant="secondary"
                isDisabled={isFirst}
                onPress={() => setStepIdx((i) => Math.max(0, i - 1))}
              >
                <ArrowLeft className="size-3.5" />
                {t.onboardingPrev}
              </Button>
              {!isLast ? (
                <Button
                  size="sm"
                  variant="primary"
                  onPress={() => setStepIdx((i) => Math.min(STEPS.length - 1, i + 1))}
                >
                  {t.onboardingNext}
                  <ArrowRight className="size-3.5" />
                </Button>
              ) : (
                <Button size="sm" variant="primary" onPress={finish}>
                  <Sparkles className="size-3.5" />
                  {t.onboardingFinish}
                </Button>
              )}
            </div>
          </Modal.Footer>
        </Modal.Dialog>
      </Modal.Container>
    </Modal.Backdrop>
  );
}
