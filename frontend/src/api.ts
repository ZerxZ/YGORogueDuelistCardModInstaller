const params = new URLSearchParams(window.location.search);
const API_BASE = (params.get('api') ?? '').replace(/\/+$/, '');

export function resolveApiUrl(path: string): string {
  if (!path) return '';
  if (path.startsWith('http://') || path.startsWith('https://') || path.startsWith('data:')) {
    return path;
  }
  return `${API_BASE}${path.startsWith('/') ? path : `/${path}`}`;
}

export async function apiGet<T>(path: string): Promise<T> {
  const res = await fetch(resolveApiUrl(path), {
    method: 'GET',
    headers: { Accept: 'application/json' },
  });
  const data = await res.json();
  if (!res.ok || data?.ok === false) {
    throw new Error(data?.error || `HTTP ${res.status}`);
  }
  return data as T;
}

export async function apiPost<T>(path: string, body: Record<string, unknown> = {}): Promise<T> {
  const res = await fetch(resolveApiUrl(path), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
    body: JSON.stringify(body),
  });
  const data = await res.json();
  if (!res.ok || data?.ok === false) {
    throw new Error(data?.error || `HTTP ${res.status}`);
  }
  return data as T;
}

export async function apiUploadBinary<T>(path: string, file: File): Promise<T> {
  const buf = await file.arrayBuffer();
  const res = await fetch(resolveApiUrl(path), {
    method: 'POST',
    headers: {
      'Content-Type': file.type || 'application/octet-stream',
      Accept: 'application/json',
    },
    body: buf,
  });
  const data = await res.json();
  if (!res.ok || data?.ok === false) {
    throw new Error(data?.error || `HTTP ${res.status}`);
  }
  return data as T;
}

import type { McpCallRecord, StudioSyncEvent } from './types';

export function subscribeEvents(handlers: {
  onLog?: (e: { time: string; level: string; message: string }) => void;
  onProgress?: (e: {
    status: string;
    percent: number | null;
    isDownloading?: boolean;
    label?: string | null;
  }) => void;
  onHotReload?: (e: {
    running: boolean;
    port: number;
    url: string | null;
    revision: number;
    cssOnly?: boolean;
    error?: string | null;
    changedFiles?: string[];
  }) => void;
  onMcpCall?: (e: McpCallRecord) => void;
  onStudioSync?: (e: StudioSyncEvent) => void;
}): () => void {
  const es = new EventSource(resolveApiUrl('/api/events'));
  es.addEventListener('log', (ev) => {
    try {
      handlers.onLog?.(JSON.parse((ev as MessageEvent).data));
    } catch {
      // ignore
    }
  });
  es.addEventListener('progress', (ev) => {
    try {
      handlers.onProgress?.(JSON.parse((ev as MessageEvent).data));
    } catch {
      // ignore
    }
  });
  es.addEventListener('hotreload', (ev) => {
    try {
      handlers.onHotReload?.(JSON.parse((ev as MessageEvent).data));
    } catch {
      // ignore
    }
  });
  es.addEventListener('mcp-call', (ev) => {
    try {
      handlers.onMcpCall?.(JSON.parse((ev as MessageEvent).data));
    } catch {
      // ignore
    }
  });
  es.addEventListener('studio-sync', (ev) => {
    try {
      handlers.onStudioSync?.(JSON.parse((ev as MessageEvent).data));
    } catch {
      // ignore
    }
  });
  return () => es.close();
}
