import dagre from '@dagrejs/dagre';
import { Position, type Node, type Edge } from '@xyflow/react';

const NODE_W = 190;
const NODE_H = 74;

/**
 * Раскладка карты:
 *  - связный подграф — dagre слева-направо (поток связей как в Blueprints);
 *  - узлы-одиночки (без рёбер) — аккуратной сеткой под ним, сгруппированы по слою.
 * Так «столб» из несвязанных типов превращается в компактную решётку.
 */
export function layout(nodes: Node[], edges: Edge[], dir: 'LR' | 'TB' = 'LR'): Node[] {
  const degree = new Map<string, number>();
  for (const e of edges) {
    degree.set(e.source, (degree.get(e.source) ?? 0) + 1);
    degree.set(e.target, (degree.get(e.target) ?? 0) + 1);
  }
  const connected = nodes.filter((n) => degree.get(n.id));
  const isolated = nodes.filter((n) => !degree.get(n.id));

  const horizontal = dir === 'LR';
  const handles = {
    targetPosition: horizontal ? Position.Left : Position.Top,
    sourcePosition: horizontal ? Position.Right : Position.Bottom,
  };

  // --- связный подграф через dagre ---
  const g = new dagre.graphlib.Graph();
  g.setDefaultEdgeLabel(() => ({}));
  g.setGraph({ rankdir: dir, nodesep: 36, ranksep: 90 });
  connected.forEach((n) => g.setNode(n.id, { width: NODE_W, height: NODE_H }));
  edges.forEach((e) => g.setEdge(e.source, e.target));
  dagre.layout(g);

  let minX = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  const placedConnected = connected.map((n) => {
    const p = g.node(n.id);
    const x = p.x - NODE_W / 2;
    const y = p.y - NODE_H / 2;
    minX = Math.min(minX, x);
    maxX = Math.max(maxX, x + NODE_W);
    maxY = Math.max(maxY, y + NODE_H);
    return { ...n, position: { x, y }, ...handles };
  });

  if (isolated.length === 0) return placedConnected;

  // --- одиночки: сетка под связным графом, сортировка по слою+имени ---
  const baseX = Number.isFinite(minX) ? minX : 0;
  const spanX = Number.isFinite(maxX) && Number.isFinite(minX) ? maxX - minX : 0;
  const GAP_X = NODE_W + 26;
  const GAP_Y = NODE_H + 22;
  const cols = Math.max(3, Math.floor(Math.max(spanX, GAP_X * 5) / GAP_X));
  const startY = placedConnected.length ? maxY + 70 : 0;

  const layerOf = (n: Node) => String((n.data as { layer?: string }).layer ?? '');
  const sortedIso = [...isolated].sort(
    (a, b) => layerOf(a).localeCompare(layerOf(b)) || a.id.localeCompare(b.id),
  );

  const placedIsolated = sortedIso.map((n, i) => ({
    ...n,
    position: { x: baseX + (i % cols) * GAP_X, y: startY + Math.floor(i / cols) * GAP_Y },
    ...handles,
  }));

  return [...placedConnected, ...placedIsolated];
}
