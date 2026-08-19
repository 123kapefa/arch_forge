import type { GraphDto } from './api';

export interface LayerAgg { layer: string; count: number; }
export interface LayerLink { source: string; target: string; weight: number; }

/** Агрегат по слоям (обзор): узел = слой (число типов), ребро = межслойная зависимость (вес). */
export function aggregateLayers(g: GraphDto): { layers: LayerAgg[]; links: LayerLink[] } {
  const count = new Map<string, number>();
  for (const n of g.nodes) count.set(n.layer, (count.get(n.layer) ?? 0) + 1);

  const layerOf = new Map(g.nodes.map((n) => [n.id, n.layer] as const));
  const weights = new Map<string, Map<string, number>>(); // source -> (target -> weight)
  for (const e of g.edges) {
    const s = layerOf.get(e.source);
    const t = layerOf.get(e.target);
    if (!s || !t || s === t) continue; // только межслойные связи
    const row = weights.get(s) ?? new Map<string, number>();
    row.set(t, (row.get(t) ?? 0) + 1);
    weights.set(s, row);
  }

  const layers = [...count.entries()]
    .map(([layer, c]) => ({ layer, count: c }))
    .sort((a, b) => a.layer.localeCompare(b.layer));
  const links: LayerLink[] = [];
  for (const [source, row] of weights) {
    for (const [target, weight] of row) links.push({ source, target, weight });
  }
  return { layers, links };
}
