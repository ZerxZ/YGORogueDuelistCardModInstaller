import { useCallback, useEffect, useMemo, useState } from 'react';
import { Button, Card, Chip, Label, Surface, TextArea } from '@heroui/react';
import {
  Activity,
  Bot,
  Check,
  CheckCircle2,
  Copy,
  FileCode2,
  Play,
  RefreshCw,
  Sparkles,
  Terminal,
  Trash2,
  Wrench,
  XCircle,
} from 'lucide-react';
import { AI_PROMPT_PRESETS, buildAiPrompt, type AiPromptPresetKey } from '../aiPrompts';
import { apiGet, apiPost } from '../api';
import { fmt, type I18nDict } from '../i18n';
import type { Lang, McpCallRecord, McpInfoPayload, McpToolDef } from '../types';

interface McpHubTabProps {
  t: I18nDict;
  lang: Lang;
  apiBaseUrl: string;
  gameVersion: string | null;
  activeModId: string;
  activeModName?: string;
  liveCalls: McpCallRecord[];
  onClearLiveCalls: () => void;
  onNotify: (msg: string, kind?: 'ok' | 'err' | 'info') => void;
}

type ClientSnippetTab = 'http' | 'claude' | 'sse' | 'stdio';

export function McpHubTab({
  t,
  lang,
  apiBaseUrl,
  gameVersion,
  activeModId,
  activeModName,
  liveCalls,
  onClearLiveCalls,
  onNotify,
}: McpHubTabProps) {
  const [info, setInfo] = useState<McpInfoPayload | null>(null);
  const [snippetTab, setSnippetTab] = useState<ClientSnippetTab>('http');
  const [promptPreset, setPromptPreset] = useState<AiPromptPresetKey>('all_in_one');
  const [customTask, setCustomTask] = useState('');
  const [copiedKey, setCopiedKey] = useState<string | null>(null);
  const [selectedTool, setSelectedTool] = useState<McpToolDef | null>(null);
  const [toolArgsJson, setToolArgsJson] = useState('{}');
  const [toolOutput, setToolOutput] = useState('');
  const [runningTool, setRunningTool] = useState(false);
  const [installingCfg, setInstallingCfg] = useState(false);

  const loadInfo = useCallback(async () => {
    try {
      const res = await apiGet<McpInfoPayload>('/api/mcp/info');
      setInfo(res);
      setSelectedTool((prev) => prev ?? res.tools[0] ?? null);
    } catch (e) {
      onNotify((e as Error).message, 'err');
    }
  }, [onNotify]);

  useEffect(() => {
    loadInfo();
  }, [loadInfo]);

  const copyText = (key: string, text: string) => {
    navigator.clipboard.writeText(text).catch(() => {});
    setCopiedKey(key);
    setTimeout(() => setCopiedKey(null), 1500);
  };

  const clientSnippet = useMemo(() => {
    if (!info) return '';
    if (snippetTab === 'http') {
      return JSON.stringify(
        {
          mcpServers: {
            'ygo-rogue-mod': {
              type: 'http',
              url: info.httpUrl,
            },
          },
        },
        null,
        2,
      );
    }
    if (snippetTab === 'claude') {
      return `claude mcp add --transport http ygo-rogue-mod ${info.httpUrl}`;
    }
    if (snippetTab === 'sse') {
      return JSON.stringify(
        {
          mcpServers: {
            'ygo-rogue-mod': {
              url: info.sseUrl,
            },
          },
        },
        null,
        2,
      );
    }
    return JSON.stringify(
      {
        mcpServers: {
          'ygo-rogue-mod': {
            command: info.stdioCommand,
            args: info.stdioArgs,
          },
        },
      },
      null,
      2,
    );
  }, [info, snippetTab]);

  const mergedCalls = useMemo(() => {
    const seen = new Set<string>();
    const out: McpCallRecord[] = [];
    for (const c of [...liveCalls, ...(info?.recentCalls || [])]) {
      if (seen.has(c.id)) continue;
      seen.add(c.id);
      out.push(c);
    }
    return out.slice(0, 50);
  }, [liveCalls, info?.recentCalls]);

  const generatedPrompt = useMemo(
    () =>
      buildAiPrompt(promptPreset, {
        lang,
        apiBaseUrl,
        mcpUrl: info?.httpUrl || `${apiBaseUrl.replace(/\/+$/, '')}/mcp`,
        mcpSseUrl: info?.sseUrl || `${apiBaseUrl.replace(/\/+$/, '')}/mcp/sse`,
        workspaceRoot: info?.workspaceRoot,
        gameVersion,
        activeModId,
        activeModName,
        focus: info?.focus,
        tools: info?.tools,
        customTask,
      }),
    [promptPreset, lang, apiBaseUrl, info, gameVersion, activeModId, activeModName, customTask],
  );

  const activePresetMeta =
    AI_PROMPT_PRESETS.find((p) => p.key === promptPreset) ?? AI_PROMPT_PRESETS[0];

  const handleSelectTool = (tool: McpToolDef) => {
    setSelectedTool(tool);
    setToolOutput('');
    const sample: Record<string, unknown> = {};
    const props = tool.inputSchema?.properties || {};
    const reqs = new Set(tool.inputSchema?.required || []);
    for (const [k, def] of Object.entries(props)) {
      if (!reqs.has(k) && Object.keys(props).length > 3) continue;
      if (k === 'modId' && info?.focus?.modId) sample[k] = info.focus.modId;
      else if (k === 'relativePath' && info?.focus?.scriptPath) sample[k] = info.focus.scriptPath;
      else if (k === 'query') sample[k] = 'Dark Magician';
      else if (k === 'kind') sample[k] = 'cards';
      else if (k === 'topic') sample[k] = 'lua';
      else if (def.type === 'boolean') sample[k] = true;
      else if (def.type === 'integer' || def.type === 'number') sample[k] = 0;
      else if (def.type === 'array') sample[k] = [];
      else sample[k] = '';
    }
    setToolArgsJson(JSON.stringify(sample, null, 2));
  };

  const handleRunTool = async () => {
    if (!selectedTool) return;
    setRunningTool(true);
    try {
      const parsed = toolArgsJson.trim() ? JSON.parse(toolArgsJson) : {};
      const res = await apiPost<{ output: string }>('/api/mcp/call', {
        tool: selectedTool.name,
        arguments: parsed,
      });
      setToolOutput(res.output || '');
      onNotify(fmt(t.toastMcpToolDone, { tool: selectedTool.name }), 'ok');
    } catch (e) {
      setToolOutput(`Error: ${(e as Error).message}`);
      onNotify((e as Error).message, 'err');
    } finally {
      setRunningTool(false);
    }
  };

  const handleInstallConfig = async (transport: 'http' | 'stdio') => {
    setInstallingCfg(true);
    try {
      const res = await apiPost<{ configPath: string }>('/api/mcp/install-config', { transport });
      onNotify(fmt(t.toastMcpConfigWritten, { path: res.configPath }), 'ok');
    } catch (e) {
      onNotify((e as Error).message, 'err');
    } finally {
      setInstallingCfg(false);
    }
  };

  const handleClearCalls = async () => {
    await apiPost('/api/mcp/clear-logs').catch(() => {});
    onClearLiveCalls();
    loadInfo();
  };

  return (
    <div className="grid h-full min-h-0 flex-1 grid-cols-1 gap-3 overflow-y-auto bg-background p-4 lg:grid-cols-12">
      {/* LEFT COLUMN: Built-in Server Status, Client Snippets & Live Call Feed */}
      <div className="flex min-h-0 flex-col gap-3 lg:col-span-6">
        <Card variant="default" className="p-4 gap-3">
          <Card.Header className="p-0 flex flex-row items-start justify-between gap-2">
            <div className="flex items-start gap-2.5">
              <div className="flex size-9 shrink-0 items-center justify-center rounded-xl border border-accent/35 bg-accent/15 text-accent">
                <Bot className="size-4.5" />
              </div>
              <div>
                <div className="flex items-center gap-2">
                  <Card.Title className="text-sm font-bold">{t.mcpTitle}</Card.Title>
                  <Chip size="sm" color="success" variant="soft" className="h-5">
                    <Chip.Label className="font-mono text-[10px]">
                      {fmt(t.mcpRunning, { n: info?.tools.length ?? 21 })}
                    </Chip.Label>
                  </Chip>
                </div>
                <p className="mt-0.5 text-[11px] leading-relaxed text-muted">{t.mcpSubtitle}</p>
              </div>
            </div>
            <Button isIconOnly size="sm" variant="ghost" aria-label={t.refresh} onPress={loadInfo} className="size-7 shrink-0">
              <RefreshCw className="size-3.5" />
            </Button>
          </Card.Header>

          {info && (
            <Card.Content className="p-0 space-y-2 text-xs">
              {[
                { key: 'http', label: t.mcpEndpointHttp, val: info.httpUrl, accent: true },
                { key: 'sse', label: t.mcpEndpointSse, val: info.sseUrl, accent: false },
                {
                  key: 'stdio',
                  label: t.mcpEndpointStdio,
                  val: `${info.stdioCommand}${info.stdioArgs.length ? ' ' + info.stdioArgs.join(' ') : ''}`,
                  accent: false,
                },
              ].map((row) => (
                <div
                  key={row.key}
                  className="flex items-center justify-between gap-2 rounded-lg border border-border/80 bg-background px-2.5 py-1.5"
                >
                  <div className="min-w-0 flex-1">
                    <div className="text-[10px] text-muted">{row.label}</div>
                    <div className={`truncate font-mono text-[11px] ${row.accent ? 'font-semibold text-accent' : 'text-foreground'}`}>
                      {row.val}
                    </div>
                  </div>
                  <Button
                    isIconOnly
                    size="sm"
                    variant="ghost"
                    aria-label={t.copy}
                    onPress={() => copyText(row.key, row.val)}
                    className="size-7 shrink-0"
                  >
                    {copiedKey === row.key ? <Check className="size-3.5 text-success" /> : <Copy className="size-3.5" />}
                  </Button>
                </div>
              ))}

              {/* Current Studio Focus */}
              <div className="rounded-lg border border-warning/30 bg-warning-soft/30 px-3 py-2">
                <div className="flex items-center gap-1.5 text-[10px] font-semibold uppercase tracking-wide text-warning">
                  <FileCode2 className="size-3" />
                  <span>{t.mcpCurrentFocus}</span>
                </div>
                {info.focus?.modId ? (
                  <div className="mt-1 flex flex-wrap items-center gap-1.5 font-mono text-[11px] text-foreground">
                    <Chip size="sm" variant="soft" className="h-5">
                      <Chip.Label className="text-[10px]">{info.focus.modId}</Chip.Label>
                    </Chip>
                    {info.focus.itemName && <span className="font-sans font-medium">{info.focus.itemName}</span>}
                    {info.focus.scriptPath && <span className="text-accent">{info.focus.scriptPath}</span>}
                  </div>
                ) : (
                  <p className="mt-1 text-[11px] text-muted">{t.mcpNoFocus}</p>
                )}
              </div>
            </Card.Content>
          )}

          <Card.Footer className="p-0 pt-1 flex flex-wrap gap-2">
            <Button
              size="sm"
              variant="primary"
              isDisabled={installingCfg}
              onPress={() => handleInstallConfig('http')}
              className="h-8 text-xs"
            >
              <CheckCircle2 className="size-3.5" />
              {t.mcpWriteConfigHttp}
            </Button>
            <Button
              size="sm"
              variant="secondary"
              isDisabled={installingCfg}
              onPress={() => handleInstallConfig('stdio')}
              className="h-8 text-xs"
            >
              <Terminal className="size-3.5" />
              {t.mcpWriteConfigStdio}
            </Button>
          </Card.Footer>
        </Card>

        {/* Quick AI Prompt Copy Center (MCP Guides + Local API + Features) */}
        <Card variant="default" className="p-4 gap-3 border-accent/35">
          <div className="flex items-start justify-between gap-2">
            <div className="min-w-0">
              <div className="flex items-center gap-1.5">
                <Sparkles className="size-4 shrink-0 text-warning" />
                <span className="text-xs font-bold text-foreground">{t.aiPromptCenterTitle}</span>
              </div>
              <p className="mt-0.5 text-[11px] leading-relaxed text-muted">{t.aiPromptCenterHint}</p>
            </div>
            <Button
              size="sm"
              variant="primary"
              onPress={() => {
                copyText('ai-prompt', generatedPrompt);
                onNotify(t.toastAiSuperPromptCopied, 'ok');
              }}
              className="h-7 shrink-0 text-xs"
            >
              {copiedKey === 'ai-prompt' ? <Check className="size-3.5" /> : <Copy className="size-3.5" />}
              {copiedKey === 'ai-prompt' ? t.copied : t.copySelectedPromptBtn}
            </Button>
          </div>

          <div className="flex flex-wrap gap-1.5">
            {AI_PROMPT_PRESETS.map((p) => {
              const active = p.key === promptPreset;
              return (
                <button
                  key={p.key}
                  type="button"
                  onClick={() => setPromptPreset(p.key)}
                  className={`rounded-lg border px-2.5 py-1 text-[11px] font-medium transition-colors ${
                    active
                      ? 'border-accent/70 bg-accent/15 text-accent'
                      : 'border-border/75 bg-background text-muted hover:border-border hover:text-foreground'
                  }`}
                >
                  {lang === 'zh' ? p.labelZh : p.labelEn}
                </button>
              );
            })}
          </div>

          <p className="text-[11px] text-muted">
            {lang === 'zh' ? activePresetMeta.descZh : activePresetMeta.descEn}
          </p>

          <input
            type="text"
            value={customTask}
            onChange={(e) => setCustomTask(e.target.value)}
            placeholder={t.aiPromptCustomTaskPh}
            className="h-8 w-full rounded-lg border border-border/80 bg-background px-2.5 text-xs text-foreground placeholder:text-muted/70 focus:border-accent focus:outline-none"
          />

          <pre className="max-h-44 overflow-auto rounded-lg border border-border/80 bg-[#080a0e] p-2.5 font-mono text-[10.5px] leading-relaxed text-zinc-200 whitespace-pre-wrap break-words">
            {generatedPrompt}
          </pre>
        </Card>

        {/* Client Setup Snippets */}
        <Card variant="default" className="p-4 gap-2.5">
          <div className="flex items-center justify-between gap-2">
            <span className="text-xs font-bold text-foreground">{t.mcpConfigSnippetTitle}</span>
            <Button
              size="sm"
              variant="ghost"
              onPress={() => copyText('snippet', clientSnippet)}
              className="h-6 px-2 text-[11px]"
            >
              {copiedKey === 'snippet' ? <Check className="size-3 text-success" /> : <Copy className="size-3" />}
              {copiedKey === 'snippet' ? t.copied : t.copy}
            </Button>
          </div>
          <div className="flex flex-wrap gap-1">
            {(
              [
                ['http', t.mcpClientHttp],
                ['claude', t.mcpClientClaudeCli],
                ['sse', t.mcpClientSse],
                ['stdio', t.mcpClientStdio],
              ] as [ClientSnippetTab, string][]
            ).map(([k, lbl]) => (
              <button
                key={k}
                type="button"
                onClick={() => setSnippetTab(k)}
                className={`rounded-md px-2.5 py-1 text-[11px] font-medium transition-colors ${
                  snippetTab === k
                    ? 'bg-accent/15 text-accent border border-accent/40'
                    : 'bg-background text-muted border border-border/70 hover:text-foreground'
                }`}
              >
                {lbl}
              </button>
            ))}
          </div>
          <pre className="max-h-40 overflow-auto rounded-lg border border-border/80 bg-[#080a0e] p-2.5 font-mono text-[11px] leading-relaxed text-sky-300">
            {clientSnippet}
          </pre>
        </Card>

        {/* Live MCP Call Activity Feed */}
        <Surface variant="default" className="flex min-h-[200px] flex-1 flex-col rounded-xl border border-border p-3.5">
          <div className="mb-2 flex items-center justify-between gap-2">
            <div className="flex items-center gap-1.5 text-xs font-bold text-foreground">
              <Activity className="size-3.5 text-success" />
              <span>{t.mcpLiveFeedTitle}</span>
              <Chip size="sm" variant="soft" className="h-4">
                <Chip.Label className="font-mono text-[10px]">{mergedCalls.length}</Chip.Label>
              </Chip>
            </div>
            <Button size="sm" variant="ghost" onPress={handleClearCalls} className="h-6 px-2 text-[11px]">
              <Trash2 className="size-3" />
              {t.clearLogs}
            </Button>
          </div>
          <div className="min-h-0 flex-1 overflow-y-auto space-y-1.5 pr-1">
            {mergedCalls.length === 0 ? (
              <p className="py-8 text-center text-xs text-muted">{t.mcpLiveFeedEmpty}</p>
            ) : (
              mergedCalls.map((c) => (
                <div
                  key={c.id}
                  className="rounded-lg border border-border/75 bg-background px-2.5 py-1.5 font-mono text-[11px]"
                >
                  <div className="flex items-center justify-between gap-2">
                    <div className="flex items-center gap-1.5 truncate">
                      {c.ok ? (
                        <CheckCircle2 className="size-3 shrink-0 text-success" />
                      ) : (
                        <XCircle className="size-3 shrink-0 text-danger" />
                      )}
                      <span className="font-semibold text-accent">{c.tool}</span>
                      <span className="truncate text-muted">{c.args}</span>
                    </div>
                    <span className="shrink-0 text-[10px] text-muted">
                      {c.durationMs}ms · {c.time}
                    </span>
                  </div>
                </div>
              ))
            )}
          </div>
        </Surface>
      </div>

      {/* RIGHT COLUMN: Built-in 21 MCP Tools Browser & Interactive Playground */}
      <Surface variant="default" className="flex min-h-0 flex-col rounded-xl border border-border p-4 lg:col-span-6">
        <div className="mb-3 flex items-center justify-between gap-2">
          <div className="flex items-center gap-2">
            <Wrench className="size-4 text-accent" />
            <h3 className="text-sm font-bold text-foreground">{t.mcpToolsTitle}</h3>
          </div>
          <Chip size="sm" color="accent" variant="soft">
            <Chip.Label className="font-mono text-[10px]">{info?.tools.length ?? 0} tools</Chip.Label>
          </Chip>
        </div>

        <div className="mb-3 grid max-h-56 grid-cols-1 gap-1.5 overflow-y-auto pr-1 sm:grid-cols-2">
          {(info?.tools ?? []).map((tool) => {
            const active = selectedTool?.name === tool.name;
            return (
              <button
                key={tool.name}
                type="button"
                onClick={() => handleSelectTool(tool)}
                className={`rounded-lg border px-2.5 py-2 text-left transition-colors ${
                  active
                    ? 'border-accent bg-accent/12 text-foreground'
                    : 'border-border/75 bg-background text-foreground/85 hover:border-border hover:bg-surface-secondary'
                }`}
              >
                <div className="truncate font-mono text-[11.5px] font-semibold text-accent">{tool.name}</div>
                <p className="mt-0.5 line-clamp-2 text-[10.5px] leading-snug text-muted">{tool.description}</p>
              </button>
            );
          })}
        </div>

        {selectedTool && (
          <div className="flex min-h-0 flex-1 flex-col gap-2.5 border-t border-border/80 pt-3">
            <div>
              <div className="flex items-center justify-between gap-2">
                <span className="font-mono text-xs font-bold text-foreground">{selectedTool.name}</span>
                <Button
                  size="sm"
                  variant="primary"
                  isDisabled={runningTool}
                  onPress={handleRunTool}
                  className="h-7 text-xs"
                >
                  <Play className="size-3" />
                  {t.mcpRunTool}
                </Button>
              </div>
              <p className="mt-0.5 text-[11px] text-muted">{selectedTool.description}</p>
            </div>

            <div>
              <Label className="mb-1 block text-[11px] text-muted">{t.mcpArgsJsonLabel}</Label>
              <TextArea
                rows={4}
                value={toolArgsJson}
                onChange={(e) => setToolArgsJson(e.target.value)}
                className="w-full font-mono text-xs"
              />
            </div>

            <div className="flex min-h-0 flex-1 flex-col">
              <div className="mb-1 flex items-center justify-between">
                <span className="text-[11px] text-muted">{t.mcpOutputLabel}</span>
                {toolOutput && (
                  <Button
                    size="sm"
                    variant="ghost"
                    onPress={() => copyText('out', toolOutput)}
                    className="h-5 px-1.5 text-[10px]"
                  >
                    {copiedKey === 'out' ? <Check className="size-3 text-success" /> : <Copy className="size-3" />}
                    {copiedKey === 'out' ? t.copied : t.copy}
                  </Button>
                )}
              </div>
              <pre className="min-h-[120px] flex-1 overflow-auto rounded-lg border border-border/80 bg-[#080a0e] p-2.5 font-mono text-[11px] leading-relaxed text-emerald-300 whitespace-pre-wrap break-words">
                {toolOutput || '// ...'}
              </pre>
            </div>
          </div>
        )}
      </Surface>
    </div>
  );
}
