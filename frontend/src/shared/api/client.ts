// Тонкий клиент запросов: один fetch, единая обработка ошибок и отмены.
// Базовый URL — относительный `/api` (в dev его проксирует Vite на сервис Map, в проде — прокси/сам Map).

export const API_BASE = '/api';

/** Ошибка API: статус + сообщение сервера (если тело — JSON `{ error }`), иначе текст/код. */
export class ApiError extends Error {
  readonly status: number;
  constructor(status: number, message: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

/** Отмена запроса (смена экрана/параметров) — не ошибка для показа. */
export function isAbortError(e: unknown): boolean {
  return e instanceof DOMException && e.name === 'AbortError';
}

export interface RequestOptions {
  /** Внешняя отмена — TanStack Query передаёт свой signal. */
  signal?: AbortSignal;
  /** Таймаут, мс. По умолчанию 120 с: код-линзы гоняют Roslyn по сервисам. */
  timeoutMs?: number;
  query?: Record<string, string | number | boolean | undefined>;
}

const DEFAULT_TIMEOUT_MS = 120_000;

/** Значения query: `undefined` и `false` отбрасываются, `true` уходит строкой "true" (флаги бэкенда: `?drift=true`). */
function buildUrl(path: string, query: RequestOptions['query']): string {
  const params = new URLSearchParams();
  for (const [k, v] of Object.entries(query ?? {})) {
    if (v !== undefined && v !== false) params.set(k, String(v));
  }
  const qs = params.toString();
  return `${API_BASE}${path}${qs ? `?${qs}` : ''}`;
}

async function errorMessage(res: Response): Promise<string> {
  const body = await res.text();
  try {
    const parsed = JSON.parse(body) as { error?: string };
    if (parsed?.error) return parsed.error;
  } catch {
    /* тело не JSON — берём как есть */
  }
  return body || `Ошибка запроса (${res.status})`;
}

/** GET `/api{path}` → JSON. Внешняя отмена пробрасывается как AbortError, таймаут — как ApiError(408). */
export async function getJson<T>(path: string, opts: RequestOptions = {}): Promise<T> {
  const { signal, timeoutMs = DEFAULT_TIMEOUT_MS, query } = opts;
  const ctrl = new AbortController();
  const onAbort = () => ctrl.abort();
  if (signal?.aborted) ctrl.abort();
  else signal?.addEventListener('abort', onAbort, { once: true });
  const timer = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    const res = await fetch(buildUrl(path, query), { signal: ctrl.signal, headers: { Accept: 'application/json' } });
    if (!res.ok) throw new ApiError(res.status, await errorMessage(res));
    return (await res.json()) as T;
  } catch (e) {
    if (signal?.aborted) throw e;
    if (isAbortError(e)) throw new ApiError(408, `Превышено время ожидания (${timeoutMs / 1000}с) — большой проект, анализ ещё идёт`);
    // fetch сам кидает TypeError при обрыве соединения / недоступном сервисе
    if (e instanceof TypeError) throw new ApiError(0, 'Нет связи с сервисом — проверьте, что Map запущен');
    throw e;
  } finally {
    clearTimeout(timer);
    signal?.removeEventListener('abort', onAbort);
  }
}
