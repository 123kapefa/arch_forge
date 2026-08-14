import type { GraphDto } from './api';

// Ранг слоя чистой архитектуры (меньше = ближе к ядру). Зависимость должна идти ВНУТРЬ:
// ранг источника (кто зависит) ≥ ранга цели (от кого зависит). Иначе — нарушение.
const RANK: Record<string, number> = {
  domain: 0,
  contracts: 1, application: 1, core: 1, sharedkernel: 1,
  infrastructure: 2, persistence: 2,
  api: 3, web: 3, host: 3,
  tooling: 4,
};

export function layerRank(layer: string): number {
  return RANK[layer.toLowerCase()] ?? 1;
}

export interface Violation {
  id: string; // id ребра — для подсветки
  source: string;
  target: string;
  fromName: string;
  toName: string;
  fromLayer: string;
  toLayer: string;
}

/** Рёбра, нарушающие направление слоёв: источник (зависящий) — внутреннее цели (от кого зависит). tests игнорируем. */
export function layerViolations(g: GraphDto): Violation[] {
  const info = new Map(g.nodes.map((n) => [n.id, n]));
  const out: Violation[] = [];
  for (const e of g.edges) {
    const s = info.get(e.source);
    const t = info.get(e.target);
    if (!s || !t) continue;
    if (s.layer.toLowerCase() === 'tests' || t.layer.toLowerCase() === 'tests') continue;
    if (layerRank(s.layer) < layerRank(t.layer)) {
      out.push({
        id: e.id,
        source: e.source,
        target: e.target,
        fromName: s.name,
        toName: t.name,
        fromLayer: s.layer,
        toLayer: t.layer,
      });
    }
  }
  return out;
}
