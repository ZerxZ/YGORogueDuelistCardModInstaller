import { useEffect, useMemo, useState } from 'react';
import CodeMirror from '@uiw/react-codemirror';
import { oneDark } from '@codemirror/theme-one-dark';
import { javascript } from '@codemirror/lang-javascript';
import { json, jsonParseLinter } from '@codemirror/lang-json';
import { css } from '@codemirror/lang-css';
import { StreamLanguage } from '@codemirror/language';
import { lua } from '@codemirror/legacy-modes/mode/lua';
import { autocompletion, type CompletionContext, type CompletionResult } from '@codemirror/autocomplete';
import { linter } from '@codemirror/lint';
import { keymap } from '@codemirror/view';
import type { Extension } from '@codemirror/state';
import { Button, Chip } from '@heroui/react';
import { Bot, Check, Copy, Maximize2, Minimize2, Save, Sparkles } from 'lucide-react';
import {
  OCGCORE_LUA_COMPLETIONS,
  XMAKE_LUA_COMPLETIONS,
  YGO_JS_COMPLETIONS,
} from './completionData';

export type CodeMode = 'lua' | 'dsl-lua' | 'js' | 'json' | 'css' | 'text';

export function detectCodeMode(filePath?: string): CodeMode {
  if (!filePath) return 'text';
  const lower = filePath.toLowerCase();
  if (lower.endsWith('mod.lua')) return 'dsl-lua';
  if (lower.endsWith('.lua')) return 'lua';
  if (lower.endsWith('.js') || lower.endsWith('.mjs')) return 'js';
  if (lower.endsWith('.json')) return 'json';
  if (lower.endsWith('.css')) return 'css';
  return 'text';
}

interface CodeEditorProps {
  value: string;
  onChange?: (val: string) => void;
  mode?: CodeMode;
  filePath?: string;
  readOnly?: boolean;
  height?: string;
  onSave?: () => void;
  title?: string;
  hint?: string;
  copyLabel?: string;
  copiedLabel?: string;
  expandLabel?: string;
  exitExpandLabel?: string;
  aiPromptLabel?: string;
  onCopyAiPrompt?: () => void;
}

