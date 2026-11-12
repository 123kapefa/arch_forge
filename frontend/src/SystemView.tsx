import { SessionBadge } from './SessionBadge';
import {
  forwardRef, useCallback, useEffect, useImperativeHandle, useMemo, useRef, useState,
  type MouseEvent as ReactMouseEvent,
} from 'react';
import {
  ReactFlow, ReactFlowProvider, useReactFlow, useStoreApi, useNodesState, useEdgesState, useNodesInitialized,
  Background, Controls, MiniMap, type Edge, type Node, type NodeChange,
} from '@xyflow/react';
import { routeAll, type Rect, type XY } from './orthoRouter';
import '@xyflow/react/dist/style.css';
import './system.css';

import { fetchSystem, type SystemGraph, type ProblemItem, type Problem, type SystemNode } from './system';
import { fetchMap, fetchImports, type GraphDto, type MemberDto, type ImportsDto } from './api';
import { isAbortError } from './http';
import { ImportsScreen } from './ImportsScreen';
import { ConventionsView } from './ConventionsView';
import { ProfileView } from './ProfileView';
import { buildSystemFlow, buildLayerFlow, filterFlow, edgeKindsOf, edgeStyle, portExposure, portLabel, type SystemNodeData } from './systemFlow';
import { buildLayerFlowRich, buildTypeFlow, type LayerCardData } from './layerFlow';
import { dagreLayout } from './dagreLayout';
import { OrthoEdge } from './OrthoEdge';
import { SystemNodeView } from './nodes/SystemNodeView';
import { LayerCard } from './nodes/LayerCard';
import { TypeCard, type TypeCardData } from './nodes/TypeCard';
import { problemsByLens, severityCounts, categoryOf, namespaceOf, ownerByRoot, lensHitsByNode, type Lens } from './systemModel';
import { repoColor, layerColor } from './theme';
import { HiddenLayersContext } from './layersContext';
import { LensContext } from './lensContext';
import { FolderPicker } from './FolderPicker';
import { fetchGitBranch } from './fs';
import { Icon, type IconName } from './Icon';
import {
  readViewport, writeViewport, readBool, readNum, setSS, readGraphCache, writeGraphCache,
} from './persist';

const nodeTypes = { sys: SystemNodeView, layer: LayerCard, type: TypeCard };
const edgeTypes = { ortho: OrthoEdge };
const GRID = 20; // шаг сетки: точки фона + шаг магнита
// Порог скорости указателя (px/мс): медленнее (малая инерция) → магнит к сетке; быстрее → свободно.
// Низкий порог = магнит только при явно медленном, «прицельном» движении, а не при обычном перетаскивании.
const MAGNET_SPEED = 0.05;

export type FlowHandle = { save: () => void; reset: () => void; focus: (ids: string[]) => void };

// Вьюпорт доски L0 сохраняется по ключу проекта, чтобы перезагрузка не сбрасывала камеру в fitView
// (пользователь остаётся там, где был). Персист-хелперы — в ./persist (покрыты тестами).

/**
 * Доска: узлы в state → перетаскиваемы (onNodesChange); пересев из flow + fitView при загрузке/фильтре.
 * Магнит к сетке — по инерции: тянешь медленно → snapToGrid включается, быстро → свободно.
 * Раскладка (позиции узлов) сохраняется в localStorage по ключу проекта и восстанавливается при загрузке.
 */
const Flow = forwardRef<FlowHandle, {
  flow: { nodes: Node[]; edges: Edge[] };
  magnet: boolean;
  storageKey: string;                // ключ localStorage для раскладки ('' → не сохранять)
  onDirty: (saved: boolean) => void; // true = совпадает с сохранённой, false = есть изменения
  selectedId: string | null;         // выбранный узел — его связи подсвечиваем
  onSelect: (n: Node) => void;
  onDoubleClick: (n: Node) => void;  // двойной клик — drill на следующий уровень
  onClear: () => void;
  focusTargetRef?: { current: string | null }; // id карточки для центровки при пересборке (переход «к плашке»)
}>(function Flow({ flow, magnet, storageKey, onDirty, selectedId, onSelect, onDoubleClick, onClear, focusTargetRef }, ref) {
  const [nodes, setNodes, onNodesChange] = useNodesState(flow.nodes);
  const [edges, setEdges, onEdgesChange] = useEdgesState(flow.edges);
  const [snapNow, setSnapNow] = useState(false); // притяжение активно прямо сейчас (низкая скорость)
  const [version, setVersion] = useState(0); // версия раскладки: растёт при новой раскладке и drag-stop
  const [routes, setRoutes] = useState<Map<string, XY[]>>(new Map()); // предрассчитанные маршруты (с развязкой)
  const [hoverId, setHoverId] = useState<string | null>(null); // узел под курсором — подсветка связей
  const last = useRef<{ x: number; y: number; t: number } | null>(null);
  const nodesRef = useRef(nodes);
  nodesRef.current = nodes;
  const rf = useReactFlow();
  const storeApi = useStoreApi();
  const nodesInitialized = useNodesInitialized(); // все узлы измерены → можно точно посчитать маршруты рёбер
  // начальный вьюпорт L0 читаем один раз на маунте (не на каждый рендер) — для defaultViewport/onInit
  const initialVp = useMemo(() => (storageKey ? readViewport(storageKey) : null), []); // eslint-disable-line react-hooks/exhaustive-deps

  // Централизованная маршрутизация всех рёбер + пучковая развязка — при новой раскладке и после drag-stop
  // (version), по измеренным позициям из nodeLookup. Наведение/выбор её не трогают.
  useEffect(() => {
    const { nodeLookup } = storeApi.getState();
    if (nodeLookup.size === 0) { setRoutes(new Map()); return; }
    const rects = new Map<string, Rect>();
    for (const n of nodeLookup.values()) {
      // пока хоть один узел не измерен — не считаем маршруты по fallback-размерам и СБРАСЫВАЕМ прежние:
      // иначе при дрилле (L0↔L1↔L2) остаётся Map прошлого уровня, рёбра нового уровня не скрываются
      // (routesReady=true), а route для их id нет → OrthoEdge мелькает неточным маршрутом.
      if (n.measured?.width == null || n.measured?.height == null) { setRoutes((prev) => (prev.size ? new Map() : prev)); return; }
      const p = n.internals.positionAbsolute;
      rects.set(n.id, { x: p.x, y: p.y, w: n.measured.width, h: n.measured.height });
    }
    setRoutes(routeAll(rects, flow.edges.map((e) => ({ id: e.id, source: e.source, target: e.target }))));
  }, [version, flow.edges, storeApi]);

  // Как только все узлы измерены (маунт/reload/drill) — пересчитываем маршруты по реальным позициям.
  // До этого рёбра скрыты (см. displayEdges), чтобы не мелькала дефолтная линия и не «возвращалась» в маршрут.
  useEffect(() => { if (nodesInitialized) setVersion((v) => v + 1); }, [nodesInitialized]);

  // Наложить сохранённые позиции на вычисленную раскладку (по id узла).
  const applySaved = useCallback((ns: Node[]): Node[] => {
    if (!storageKey) return ns;
    try {
      const raw = localStorage.getItem(storageKey);
      if (!raw) return ns;
      const pos = JSON.parse(raw) as Record<string, { x: number; y: number }>;
      return ns.map((n) => (pos[n.id] ? { ...n, position: pos[n.id] } : n));
    } catch { return ns; }
  }, [storageKey]);

  // Записать текущие позиции узлов в localStorage (авто-сохранение раскладки).
  const persist = useCallback(() => {
    if (!storageKey) return;
    const pos: Record<string, { x: number; y: number }> = {};
    for (const n of nodesRef.current) pos[n.id] = n.position;
    try { localStorage.setItem(storageKey, JSON.stringify(pos)); } catch { /* quota */ }
    onDirty(true);
  }, [storageKey, onDirty]);

  useEffect(() => {
    // при переходе «к плашке» помечаем целевую карточку selected прямо в посеве (кольцо; без доп. setNodes, чтобы не сбить центровку)
    const focusId = focusTargetRef?.current;
    const seeded = applySaved(flow.nodes);
    setNodes(focusId ? seeded.map((n) => (n.id === focusId ? { ...n, selected: true } : n)) : seeded);
    setEdges(flow.edges);
    onDirty(true); // после загрузки/восстановления раскладка считается сохранённой
    if (flow.nodes.length === 0) { setVersion((v) => v + 1); return; }
    // переход «к плашке»: цель есть на доске → центрируем её; иначе — обычный fitView всех
    const target = focusTargetRef?.current;
    const node = target ? flow.nodes.find((n) => n.id === target) : undefined;
    const timers: ReturnType<typeof setTimeout>[] = [];
    if (node) {
      // позиция карточки уже посчитана ELK (node.position) — центрируем мгновенно, без ожидания измерения.
      // Переустанавливаем несколько раз: React Flow делает внутренний fitView, когда контейнер получает
      // размер (см. warning «parent container needs width/height») — иначе он затирает центровку.
      const w = (node.width ?? node.measured?.width ?? 220) as number;
      const h = (node.height ?? node.measured?.height ?? 120) as number;
      const cx = node.position.x + w / 2, cy = node.position.y + h / 2;
      const center = () => rf.setCenter(cx, cy, { zoom: 1 });
      [70, 220, 450, 750].forEach((d) => timers.push(setTimeout(center, d)));
      if (focusTargetRef) focusTargetRef.current = null; // цель достигнута — снять
      timers.push(setTimeout(() => setVersion((v) => v + 1), 80));
    } else {
      // L0 (storageKey задан): восстанавливаем сохранённый вьюпорт — reload/фильтр/возврат оставляют камеру
      // там, где был пользователь (saved обновляется на каждый его пан/зум). Drill (storageKey='') → fitView.
      // Re-assert несколько раз: React Flow делает внутренний fitView, когда контейнер получает размер
      // (render-gate монтирует его с задержкой), иначе он затирает восстановление — как в центровке «к плашке».
      const saved = storageKey ? readViewport(storageKey) : null;
      if (saved) {
        [70, 220, 450, 750].forEach((d) => timers.push(setTimeout(() => rf.setViewport(saved), d)));
        timers.push(setTimeout(() => setVersion((v) => v + 1), 80));
      } else {
        timers.push(setTimeout(() => { rf.fitView({ padding: 0.2 }); setVersion((v) => v + 1); }, 70));
      }
    }
    return () => timers.forEach(clearTimeout);
  }, [flow, applySaved, setNodes, setEdges, rf, onDirty, focusTargetRef, storageKey]);

  useImperativeHandle(ref, () => ({
    save() { persist(); },
    reset() {
      if (storageKey) { try { localStorage.removeItem(storageKey); localStorage.removeItem(storageKey + ':vp'); } catch { /* ignore */ } }
      setNodes(flow.nodes); // вернуть вычисленную раскладку (без сохранённых позиций)
      onDirty(true);
      setTimeout(() => rf.fitView({ padding: 0.2 }), 60);
    },
    focus(ids) {
      if (ids.length === 0) return;
      rf.fitView({ nodes: ids.map((id) => ({ id })), duration: 400, maxZoom: 1.15, padding: 0.45 });
    },
  }), [persist, storageKey, flow, setNodes, rf, onDirty]);

  // Перетаскивание узла → раскладка расходится с сохранённой.
  const handleNodesChange = useCallback((changes: NodeChange[]) => {
    onNodesChange(changes);
    if (changes.some((c) => c.type === 'position' && c.dragging)) onDirty(false);
  }, [onNodesChange, onDirty]);

  // Скорость указателя за кадр → включаем/выключаем притяжение к сетке во время перетаскивания.
  const onNodeDrag = useCallback((e: MouseEvent | TouchEvent) => {
    const p = 'clientX' in e ? e : e.touches[0];
    if (!p) return;
    const t = performance.now();
    const prev = last.current;
    last.current = { x: p.clientX, y: p.clientY, t };
    if (!prev || t <= prev.t) return;
    const speed = Math.hypot(p.clientX - prev.x, p.clientY - prev.y) / (t - prev.t);
    const want = speed < MAGNET_SPEED; // медленно → магнит
    setSnapNow((cur) => (cur === want ? cur : want));
  }, []);

  // Подсветка связей: наведение (или выбор) узла → его рёбра ярче и над блоками, остальные приглушены.
  const highlightId = hoverId ?? selectedId;
  const routesReady = routes.size > 0; // маршруты посчитаны — можно показывать рёбра (иначе мелькнёт дефолт)
  const displayEdges = useMemo(() => edges.map((e) => {
    const route = routes.get(e.id);                    // предрассчитанный маршрут с развязкой
    const on = highlightId != null && (e.source === highlightId || e.target === highlightId);
    return {
      ...e,
      data: { ...e.data, route },
      hidden: !routesReady,                            // прячем до готовности маршрутов (без flicker при reload/drill)
      zIndex: on ? 1000 : 0,
      style: { ...e.style, opacity: on ? 1 : highlightId ? 0.08 : 1, strokeWidth: on ? 3 : e.style?.strokeWidth },
    };
  }), [edges, routes, routesReady, highlightId]);

  return (
    <ReactFlow
      nodes={nodes}
      edges={displayEdges}
      onNodesChange={handleNodesChange}
      onEdgesChange={onEdgesChange}
      nodeTypes={nodeTypes}
      edgeTypes={edgeTypes}
      minZoom={0.08}
      // начальная камера L0 = сохранённый вьюпорт (переживает reload); onInit добивает, когда ReactFlow готов
      // (render-gate монтирует его с задержкой — reseed-таймеры могут не застать). Drill: storageKey='' → fitView.
      defaultViewport={initialVp ?? undefined}
      onInit={() => { if (initialVp) rf.setViewport(initialVp); }}
      proOptions={{ hideAttribution: true }}
      nodesConnectable={false}
      elevateEdgesOnSelect
      snapToGrid={magnet && snapNow}
      snapGrid={[GRID, GRID]}
      onNodeDragStart={() => { last.current = null; }}
      onNodeDrag={onNodeDrag}
      onNodeDragStop={() => { last.current = null; setSnapNow(false); setVersion((v) => v + 1); }}
      onNodeMouseEnter={(_, n) => setHoverId(n.id)}
      onNodeMouseLeave={() => setHoverId(null)}
      onNodeClick={(_, n) => onSelect(n)}
      onNodeDoubleClick={(_, n) => onDoubleClick(n)}
      onPaneClick={onClear}
      onMoveEnd={(e, vp) => { if (e && storageKey) writeViewport(storageKey, vp); }} // сохраняем только пользовательский пан/зум (event≠null), не программный fitView
    >
      <Background gap={GRID} />
      <Controls />
      <MiniMap pannable zoomable nodeColor="#3a4658" maskColor="rgba(11,15,23,0.6)" style={{ width: 210, height: 168 }} />
    </ReactFlow>
  );
});

