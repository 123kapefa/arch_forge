// Общий fetch с таймаутом — не зависаем на большом/недоступном сервисе (робастность P1-H).
// Единый источник для api.ts (Фаза 1) и system.ts (Фаза 1.5), таймаут — параметром.
export async function fetchWithTimeout(url: string, timeoutMs: number): Promise<Response> {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    return await fetch(url, { signal: ctrl.signal });
  } catch (e) {
    if (e instanceof DOMException && e.name === 'AbortError') {
      throw new Error(`Превышено время ожидания (${timeoutMs / 1000}с) — большой проект или сервис недоступен`);
    }
    throw e;
  } finally {
    clearTimeout(timer);
  }
}