export function CodeEditor({
  value,
  onChange,
  mode,
  filePath,
  readOnly = false,
  height = '260px',
  onSave,
  title,
  hint,
  copyLabel = '复制',
  copiedLabel = '已复制',
  expandLabel = '全屏编辑',
  exitExpandLabel = '退出全屏',
  aiPromptLabel,
  onCopyAiPrompt,
}: CodeEditorProps) {
  const [copied, setCopied] = useState(false);
  const [expanded, setExpanded] = useState(false);
  const resolvedMode = mode ?? detectCodeMode(filePath);

  useEffect(() => {
    if (!expanded) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setExpanded(false);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [expanded]);

  const extensions = useMemo<Extension[]>(() => {
    const exts: Extension[] = [];

    if (resolvedMode === 'lua' || resolvedMode === 'dsl-lua') {
      exts.push(StreamLanguage.define(lua));
      const pool =
        resolvedMode === 'dsl-lua'
          ? [...XMAKE_LUA_COMPLETIONS, ...OCGCORE_LUA_COMPLETIONS]
          : [...OCGCORE_LUA_COMPLETIONS, ...XMAKE_LUA_COMPLETIONS];
      exts.push(
        autocompletion({
          override: [
            (ctx: CompletionContext): CompletionResult | null => {
              const word = ctx.matchBefore(/[\w.:]+/);
              if (!word && !ctx.explicit) return null;
              return {
                from: word ? word.from : ctx.pos,
                options: pool,
                validFor: /^[\w.:]*$/,
              };
            },
          ],
        }),
      );
    } else if (resolvedMode === 'js') {
      exts.push(javascript({ jsx: false, typescript: false }));
      exts.push(
        autocompletion({
          override: [
            (ctx: CompletionContext): CompletionResult | null => {
              const word = ctx.matchBefore(/[\w.]+/);
              if (!word && !ctx.explicit) return null;
              return {
                from: word ? word.from : ctx.pos,
                options: YGO_JS_COMPLETIONS,
                validFor: /^[\w.]*$/,
              };
            },
          ],
        }),
      );
    } else if (resolvedMode === 'json') {
      exts.push(json());
      exts.push(linter(jsonParseLinter()));
    } else if (resolvedMode === 'css') {
      exts.push(css());
    }

    if (onSave && !readOnly) {
      exts.push(
        keymap.of([
          {
            key: 'Mod-s',
            preventDefault: true,
            run: () => {
              onSave();
              return true;
            },
          },
        ]),
      );
    }

    return exts;
  }, [resolvedMode, onSave, readOnly]);

  const badgeLabel =
    resolvedMode === 'dsl-lua'
      ? 'XMAKE LUA DSL'
      : resolvedMode === 'lua'
        ? 'OCGCORE LUA'
        : resolvedMode === 'js'
          ? 'YGO JS / DSL'
          : resolvedMode.toUpperCase();

  const handleCopy = () => {
    navigator.clipboard.writeText(value || '').catch(() => {});
    setCopied(true);
    setTimeout(() => setCopied(false), 1400);
  };

  const renderToolbar = (isFullscreen: boolean) => (
    <div className="flex items-center justify-between gap-2 border-b border-border/80 bg-surface px-2.5 py-1.5 text-xs">
      <div className="flex min-w-0 items-center gap-2">
        <Chip color="accent" size="sm" variant="soft" className="h-5">
          <Sparkles className="size-2.5" />
          <Chip.Label className="font-mono text-[10px] font-semibold">{badgeLabel}</Chip.Label>
        </Chip>
        {title && <span className="truncate font-mono text-[11px] text-foreground/85">{title}</span>}
      </div>
      <div className="flex shrink-0 items-center gap-1.5">
        {hint && <span className="hidden text-[10px] text-muted 2xl:inline">{hint}</span>}
        {onCopyAiPrompt && (
          <Button
            size="sm"
            variant="ghost"
            onPress={onCopyAiPrompt}
            className="h-6 min-w-0 px-2 text-[11px] text-accent"
          >
            <Bot className="size-3" />
            {aiPromptLabel || 'AI (MCP)'}
          </Button>
        )}
        {isFullscreen && onSave && !readOnly && (
          <Button size="sm" variant="primary" onPress={onSave} className="h-6 min-w-0 px-2 text-[11px]">
            <Save className="size-3" />
            Ctrl+S
          </Button>
        )}
        <Button size="sm" variant="ghost" onPress={handleCopy} className="h-6 min-w-0 px-1.5 text-[11px]">
          {copied ? <Check className="size-3 text-success" /> : <Copy className="size-3" />}
          {copied ? copiedLabel : copyLabel}
        </Button>
        <Button
          size="sm"
          variant="ghost"
          onPress={() => setExpanded((v) => !v)}
          className="h-6 min-w-0 px-1.5 text-[11px]"
        >
          {isFullscreen ? <Minimize2 className="size-3" /> : <Maximize2 className="size-3" />}
          <span className="hidden sm:inline">{isFullscreen ? exitExpandLabel : expandLabel}</span>
        </Button>
      </div>
    </div>
  );

  const cmEl = (cmHeight: string) => (
    <CodeMirror
      value={value}
      height={cmHeight}
      theme={oneDark}
      extensions={extensions}
      readOnly={readOnly}
      editable={!readOnly}
      onChange={(val) => onChange?.(val)}
      basicSetup={{
        lineNumbers: true,
        highlightActiveLineGutter: true,
        highlightActiveLine: !readOnly,
        foldGutter: true,
        autocompletion: true,
        bracketMatching: true,
        closeBrackets: true,
        tabSize: 2,
      }}
    />
  );

  return (
    <>
      <div className="flex flex-col overflow-hidden rounded-xl border border-border bg-[#0b0b0e]">
        {renderToolbar(false)}
        <div style={{ height }} className="min-h-0 overflow-hidden bg-[#0b0b0e]">
          {!expanded && cmEl(height)}
        </div>
      </div>

      {expanded && (
        <div className="fixed inset-0 z-50 flex flex-col bg-black/75 p-4 backdrop-blur-sm">
          <div className="flex min-h-0 flex-1 flex-col overflow-hidden rounded-2xl border border-border bg-[#0b0b0e] shadow-2xl">
            {renderToolbar(true)}
            <div className="min-h-0 flex-1 overflow-hidden bg-[#0b0b0e]">
              {cmEl('calc(100vh - 86px)')}
            </div>
          </div>
        </div>
      )}
    </>
  );
}