const KIND_LABEL: Record<string, string> = {
  service: 'сервис', datastore: 'хранилище', topic: 'шина', external: 'внешний', shared: 'сквозной kernel',
};

/** Инспектор для выбранного узла — диспетчер по уровню: системный узел (L0) / слой (L1) / тип+члены (L2). */
function Inspector({ node, graph, lens, onFocus }: {
  node: Node; graph: SystemGraph; lens: Lens; onFocus: (id: string) => void;
}) {
  const d = node.data as Record<string, unknown>;
  if (d.node) return <SystemInspector node={node} graph={graph} lens={lens} onFocus={onFocus} />;
  if ('role' in d && 'via' in d) {
    return <TypeInspector data={node.data as unknown as TypeCardData} members={(d.members as MemberDto[] | undefined) ?? []} />;
  }
  if ('count' in d) return <LayerInspector data={node.data as unknown as LayerCardData} />;
  return null;
}

const MEMBER_KIND_ORDER: Record<string, number> = { endpoint: 0, method: 1, property: 2, field: 3 };
const MEMBER_KIND_LABEL: Record<string, string> = { endpoint: 'эндпоинты', method: 'методы', property: 'свойства', field: 'поля' };
const IMEMBER_MARK: Record<string, string> = { endpoint: '▪', method: 'ƒ', property: '▸', field: '▸' };

// Одна компактная строка: свойство «: Тип», метод «(параметры) : Возврат», эндпоинт — маршрут.
function memberDetail(m: MemberDto): string {
  if (m.kind === 'property' || m.kind === 'field') return m.signature ? `: ${m.signature}` : '';
  if (m.kind === 'endpoint' && m.route) return `/${m.route.replace(/^\//, '')}`;
  return m.signature ?? '';
}
function memberTitle(m: MemberDto): string {
  const parts = [m.name];
  if (m.signature) parts.push(m.signature);
  if (m.route) parts.push(`/${m.route.replace(/^\//, '')}`);
  return parts.join(' ');
}

