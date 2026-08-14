// Контракт IR — зеркалит ArchForge.Contracts (System.Text.Json Web = camelCase).

export interface GraphDto {
  project: string;
  nodes: NodeDto[];
  edges: EdgeDto[];
  coverage: CoverageDto;
  drift: string[];
}

export interface NodeDto {
  id: string;
  name: string;
  kind: string;   // class | record | interface | enum | struct
  layer: string;  // Domain | Application | Api | ...
  role: string;   // стереотип: Aggregate | Handler | Endpoint | ...
  via: string;    // marker | heuristic | unknown
  file: string;
  drift: string[];
}

export interface EdgeDto {
  id: string;
  source: string;
  target: string;
  kind: string;   // implements | holds | uses | ...
}

export interface CoverageDto {
  marker: number;
  heuristic: number;
  unknown: number;
  total: number;
}

export interface MapQuery {
  path: string;
  layer?: string;
  focus?: string;
}

const TIMEOUT_MS = 60_000; // семантика на большом проекте небыстрая, но не бесконечная

/** fetch с таймаутом — не зависаем на большом/недоступном сервисе (робастность P1-H). */
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

/** GET /api/map (через сервис Map). */
export async function fetchMap(q: MapQuery): Promise<GraphDto> {
  const params = new URLSearchParams({ path: q.path });
  if (q.layer) params.set('layer', q.layer);
  if (q.focus) params.set('focus', q.focus);

  const res = await fetchWithTimeout(`/api/map?${params.toString()}`);
  if (!res.ok) {
    const body = await res.text();
    let message = body;
    try {
      const parsed = JSON.parse(body) as { error?: string };
      if (parsed?.error) message = parsed.error;
    } catch {
      /* тело не JSON — оставляем как есть */
    }
    throw new Error(message || `Ошибка Map (${res.status})`);
  }
  return res.json() as Promise<GraphDto>;
}

// Импорты (сторонние библиотеки) — «за кнопкой», не в графе.
export interface ImportsDto {
  project: string;
  packages: PackageDto[];
}
export interface PackageDto {
  name: string;
  versions: string[];
  usedBy: string[];
  versionConflict: boolean;
}

/** GET /api/imports (через сервис Map). */
export async function fetchImports(path: string): Promise<ImportsDto> {
  const res = await fetchWithTimeout(`/api/imports?path=${encodeURIComponent(path)}`);
  if (!res.ok) throw new Error(`Импорты ${res.status}: ${await res.text()}`);
  return res.json() as Promise<ImportsDto>;
}
