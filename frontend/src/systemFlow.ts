import { MarkerType, type Edge, type Node } from '@xyflow/react';
import type { Problem, ServiceSummary, SystemGraph, SystemNode } from './system';
import { namespaceOf, problemsAt, problemsUnder } from './systemModel';

// Данные узлов L0 (для рендереров).
export interface ServiceCardData {
  summary: ServiceSummary;
  problems: Problem[]; // проблемы сервиса (node-located на его узлах + namespace-роллап дрейфа/слоёв)
  [key: string]: unknown;
}
export interface InfraNodeData {
  node: SystemNode;
  health: string;      // ok | warning | critical
  problems: Problem[];
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

// Ярусная раскладка L0: внешние сверху → сервисы в центре → хранилища/топики снизу.
const TIER_Y = { external: 0, service: 300, infra: 680 };
const GAP = { external: 230, service: 300, infra: 210 };

function place(ids: string[], y: number, gap: number): Map<string, { x: number; y: number }> {
  const total = Math.max(ids.length - 1, 0) * gap;
  const map = new Map<string, { x: number; y: number }>();
  ids.forEach((id, i) => map.set(id, { x: i * gap - total / 2, y }));
  return map;
}

/**
 * SystemGraph → узлы/рёбра React Flow для L0.
 * Compose-узлы-сервисы сворачиваются в карточки .sln (по namespace-префиксу); инфра/внешние — как есть.
 * Рёбра роллапятся на уровень карточек; петли внутри одной карточки убираются; дедуп по (src,tgt,kind).
 */
export function buildSystemFlow(g: SystemGraph): { nodes: Node[]; edges: Edge[] } {
  const services = g.services ?? [];
  const cardIds = new Set(services.map((s) => s.id));
  const nodeById = new Map(g.nodes.map((n) => [n.id, n]));

  // id узла → id узла L0: сервис-узел → карточка .sln; инфра/внешние → сам узел.
  const toL0 = (n: SystemNode | undefined): string | null => {
    if (!n) return null;
    if (n.kind === 'service') {
      const ns = namespaceOf(n.id);
      return cardIds.has(ns) ? ns : n.id; // без карточки (нет код-линз) — оставляем узел-сервис как есть
    }
    return n.id;
  };

  const externalNodes = g.nodes.filter((n) => n.kind === 'external');
  const infraNodes = g.nodes.filter((n) => n.kind === 'datastore' || n.kind === 'topic');

  const posE = place(externalNodes.map((n) => n.id), TIER_Y.external, GAP.external);
  const posS = place(services.map((s) => s.id), TIER_Y.service, GAP.service);
  const posI = place(infraNodes.map((n) => n.id), TIER_Y.infra, GAP.infra);

  const cardFlow: Node<ServiceCardData>[] = services.map((s) => ({
    id: s.id,
    type: 'serviceCard',
    position: posS.get(s.id)!,
    data: { summary: s, problems: [...problemsAt(g.problems, s.id), ...problemsUnder(g.problems, s.id)] },
  }));
  const infraFlow: Node<InfraNodeData>[] = [...externalNodes, ...infraNodes].map((n) => ({
    id: n.id,
    type: 'infra',
    position: (posE.get(n.id) ?? posI.get(n.id))!,
    data: { node: n, health: n.health ?? 'ok', problems: problemsAt(g.problems, n.id) },
  }));

  // рёбра, свёрнутые на L0
  const edges = new Map<string, Edge>();
  for (const e of g.edges) {
    const src = toL0(nodeById.get(e.source));
    const tgt = toL0(nodeById.get(e.target));
    if (!src || !tgt || src === tgt) continue; // петля внутри карточки — не рисуем
    const key = `${src}|${tgt}|${e.kind}`;
    const violation = e.violation ?? false;
    const prev = edges.get(key);
    if (prev) {
      if (violation) prev.style = { ...prev.style, ...edgeStyle(e.kind, true) }; // нарушение важнее
      continue;
    }
    const style = edgeStyle(e.kind, violation);
    edges.set(key, {
      id: key,
      source: src,
      target: tgt,
      style,
      markerEnd: { type: MarkerType.ArrowClosed, color: style.stroke },
    });
  }

  return { nodes: [...cardFlow, ...infraFlow], edges: [...edges.values()] };
}
