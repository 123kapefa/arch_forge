import dagre from '@dagrejs/dagre';
import { MarkerType, Position, type Edge, type Node } from '@xyflow/react';
import type { Problem, SystemGraph, SystemNode } from './system';
import { problemsAt } from './systemModel';

// Данные узла L0 (сервис/инфра — единая форма; рендер различается по node.kind).
export interface SystemNodeData {
  node: SystemNode;
  health: string;      // ok | warning | critical
  problems: Problem[]; // проблемы, локализованные на самом узле
  [key: string]: unknown;
}

// Стиль ребра по виду: цвет + пунктир; нарушение → красный.
// цвета по палитре прототипа (Claude Design)
const EDGE_COLOR: Record<string, string> = {
  'http-call': '#e08a3c', // оранжевый — вызов по HTTP
  'rw-db': '#6fcf77',     // зелёный — чтение/запись БД
  'uses-bus': '#b98cff',  // фиолетовый — шина
  publishes: '#b98cff',
  subscribes: '#b98cff',
};
const DASHED = new Set(['http-call', 'uses-bus', 'publishes', 'subscribes']);
const VIOLATION_COLOR = '#e5484d';

export function edgeStyle(kind: string, violation: boolean): { stroke: string; strokeDasharray?: string } {
  const stroke = violation ? VIOLATION_COLOR : EDGE_COLOR[kind] ?? '#7c8698';
  return DASHED.has(kind) ? { stroke, strokeDasharray: '6 4' } : { stroke };
}

const INFRA_KINDS = new Set(['datastore', 'topic', 'external']);
export function isInfra(kind: string): boolean {
  return INFRA_KINDS.has(kind);
}

export interface SystemFilters {
  edgeKinds: ReadonlySet<string>; // какие виды рёбер показывать
  minConfidence: number;          // порог уверенности 0..1 (прячем слабые/эвристические связи)
  hideIsolated: boolean;          // спрятать узлы без связей
}

/** Клиентская фильтрация вида — мгновенно, без запроса: прореживает «клубок» рёбер и одиночек. */
export function filterGraph(g: SystemGraph, f: SystemFilters): SystemGraph {
  const edges = g.edges.filter((e) => f.edgeKinds.has(e.kind) && (e.confidence ?? 1) >= f.minConfidence);
  let nodes = g.nodes;
  if (f.hideIsolated) {
    const linked = new Set<string>();
    for (const e of edges) { linked.add(e.source); linked.add(e.target); }
    nodes = nodes.filter((n) => linked.has(n.id));
  }
  return { ...g, nodes, edges };
}

/** Виды рёбер, реально присутствующие в графе (для списка тумблеров). */
export function edgeKindsOf(g: SystemGraph): string[] {
  return [...new Set(g.edges.map((e) => e.kind))].sort();
}

// Раскладка dagre сверху-вниз: поток вызовов/зависимостей как в Blueprints (внешние → сервисы → инфра).
const NODE_W = 190;
const NODE_H = 56;
function layout(nodes: Node[], edges: Edge[]): Node[] {
  const g = new dagre.graphlib.Graph();
  g.setDefaultEdgeLabel(() => ({}));
  g.setGraph({ rankdir: 'TB', nodesep: 38, ranksep: 82 });
  nodes.forEach((n) => g.setNode(n.id, { width: NODE_W, height: NODE_H }));
  edges.forEach((e) => g.setEdge(e.source, e.target));
  dagre.layout(g);
  return nodes.map((n) => {
    const p = g.node(n.id);
    return {
      ...n,
      position: { x: p.x - NODE_W / 2, y: p.y - NODE_H / 2 },
      targetPosition: Position.Top,
      sourcePosition: Position.Bottom,
    };
  });
}

/**
 * SystemGraph → узлы/рёбра React Flow для L0.
 * Узлы — как есть (compose-микросервисы + инфра + внешние), БЕЗ роллапа в .sln: настоящая карта системы
 * с графом вызовов. Рёбра: петли убираем, дедуп по (src,tgt,kind), нарушение важнее. Раскладка dagre TB.
 */
export function buildSystemFlow(g: SystemGraph): { nodes: Node[]; edges: Edge[] } {
  const nodes: Node<SystemNodeData>[] = g.nodes.map((n) => ({
    id: n.id,
    type: 'sys', // единый рендерер L0; вид (сервис/инфра) — по node.kind внутри
    position: { x: 0, y: 0 },
    data: { node: n, health: n.health ?? 'ok', problems: problemsAt(g.problems, n.id) },
  }));

  const edges = new Map<string, Edge>();
  for (const e of g.edges) {
    if (e.source === e.target) continue;
    const key = `${e.source}|${e.target}|${e.kind}`;
    const violation = e.violation ?? false;
    const prev = edges.get(key);
    if (prev) {
      if (violation) prev.style = { ...prev.style, ...edgeStyle(e.kind, true) };
      continue;
    }
    const style = edgeStyle(e.kind, violation);
    edges.set(key, {
      id: key,
      source: e.source,
      target: e.target,
      style,
      markerEnd: { type: MarkerType.ArrowClosed, color: style.stroke },
    });
  }

  const edgeList = [...edges.values()];
  return { nodes: layout(nodes, edgeList), edges: edgeList };
}
