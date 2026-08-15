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

// Детерминированная раскладка ярусами (без наложений, со структурой): внешние сверху → сервисы сеткой → инфра снизу.
// Карточки со слоями крупные, поэтому сетка с большим шагом; dagre на 70+ узлах давал кашу.
const KIND_OF = (n: Node) => (n.data as SystemNodeData).node.kind;
const COLS = 6;
const CELL_W = 290; // ширина колонки (карточка ~236 + зазор)
const ROW_H = 250;  // высота ряда сервисов (карточка со слоями + зазор)
const BAND_GAP = 210;

function layout(nodes: Node[]): Node[] {
  const ext = nodes.filter((n) => KIND_OF(n) === 'external');
  const infra = nodes.filter((n) => KIND_OF(n) === 'datastore' || KIND_OF(n) === 'topic');
  const svc = nodes.filter((n) => KIND_OF(n) === 'service');

  const out: Node[] = [];
  const put = (n: Node, x: number, y: number) =>
    out.push({ ...n, position: { x, y }, targetPosition: Position.Top, sourcePosition: Position.Bottom });

  const gridW = COLS * CELL_W;
  const svcTop = 180;
  svc.forEach((n, i) => put(n, (i % COLS) * CELL_W, svcTop + Math.floor(i / COLS) * ROW_H));

  // внешние — строка сверху, по центру над сеткой
  ext.forEach((n, i) => put(n, (gridW - ext.length * BAND_GAP) / 2 + i * BAND_GAP, 0));

  // инфра — строка снизу
  const infraY = svcTop + Math.ceil(svc.length / COLS) * ROW_H + 40;
  infra.forEach((n, i) => put(n, (gridW - infra.length * BAND_GAP) / 2 + i * BAND_GAP, infraY));

  return out;
}

// Данные ребра L0 для фильтра и маршрутизации (points добавляет ELK-раскладка).
interface EdgeData {
  kind: string;
  confidence: number;
  violation: boolean;
  [key: string]: unknown;
}

/**
 * SystemGraph → узлы/рёбра React Flow для L0.
 * Узлы — как есть (compose-микросервисы + инфра + внешние), БЕЗ роллапа в .sln: настоящая карта системы
 * с графом вызовов. Рёбра: петли убираем, дедуп по (src,tgt,kind), нарушение важнее.
 * Позиции узлов и маршруты рёбер (обход плашек) считает ELK (elkLayout); тут — сетка-заглушка до раскладки.
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
    const conf = e.confidence ?? 1;
    const prev = edges.get(key);
    if (prev) {
      const d = prev.data as EdgeData;
      d.violation ||= violation;
      d.confidence = Math.max(d.confidence, conf);
      if (violation) prev.style = { ...prev.style, ...edgeStyle(e.kind, true) };
      continue;
    }
    const style = edgeStyle(e.kind, violation);
    edges.set(key, {
      id: key,
      source: e.source,
      target: e.target,
      type: 'routed',                              // кастомное ребро: маршрут ELK с обходом плашек
      style,
      markerEnd: { type: MarkerType.ArrowClosed, color: style.stroke },
      data: { kind: e.kind, confidence: conf, violation } satisfies EdgeData,
    });
  }

  return { nodes: layout(nodes), edges: [...edges.values()] };
}

/** Фильтр вида поверх готовой раскладки (сохраняет позиции): прячет рёбра по виду/уверенности и одиночек. */
export function filterFlow(
  flow: { nodes: Node[]; edges: Edge[] },
  f: SystemFilters,
): { nodes: Node[]; edges: Edge[] } {
  const edges = flow.edges.filter((e) => {
    const d = e.data as EdgeData;
    return f.edgeKinds.has(d.kind) && d.confidence >= f.minConfidence;
  });
  let nodes = flow.nodes;
  if (f.hideIsolated) {
    const linked = new Set<string>();
    for (const e of edges) { linked.add(e.source); linked.add(e.target); }
    nodes = nodes.filter((n) => linked.has(n.id));
  }
  return { nodes, edges };
}
