// Чистая логика над системным графом (для оболочки L0/L1): линзы, привязка проблем к узлам, счётчики.
// Без React/DOM — тестируемо. Визуальная раскладка/рендер — на стороне оболочки.

import type { Problem, SystemGraph, SystemNode } from './system';

export type Lens = 'architecture' | 'convention';

// Проблемы без category считаем architecture (обратная совместимость со старым JSON).
function categoryOf(p: Problem): Lens {
  return p.category === 'convention' ? 'convention' : 'architecture';
}

/** Проблемы выбранной линзы: architecture — реальные арх-проблемы; convention — дрейф от свода. */
export function problemsByLens(problems: Problem[] | undefined, lens: Lens): Problem[] {
  return (problems ?? []).filter((p) => categoryOf(p) === lens);
}

// Порядок суровости (меньше = суровее) — как в бэкенде: critical < warning < coverage < info.
const SEVERITY_ORDER: Record<string, number> = { critical: 0, warning: 1, coverage: 2, info: 3 };

function rank(severity: string): number {
  return severity in SEVERITY_ORDER ? SEVERITY_ORDER[severity] : SEVERITY_ORDER.info;
}

/** Самая суровая проблема набора (для подсветки узла/группы); null — если пусто. */
export function worstSeverity(problems: Problem[] | undefined): string | null {
  let worst: string | null = null;
  for (const p of problems ?? []) {
    if (worst === null || rank(p.severity) < rank(worst)) worst = p.severity;
  }
  return worst;
}

/** Счётчики по важности — для бейджа панели проблем. */
export function severityCounts(problems: Problem[] | undefined): Record<string, number> {
  const counts: Record<string, number> = { critical: 0, warning: 0, coverage: 0, info: 0 };
  for (const p of problems ?? []) {
    if (p.severity in counts) counts[p.severity] += 1;
  }
  return counts;
}

/** Проблемы, чей `location` точно совпадает с id узла (node-located: shared-db, service-cycle, dangling…). */
export function problemsAt(problems: Problem[] | undefined, nodeId: string): Problem[] {
  return (problems ?? []).filter((p) => p.location === nodeId);
}

/**
 * Проблемы в пределах namespace сервиса: `location` = сам namespace ИЛИ префикс id узла (`{system}/…`).
 * Ловит per-service проблемы (дрейф, нарушения слоёв) для роллапа на сервис/группу.
 */
export function problemsUnder(problems: Problem[] | undefined, namespace: string): Problem[] {
  return (problems ?? []).filter(
    (p) => p.location === namespace || p.location.startsWith(`${namespace}/`),
  );
}

/** Узлы системного графа, сгруппированные по виду (service/datastore/topic/external) — для L0-легенды. */
export function nodesByKind(graph: SystemGraph): Record<string, SystemNode[]> {
  const by: Record<string, SystemNode[]> = {};
  for (const n of graph.nodes) (by[n.kind] ??= []).push(n);
  return by;
}

/** Namespace узла — префикс id до последнего `/` ({system}); пусто, если id без `/`. */
export function namespaceOf(nodeId: string): string {
  const slash = nodeId.lastIndexOf('/');
  return slash > 0 ? nodeId.slice(0, slash) : '';
}

/**
 * Абсолютный путь сервиса для drill L0→L1 (граф типов через `/api/map`): корень скана + namespace узла.
 * Системный граф даёт узлы с id `{system}/{svc}`, а `/api/map` ждёт путь-папку — собираем его из корня.
 */
export function serviceRootPath(scanRoot: string, node: SystemNode): string {
  const ns = namespaceOf(node.id);
  const root = scanRoot.replace(/\/+$/, '');
  return ns ? `${root}/${ns}` : root;
}
