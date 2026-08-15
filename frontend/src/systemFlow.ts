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
const EDGE_COLOR: Record<string, string> = {
  'http-call': '#e08a3c', // оранжевый — вызов по HTTP
  'rw-db': '#57c78a',     // зелёный — чтение/запись БД
  'uses-bus': '#9b8cff',  // фиолетовый — шина
  publishes: '#9b8cff',
  subscribes: '#9b8cff',
};
const DASHED = new Set(['http-call', 'uses-bus', 'publishes', 'subscribes']);
const VIOLATION_COLOR = '#e5534b';

export function edgeStyle(kind: string, violation: boolean): { stroke: string; strokeDasharray?: string } {
  const stroke = violation ? VIOLATION_COLOR : EDGE_COLOR[kind] ?? '#7c8698';
  return DASHED.has(kind) ? { stroke, strokeDasharray: '6 4' } : { stroke };
}

const INFRA_KINDS = new Set(['datastore', 'topic', 'external']);
export function isInfra(kind: string): boolean {
  return INFRA_KINDS.has(kind);
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
