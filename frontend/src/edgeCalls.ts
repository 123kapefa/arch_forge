// Вызовы между сервисами на ребре (E3.1): типы и чистая логика показа. Данные приходят от Analysis (EdgeDto.Calls).

/** Конкретный HTTP-вызов: зеркалит HttpCallDto. */
export interface HttpCall {
  method: string;
  route: string;
  via?: string;          // config | code (провенанс вызова)
  confidence?: number;   // 0..1 по виду резолва цели
  matched?: boolean;     // маршрут найден среди endpoint-ов цели; undefined — сопоставление не проводилось
  file?: string;
  line?: number;
  evidence?: string;     // нормализованная строка вызова без query
}

export type ConfidenceLevel = 'high' | 'medium' | 'low';

/** Уровень доверия по числу: типизированный/в той же системе — высокий, выведенный — средний, остальное — низкий. */
export function confidenceLevel(c: number | undefined): ConfidenceLevel {
  if (c == null) return 'medium';
  if (c >= 0.8) return 'high';
  if (c >= 0.5) return 'medium';
  return 'low';
}

export const CONFIDENCE_LABEL: Record<ConfidenceLevel, string> = { high: 'высокая', medium: 'средняя', low: 'низкая' };

/** Понятная подпись сопоставления с endpoint-ом цели. Не выдаём «найден», когда не проверяли. */
export function matchLabel(m: boolean | undefined): { text: string; tone: 'ok' | 'warn' | 'idle' } {
  if (m === true) return { text: 'endpoint найден', tone: 'ok' };
  if (m === false) return { text: 'endpoint не найден', tone: 'warn' };
  return { text: 'не проверялось', tone: 'idle' };
}

const callKey = (c: HttpCall) => `${c.method}|${c.route}|${c.file ?? ''}|${c.line ?? ''}`;

/** Склейка вызовов нескольких рёбер одной пары (рёбра одного вида сливаются в одну линию): без повторов. */
export function mergeCalls(a: HttpCall[] | undefined, b: HttpCall[] | undefined): HttpCall[] | undefined {
  if (!a && !b) return undefined;
  const seen = new Set<string>();
  const out: HttpCall[] = [];
  for (const c of [...(a ?? []), ...(b ?? [])]) {
    const k = callKey(c);
    if (seen.has(k)) continue;
    seen.add(k);
    out.push(c);
  }
  return out;
}

/** Порядок показа: по маршруту, затем по методу, затем по месту в коде. */
export function sortCalls(calls: readonly HttpCall[]): HttpCall[] {
  return [...calls].sort((x, y) =>
    x.route.localeCompare(y.route) || x.method.localeCompare(y.method)
    || (x.file ?? '').localeCompare(y.file ?? '') || (x.line ?? 0) - (y.line ?? 0));
}

/** Короткая сводка для заголовка: «3 вызова · 1 без найденного endpoint».
 *  `requested` — запрашивали ли вызовы: сервер не присылает поле, когда вызовов нет, поэтому «нет поля» значит «не нашли», только если просили. */
export function summarizeCalls(calls: readonly HttpCall[] | undefined, requested = true): string {
  if (!calls && !requested) return 'вызовы не запрашивались';
  if (!calls || calls.length === 0) return 'конкретных вызовов в коде не найдено';
  const unmatched = calls.filter((c) => c.matched === false).length;
  const word = callsWord(calls.length);
  return unmatched > 0 ? `${word} · ${unmatched} без найденного endpoint` : word;
}

function callsWord(n: number): string {
  const a = n % 100, b = n % 10;
  if (a >= 11 && a <= 14) return `${n} вызовов`;
  if (b === 1) return `${n} вызов`;
  if (b >= 2 && b <= 4) return `${n} вызова`;
  return `${n} вызовов`;
}

/** Хвост пути для узкой панели: последние `n` частей (имя файла важнее начала пути); полный путь — в подсказке. */
export function shortFile(file: string, n = 2): string {
  const parts = file.split(/[\\/]/).filter(Boolean);
  return parts.slice(-n).join('/');
}

/** Путь файла для показа: относительно корня разбираемого проекта, если он внутри. */
export function relativeFile(file: string | undefined, root: string): string | undefined {
  if (!file) return undefined;
  const r = root.replace(/[\\/]+$/, '');
  return r && file.startsWith(r + '/') ? file.slice(r.length + 1) : file;
}
