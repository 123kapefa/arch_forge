// Чистая логика над системным графом (для оболочки L0/L1): линзы, привязка проблем к узлам, счётчики.
// Без React/DOM — тестируемо. Визуальная раскладка/рендер — на стороне оболочки.

import type { Problem, SystemGraph, SystemNode } from './system';

export type Lens = 'architecture' | 'convention';

// Проблемы без category считаем architecture (обратная совместимость со старым JSON).
export function categoryOf(p: Problem): Lens {
  return p.category === 'convention' ? 'convention' : 'architecture';
}

/** Проблемы выбранной линзы: architecture — реальные арх-проблемы; convention — дрейф от свода. */
export function problemsByLens(problems: Problem[] | undefined, lens: Lens): Problem[] {
  return (problems ?? []).filter((p) => categoryOf(p) === lens);
}

// Порядок суровости (меньше = суровее) — как в бэкенде: critical < warning < coverage < info.
const SEVERITY_ORDER: Record<string, number> = { critical: 0, warning: 1, coverage: 2, info: 3 };

function rank(severity: string): number {
  // Object.hasOwn, не `in` — иначе severity вроде 'constructor'/'toString' ложно матчит Object.prototype.
  return Object.hasOwn(SEVERITY_ORDER, severity) ? SEVERITY_ORDER[severity] : SEVERITY_ORDER.info;
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
    if (Object.hasOwn(counts, p.severity)) counts[p.severity] += 1; // не `in` — защита от prototype-ключей
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
  // префикс-матч только для непустого namespace (иначе '' дал бы startsWith('/') — ложные совпадения)
  return (problems ?? []).filter(
    (p) => p.location === namespace || (namespace !== '' && p.location.startsWith(`${namespace}/`)),
  );
}

/** Узел-владелец файла: самый специфичный (самый ДЛИННЫЙ root-префикс). null — файл не под чьим-то root.
 *  Длиннейший, а не первый: широкий root (напр. gateway = корень backend) — префикс файлов всех сервисов. */
export function ownerByRoot(nodes: SystemNode[], file: string | undefined): SystemNode | null {
  if (!file) return null;
  let best: SystemNode | null = null;
  for (const n of nodes) {
    if (!n.root) continue;
    // совпадение по ГРАНИЦЕ сегмента: файл = root или начинается с root + '/' (иначе «…/Cat» ложно матчит «…/Catalog/…»)
    const match = file === n.root || file.startsWith(n.root.endsWith('/') ? n.root : n.root + '/');
    if (match && (best === null || n.root.length > (best.root?.length ?? 0))) best = n;
  }
  return best;
}

/** Сколько проблем активной линзы «попадает» в каждый узел — для подсветки L0 (#70).
 *  Дрейф локализован на системе (Location — не узел) → красим владельцев файлов-фактов, даже если Location
 *  случайно = id узла (напр. dto-mapping кладёт Location = первый сервис по discovery). Арх — по узлу-локации. */
export function lensHitsByNode(graph: SystemGraph | null | undefined, lens: Lens): Map<string, number> {
  const hits = new Map<string, number>();
  if (!graph) return hits;
  const ids = new Set(graph.nodes.map((n) => n.id));
  const add = (id: string, n = 1) => hits.set(id, (hits.get(id) ?? 0) + n);
  for (const p of graph.problems ?? []) {
    if (categoryOf(p) !== lens) continue;
    const items = p.items ?? [];
    if (lens === 'convention' && items.length > 0) {
      for (const it of items) { const svc = ownerByRoot(graph.nodes, it.file); if (svc) add(svc.id); }
    } else if (ids.has(p.location)) {
      add(p.location, Math.max(1, items.length));
    }
  }
  return hits;
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
  const root = scanRoot.replace(/[/\\]+$/, ''); // хвостовой разделитель (POSIX или Windows)
  return ns ? `${root}/${ns}` : root;
}
