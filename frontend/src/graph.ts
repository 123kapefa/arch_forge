import { MarkerType, type Node, type Edge } from '@xyflow/react';
import { layout } from './layout';
import type { GraphDto } from './api';

export type Filters = { hideTests: boolean; hideIsolated: boolean };

/** Клиентская фильтрация вида — мгновенно, без запроса. Покрытие не трогаем: оно честное. */
export function filterGraph(g: GraphDto, f: Filters): GraphDto {
  let nodes = g.nodes;
  if (f.hideTests) nodes = nodes.filter((n) => n.layer.toLowerCase() !== 'tests');
  const ids = new Set(nodes.map((n) => n.id));
  let edges = g.edges.filter((e) => ids.has(e.source) && ids.has(e.target));
  if (f.hideIsolated) {
    const linked = new Set<string>();
    edges.forEach((e) => { linked.add(e.source); linked.add(e.target); });
    nodes = nodes.filter((n) => linked.has(n.id));
  }
  return { ...g, nodes, edges };
}

/** GraphDto → узлы/рёбра React Flow с раскладкой. */
export function toFlow(g: GraphDto | null): { nodes: Node[]; edges: Edge[] } {
  if (!g) return { nodes: [], edges: [] };
  const nodes: Node[] = g.nodes.map((n) => ({
    id: n.id,
    type: 'type',
    position: { x: 0, y: 0 },
    data: { name: n.name, kind: n.kind, role: n.role, layer: n.layer, via: n.via, drift: n.drift },
  }));
  const edges: Edge[] = g.edges.map((e) => ({
    id: e.id,
    source: e.source,
    target: e.target,
    label: e.kind,
    markerEnd: { type: MarkerType.ArrowClosed },
  }));
  return { nodes: layout(nodes, edges), edges };
}
