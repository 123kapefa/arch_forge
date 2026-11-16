// E3.4: поток — цепочка вызовов от сервиса-входа (GET /api/system/flow). Зеркалит ArchForge.Contracts.Flow.
// Статика: вероятный happy-path, без рантайма; нерешённое и усечение показываем явно (adr/0008).

import { fetchWithTimeout } from './http';
import type { HttpCall } from './edgeCalls';

export type FlowStepKind = 'http' | 'publish' | 'consume' | 'data' | 'bus' | 'external';
/** ok | unmatched | unresolved | cycle | repeat | truncated — набор открыт. */
export type FlowStepStatus = 'ok' | 'unmatched' | 'unresolved' | 'cycle' | 'repeat' | 'truncated' | string;

export interface FlowStep {
  index: number;
  depth: number;
  from: string;
  to: string;
  toName: string;
  kind: FlowStepKind | string;
  label: string;
  status: FlowStepStatus;
  parent?: number;
  sync?: boolean;
  edgeId?: string;
  confidence?: number;
  via?: string;
  calls?: HttpCall[];
  note?: string;
}

export interface Flow {
  entry: string;
  entryName: string;
  endpoint?: string;
  steps: FlowStep[];
  truncated: boolean;
  limits: { maxDepth: number; maxSteps: number };
  notes: string[];
}

export interface FlowQuery { path: string; entry: string; env?: string; endpoint?: string; maxDepth?: number }

const TIMEOUT_MS = 120_000;

export async function fetchFlow(q: FlowQuery, signal?: AbortSignal): Promise<Flow> {
  const params = new URLSearchParams({ path: q.path, entry: q.entry });
  if (q.env) params.set('env', q.env);
  if (q.endpoint) params.set('endpoint', q.endpoint);
  if (q.maxDepth) params.set('maxDepth', String(q.maxDepth));
  const res = await fetchWithTimeout(`/api/system/flow?${params.toString()}`, TIMEOUT_MS, signal);
  if (!res.ok) {
    const body = await res.text();
    let message = body;
    try { const p = JSON.parse(body) as { error?: string }; if (p?.error) message = p.error; } catch { /* не JSON */ }
    throw new Error(message || `Ошибка потока (${res.status})`);
  }
  return res.json() as Promise<Flow>;
}

/** Ключ ребра на холсте (systemFlow: `source|target|kind`) для шага. Подписка на карте идёт подписчик→шина. */
export function stepEdgeKey(s: FlowStep): string | null {
  switch (s.kind) {
    case 'http': case 'external': return `${s.from}|${s.to}|http-call`;
    case 'publish': return `${s.from}|${s.to}|publishes`;
    case 'consume': return `${s.to}|${s.from}|subscribes`;
    case 'data': return `${s.from}|${s.to}|rw-db`;
    case 'bus': return `${s.from}|${s.to}|uses-bus`;
    default: return null;
  }
}

/** Рёбра холста, по которым идёт поток (подсветка пути). */
export function flowEdgeKeys(flow: Flow | null): ReadonlySet<string> | null {
  if (!flow) return null;
  const keys = new Set<string>();
  for (const s of flow.steps) { const k = stepEdgeKey(s); if (k) keys.add(k); }
  return keys;
}

/** Узлы потока (вход + все цели) — подсвечиваются вместе с рёбрами. */
export function flowNodeIds(flow: Flow | null): ReadonlySet<string> | null {
  if (!flow) return null;
  const ids = new Set<string>([flow.entry]);
  for (const s of flow.steps) { ids.add(s.from); ids.add(s.to); }
  return ids;
}

export const STATUS_LABEL: Record<string, string> = {
  unmatched: 'endpoint не найден у цели',
  unresolved: 'детали не определены',
  cycle: 'цикл: сервис уже в цепочке',
  repeat: 'раскрыт выше',
  truncated: 'лимит глубины — дальше не раскрыто',
};

export const KIND_ICON: Record<string, string> = { http: '→', external: '⇢', publish: '⇒', consume: '⇐', data: '▤', bus: '≋' };
