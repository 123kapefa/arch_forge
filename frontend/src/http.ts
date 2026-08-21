// Общий fetch с таймаутом — не зависаем на большом/недоступном сервисе (робастность P1-H).
// Единый источник для api.ts (Фаза 1) и system.ts (Фаза 1.5), таймаут — параметром.
// signal — внешняя отмена (сменили проект/флаги/дрилл): рвём запрос, чтобы бэкенд не жёг Roslyn впустую (P1.5-H).
export async function fetchWithTimeout(url: string, timeoutMs: number, signal?: AbortSignal): Promise<Response> {
  const ctrl = new AbortController();
  const onExternalAbort = () => ctrl.abort();
  if (signal) {
    if (signal.aborted) ctrl.abort();
    else signal.addEventListener('abort', onExternalAbort, { once: true });
  }
  const timer = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    return await fetch(url, { signal: ctrl.signal });
  } catch (e) {
    // внешняя отмена (вытеснение) — НЕ таймаут: пробрасываем AbortError, вызывающий его проглотит
    if (signal?.aborted) throw e;
    if (e instanceof DOMException && e.name === 'AbortError') {
      throw new Error(`Превышено время ожидания (${timeoutMs / 1000}с) — большой проект или сервис недоступен`);
    }
    throw e;
  } finally {
    clearTimeout(timer);
    signal?.removeEventListener('abort', onExternalAbort);
  }
}

/** Отмена по AbortSignal (вытеснение/размонтирование) — не ошибка для показа, вызывающий её проглатывает. */
export function isAbortError(e: unknown): boolean {
  return e instanceof DOMException && e.name === 'AbortError';
}