/** L2: инспектор типа — стереотип/слой/дрейф + ВСЕ члены (эндпоинты/методы/свойства/поля), сгруппированные по виду. */
function TypeInspector({ data, members }: { data: TypeCardData; members: MemberDto[] }) {
  const groups = useMemo(() => {
    const by = new Map<string, MemberDto[]>();
    for (const m of members) {
      const arr = by.get(m.kind);
      if (arr) arr.push(m); else by.set(m.kind, [m]);
    }
    for (const arr of by.values()) arr.sort((a, b) => (a.line ?? 0) - (b.line ?? 0) || a.name.localeCompare(b.name));
    return [...by.entries()].sort((a, b) => (MEMBER_KIND_ORDER[a[0]] ?? 9) - (MEMBER_KIND_ORDER[b[0]] ?? 9));
  }, [members]);

  return (
    <div className="inspector">
      <div className="inspector__head">
        <span className="inspector__dot" style={{ background: layerColor(data.layer) }} />
        <span className="inspector__name">{data.name}</span>
      </div>
      <div className="inspector__kind">{data.kind} · {data.layer}</div>
      <div className="inspector__block">
        <div className="inspector__sub">СТЕРЕОТИП</div>
        <div>{data.role || '—'} <span className="iedge__kind">{data.via}</span></div>
      </div>
      {data.drift && data.drift.length > 0 && (
        <div className="inspector__block">
          <div className="inspector__sub">дрейф от свода</div>
          <ul className="inspector__problems">
            {data.drift.map((x) => <li key={x} className="sev-info">{x}</li>)}
          </ul>
        </div>
      )}
      {members.length > 0 && (
        <div className="inspector__block">
          <div className="inspector__sub">члены · {members.length}</div>
          {groups.map(([kind, list]) => (
            <div key={kind} className={`imembers kind-${kind}`}>
              <div className="imembers__group">{MEMBER_KIND_LABEL[kind] ?? kind} · {list.length}</div>
              <ul className="imembers__list">
                {list.map((m) => (
                  <li key={m.id} className="imember" title={memberTitle(m)}>
                    <span className="imember__mark">{IMEMBER_MARK[m.kind] ?? '·'}</span>
                    <span className="imember__name">{m.name}</span>
                    {m.httpMethod && <span className={`verb verb-${m.httpMethod}`}>{m.httpMethod}</span>}
                    <span className="imember__type">{memberDetail(m)}</span>
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

/** L1: инспектор слоя — число типов, нарушения (красное) и заметки (дрейф/покрытие). */
function LayerInspector({ data }: { data: LayerCardData }) {
  return (
    <div className="inspector">
      <div className="inspector__head">
        <span className="inspector__dot" style={{ background: layerColor(data.name) }} />
        <span className="inspector__name">{data.name}</span>
      </div>
      <div className="inspector__kind">слой · {data.count} тип(ов){data.health ? ` · ${data.health}` : ''}</div>
      {data.warnings && data.warnings.length > 0 && (
        <div className="inspector__block">
          <div className="inspector__sub">нарушения свода</div>
          <ul className="inspector__problems">
            {data.warnings.map((w) => <li key={w} className="sev-critical">{w}</li>)}
          </ul>
        </div>
      )}
      {data.notes && data.notes.length > 0 && (
        <div className="inspector__block">
          <div className="inspector__sub">заметки</div>
          <ul className="inspector__problems">
            {data.notes.map((n) => <li key={n} className="sev-coverage">{n}</li>)}
          </ul>
        </div>
      )}
      <div className="inspector__block panel__empty">двойной клик по слою → его типы (L2)</div>
    </div>
  );
}

/** L1: список HTTP-эндпоинтов сервиса (контроллеры + minimal API) — когда на L1 слой не выбран (#71). */
function ServiceEndpoints({ node }: { node: SystemNode }) {
  const eps = node.endpoints ?? [];
  return (
    <div className="inspector">
      <div className="inspector__head">
        <span className="inspector__dot" style={{ background: repoColor(node.name) }} />
        <span className="inspector__name">{node.name}</span>
      </div>
      <div className="inspector__kind">сервис · {eps.length} endpoint(ов)</div>
      {eps.length === 0 ? (
        <div className="inspector__block panel__empty">эндпоинты не найдены (контроллеры / minimal API)</div>
      ) : (
        <div className="inspector__block">
          <div className="inspector__sub">ENDPOINTS · {eps.length}</div>
          <ul className="imembers__list">
            {eps.map((e, i) => (
              <li key={`${e.method}-${e.route}-${i}`} className="imember"
                  title={`${e.method} ${e.route}${e.type ? ' · ' + e.type : ''}`}>
                <span className={`verb verb-${e.method}`}>{e.method}</span>
                <span className="imember__type ep__route">{e.route || '—'}</span>
                {e.source === 'minimal' && <span className="iedge__kind">minimal</span>}
              </li>
            ))}
          </ul>
        </div>
      )}
      <div className="inspector__block panel__empty">двойной клик по слою → его типы (L2)</div>
    </div>
  );
}

function SystemInspector({ node, graph, lens, onFocus }: {
  node: Node; graph: SystemGraph; lens: Lens; onFocus: (id: string) => void;
}) {
  const n = (node.data as SystemNodeData).node;
  const problems = problemsByLens((node.data as SystemNodeData).problems, lens);
  const nameById = useMemo(() => new Map(graph.nodes.map((x) => [x.id, x.name])), [graph]);
  const { out, inc } = useMemo(() => {
    const seen = new Set<string>();
    const o: typeof graph.edges = [], i: typeof graph.edges = [];
    for (const e of graph.edges) {
      const key = `${e.source}|${e.target}|${e.kind}`;
      if (seen.has(key)) continue; seen.add(key);
      if (e.source === n.id) o.push(e);
      else if (e.target === n.id) i.push(e);
    }
    return { out: o, inc: i };
  }, [graph, n.id]);

  return (
    <div className="inspector">
      <div className="inspector__head">
        <span className="inspector__dot" style={{ background: healthColor(n.health) }} />
        <span className="inspector__name">{n.name}</span>
      </div>
      <div className="inspector__kind">
        {KIND_LABEL[n.kind] ?? n.kind}{n.health ? ` · ${n.health}` : ''}
      </div>

      {n.layers && n.layers.length > 0 && (
        <div className="inspector__block">
          <div className="inspector__sub">СЛОИ</div>
          <ul className="inspector__layers">
            {n.layers.map((l) => (
              <li key={l.name} className={`ilayer health-${l.health}`}>
                <span className="ilayer__name">{l.name}</span>
                <span className="ilayer__types">{l.types} тип.</span>
              </li>
            ))}
          </ul>
        </div>
      )}

      {n.deploy && (
        <div className="inspector__block">
          <div className="inspector__sub">DEPLOY</div>
          {n.deploy.ports.length > 0 && (
            <ul className="deploy__ports">
              {n.deploy.ports.map((p, i) => {
                const ex = portExposure(p, n.kind);
                const tag = ex === 'exposed' ? 'наружу' : ex === 'public' ? 'публичный'
                  : ex === 'bound' ? (p.hostIp ?? 'bound') : 'внутр.';
                return (
                  <li key={`${p.container}-${i}`} className="deploy__port" title={portLabel(p)}>
                    <span className="deploy__portmap">{portLabel(p)}</span>
                    <span className={`dep-tag dep-tag--${ex}`}>{tag}</span>
                  </li>
                );
              })}
            </ul>
          )}
          <div className="deploy__meta">
            <span className={`dep-hc dep-hc--${n.deploy.hasHealthcheck ? 'on' : 'off'}`}>
              healthcheck {n.deploy.hasHealthcheck ? '✓' : '—'}
            </span>
            {n.deploy.networks.map((net, i) => <span key={`${net}-${i}`} className="dep-net">{net}</span>)}
          </div>
        </div>
      )}

      <div className="inspector__block">
        <div className="inspector__sub">проблемы ({lens === 'architecture' ? 'реальные' : 'дрейф'})</div>
        {problems.length === 0 ? (
          <div className="panel__empty">нет</div>
        ) : (
          <ul className="inspector__problems">
            {problems.map((p) => (
              <li key={p.id} className={`sev-${p.severity}`}>
                <div><b>{p.kind}</b> {p.title}</div>
                {p.fix && <div className="inspector__fix">→ {p.fix}</div>}
              </li>
            ))}
          </ul>
        )}
      </div>

      {(out.length > 0 || inc.length > 0) && (
        <div className="inspector__block">
          <div className="inspector__sub">СВЯЗИ</div>
          <ul className="inspector__edges">
            {out.map((e) => (
              <li key={`o-${e.target}-${e.kind}`} className={e.violation ? 'iedge--bad' : ''}>
                <button onClick={() => onFocus(e.target)}>→ {nameById.get(e.target) ?? e.target}</button>
                <span className="iedge__kind">{e.kind}</span>
              </li>
            ))}
            {inc.map((e) => (
              <li key={`i-${e.source}-${e.kind}`} className={e.violation ? 'iedge--bad' : ''}>
                <button onClick={() => onFocus(e.source)}>← {nameById.get(e.source) ?? e.source}</button>
                <span className="iedge__kind">{e.kind}</span>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}

// --- Меню проекта (по образцу прототипа): недавние + действия ---
type Recent = { path: string; name: string; nodes: number; at: number };
const RECENTS_KEY = 'af_recents';

function loadRecents(): Recent[] {
  try { return JSON.parse(localStorage.getItem(RECENTS_KEY) || '[]') as Recent[]; } catch { return []; }
}
function pushRecent(list: Recent[], r: Recent): Recent[] {
  const next = [r, ...list.filter((x) => x.path !== r.path)].slice(0, 8);
  try { localStorage.setItem(RECENTS_KEY, JSON.stringify(next)); } catch { /* quota */ }
  return next;
}
function relTime(at: number): string {
  const s = (Date.now() - at) / 1000;
  if (s < 60) return 'сейчас';
  if (s < 3600) return `${Math.floor(s / 60)} мин назад`;
  if (s < 86400) return `${Math.floor(s / 3600)} ч назад`;
  const d = Math.floor(s / 86400);
  if (d === 1) return 'вчера';
  if (d < 7) return `${d} дн. назад`;
  return 'нед. назад';
}
function healthColor(h?: string): string {
  return h === 'critical' ? '#e5484d' : h === 'warning' ? '#e0b24a' : h === 'ok' ? '#6fcf77' : '#5e6a7d';
}

// Раскрытый список отдельных фактов агрегированной проблемы (дрейф/нарушения слоёв).
// Фильтр — чтобы найти конкретный тип среди сотен (кейс 144 дрейфа): «где именно эта проблема».
// Клик по строке → факт в инспекторе (имя · слой · заметка · файл + переход к плашке).
function ProblemItems({ items, active, onReveal }: {
  items: ProblemItem[];
  active: ProblemItem | null;
  onReveal: (item: ProblemItem) => void;
}) {
  const [q, setQ] = useState('');
  const filtered = useMemo(() => {
    const s = q.trim().toLowerCase();
    if (!s) return items;
    return items.filter((it) =>
      it.name.toLowerCase().includes(s) || it.layer.toLowerCase().includes(s) || it.note.toLowerCase().includes(s));
  }, [items, q]);
  return (
    <div className="pd-items">
      {items.length > 8 && (
        <input className="pd-items__search" value={q} onChange={(e) => setQ(e.target.value)}
          placeholder={`фильтр среди ${items.length}…`} />
      )}
      <ul className="pd-items__list">
        {filtered.length === 0 ? (
          <li className="pd-items__empty">ничего не найдено</li>
        ) : filtered.map((it, i) => (
          <li key={`${it.name}-${i}`}>
            <button type="button" className={`pd-item-row${active === it ? ' is-active' : ''}`} onClick={() => onReveal(it)}
              title="показать факт (файл, слой, переход к плашке)">
              <span className="pd-item-row__name">{it.name}</span>
              {it.layer && <span className="pd-item-row__layer">{it.layer}</span>}
              {it.note && <span className="pd-item-row__note">{it.note}</span>}
            </button>
          </li>
        ))}
      </ul>
    </div>
  );
}

// Путь к файлу относительно корня скана (для читаемости): /…/product_site/backend/… → backend/…
function relPath(file: string | undefined, root: string): string {
  if (!file) return '';
  const f = file.replace(/\\/g, '/');
  const r = root.replace(/\\/g, '/').replace(/\/+$/, '');
  return r && f.startsWith(r) ? f.slice(r.length).replace(/^\/+/, '') : f;
}

// Инспектор факта: что это (имя · слой · заметка), где (файл) и переход к плашке (если тип живёт в сервисе).
function FactInspector({ fact, scanRoot, canGoto, onGoto }: {
  fact: { item: ProblemItem; problem: Problem };
  scanRoot: string;
  canGoto: boolean;
  onGoto: (item: ProblemItem) => void;
}) {
  const { item, problem } = fact;
  const rel = relPath(item.file, scanRoot);
  const cat = categoryOf(problem);
  return (
    <div className="finspect">
      <div className="finspect__name">{item.name}</div>
      <div className="finspect__tags">
        <span className={`pd-row__cat cat-${cat}`}>{cat === 'convention' ? 'свод' : 'арх'}</span>
        {item.layer && <span className="finspect__layer">{item.layer}</span>}
      </div>
      {item.note && <div className="finspect__note">{item.note}</div>}

      {problem.fix && (
        <div className="finspect__fix">
          <div className="finspect__label">как чинить</div>
          <div>{problem.fix}</div>
        </div>
      )}

      {item.file ? (
        <div className="finspect__file">
          <div className="finspect__label">файл</div>
          <div className="finspect__path" title={item.file}>{rel}{item.line ? ` : ${item.line}` : ''}</div>
          <button type="button" className="finspect__copy"
            onClick={() => void navigator.clipboard?.writeText(item.file ?? '')}>копировать путь</button>
        </div>
      ) : (
        <div className="finspect__file panel__empty">путь к файлу неизвестен</div>
      )}

      {canGoto ? (
        <button type="button" className="finspect__goto" onClick={() => onGoto(item)}>
          показать плашку <Icon name="chevronRight" size={13} />
        </button>
      ) : (
        <div className="finspect__hint">плашка недоступна: тип вне сервисного слоя (shared / unknown)</div>
      )}
    </div>
  );
}

// UI-предпочтения панелей (открыто/ширина/высота) держим до перезапуска приложения (sessionStorage,
// как ширину инспектора) — чтобы переживали перезагрузку страницы (#65/#66).
const RIGHT_MIN = 240;
const LEFT_MIN = 170, LEFT_DEFAULT = 210;
const BOTTOM_MIN = 120, BOTTOM_DEFAULT = 260;
function readRightW(): number {
  try { const v = Number(sessionStorage.getItem('af_rightW')); return v >= RIGHT_MIN ? v : 320; } catch { return 320; }
}
function readRightFull(): boolean {
  try { return sessionStorage.getItem('af_rightFull') === '1'; } catch { return false; }
}
interface ProjectActions {
  reanalyze: () => void; save: () => void; reset: () => void;
  exportJson: () => void; closeProject: () => void;
}

/**
 * Меню проекта — всегда доступно (даже без открытого проекта).
 * Есть проект → шапка (имя/путь/сводка) + действия; всегда → поле «Открыть проект» + недавние.
 */
function ProjectMenu({ graph, path, recents, onClose, actions, onBrowse, onPick }: {
  graph: SystemGraph | null;
  path: string;
  recents: Recent[];
  onClose: () => void;
  actions: ProjectActions;
  onBrowse: () => void;
  onPick: (r: Recent) => void;
}) {
  const rows: { icon: IconName; label: string; sc?: string; run: () => void }[] = [
    { icon: 'refresh', label: 'Пере-анализировать', sc: '⌘R', run: actions.reanalyze },
    { icon: 'save', label: 'Сохранить раскладку', sc: '⌘S', run: actions.save },
    { icon: 'reset', label: 'Сбросить раскладку', run: actions.reset },
    { icon: 'download', label: 'Экспорт: JSON', run: actions.exportJson },
    { icon: 'close', label: 'Закрыть проект', run: actions.closeProject },
  ];
  return (
    <div className="pm" role="menu">
      {graph && (
        <>
          <div className="pm__head">
            <div className="pm__title"><span className="pm__dot" style={{ background: healthColor(graph.health) }} />{graph.project}</div>
            <div className="pm__path" title={path}>{path}</div>
            <div className="pm__metaline">{graph.nodes.length} узлов · health {graph.health ?? '—'}</div>
          </div>
          <div className="pm__group">
            {rows.map((r) => (
              <button key={r.label} className="pm__row" onClick={() => { r.run(); onClose(); }}>
                <span className="pm__ic"><Icon name={r.icon} /></span>
                <span className="pm__rowlabel">{r.label}</span>
                {r.sc && <span className="pm__sc">{r.sc}</span>}
              </button>
            ))}
          </div>
        </>
      )}
      <div className="pm__open">
        <div className="pm__section">{graph ? 'ОТКРЫТЬ ДРУГОЙ ПРОЕКТ' : 'ОТКРЫТЬ ПРОЕКТ'}</div>
        <button className="pm__row" onClick={() => { onBrowse(); onClose(); }}>
          <span className="pm__ic"><Icon name="folder" /></span>
          <span className="pm__rowlabel">Выбрать папку…</span>
        </button>
      </div>
      {recents.length > 0 && (
        <div className="pm__recents">
          <div className="pm__section">НЕДАВНИЕ ПРОЕКТЫ</div>
          {recents.map((r) => (
            <button key={r.path} className="pm__recent" onClick={() => { onPick(r); onClose(); }}>
              <span className="pm__dot" style={{ background: r.path === path ? healthColor(graph?.health) : '#5e6a7d' }} />
              <span className="pm__rname">{r.name}</span>
              <span className="pm__rmeta">{r.nodes} узлов · {relTime(r.at)}</span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

/** L0 System Overview: карточки-сервисы + инфра + рёбра, панель «Вид», инспектор, статус-бар. */
export function SystemView() {
  const [graph, setGraph] = useState<SystemGraph | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lens, setLens] = useState<Lens>('architecture');
  const [runLayers, setRunLayers] = useState(() => readBool('af_runLayers', true)); // анализ кода: нарушения слоёв (architecture)
  const [runDrift, setRunDrift] = useState(() => readBool('af_runDrift', true));     // анализ кода: дрейф от свода (convention)
  const [selected, setSelected] = useState<Node | null>(null);
  const [drill, setDrill] = useState<Node | null>(null); // сервис, раскрытый в слои (L1); null → L0
  const [drill2, setDrill2] = useState<string | null>(null); // слой, раскрытый в типы (L2); null → L1
  const [l1Map, setL1Map] = useState<GraphDto | null>(null); // тип-граф раскрытого сервиса (богатый L1)
  const [l1Loading, setL1Loading] = useState(false);
  const [edgeKinds, setEdgeKinds] = useState<ReadonlySet<string>>(new Set());
  const [minConfidence, setMinConfidence] = useState(0);
  const [hideIsolated, setHideIsolated] = useState(false);
  const [hideTests, setHideTests] = useState(false);
  const [problemsOpen, setProblemsOpen] = useState(() => readBool('af_problemsOpen', false)); // нижняя панель «Проблемы проекта»
  // высота нижней панели (#66): при чтении клэмпим под текущий вьюпорт (сохранили на большом мониторе →
  // открыли на меньшем: без клэмпа панель выдавит статус-бар/карту). CSS max-height — бэкстоп на live-resize окна.
  const [bottomH, setBottomH] = useState(() => Math.max(BOTTOM_MIN, Math.min(readNum('af_bottomH', BOTTOM_DEFAULT, BOTTOM_MIN), Math.round(window.innerHeight * 0.7))));
  const [expandedProblems, setExpandedProblems] = useState<ReadonlySet<string>>(new Set()); // раскрытые проблемы (можно несколько сразу)
  const [selectedFact, setSelectedFact] = useState<{ item: ProblemItem; problem: Problem } | null>(null); // выбранный факт (инспектор)
  // цель перехода «к плашке»: сервис-владелец + файл/имя типа; focusId — id карточки в L2 (когда найдена)
  const revealRef = useRef<{ serviceId: string; file: string; name: string; focusId?: string } | null>(null);
  // id карточки, которую Flow центрирует при ближайшей пересборке (вместо fitView всех) — без гонки таймеров
  const focusCardRef = useRef<string | null>(null);
  // тик «показать плашку»: запускает шаг-1 на КАЖДЫЙ клик, даже если сервис уже раскрыт (drill не сменился)
  const [revealTick, setRevealTick] = useState(0);
  const [hiddenLayers, setHiddenLayers] = useState<ReadonlySet<string>>(new Set()); // скрытые слои (секция СЛОИ)
  const [leftOpen, setLeftOpen] = useState(() => readBool('af_leftOpen', true));   // панель ВИД
  const [leftW, setLeftW] = useState(() => readNum('af_leftW', LEFT_DEFAULT, LEFT_MIN)); // ширина левой панели (#66)
  const [rightOpen, setRightOpen] = useState(() => readBool('af_rightOpen', true));  // панель ИНСПЕКТОР
  // ширина инспектора: перетаскивание границы + «на всю ширину». Держим до перезапуска (sessionStorage).
  const [rightW, setRightW] = useState(readRightW);
  const [rightFull, setRightFull] = useState(readRightFull);
  const bodyRef = useRef<HTMLDivElement>(null);
  const drawerRef = useRef<HTMLDivElement>(null); // нижняя панель проблем — для ресайза по высоте (#66)
  const flowObsRef = useRef<ResizeObserver | null>(null);
  const [flowReady, setFlowReady] = useState(false); // монтируем ReactFlow только когда контейнер измерен (>0×0) — иначе warning про размер (#58)
  const [magnet, setMagnet] = useState(true); // магнит к сетке по инерции: медленно тянешь → притягивает
  const [loadedPath, setLoadedPath] = useState(''); // путь загруженного проекта — ключ раскладки
  const [branch, setBranch] = useState<string | null>(null); // git-ветка открытого проекта
  const [saved, setSaved] = useState(true);         // раскладка совпадает с сохранённой
  const [menuOpen, setMenuOpen] = useState(false);  // выпадающее меню проекта
  const [pickerOpen, setPickerOpen] = useState(false); // модалка выбора папки
  const [recents, setRecents] = useState<Recent[]>(() => loadRecents());
  const [importsOpen, setImportsOpen] = useState(false); // экран «Импорты» вместо карты
  const [imports, setImports] = useState<ImportsDto | null>(null); // прямые пакеты (ленивая загрузка)
  const [importsLoading, setImportsLoading] = useState(false);
  const [convOpen, setConvOpen] = useState(false); // модалка просмотра свода
  const [profileOpen, setProfileOpen] = useState(false); // окно «Профиль проекта» (adr/0012)
  const flowRef = useRef<FlowHandle>(null);
  const flagsRef = useRef({ layers: true, drift: true }); // текущие флаги анализа (для стабильного load)
  flagsRef.current = { layers: runLayers, drift: runDrift };
  const envRef = useRef<string | undefined>(undefined); // выбранная среда (E9.1 слой 2); undefined → default
  const loadAbortRef = useRef<AbortController | null>(null); // текущий тяжёлый анализ — рвём при вытеснении

  // Гейт рендера графа: монтируем ReactFlow только когда контейнер .sysview__flow измерен (>0×0) — иначе на
  // первом тике флекс ещё не разрешён и React Flow пишет warning про размер (#58). Callback-ref, а не одноразовый
  // эффект: пере-вооружается на КАЖДЫЙ маунт контейнера (включая возврат с экрана «Импорты»), на размонтировании
  // отключает observer и сбрасывает флаг — иначе observer течёт на отсоединённом узле, а гейт не защищает ремаунт.
  const measureFlowBox = useCallback((el: HTMLDivElement | null) => {
    flowObsRef.current?.disconnect();
    if (!el) { flowObsRef.current = null; setFlowReady(false); return; }
    const check = () => { if (el.clientWidth > 0 && el.clientHeight > 0) setFlowReady(true); };
    check();
    const ro = new ResizeObserver(check);
    ro.observe(el);
    flowObsRef.current = ro;
  }, []);

  const load = useCallback(async (rawPath: string, opts?: { cache?: boolean; refresh?: boolean }) => {
    const path = rawPath.trim();
    if (!path) return;
    const { drift, layers } = flagsRef.current; // какие код-анализы гнать (выбор пользователя)
    const env = envRef.current;                 // выбранная среда развёртывания (E9.1 слой 2)
    // Кэш на перезагрузку (#64): при restore берём готовый граф, не гоняем полный разбор.
    // Свежий разбор — «Пере-анализировать» и смена флагов (там cache не передаётся).
    if (opts?.cache) {
      const cached = readGraphCache(path, drift, layers, env);
      if (cached) {
        setGraph(cached);
        setLoadedPath(path);
        setEdgeKinds(new Set(edgeKindsOf(cached)));
        void fetchGitBranch(path).then(setBranch);
        return;
      }
    }
    loadAbortRef.current?.abort();                 // вытесняем прежний анализ → бэкенд перестаёт жечь Roslyn
    const ctrl = new AbortController();
    loadAbortRef.current = ctrl;
    setLoading(true);
    setError(null);
    setSelected(null);
    setDrill(null);
    setDrill2(null);
    setImports(null); // новый проект — сбросить кеш импортов (перезагрузятся при открытии экрана)
    try {
      const g = await fetchSystem({ path, drift, layers, env, refresh: opts?.refresh }, ctrl.signal);
      if (loadAbortRef.current !== ctrl) return; // нас вытеснили/закрыли, пока ждали — не воскрешаем состояние
      setGraph(g);
      setLoadedPath(path);
      setEdgeKinds(new Set(edgeKindsOf(g))); // по умолчанию все виды рёбер включены
      writeGraphCache(path, drift, layers, g, env); // обновить кэш на следующую перезагрузку (#64)
      setRecents((prev) => pushRecent(prev, { path, name: g.project, nodes: g.nodes.length, at: Date.now() }));
      void fetchGitBranch(path).then(setBranch); // git-ветка для статус-бара (не блокирует)
      try { localStorage.setItem('af_path', path); } catch { /* quota */ }
    } catch (e) {
      if (isAbortError(e) || loadAbortRef.current !== ctrl) return; // нас вытеснили — новый load рулит состоянием
      setError(e instanceof Error ? e.message : String(e));
      setGraph(null);
    } finally {
      if (loadAbortRef.current === ctrl) setLoading(false); // только актуальный load снимает индикатор
    }
  }, []);

  // Выбрать, какие код-анализы гнать → пере-анализировать текущий проект с новыми флагами.
  const setAnalysis = useCallback((layers: boolean, drift: boolean) => {
    setRunLayers(layers);
    setRunDrift(drift);
    flagsRef.current = { layers, drift };
    if (loadedPath) void load(loadedPath);
  }, [loadedPath, load]);

  // Выбрать среду развёртывания (E9.1 слой 2) → пере-запросить систему с ?env= (граф/deploy зависят от среды).
  const chooseEnv = useCallback((name: string) => {
    const next = name === 'default' ? undefined : name;
    if (next === envRef.current) return; // среда уже активна — не гоняем повторный анализ впустую
    envRef.current = next;
    if (loadedPath) void load(loadedPath);
  }, [loadedPath, load]);

  // Открыть НОВЫЙ проект: среду сбрасываем на default (у другого проекта свой набор сред).
  const openFresh = useCallback((path: string) => {
    envRef.current = undefined;
    void load(path);
  }, [load]);

  const closeProject = useCallback(() => {
    loadAbortRef.current?.abort(); // закрыли проект во время анализа → рвём запрос
    loadAbortRef.current = null;
    envRef.current = undefined;    // сброс выбранной среды
    setGraph(null);
    setLoadedPath('');
    setBranch(null);
    setSelected(null);
    setDrill(null);
    setDrill2(null);
    setImports(null);
    setImportsOpen(false);
    try { localStorage.removeItem('af_path'); } catch { /* ignore */ }
  }, []);

  // Экран «Импорты» открыт и данных ещё нет → ленивая загрузка /api/imports по текущему проекту.
  useEffect(() => {
    if (!importsOpen || imports || !loadedPath) return;
    let alive = true;
    const ctrl = new AbortController(); // закрыли экран/сменили проект → рвём запрос импортов
    setImportsLoading(true);
    fetchImports(loadedPath, ctrl.signal)
      .then((d) => { if (alive) setImports(d); })
      .catch((e) => { if (alive && !isAbortError(e)) setImports(null); })
      .finally(() => { if (alive) setImportsLoading(false); });
    return () => { alive = false; ctrl.abort(); };
  }, [importsOpen, imports, loadedPath]);

  const exportJson = useCallback(() => {
    if (!graph) return;
    const blob = new Blob([JSON.stringify(graph, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `${graph.project || 'system'}.json`;
    a.click();
    URL.revokeObjectURL(url);
  }, [graph]);

  // Ширина инспектора живёт до перезапуска приложения (sessionStorage).
  useEffect(() => { try { sessionStorage.setItem('af_rightW', String(rightW)); } catch { /* ignore */ } }, [rightW]);
  useEffect(() => { try { sessionStorage.setItem('af_rightFull', rightFull ? '1' : '0'); } catch { /* ignore */ } }, [rightFull]);
  // Персист состояния/размеров панелей — переживают перезагрузку (#65/#66).
  useEffect(() => setSS('af_leftOpen', leftOpen ? '1' : '0'), [leftOpen]);
  useEffect(() => setSS('af_rightOpen', rightOpen ? '1' : '0'), [rightOpen]);
  useEffect(() => setSS('af_problemsOpen', problemsOpen ? '1' : '0'), [problemsOpen]);
  useEffect(() => setSS('af_leftW', String(leftW)), [leftW]);
  useEffect(() => setSS('af_bottomH', String(bottomH)), [bottomH]);
  // Флаги код-анализа переживают перезагрузку (#65): иначе выбор пользователя сбрасывается,
  // а кэш графа (#64) записан под ключом старых флагов → промах и полный повторный разбор.
  useEffect(() => setSS('af_runLayers', runLayers ? '1' : '0'), [runLayers]);
  useEffect(() => setSS('af_runDrift', runDrift ? '1' : '0'), [runDrift]);

  const toggleRightFull = useCallback(() => setRightFull((f) => !f), []);
  // Перетаскивание левой границы инспектора → ширина (снимает режим «на всю ширину»).
  const startRightResize = useCallback((e: ReactMouseEvent) => {
    e.preventDefault();
    const body = bodyRef.current;
    if (!body) return;
    const rect = body.getBoundingClientRect();
    const leftPanelW = leftOpen ? leftW : 40;
    const max = Math.max(RIGHT_MIN, rect.width - leftPanelW - 60);
    const onMove = (ev: MouseEvent) => {
      setRightFull(false);
      setRightW(Math.min(max, Math.max(RIGHT_MIN, rect.right - ev.clientX)));
    };
    const onUp = () => {
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
      document.body.style.userSelect = '';
    };
    document.body.style.userSelect = 'none';
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  }, [leftOpen, leftW]);

  // Перетаскивание правой границы левой панели → её ширина (#66).
  const startLeftResize = useCallback((e: ReactMouseEvent) => {
    e.preventDefault();
    const body = bodyRef.current;
    if (!body) return;
    const rect = body.getBoundingClientRect();
    // когда инспектор «на всю ширину», его реальная ширина ≈ вся доска (см. calc), а не rightW —
    // тогда левую панель не даём растянуть (иначе наложение на полноширинный инспектор).
    const rightTaken = rightOpen ? (rightFull ? rect.width : rightW) : 40;
    const max = Math.max(LEFT_MIN, rect.width - rightTaken - 60);
    const onMove = (ev: MouseEvent) => setLeftW(Math.min(max, Math.max(LEFT_MIN, ev.clientX - rect.left)));
    const onUp = () => {
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
      document.body.style.userSelect = '';
    };
    document.body.style.userSelect = 'none';
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  }, [rightOpen, rightW, rightFull]);

  // Перетаскивание верхней границы нижней панели → её высота (#66). Низ панели закреплён, тянем вверх.
  const startBottomResize = useCallback((e: ReactMouseEvent) => {
    e.preventDefault();
    const bottom = drawerRef.current?.getBoundingClientRect().bottom ?? window.innerHeight;
    const max = Math.max(BOTTOM_MIN, window.innerHeight * 0.7);
    const onMove = (ev: MouseEvent) => setBottomH(Math.min(max, Math.max(BOTTOM_MIN, bottom - ev.clientY)));
    const onUp = () => {
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
      document.body.style.userSelect = '';
    };
    document.body.style.userSelect = 'none';
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  }, []);


  // Восстановить последний открытый проект при старте.
  useEffect(() => {
    let lastPath = '';
    try { lastPath = localStorage.getItem('af_path') ?? ''; } catch { /* ignore */ }
    if (lastPath) void load(lastPath, { cache: true }); // перезагрузка — из кэша, без повторного разбора (#64)
  }, [load]);

  // Клавиатура: ⌘S — сохранить раскладку; Esc — закрыть меню проекта.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 's') {
        e.preventDefault();
        flowRef.current?.save();
      } else if (e.key === 'Escape') {
        setMenuOpen(false);
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  // Полный граф → узлы/рёбра, затем детерминированная сетка (ряды сервисов + спутники под ними). Один раз на проект.
  const fullFlow = useMemo(
    () => (graph ? buildSystemFlow(graph) : { nodes: [] as Node[], edges: [] as Edge[] }),
    [graph],
  );
  const laid = useMemo(() => dagreLayout(fullFlow.nodes, fullFlow.edges), [fullFlow]);

  // Фильтр вида поверх готовой раскладки — мгновенно, без перераскладки.
  const flow = useMemo(
    () => filterFlow(laid, { edgeKinds, minConfidence, hideIsolated, hideTests }),
    [laid, edgeKinds, minConfidence, hideIsolated, hideTests],
  );
  // Раскрыли сервис → тянем его тип-граф (/api/map по папке сервиса) для богатого L1.
  useEffect(() => {
    const node = drill ? (drill.data as SystemNodeData).node : null;
    const root = node?.root;
    if (!root) { setL1Map(null); setL1Loading(false); return; } // нет папки → простой L1 по L0-сводке
    let alive = true;
    const ctrl = new AbortController(); // сменили сервис/вышли из L1 → рвём запрос карты (бэкенд не досчитывает)
    setL1Loading(true);
    setL1Map(null);
    fetchMap({ path: root }, ctrl.signal)
      .then((m) => { if (alive) setL1Map(m); })
      .catch((e) => { if (alive && !isAbortError(e)) setL1Map(null); }) // ошибка карты → откат; вытеснение — молча
      .finally(() => { if (alive) setL1Loading(false); });
    return () => { alive = false; ctrl.abort(); };
  }, [drill]);

  // L1: сервис раскрыт в слои. Есть тип-граф → богатые карточки (ELK-раскладка, async); иначе — по L0-сводке.
  const [drilledFlow, setDrilledFlow] = useState<{ nodes: Node[]; edges: Edge[] }>({ nodes: [], edges: [] });
  useEffect(() => {
    if (!drill) { setDrilledFlow({ nodes: [], edges: [] }); return; }
    const node = (drill.data as SystemNodeData).node;
    if (!l1Map) { setDrilledFlow(buildLayerFlow(node)); return; } // карта ещё грузится → простые карточки
    let alive = true;
    void buildLayerFlowRich(node, l1Map).then((rich) => {
      if (alive) setDrilledFlow(rich.nodes.length > 0 ? rich : buildLayerFlow(node));
    });
    return () => { alive = false; };
  }, [drill, l1Map]);
  // хотспоты маппинга (свод-линза): имя DTO → сколько мест сборки вне мапперов (из проблем kind dto-mapping)
  const dtoHotspots = useMemo(() => {
    const m = new Map<string, number>();
    for (const p of graph?.problems ?? []) {
      if (p.kind !== 'dto-mapping') continue;
      const name = /«([^»]+)»/.exec(p.title)?.[1];
      if (name) m.set(name, new Set((p.items ?? []).map((i) => i.file)).size || (p.items?.length ?? 0));
    }
    return m;
  }, [graph]);
  // L2: слой раскрыт в свои типы (плашки Фазы 1 + внутрислойные связи). Раскладка ELK — асинхронная.
  const [typeFlow, setTypeFlow] = useState<{ nodes: Node[]; edges: Edge[] }>({ nodes: [], edges: [] });
  useEffect(() => {
    if (!drill2 || !l1Map) { setTypeFlow({ nodes: [], edges: [] }); return; }
    let alive = true;
    void buildTypeFlow(l1Map, drill2, dtoHotspots).then((f) => { if (alive) setTypeFlow(f); });
    return () => { alive = false; };
  }, [drill2, l1Map, dtoHotspots]);
  const activeFlow = drill2 ? typeFlow : drill ? drilledFlow : flow;
  const onDoubleClick = useCallback((n: Node) => {
    if (drill2) return; // L2 — лист: члены типа смотрим в инспекторе (клик по типу)
    if (drill) { // L1 → L2: двойной клик по карточке слоя раскрывает её типы
      const layer = (n.data as LayerCardData).name;
      if (layer && l1Map) { setSelected(null); setDrill2(layer); }
      return;
    }
    const sn = (n.data as SystemNodeData).node;
    if (sn.layers && sn.layers.length > 0) { setSelected(null); setDrill(n); } // сервис со слоями → L1
  }, [drill, drill2, l1Map]);
  const allEdgeKinds = useMemo(() => (graph ? edgeKindsOf(graph) : []), [graph]);

  // Сервис-владелец файла (корень которого — префикс пути) → к нему ведёт drill для перехода к плашке.
  // владелец файла = самый специфичный root (иначе широкий root gateway перехватывает все сервисы) — #70
  const ownerService = useCallback((file?: string): SystemNode | null => ownerByRoot(graph?.nodes ?? [], file), [graph]);

  // Клик по факту → показать его в инспекторе (имя · слой · заметка · файл).
  const revealFact = useCallback((item: ProblemItem, problem: Problem) => {
    setSelected(null);
    setSelectedFact({ item, problem });
    setRightOpen(true);
  }, []);

  // «Показать плашку»: drill в сервис-владелец, дальше эффекты доводят до слоя и центрируют карточку.
  const gotoBlock = useCallback((item: ProblemItem) => {
    const svc = ownerService(item.file);
    if (!svc || !item.file) return;
    const ln = laid.nodes.find((x) => x.id === svc.id);
    if (!ln) return;
    revealRef.current = { serviceId: svc.id, file: item.file, name: item.name };
    setSelected(null);
    setSelectedFact(null);
    setDrill2(null);
    setDrill(ln);
    setRevealTick((n) => n + 1); // сервис мог быть уже раскрыт (тот же объект drill) → шаг-1 без тика не запустится
  }, [ownerService, laid]);

  // Шаг 1 навигации: сервис раскрыт (l1Map готов) → находим карточку типа по имени+файлу → открываем её слой (L2).
  useEffect(() => {
    const t = revealRef.current;
    if (!t || t.focusId || !l1Map || !drill) return;
    if ((drill.data as SystemNodeData).node.id !== t.serviceId) return; // drill ещё не на нужном сервисе — ждём
    const base = (p: string) => p.replace(/\\/g, '/').split('/').pop();
    const m = l1Map.nodes.find((n) => n.name === t.name && n.file != null && base(n.file) === base(t.file))
      ?? l1Map.nodes.find((n) => n.name === t.name);
    // не нашли — не сбрасываем цель: при смене сервиса l1Map мгновение остаётся от прошлого; дождёмся своей карты
    if (!m) return;
    t.focusId = m.id;
    focusCardRef.current = m.id;                     // Flow отцентрирует её при пересборке typeFlow
    setDrill2(m.layer || null);
  }, [l1Map, drill, revealTick]);

  // Шаг 2: слой раскрыт (typeFlow построен) → выбираем карточку (инспектор типа); центрирует её сам Flow.
  useEffect(() => {
    const t = revealRef.current;
    if (!t?.focusId) return;
    const tn = typeFlow.nodes.find((n) => n.id === t.focusId);
    if (!tn) return;                                 // typeFlow ещё не для нужного слоя
    revealRef.current = null;
    setSelected(tn);
  }, [typeFlow]);

  // Клик по проблеме/связи → навести на узел. Если location — узел: выбрать + центрировать.
  // Если это namespace-префикс (дрейф/слой per-service): навести на все узлы репозитория. Иначе — не двигать.
  const focusNode = useCallback((location: string) => {
    const node = laid.nodes.find((x) => x.id === location);
    if (node) { setSelected(node); flowRef.current?.focus([location]); return; }
    const nsIds = laid.nodes.filter((x) => namespaceOf(x.id) === location).map((x) => x.id);
    if (nsIds.length > 0) { setSelected(null); flowRef.current?.focus(nsIds); }
  }, [laid]);
  const nameById = useMemo(() => new Map((graph?.nodes ?? []).map((n) => [n.id, n.name])), [graph]);
  // Легенда «цвет плашки»: репозитории (namespace) сервис-узлов → цвет.
  const repos = useMemo(() => {
    const set = new Set<string>();
    for (const n of graph?.nodes ?? []) if (n.kind === 'service') set.add(namespaceOf(n.id));
    return [...set].sort();
  }, [graph]);
  // Секция СЛОИ: различные имена слоёв в каноническом порядке Clean Arch.
  const allLayers = useMemo(() => {
    const set = new Set<string>();
    for (const n of graph?.nodes ?? []) for (const l of n.layers ?? []) set.add(l.name);
    const order = ['API', 'Application', 'Domain', 'Infrastructure', 'Contracts', 'Persistence', 'Web', 'Shared'];
    return [...set].sort((a, b) => {
      const ia = order.indexOf(a), ib = order.indexOf(b);
      return (ia < 0 ? 99 : ia) - (ib < 0 ? 99 : ib) || a.localeCompare(b);
    });
  }, [graph]);
  const toggleLayer = (name: string) =>
    setHiddenLayers((prev) => {
      const next = new Set(prev);
      if (next.has(name)) next.delete(name); else next.add(name);
      return next;
    });
  const toggleEdgeKind = (k: string) =>
    setEdgeKinds((prev) => {
      const next = new Set(prev);
      if (next.has(k)) next.delete(k); else next.add(k);
      return next;
    });
  const counts = useMemo(() => severityCounts(graph?.problems), [graph]);
  // замечания линзы «свод» (дрейф + маппинг и т.п.) — мягкие, не входят в crit/warn статус-бара
  const convCount = useMemo(() => (graph?.problems ?? []).filter((p) => categoryOf(p) === 'convention').length, [graph]);
  const lensProblems = useMemo(() => problemsByLens(graph?.problems, lens), [graph, lens]);
  // Линза красит узлы L0 по её проблемам (#70) — чистая логика в systemModel (тестируемо).
  const lensHits = useMemo(() => lensHitsByNode(graph, lens), [graph, lens]);
  // Нижний список — ВСЕ проблемы (обе линзы), с тегом категории: линза красит граф, а не прячет список.
  const allProblems = useMemo(() => {
    const ord: Record<string, number> = { critical: 0, warning: 1, coverage: 2, info: 3 };
    return [...(graph?.problems ?? [])].sort((a, b) => (ord[a.severity] ?? 9) - (ord[b.severity] ?? 9));
  }, [graph]);
  // v3: игнорируем прежние авто-сохранённые раскладки — при открытии всегда свежая ELK-раскладка.
  const storageKey = loadedPath ? `af_layout3:${loadedPath}` : '';

  return (
    <div className="sysview">
      <header className="sysview__top">
        <div className="crumb-wrap">
          <button className="crumb crumb--project" onClick={() => setMenuOpen((o) => !o)}>
            {graph ? (
              <><span className="crumb__dot" style={{ background: healthColor(graph.health) }} />{graph.project}</>
            ) : (
              <><Icon name="folderOpen" /> Открыть проект</>
            )}
            <span className="crumb__chev"><Icon name={menuOpen ? 'chevronUp' : 'chevronDown'} size={14} /></span>
          </button>
          {menuOpen && (
            <>
              <div className="pm-backdrop" onClick={() => setMenuOpen(false)} />
              <ProjectMenu
                graph={graph}
                path={loadedPath}
                recents={recents}
                onClose={() => setMenuOpen(false)}
                actions={{
                  reanalyze: () => void load(loadedPath, { refresh: true }),
                  save: () => flowRef.current?.save(),
                  reset: () => flowRef.current?.reset(),
                  exportJson,
                  closeProject,
                }}
                onBrowse={() => setPickerOpen(true)}
                onPick={(r) => openFresh(r.path)}
              />
            </>
          )}
        </div>
        <nav className="crumbs">
          {drill2 && drill ? (
            <>
              <button className="crumb-link" onClick={() => { setDrill(null); setDrill2(null); setSelected(null); }}>System</button>
              {' ▸ '}<button className="crumb-link" onClick={() => { setDrill2(null); setSelected(null); }}>{(drill.data as SystemNodeData).node.name}</button>
              {' ▸ '}<b>{drill2}</b>{' ▸ Типы'}
              <span className="crumbs__hint"> · {typeFlow.nodes.length} тип(ов) слоя · клик по типу → его члены в инспекторе</span>
            </>
          ) : drill ? (
            <>
              <button className="crumb-link" onClick={() => { setDrill(null); setSelected(null); }}>System</button>
              {' ▸ '}<b>{(drill.data as SystemNodeData).node.name}</b>{' ▸ Слои'}
              {(() => { const eps = (drill.data as SystemNodeData).node.endpoints?.length ?? 0;
                return eps > 0 ? <span className="crumbs__hint"> · {eps} endpoint(ов) справа</span> : null; })()}
              {l1Loading && <span className="crumbs__hint"> · загрузка слоёв…</span>}
            </>
          ) : (
            <><b>System</b> ▸ Service ▸ Layer ▸ Type ▸ Member</>
          )}
        </nav>
        <div className="sysview__right">
          <SessionBadge />
          {graph?.environments && graph.environments.length > 1 && !importsOpen && (
            <div className="envsw" role="group" aria-label="Среда развёртывания">
              <span className="envsw__label">СРЕДА</span>
              {graph.environments.map((e) => (
                <button
                  key={e.id}
                  className={`envsw__opt ${e.active ? 'is-active' : ''}`}
                  aria-pressed={e.active ?? false}
                  onClick={() => chooseEnv(e.name)}
                  disabled={loading}
                  title={`Собрать систему для среды «${e.name}»`}
                >
                  {e.name}
                </button>
              ))}
            </div>
          )}
          {graph && !importsOpen && (
            <span className={`saved ${saved ? 'saved--ok' : 'saved--dirty'}`}>
              {saved ? '✓ сохранено' : '● есть изменения'}
            </span>
          )}
          {graph && (
            <button
              className={`sysview__nav ${importsOpen ? 'is-on' : ''}`}
              onClick={() => setImportsOpen((o) => !o)}
              title={importsOpen ? 'Вернуться к карте' : 'Импорты (прямые пакеты)'}
            >
              <Icon name="box" size={15} />{importsOpen ? 'карта' : 'импорты'}
            </button>
          )}
        </div>
      </header>

      <div className="sysview__body" ref={bodyRef}>
        {importsOpen ? (
          <ImportsScreen imports={imports} loading={importsLoading} />
        ) : (
        <>
        <aside className={`panel panel--view ${leftOpen ? '' : 'is-collapsed'}`} style={leftOpen ? { width: leftW } : undefined}>
          {leftOpen && <div className="panel__resize panel__resize--right" onMouseDown={startLeftResize} title="Потянуть — изменить ширину" />}
          {!leftOpen ? (
            <button className="panel__collapsed" onClick={() => setLeftOpen(true)} title="Показать ВИД">
              <Icon name="chevronRight" size={16} />
              <span className="panel__vtitle">ВИД</span>
            </button>
          ) : (
          <>
          <button className="panel__header" onClick={() => setLeftOpen(false)}>
            ВИД<Icon name="chevronLeft" size={15} />
          </button>
          <div className="panel__body">
          <div className="panel__group">
            <div className="panel__label">ЛИНЗА</div>
            <label className="opt">
              <input type="radio" name="lens" checked={lens === 'architecture'} onChange={() => setLens('architecture')} />
              реальные проблемы
            </label>
            <label className="opt">
              <input type="radio" name="lens" checked={lens === 'convention'} onChange={() => setLens('convention')} />
              дрейф от свода
            </label>
            <div className="panel__hint">
              {lens === 'architecture'
                ? 'красным — узлы с реальными арх-проблемами (общая БД, циклы, нарушения слоёв)'
                : 'жёлтым — узлы с дрейфом от свода (типы не по конвенциям)'}
            </div>
          </div>
          <div className="panel__group">
            <div className="panel__label">АНАЛИЗ КОДА</div>
            <label className="opt">
              <input type="checkbox" checked={runLayers} disabled={loading}
                onChange={(e) => setAnalysis(e.target.checked, runDrift)} />
              нарушения слоёв
            </label>
            <label className="opt">
              <input type="checkbox" checked={runDrift} disabled={loading}
                onChange={(e) => setAnalysis(runLayers, e.target.checked)} />
              дрейф от свода
            </label>
            {graph?.conventions && (
              <button className="panel__conv" onClick={() => setConvOpen(true)}
                title="archforge.conventions.json в корне проекта переопределяет/дополняет свод. Клик — посмотреть содержимое.">
                <span className="panel__conv-src">свод: {graph.conventions.source}</span>
                <span className="panel__conv-cnt">
                  {graph.conventions.markers.length + graph.conventions.fallback.length} маркер(ов) · {graph.conventions.rules.length} правил(о) · смотреть →
                </span>
              </button>
            )}
            {loadedPath && (
              <button className="panel__conv" onClick={() => setProfileOpen(true)}
                title="Уровень проекта, строгость правил, словарь ролей и свои конфигурации анализа">
                <span className="panel__conv-src">профиль проекта</span>
                <span className="panel__conv-cnt">уровень · правила · словарь · настроить →</span>
              </button>
            )}
            <div className="panel__hint">{loading ? 'анализирую…' : 'по отдельности или вместе; структурный анализ идёт всегда'}</div>
          </div>
          {allLayers.length > 0 && (
            <div className="panel__group">
              <div className="panel__label">СЛОИ</div>
              {allLayers.map((name) => (
                <label key={name} className="opt">
                  <input type="checkbox" checked={!hiddenLayers.has(name)} onChange={() => toggleLayer(name)} />
                  <span className="legend__bar" style={{ background: layerColor(name) }} />
                  {name}
                </label>
              ))}
            </div>
          )}
          <div className="panel__group">
            <div className="panel__label">РЁБРА</div>
            {allEdgeKinds.length === 0 && <div className="panel__empty">—</div>}
            {allEdgeKinds.map((k) => {
              const st = edgeStyle(k, false);
              return (
                <label key={k} className="opt">
                  <input type="checkbox" checked={edgeKinds.has(k)} onChange={() => toggleEdgeKind(k)} />
                  <span
                    className="legend__line"
                    style={{ borderTopColor: st.stroke, borderTopStyle: st.strokeDasharray ? 'dashed' : 'solid' }}
                  />
                  {k}
                </label>
              );
            })}
          </div>
          <div className="panel__group">
            <div className="panel__label">УВЕРЕННОСТЬ · порог {minConfidence.toFixed(2)}</div>
            <input
              type="range" min={0} max={1} step={0.05} value={minConfidence}
              onChange={(e) => setMinConfidence(Number(e.target.value))} style={{ width: '100%' }}
            />
            <div className="panel__hint">толщина линии = уверенность; порог прячет слабые</div>
          </div>
          <div className="panel__group">
            <div className="panel__label">ФИЛЬТРЫ</div>
            <label className="opt">
              <input type="checkbox" checked={hideIsolated} onChange={(e) => setHideIsolated(e.target.checked)} />
              без одиночек
            </label>
            <label className="opt">
              <input type="checkbox" checked={hideTests} onChange={(e) => setHideTests(e.target.checked)} />
              без tests
            </label>
            <label className="opt">
              <input type="checkbox" checked={magnet} onChange={(e) => setMagnet(e.target.checked)} />
              магнит к сетке
            </label>
            <div className="panel__hint">медленно тянешь — притягивает к точкам, быстро — свободно</div>
          </div>
          {repos.length > 0 && (
            <div className="panel__group">
              <div className="panel__label">ЦВЕТ ПЛАШКИ = РЕПО</div>
              {repos.map((ns) => (
                <div key={ns} className="legend">
                  <span className="legend__dot" style={{ background: repoColor(ns) }} />
                  {ns || '—'}
                </div>
              ))}
            </div>
          )}
          </div>
          </>
          )}
        </aside>

        <main className="sysview__board">
          {error && <div className="sysview__error">{error}</div>}
          <div className="sysview__flow" ref={measureFlowBox}>
            {graph ? (
              flowReady && (
              <HiddenLayersContext.Provider value={hiddenLayers}>
                <LensContext.Provider value={{ lens, hits: lensHits }}>
                <ReactFlowProvider>
                  <Flow
                    ref={flowRef}
                    flow={activeFlow}
                    magnet={magnet}
                    storageKey={drill ? '' : storageKey}
                    onDirty={setSaved}
                    selectedId={selected?.id ?? null}
                    onSelect={(n) => { setSelected(n); setSelectedFact(null); }}
                    onDoubleClick={onDoubleClick}
                    onClear={() => setSelected(null)}
                    focusTargetRef={focusCardRef}
                  />
                </ReactFlowProvider>
                </LensContext.Provider>
              </HiddenLayersContext.Provider>
              )
            ) : (
              <div className="sysview__empty">
                {loading ? 'анализирую…' : <>Проект не открыт. Нажми <b>«Открыть проект»</b> вверху слева.</>}
              </div>
            )}
          </div>
        </main>

        <aside
          className={`panel panel--inspector ${rightOpen ? '' : 'is-collapsed'}`}
          style={rightOpen ? { width: rightFull ? `calc(100% - ${leftOpen ? leftW : 40}px - 6px)` : rightW } : undefined}
        >
          {!rightOpen ? (
            <button className="panel__collapsed" onClick={() => setRightOpen(true)} title="Показать инспектор">
              <Icon name="chevronLeft" size={16} />
              <span className="panel__vtitle">ИНСПЕКТОР</span>
            </button>
          ) : (
            <>
              <div className="panel__resize" onMouseDown={startRightResize} title="Потянуть — изменить ширину" />
              <div className="panel__header panel__header--tools">
                <span className="panel__htitle">ИНСПЕКТОР</span>
                <button
                  className="panel__tool"
                  onClick={toggleRightFull}
                  title={rightFull ? 'Вернуть ширину' : 'На всю ширину'}
                >
                  <Icon name={rightFull ? 'shrinkRight' : 'expandLeft'} size={15} />
                </button>
                <button className="panel__tool" onClick={() => setRightOpen(false)} title="Свернуть">
                  <Icon name="chevronRight" size={15} />
                </button>
              </div>
              <div className="panel__body">
                {selected && graph
                  ? <Inspector node={selected} graph={graph} lens={lens} onFocus={focusNode} />
                  : selectedFact
                    ? <FactInspector fact={selectedFact} scanRoot={loadedPath}
                        canGoto={!!ownerService(selectedFact.item.file)} onGoto={gotoBlock} />
                    : drill && !drill2
                      ? <ServiceEndpoints node={(drill.data as SystemNodeData).node} />
                      : <div className="panel__empty">кликните узел</div>}
              </div>
            </>
          )}
        </aside>
        </>
        )}
      </div>

      {/* раскрывающийся список проблем (над полосой «ПРОБЛЕМЫ ПРОЕКТА») */}
      {graph && problemsOpen && (
        <div className="problems-drawer" ref={drawerRef} style={{ height: bottomH }}>
          <div className="pd__resize" onMouseDown={startBottomResize} title="Потянуть — изменить высоту" />
          <div className="pd__list">
            {allProblems.length === 0 ? (
              <div className="panel__empty">проблем не найдено</div>
            ) : allProblems.map((p) => {
              const hasItems = (p.items?.length ?? 0) > 0;
              const open = expandedProblems.has(p.id);
              const toggle = () => setExpandedProblems((prev) => {
                const next = new Set(prev);
                if (next.has(p.id)) next.delete(p.id); else next.add(p.id);
                return next;
              });
              return (
                <div key={p.id} className={`pd-item sev-${p.severity}`}>
                  <button className={`pd-row sev-${p.severity}`}
                    onClick={() => (hasItems ? toggle() : focusNode(p.location))}
                    title={hasItems ? 'показать все факты' : 'навести на узел'}>
                    <span className="pd-row__chev">{hasItems ? <Icon name={open ? 'chevronDown' : 'chevronRight'} size={12} /> : null}</span>
                    <span className={`pd-row__cat cat-${categoryOf(p)}`}>{categoryOf(p) === 'convention' ? 'свод' : 'арх'}</span>
                    <span className="pd-row__kind">{p.kind}</span>
                    <span className="pd-row__title">{p.title}</span>
                    <span className="pd-row__loc">{nameById.get(p.location) ?? p.location}</span>
                  </button>
                  {open && hasItems && (
                    <ProblemItems items={p.items!}
                      active={selectedFact?.problem.id === p.id ? selectedFact.item : null}
                      onReveal={(it) => revealFact(it, p)} />
                  )}
                </div>
              );
            })}
          </div>
        </div>
      )}

      {/* полоса-заголовок панели проблем — выше статус-бара, раскрывается */}
      {graph && (
        <button className="problems-bar" onClick={() => setProblemsOpen((o) => !o)}>
          <span className="pb__title">ПРОБЛЕМЫ ПРОЕКТА</span>
          <span className="pb-badge pb-badge--crit">⊖ {counts.critical}</span>
          <span className="pb-badge pb-badge--warn">⚠ {counts.warning}</span>
          <span className="pb-badge pb-badge--cov">○ {counts.coverage}</span>
          <span className="pb-badge pb-badge--drift">≈ {convCount} свод</span>
          <span className="pb__toggle">
            {problemsOpen ? 'свернуть' : 'развернуть'}
            <Icon name={problemsOpen ? 'chevronDown' : 'chevronUp'} size={13} />
          </span>
        </button>
      )}

      <footer className="sysview__status">
        <span className="status__proj">◆ arch_forge</span>
        {branch && <span className="status__branch"><Icon name="branch" size={13} /> {branch}</span>}
        {graph && (graph.degradations?.length
          ? <span className="status__degraded" title={`Карта неполная — не всё удалось разобрать:\n• ${graph.degradations.join('\n• ')}`}>
              ⚠ карта неполная: {graph.degradations.length}
            </span>
          : <span className="status__ok">✓ анализ актуален</span>)}
        <span className="status__right">
          <span>● {counts.critical} · ⚠ {counts.warning} · ○ {counts.coverage} · свод {convCount}</span>
          <span>{activeFlow.nodes.length} узлов · {activeFlow.edges.length} связей</span>
          <span>линза «{lens === 'architecture' ? 'реальные' : 'дрейф'}»: {lensProblems.length}</span>
        </span>
      </footer>

      {pickerOpen && (
        <FolderPicker
          initial={loadedPath || undefined}
          onSelect={(p) => { setPickerOpen(false); openFresh(p); }}
          onClose={() => setPickerOpen(false)}
        />
      )}
      {convOpen && graph?.conventions && (
        <ConventionsView conventions={graph.conventions} onClose={() => setConvOpen(false)} />
      )}
      {profileOpen && loadedPath && (
        <ProfileView path={loadedPath} onClose={() => setProfileOpen(false)} onApplied={() => void load(loadedPath)} />
      )}
    </div>
  );
}
