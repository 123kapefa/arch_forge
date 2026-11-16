// Чистая логика подсветки наведения: какие рёбра и узлы считаются «связанными» с наведённым узлом/ребром.
// Отдельно от React — тестируется без DOM; смежность строится один раз на набор рёбер (hover её не пересчитывает).

export interface EdgeRef { id: string; source: string; target: string }

export interface Adjacency {
  /** узел → id инцидентных рёбер (входящие и исходящие) */
  edgesOf: Map<string, string[]>;
  /** узел → соседи (по любому ребру, без самого узла) */
  neighborsOf: Map<string, Set<string>>;
  byId: Map<string, EdgeRef>;
}

export function buildAdjacency(edges: readonly EdgeRef[]): Adjacency {
  const edgesOf = new Map<string, string[]>();
  const neighborsOf = new Map<string, Set<string>>();
  const byId = new Map<string, EdgeRef>();
  const push = <T,>(m: Map<string, T[]>, k: string, v: T) => { const a = m.get(k); if (a) a.push(v); else m.set(k, [v]); };
  const nb = (a: string, b: string) => { const s = neighborsOf.get(a); if (s) s.add(b); else neighborsOf.set(a, new Set([b])); };
  for (const e of edges) {
    byId.set(e.id, e);
    push(edgesOf, e.source, e.id);
    if (e.target !== e.source) { push(edgesOf, e.target, e.id); nb(e.source, e.target); nb(e.target, e.source); }
  }
  return { edgesOf, neighborsOf, byId };
}

export interface Highlight {
  edgeIds: ReadonlySet<string>;
  /** узлы, которые подсвечиваем «мягко» (соседи наведённого узла) */
  nodeIds: ReadonlySet<string>;
  /** главные узлы: наведённый узел или концы наведённого ребра */
  mainIds: ReadonlySet<string>;
}

const EMPTY: Highlight = { edgeIds: new Set(), nodeIds: new Set(), mainIds: new Set() };

/** Что подсветить при наведении: узел → все его рёбра + соседи; ребро → оно само + два его конца; ничего → пусто. */
export function highlightFor(adj: Adjacency, target: { nodeId?: string | null; edgeId?: string | null }): Highlight {
  if (target.edgeId) {
    const e = adj.byId.get(target.edgeId);
    if (!e) return EMPTY;
    return { edgeIds: new Set([e.id]), nodeIds: new Set(), mainIds: new Set([e.source, e.target]) };
  }
  if (target.nodeId) {
    const id = target.nodeId;
    return {
      edgeIds: new Set(adj.edgesOf.get(id) ?? []),
      nodeIds: new Set(adj.neighborsOf.get(id) ?? []),
      mainIds: new Set([id]),
    };
  }
  return EMPTY;
}

/** Подсказка к ребру: вид связи и откуда известно (провенанс #190: via/origin), если есть. */
export function edgeTooltip(d: { kind?: string; via?: string; origin?: { evidence?: string; file?: string; line?: number }; confidence?: number } | undefined): string {
  if (!d?.kind) return '';
  const parts = [d.kind];
  if (d.via) parts.push(`источник: ${d.via}`);
  if (d.origin?.evidence) parts.push(d.origin.evidence);
  if (d.origin?.file) parts.push(`${d.origin.file}${d.origin.line ? ':' + d.origin.line : ''}`);
  if (d.confidence != null && d.confidence < 1) parts.push(`уверенность ${d.confidence.toFixed(2)}`);
  return parts.join(' · ');
}
