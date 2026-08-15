// Авто-раскладка графа через ELK с КЛАСТЕРИЗАЦИЕЙ по микросервисам:
// каждый core-сервис (со слоями) + его спутники (БД, миграции, экспортеры, ${X_DB_CONNECTION}) — в одной группе,
// поэтому лежат рядом, а не размазаны. Группы + gateway/общая инфра раскладываются слоями (gateway сверху),
// рёбра — ортогональные с обходом. Наружу отдаём ПЛОСКИЕ узлы (абсолютные позиции) + маршруты рёбер.
import ELK from 'elkjs/lib/elk.bundled.js';
import type { Edge, Node } from '@xyflow/react';
import { isInfra, type SystemNodeData } from './systemFlow';

const elk = new ELK();

// Раскладка групп и gateway/инфра: слои сверху вниз, ортогональные рёбра, Coffman-Graham (перенос в ряды под 16:10).
const ROOT_OPTIONS: Record<string, string> = {
  'elk.algorithm': 'layered',
  'elk.direction': 'DOWN',
  'elk.edgeRouting': 'ORTHOGONAL',
  'elk.hierarchyHandling': 'INCLUDE_CHILDREN',
  'elk.layered.layering.strategy': 'COFFMAN_GRAHAM',
  'elk.layered.layering.coffmanGraham.layerBound': '3',
  'elk.layered.spacing.nodeNodeBetweenLayers': '70',
  'elk.spacing.nodeNode': '45',
  'elk.spacing.edgeNode': '24',
  'elk.spacing.edgeEdge': '14',
};
// Внутренняя раскладка кластера — компактно, сервис сверху → БД/миграции ниже.
const INNER_OPTIONS: Record<string, string> = {
  'elk.algorithm': 'layered',
  'elk.direction': 'DOWN',
  'elk.spacing.nodeNode': '26',
  'elk.layered.spacing.nodeNodeBetweenLayers': '34',
  'elk.padding': '[top=10,left=10,bottom=10,right=10]',
};

export interface RoutePoint { x: number; y: number; }

// Оценка габаритов плашки (нужна ELK для обхода) — по типу и числу слоёв, как рендерит SystemNodeView.
function nodeSize(n: Node): { width: number; height: number } {
  const d = (n.data as SystemNodeData).node;
  const layers = d.layers?.length ?? 0;
  if (!isInfra(d.kind) && layers > 0) return { width: 236, height: 44 + layers * 22 + 30 };
  return { width: 190, height: 54 };
}

const norm = (s: string) => s.toLowerCase().replace(/[^a-z0-9]/g, '');

// Кластер каждого узла: база core-сервиса (имя без хвоста «api»), которая — самая длинная подстрока имени узла.
// Напр. база «catalog» ← catalog-api; ей достаются catalog-migrations, postgres-catalog, ${CATALOG_DB_CONNECTION}.
function clusterBases(nodes: Node[]): Map<string, string> {
  const bases = [...new Set(
    nodes
      .filter((n) => { const d = (n.data as SystemNodeData).node; return !isInfra(d.kind) && (d.layers?.length ?? 0) > 0; })
      .map((n) => norm((n.data as SystemNodeData).node.name).replace(/api$/, ''))
      .filter((b) => b.length >= 3),
  )];
  const of = new Map<string, string>();
  for (const n of nodes) {
    const nn = norm((n.data as SystemNodeData).node.name);
    let best = '';
    for (const b of bases) if (b.length > best.length && nn.includes(b)) best = b;
    if (best) of.set(n.id, best);
  }
  return of;
}

interface ElkChild { id: string; width?: number; height?: number; x?: number; y?: number; layoutOptions?: Record<string, string>; children?: ElkChild[]; }

export async function layoutElk(nodes: Node[], edges: Edge[]): Promise<{ nodes: Node[]; edges: Edge[] }> {
  if (nodes.length === 0) return { nodes, edges };

  const cluster = clusterBases(nodes);
  // группы с ≥2 членами становятся compound-узлами; одиночки и инфра — на верхнем уровне
  const groups = new Map<string, ElkChild[]>();
  for (const n of nodes) {
    const b = cluster.get(n.id);
    if (b) (groups.get(b) ?? groups.set(b, []).get(b)!).push({ id: n.id, ...nodeSize(n) });
  }
  const rootChildren: ElkChild[] = [];
  const grouped = new Set<string>();
  for (const [base, kids] of groups) {
    if (kids.length < 2) continue;
    rootChildren.push({ id: `cluster:${base}`, layoutOptions: INNER_OPTIONS, children: kids });
    for (const k of kids) grouped.add(k.id);
  }
  for (const n of nodes) if (!grouped.has(n.id)) rootChildren.push({ id: n.id, ...nodeSize(n) });

  const graph = {
    id: 'root',
    layoutOptions: ROOT_OPTIONS,
    children: rootChildren,
    edges: edges.map((e) => ({ id: e.id, sources: [e.source], targets: [e.target] })),
  };

  const res = (await elk.layout(graph)) as unknown as {
    children?: ElkChild[];
    edges?: { id: string; sections?: { startPoint: RoutePoint; bendPoints?: RoutePoint[]; endPoint: RoutePoint }[] }[];
  };

  // flatten: абсолютная позиция = смещение группы + позиция внутри неё
  const pos = new Map<string, RoutePoint>();
  for (const ch of res.children ?? []) {
    if (ch.children) for (const k of ch.children) pos.set(k.id, { x: (ch.x ?? 0) + (k.x ?? 0), y: (ch.y ?? 0) + (k.y ?? 0) });
    else pos.set(ch.id, { x: ch.x ?? 0, y: ch.y ?? 0 });
  }

  // рёбра объявлены на корне → секции уже в абсолютных координатах
  const routes = new Map<string, RoutePoint[]>();
  for (const e of res.edges ?? []) {
    const s = e.sections?.[0];
    if (s) routes.set(e.id, [s.startPoint, ...(s.bendPoints ?? []), s.endPoint]);
  }

  const laidNodes = nodes.map((n) => {
    const p = pos.get(n.id) ?? { x: 0, y: 0 };
    return { ...n, position: p, data: { ...n.data, elk: p } };
  });
  const laidEdges = edges.map((e) => ({ ...e, data: { ...e.data, points: routes.get(e.id) } }));
  return { nodes: laidNodes, edges: laidEdges };
}
