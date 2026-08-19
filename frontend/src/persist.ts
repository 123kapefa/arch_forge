// Персист UI-состояния между перезагрузками (#64/#65/#66) — тонкие обёртки над Web Storage
// с валидацией прочитанного. Вынесено из SystemView, чтобы покрыть чистые правила чтения тестами.
import type { SystemGraph } from './system';

// --- Вьюпорт доски (localStorage: переживает перезапуск приложения) ---

export type Viewport = { x: number; y: number; zoom: number };

// Ключ = ключ раскладки + ':vp'. Читаем только конечные числа (битый/усечённый JSON → null → fitView).
export function readViewport(layoutKey: string): Viewport | null {
  try {
    const raw = localStorage.getItem(layoutKey + ':vp');
    if (!raw) return null;
    const v = JSON.parse(raw) as Viewport;
    return Number.isFinite(v?.x) && Number.isFinite(v?.y) && Number.isFinite(v?.zoom) ? v : null;
  } catch { return null; }
}
export function writeViewport(layoutKey: string, v: Viewport): void {
  try { localStorage.setItem(layoutKey + ':vp', JSON.stringify(v)); } catch { /* quota */ }
}

// --- Скалярные настройки панелей (sessionStorage: живут до закрытия вкладки) ---

export function readBool(key: string, dflt: boolean): boolean {
  try { const v = sessionStorage.getItem(key); return v === null ? dflt : v === '1'; } catch { return dflt; }
}
// Возвращаем сохранённое число только если оно конечно и не меньше min (иначе dflt) — Infinity/NaN отсекаем.
export function readNum(key: string, dflt: number, min: number): number {
  try { const v = Number(sessionStorage.getItem(key)); return Number.isFinite(v) && v >= min ? v : dflt; } catch { return dflt; }
}
export const setSS = (key: string, val: string) => { try { sessionStorage.setItem(key, val); } catch { /* quota */ } };

// --- Кэш системного графа на перезагрузку (#64): sessionStorage, ключ = версия + флаги + путь ---

// Версия ключа инвалидирует кэш при смене формы контракта (вкладка пережила деплой) — как v3 у раскладки.
const GCACHE_V = 'v1';
const gcachePrefix = `af_gcache:${GCACHE_V}:`;
export const gcacheKey = (path: string, drift: boolean, layers: boolean) => `${gcachePrefix}${drift ? 1 : 0}${layers ? 1 : 0}:${path}`;

export function readGraphCache(path: string, drift: boolean, layers: boolean): SystemGraph | null {
  try {
    const s = sessionStorage.getItem(gcacheKey(path, drift, layers));
    if (!s) return null;
    const g = JSON.parse(s) as SystemGraph;
    // валидируем форму: устаревший/битый кэш не отправляем в рендер (иначе TypeError на g.nodes и т.п.)
    return g && typeof g.project === 'string' && Array.isArray(g.nodes) && Array.isArray(g.edges) ? g : null;
  } catch { return null; }
}
export function writeGraphCache(path: string, drift: boolean, layers: boolean, g: SystemGraph): void {
  // на путь держим один свежий кэш: чистим прежние комбинации флагов того же проекта
  // (иначе записи копятся без вытеснения → рост sessionStorage до QuotaExceeded).
  const keep = gcacheKey(path, drift, layers);
  try {
    for (let i = sessionStorage.length - 1; i >= 0; i--) {
      const k = sessionStorage.key(i);
      if (k && k.startsWith(gcachePrefix) && k.endsWith(`:${path}`) && k !== keep) sessionStorage.removeItem(k);
    }
  } catch { /* ignore */ }
  setSS(keep, JSON.stringify(g));
}
