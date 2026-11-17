// Чистая модель «Насколько верить»: доли покрытия и честность вывода «чисто». Без React/DOM.

import type { SystemCoverage, SystemGraph } from '../../../shared/api';

export interface CoverageShare {
  marker: number;
  heuristic: number;
  unknown: number;
  /** Число типов по данным бэка (`total`). */
  total: number;
  /** marker+heuristic+unknown; доли считаются от неё. */
  sum: number;
  /** `total` и сумма источников расходятся — это показывается, а не скрывается. */
  mismatch: boolean;
  /** Ненулевая доля, которая округлилась до 0: показываем «<1%». */
  tiny: { marker: boolean; heuristic: boolean; unknown: boolean };
}

/** Доли в целых % от суммы источников методом наибольших остатков (всегда ровно 100).
 *  Без разбивки по источнику (структурный скан: сумма = 0) делить не на что → null. */
export function shareOf(c: SystemCoverage | undefined): CoverageShare | null {
  if (!c) return null;
  const parts = [c.marker, c.heuristic, c.unknown].map((n) => Math.max(0, n));
  const sum = parts[0] + parts[1] + parts[2];
  if (sum <= 0) return null;
  const raw = parts.map((n) => (n / sum) * 100);
  const floor = raw.map(Math.floor);
  let left = 100 - floor.reduce((x, y) => x + y, 0);
  [...raw.keys()]
    .sort((i, j) => raw[j] - floor[j] - (raw[i] - floor[i]))
    .forEach((i) => { if (left > 0) { floor[i] += 1; left -= 1; } });
  const tiny = (i: number) => parts[i] > 0 && raw[i] < 0.5;
  return {
    marker: floor[0], heuristic: floor[1], unknown: floor[2], total: c.total, sum, mismatch: sum !== c.total,
    tiny: { marker: tiny(0), heuristic: tiny(1), unknown: tiny(2) },
  };
}

/** Подпись доли: «<1%» для малой ненулевой. */
export const pctLabel = (s: CoverageShare, k: 'marker' | 'heuristic' | 'unknown') => (s.tiny[k] ? '<1%' : `${s[k]}%`);

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

/** measured — деградаций нет и доли известны (всё равно только по разобранным сервисам); partial — есть деградации. */
export type TrustLevel = 'measured' | 'partial' | 'unknown';

export interface Trust {
  level: TrustLevel;
  overall: CoverageShare | null;
  rows: ServiceCoverageRow[];
  degradations: string[];
  /** Сервисов с покрытием / сервисов в системе; null — нет данных по сервисам. Неразобранные в сумму не входят. */
  servicesCovered: { covered: number; total: number } | null;
  /** Находок нет — но «чисто» не равно «проблем нет». */
  cleanIsNotSafe: boolean;
}

/** partial — есть деградации; unknown — нечем оценить покрытие; full — деградаций нет и доли известны. */
export function trustOf(g: SystemGraph): Trust {
  const degradations = (g.degradations ?? []).filter((d) => d.trim().length > 0);
  const overall = shareOf(g.coverage);
  const rows = serviceRows(g);
  const total = g.nodes.filter((n) => n.kind === 'service').length;
  // covered не бывает больше total (ids сервисов и узлов могут разойтись)
  const servicesCovered = g.services && total > 0 ? { covered: Math.min(rows.length, total), total } : null;
  const unparsedServices = servicesCovered !== null && servicesCovered.covered < servicesCovered.total;
  // неразобранные сервисы в доли не входят: это та же деградация, что и явные из бэка
  const level: TrustLevel = degradations.length > 0 || unparsedServices ? 'partial' : overall ? 'measured' : 'unknown';
  const noFindings = (g.problems ?? []).length === 0;
  return { level, overall, rows, degradations, servicesCovered, cleanIsNotSafe: noFindings && level !== 'measured' };
}
