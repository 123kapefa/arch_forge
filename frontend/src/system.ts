// Контракт СИСТЕМНОГО графа (`GET /api/system`, Фаза 1.5) — зеркалит ArchForge.Contracts
// (System.Text.Json Web = camelCase). Аддитивные поля Фазы 1.5 опциональны: бэкенд опускает их при null.
//
// Отдельный модуль от api.ts (Фаза 1), чтобы визуальная оболочка (L0/L1) импортировала системный
// слой явно и он не пересекался с перепереносом фронт-прототипа.

export type ProblemSeverity = 'critical' | 'warning' | 'coverage' | 'info';
export type ProblemCategory = 'architecture' | 'convention';
/** Вид системного узла (Ф1.5). Строка — набор открыт (external может расширяться). */
export type SystemNodeKind = 'service' | 'datastore' | 'topic' | 'external' | string;

export interface SystemNode {
  id: string;
  name: string;
  kind: string;          // service | datastore | topic | external
  level?: string;        // system | service | type | member
  health?: string;       // ok | warning | critical (по самой суровой проблеме на узле)
  // Ф1-поля тоже присутствуют в контракте (на системном уровне пустые):
  layer?: string;
  role?: string;
  via?: string;
  file?: string;
  drift?: string[];
  service?: string;
  members?: SystemMember[];
}

export interface SystemMember {
  id: string;
  name: string;
  kind: string;          // endpoint | method | property | field
  signature?: string;
  httpMethod?: string;
  route?: string;
  file?: string;
  line?: number;
}

export interface SystemEdge {
  id: string;
  source: string;
  target: string;
  kind: string;          // http-call | rw-db | uses-bus | publishes | subscribes | ...
  via?: string;          // compose | config | code | unknown (провенанс)
  confidence?: number;   // 0..1 (уверенность по источнику)
}

export interface Problem {
  id: string;
  severity: string;      // critical | warning | coverage | info
  kind: string;          // shared-db | service-cycle | dangling-event | layer-violation | drift | ...
  title: string;
  location: string;      // nodeId (точное) ИЛИ namespace-префикс {system} (дрейф/слои — per-service)
  via?: string;
  fix?: string;
  category?: string;     // architecture (реальные проблемы) | convention (дрейф) — линза; null → architecture
}

export interface SystemCoverage {
  marker: number;
  heuristic: number;
  unknown: number;
  total: number;
}

export interface SystemGraph {
  project: string;
  nodes: SystemNode[];
  edges: SystemEdge[];
  coverage: SystemCoverage;
  drift: string[];
  health?: string;       // ok | warning | critical (rollup системы)
  problems?: Problem[];
}

export interface SystemQuery {
  path: string;
  /** линза «дрейф от свода» (convention) — тяжелее: разбор кода per-service */
  drift?: boolean;
  /** линза «нарушение направления слоёв» (architecture) — тоже разбор кода per-service */
  layers?: boolean;
}

// Код-линзы гоняют Roslyn по каждому сервису — дольше структурного скана; но не бесконечно.
const TIMEOUT_MS = 120_000;

async function fetchWithTimeout(url: string): Promise<Response> {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), TIMEOUT_MS);
  try {
    return await fetch(url, { signal: ctrl.signal });
  } catch (e) {
    if (e instanceof DOMException && e.name === 'AbortError') {
      throw new Error(`Превышено время ожидания (${TIMEOUT_MS / 1000}с) — большой проект или сервис недоступен`);
    }
    throw e;
  } finally {
    clearTimeout(timer);
  }
}

/** GET /api/system (через сервис Map — единственная фронт-фейсинг точка, adr/0010). */
export async function fetchSystem(q: SystemQuery): Promise<SystemGraph> {
  const params = new URLSearchParams({ path: q.path });
  if (q.drift) params.set('drift', 'true');
  if (q.layers) params.set('layers', 'true');

  const res = await fetchWithTimeout(`/api/system?${params.toString()}`);
  if (!res.ok) {
    const body = await res.text();
    let message = body;
    try {
      const parsed = JSON.parse(body) as { error?: string };
      if (parsed?.error) message = parsed.error;
    } catch {
      /* тело не JSON — оставляем как есть */
    }
    throw new Error(message || `Ошибка системного графа (${res.status})`);
  }
  return res.json() as Promise<SystemGraph>;
}
