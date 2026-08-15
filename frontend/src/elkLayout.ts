// Авто-раскладка графа через ELK (layered + ортогональная маршрутизация рёбер с обходом плашек).
// Отдаёт узлы с позициями (+ data.elk = дефолтная позиция) и рёбра с data.points = маршрут (обход блоков).
import ELK from 'elkjs/lib/elk.bundled.js';
import type { Edge, Node } from '@xyflow/react';
import { isInfra, type SystemNodeData } from './systemFlow';

const elk = new ELK();

// Слоёная раскладка сверху вниз, ортогональные рёбра, зазоры — чтобы линии шли в «коридорах» между плашками.
// Coffman-Graham с ограничением ширины слоя (layerBound) переносит лишние узлы в новые ряды —
// иначе десятки «листьев» встают в один ряд и схема получается «простынёй» (плохо под 16:10).
// Подобрано экспериментом на product_site: bound=8 → соотношение ~1.4 (вместо ~7.7).
const LAYOUT_OPTIONS: Record<string, string> = {
  'elk.algorithm': 'layered',
  'elk.direction': 'DOWN',
  'elk.edgeRouting': 'ORTHOGONAL',
  'elk.layered.layering.strategy': 'COFFMAN_GRAHAM',
  'elk.layered.layering.coffmanGraham.layerBound': '8',
  'elk.layered.spacing.nodeNodeBetweenLayers': '90',
  'elk.spacing.nodeNode': '55',
  'elk.spacing.edgeNode': '24',
  'elk.spacing.edgeEdge': '14',
  'elk.layered.spacing.edgeNodeBetweenLayers': '24',
  'elk.layered.nodePlacement.strategy': 'NETWORK_SIMPLEX',
};

export interface RoutePoint { x: number; y: number; }

// Оценка габаритов плашки (нужна ELK для обхода) — по типу и числу слоёв, как рендерит SystemNodeView.
function nodeSize(n: Node): { width: number; height: number } {
  const d = (n.data as SystemNodeData).node;
  const layers = d.layers?.length ?? 0;
  if (!isInfra(d.kind) && layers > 0) return { width: 236, height: 44 + layers * 22 + 30 };
  return { width: 190, height: 54 };
}

export async function layoutElk(nodes: Node[], edges: Edge[]): Promise<{ nodes: Node[]; edges: Edge[] }> {
  if (nodes.length === 0) return { nodes, edges };

  const graph = {
    id: 'root',
    layoutOptions: LAYOUT_OPTIONS,
    children: nodes.map((n) => ({ id: n.id, ...nodeSize(n) })),
    edges: edges.map((e) => ({ id: e.id, sources: [e.source], targets: [e.target] })),
  };

  const res = (await elk.layout(graph)) as unknown as {
    children?: { id: string; x?: number; y?: number }[];
    edges?: { id: string; sections?: { startPoint: RoutePoint; bendPoints?: RoutePoint[]; endPoint: RoutePoint }[] }[];
  };

  const pos = new Map<string, RoutePoint>();
  for (const c of res.children ?? []) pos.set(c.id, { x: c.x ?? 0, y: c.y ?? 0 });

  const routes = new Map<string, RoutePoint[]>();
  for (const e of res.edges ?? []) {
    const s = e.sections?.[0];
    if (!s) continue;
    routes.set(e.id, [s.startPoint, ...(s.bendPoints ?? []), s.endPoint]);
  }

  const laidNodes = nodes.map((n) => {
    const p = pos.get(n.id) ?? { x: 0, y: 0 };
    return { ...n, position: p, data: { ...n.data, elk: p } };
  });
  const laidEdges = edges.map((e) => ({ ...e, data: { ...e.data, points: routes.get(e.id) } }));
  return { nodes: laidNodes, edges: laidEdges };
}
