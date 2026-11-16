// Чистая модель «Что не так»: ранжирование находок и доказательство. Без React/DOM.

import type { Problem, SystemGraph } from '../../../shared/api';
import { ruleLabel } from './ruleLabels';

export type Severity = 'critical' | 'warning' | 'coverage' | 'info' | 'unknown';

const SEV_RANK: Record<Severity, number> = { critical: 0, warning: 1, coverage: 2, info: 3, unknown: 4 };

export function severityOf(p: Pick<Problem, 'severity'>): Severity {
  return Object.hasOwn(SEV_RANK, p.severity) && p.severity !== 'unknown' ? (p.severity as Severity) : 'unknown';
}

/** Дрейф от свода — отдельная категория: сперва архитектурные находки. */
export const isConvention = (p: Problem) => p.category === 'convention';

/** Серьёзность → уверенность (убывание) → архитектурные раньше дрейфа → название.
 *  Отсутствие `confidence` в контракте = «не применимо» (shared-db, открытые порты из compose — факт, а не оценка),
 *  поэтому оно нейтрально-высокое: считается как 1 и не опускает находку ниже оценочных с малой уверенностью. */
export function rankFindings(problems: readonly Problem[]): Problem[] {
  return [...problems].sort(
    (a, b) =>
      SEV_RANK[severityOf(a)] - SEV_RANK[severityOf(b)] ||
      (b.confidence ?? 1) - (a.confidence ?? 1) ||
      Number(isConvention(a)) - Number(isConvention(b)) ||
      a.title.localeCompare(b.title, 'ru'),
  );
}

export interface Evidence {
  /** `файл:строка` первого факта с путём; иначе пусто. */
  location: string | null;
  /** Сколько ещё мест (файлов) у находки помимо показанного. */
  extraPlaces: number;
  /** Правило/вид находки (kind). */
  rule: string;
  /** Человеческая подпись правила; null — вид незнаком, показываем сырой kind. */
  ruleLabel: string | null;
  /** Уверенность в процентах; null — бэк её не указал. */
  confidencePct: number | null;
  /** Откуда известно (via), если указано. */
  source: string | null;
}

/** Путь относительно корня проекта (если файл внутри него), иначе как есть. */
export function relativeTo(file: string, root?: string | null): string {
  if (!root) return file;
  const base = root.replace(/[\\/]+$/, '');
  return file.startsWith(`${base}/`) || file.startsWith(`${base}\\`) ? file.slice(base.length + 1) : file;
}

export function evidenceOf(p: Problem, root?: string | null): Evidence {
  const item = p.items?.find((i) => i.file);
  const file = item?.file ? relativeTo(item.file, root) : null;
  const location = file ? (item?.line ? `${file}:${item.line}` : file) : null;
  const extraPlaces = Math.max(0, (p.items ?? []).filter((i) => i.file).length - 1);
  const confidencePct = typeof p.confidence === 'number' ? Math.round(p.confidence * 100) : null;
  return { location, extraPlaces, rule: p.kind, ruleLabel: ruleLabel(p.kind), confidencePct, source: p.via ?? null };
}

export interface SeverityCounts { critical: number; warning: number; coverage: number; info: number; total: number }

export function countBySeverity(problems: readonly Problem[]): SeverityCounts {
  const c: SeverityCounts = { critical: 0, warning: 0, coverage: 0, info: 0, total: problems.length };
  for (const p of problems) {
    const s = severityOf(p);
    if (s !== 'unknown') c[s] += 1;
  }
  return c;
}

/** Находки для экрана: все, по рангу. */
export const findingsOf = (g: SystemGraph) => rankFindings(g.problems ?? []);
