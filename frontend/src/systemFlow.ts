import { mergeCalls, type HttpCall } from './edgeCalls';
import { MarkerType, Position, type Edge, type Node } from '@xyflow/react';
import type { Problem, SystemGraph, SystemNode, SystemPort } from './system';
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

// Толщина ребра по уверенности: слабые/эвристические связи тоньше, уверенные толще; нарушение всегда заметно.
// Шкала совпадает с провенансом (P1.5-D): ~1.0 depends_on/ProjectReference, ~0.9 config, ~0.7 http/db, ~0.5 эвристика.
export function edgeWidth(confidence: number, violation = false): number {
  const w = confidence >= 0.9 ? 2.6 : confidence >= 0.7 ? 2 : confidence >= 0.5 ? 1.4 : 1;
  return violation ? Math.max(w, 2) : w;
}

const INFRA_KINDS = new Set(['datastore', 'topic', 'external']);
export function isInfra(kind: string): boolean {
  return INFRA_KINDS.has(kind);
}

// --- Deploy (E9): выставленность портов. Зеркалит backend ExposedPortDetector.IsAllInterfaces. ---

const ALL_INTERFACES = new Set(['', '0.0.0.0', '::']);
/** Порт опубликован на ВСЕ интерфейсы (0.0.0.0 / :: / без host_ip) — доступен снаружи хоста. */
export function isPortPublic(p: SystemPort): boolean {
  return p.published && ALL_INTERFACES.has(p.hostIp ?? '');
}

// Классификация для UI: exposed — хранилище наружу (красный флаг, как в детекторе);
// public — сервис/шлюз наружу (нормально); bound — привязан к конкретному IP (loopback/LAN); internal — не опубликован.
export type PortExposure = 'exposed' | 'public' | 'bound' | 'internal';
export function portExposure(p: SystemPort, kind: string): PortExposure {
  if (!p.published) return 'internal';
  if (isPortPublic(p)) return kind === 'datastore' ? 'exposed' : 'public';
  return 'bound';
}

/** Текст порта: «host_ip:host → container» (что опубликовано) либо просто container (внутренний). */
export function portLabel(p: SystemPort): string {
  if (!p.published) return p.container;
  const left = p.hostIp ? `${p.hostIp}:${p.host ?? '*'}` : (p.host ?? '*');
  return `${left} → ${p.container}`;
}

/** Есть ли у узла порт-хранилища, торчащий наружу (для красного маркера на плашке; ловит #101). */
export function nodeHasExposedPort(node: SystemNode): boolean {
  return node.deploy?.ports.some((p) => portExposure(p, node.kind) === 'exposed') ?? false;
}

export interface SystemFilters {
  edgeKinds: ReadonlySet<string>; // какие виды рёбер показывать
  minConfidence: number;          // порог уверенности 0..1 (прячем слабые/эвристические связи)
  hideIsolated: boolean;          // спрятать узлы без связей
  hideTests?: boolean;            // спрятать узлы с «test» в имени
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
  // всё остальное (service, shared kernel, будущие виды) — в «сервисный» блок, иначе узел выпадет из раскладки
  const svc = nodes.filter((n) => !ext.includes(n) && !infra.includes(n));

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
export interface EdgeData {
  kind: string;
  confidence: number;
  violation: boolean;
  via?: string;
  calls?: HttpCall[];
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
      d.calls = mergeCalls(d.calls, e.calls);
      d.via ??= e.via;
      // пересобираем стиль по слитым данным: цвет по нарушению, толщина по максимальной уверенности пары
      prev.style = { ...prev.style, ...edgeStyle(e.kind, d.violation), strokeWidth: edgeWidth(d.confidence, d.violation) };
      continue;
    }
    const style = { ...edgeStyle(e.kind, violation), strokeWidth: edgeWidth(conf, violation) };
    edges.set(key, {
      id: key,
      source: e.source,
      target: e.target,
      type: 'ortho',                               // единый ортогональный роутер (обход плашек + порты)
      style,
      markerEnd: { type: MarkerType.ArrowClosed, color: style.stroke },
      data: { kind: e.kind, confidence: conf, violation, via: e.via, calls: e.calls } satisfies EdgeData,
    });
  }

  return { nodes: layout(nodes), edges: [...edges.values()] };
}

const LAYER_ORDER = ['API', 'Application', 'Domain', 'Infrastructure', 'Contracts', 'Persistence', 'Web', 'Shared'];

/** L1: слои сервиса как узлы (колонка, канонический порядок Clean Arch). Раскрываются в типы (L2, backlog). */
export function buildLayerFlow(node: SystemNode): { nodes: Node[]; edges: Edge[] } {
  const layers = [...(node.layers ?? [])].sort((a, b) => {
    const ia = LAYER_ORDER.indexOf(a.name), ib = LAYER_ORDER.indexOf(b.name);
    return (ia < 0 ? 99 : ia) - (ib < 0 ? 99 : ib) || a.name.localeCompare(b.name);
  });
  const nodes: Node[] = layers.map((l, i) => ({
    id: `layer:${l.name}`,
    type: 'layer',
    position: { x: 0, y: i * 150 },
    data: { name: l.name, health: l.health, count: l.types, note: l.note },
  }));
  return { nodes, edges: [] };
}

/** Фильтр вида поверх готовой раскладки (сохраняет позиции): прячет рёбра по виду/уверенности, tests и одиночек. */
export function filterFlow(
  flow: { nodes: Node[]; edges: Edge[] },
  f: SystemFilters,
): { nodes: Node[]; edges: Edge[] } {
  let nodes = flow.nodes;
  const dropped = new Set<string>();
  if (f.hideTests) {
    nodes = nodes.filter((n) => {
      const drop = /test/i.test((n.data as SystemNodeData).node.name);
      if (drop) dropped.add(n.id);
      return !drop;
    });
  }
  const edges = flow.edges.filter((e) => {
    const d = e.data as EdgeData;
    return f.edgeKinds.has(d.kind) && d.confidence >= f.minConfidence
      && !dropped.has(e.source) && !dropped.has(e.target);
  });
  if (f.hideIsolated) {
    const linked = new Set<string>();
    for (const e of edges) { linked.add(e.source); linked.add(e.target); }
    nodes = nodes.filter((n) => linked.has(n.id));
  }
  return { nodes, edges };
}
