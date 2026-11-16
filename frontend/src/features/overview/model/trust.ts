// Чистая модель «Насколько верить»: доли покрытия и честность вывода «чисто». Без React/DOM.

import type { SystemCoverage, SystemGraph } from '../../../shared/api';

export interface CoverageShare { marker: number; heuristic: number; unknown: number; total: number }

/** Доли в %. Без разбивки по источнику (структурный скан: marker+heuristic+unknown = 0) делить не на что → null. */
export function shareOf(c: SystemCoverage | undefined): CoverageShare | null {
  if (!c || c.total <= 0) return null;
  if (c.marker + c.heuristic + c.unknown <= 0) return null;
  const pct = (n: number) => Math.round((n / c.total) * 100);
  return { marker: pct(c.marker), heuristic: pct(c.heuristic), unknown: pct(c.unknown), total: c.total };
}

export interface ServiceCoverageRow extends CoverageShare { id: string; name: string }

/** По сервисам; сперва с наибольшей долей неизвестного — туда стоит смотреть. Без разбивки — опускаем. */
export function serviceRows(g: SystemGraph): ServiceCoverageRow[] {
  const rows: ServiceCoverageRow[] = [];
  for (const s of g.services ?? []) {
    const share = shareOf(s.coverage);
    if (share) rows.push({ id: s.id, name: s.name, ...share });
  }
  return rows.sort((a, b) => b.unknown - a.unknown || a.name.localeCompare(b.name, 'ru'));
}

export type TrustLevel = 'full' | 'partial' | 'unknown';

export interface Trust {
  level: TrustLevel;
  overall: CoverageShare | null;
  rows: ServiceCoverageRow[];
  degradations: string[];
  /** Находок нет — но «чисто» не равно «проблем нет». */
  cleanIsNotSafe: boolean;
}

/** partial — есть деградации; unknown — нечем оценить покрытие; full — деградаций нет и доли известны. */
export function trustOf(g: SystemGraph): Trust {
  const degradations = (g.degradations ?? []).filter((d) => d.trim().length > 0);
  const overall = shareOf(g.coverage);
  const level: TrustLevel = degradations.length > 0 ? 'partial' : overall ? 'full' : 'unknown';
  const noFindings = (g.problems ?? []).length === 0;
  return { level, overall, rows: serviceRows(g), degradations, cleanIsNotSafe: noFindings && level !== 'full' };
}
