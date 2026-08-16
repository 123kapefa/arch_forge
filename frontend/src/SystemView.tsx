import {
  forwardRef, useCallback, useEffect, useImperativeHandle, useMemo, useRef, useState,
  type MouseEvent as ReactMouseEvent,
} from 'react';
import {
  ReactFlow, ReactFlowProvider, useReactFlow, useNodesState, useEdgesState,
  Background, Controls, MiniMap, type Edge, type Node, type NodeChange,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import './system.css';

import { fetchSystem, type SystemGraph } from './system';
import { fetchMap, type GraphDto, type MemberDto } from './api';
import { buildSystemFlow, buildLayerFlow, filterFlow, edgeKindsOf, edgeStyle, type SystemNodeData } from './systemFlow';
import { buildLayerFlowRich, buildTypeFlow, type LayerCardData } from './layerFlow';
import { gridLayout } from './gridLayout';
import { RoutedEdge } from './RoutedEdge';
import { OrthoEdge } from './OrthoEdge';
import { SystemNodeView } from './nodes/SystemNodeView';
import { LayerCard } from './nodes/LayerCard';
import { type TypeNodeData } from './nodes/TypeNode';
import { TypeCard } from './nodes/TypeCard';
import { problemsByLens, severityCounts, namespaceOf, type Lens } from './systemModel';
import { repoColor, layerColor } from './theme';
import { HiddenLayersContext } from './layersContext';
import { FolderPicker } from './FolderPicker';
import { fetchGitBranch } from './fs';
import { Icon, type IconName } from './Icon';

const nodeTypes = { sys: SystemNodeView, layer: LayerCard, type: TypeCard };
const edgeTypes = { routed: RoutedEdge, ortho: OrthoEdge };
const GRID = 20; // шаг сетки: точки фона + шаг магнита
// Порог скорости указателя (px/мс): медленнее (малая инерция) → магнит к сетке; быстрее → свободно.
// Низкий порог = магнит только при явно медленном, «прицельном» движении, а не при обычном перетаскивании.
const MAGNET_SPEED = 0.05;

export type FlowHandle = { save: () => void; reset: () => void; focus: (ids: string[]) => void };

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
}>(function Flow({ flow, magnet, storageKey, onDirty, selectedId, onSelect, onDoubleClick, onClear }, ref) {
  const [nodes, setNodes, onNodesChange] = useNodesState(flow.nodes);
  const [edges, setEdges, onEdgesChange] = useEdgesState(flow.edges);
  const [snapNow, setSnapNow] = useState(false); // притяжение активно прямо сейчас (низкая скорость)
  const [hoverId, setHoverId] = useState<string | null>(null); // узел под курсором — подсветка связей
  const last = useRef<{ x: number; y: number; t: number } | null>(null);
  const nodesRef = useRef(nodes);
  nodesRef.current = nodes;
  const rf = useReactFlow();

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
    setNodes(applySaved(flow.nodes));
    setEdges(flow.edges);
    onDirty(true); // после загрузки/восстановления раскладка считается сохранённой
    if (flow.nodes.length === 0) return;
    const t = setTimeout(() => rf.fitView({ padding: 0.2 }), 60);
    return () => clearTimeout(t);
  }, [flow, applySaved, setNodes, setEdges, rf, onDirty]);

  useImperativeHandle(ref, () => ({
    save() { persist(); },
    reset() {
      if (storageKey) { try { localStorage.removeItem(storageKey); } catch { /* ignore */ } }
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
  const displayEdges = useMemo(() => {
    if (!highlightId) return edges;
    return edges.map((e) => {
      const on = e.source === highlightId || e.target === highlightId;
      return {
        ...e,
        zIndex: on ? 1000 : 0,
        style: { ...e.style, opacity: on ? 1 : 0.08, strokeWidth: on ? 3 : e.style?.strokeWidth },
      };
    });
  }, [edges, highlightId]);

  return (
    <ReactFlow
      nodes={nodes}
      edges={displayEdges}
      onNodesChange={handleNodesChange}
      onEdgesChange={onEdgesChange}
      nodeTypes={nodeTypes}
      edgeTypes={edgeTypes}
      minZoom={0.08}
      proOptions={{ hideAttribution: true }}
      nodesConnectable={false}
      elevateEdgesOnSelect
      snapToGrid={magnet && snapNow}
      snapGrid={[GRID, GRID]}
      onNodeDragStart={() => { last.current = null; }}
      onNodeDrag={onNodeDrag}
      onNodeDragStop={() => { last.current = null; setSnapNow(false); }}
      onNodeMouseEnter={(_, n) => setHoverId(n.id)}
      onNodeMouseLeave={() => setHoverId(null)}
      onNodeClick={(_, n) => onSelect(n)}
      onNodeDoubleClick={(_, n) => onDoubleClick(n)}
      onPaneClick={onClear}
    >
      <Background gap={GRID} />
      <Controls />
      <MiniMap pannable zoomable nodeColor="#3a4658" maskColor="rgba(11,15,23,0.6)" style={{ width: 210, height: 168 }} />
    </ReactFlow>
  );
});

const KIND_LABEL: Record<string, string> = {
  service: 'сервис', datastore: 'хранилище', topic: 'шина', external: 'внешний',
};

/** Инспектор для выбранного узла — диспетчер по уровню: системный узел (L0) / слой (L1) / тип+члены (L2). */
function Inspector({ node, graph, lens, onFocus }: {
  node: Node; graph: SystemGraph; lens: Lens; onFocus: (id: string) => void;
}) {
  const d = node.data as Record<string, unknown>;
  if (d.node) return <SystemInspector node={node} graph={graph} lens={lens} onFocus={onFocus} />;
  if ('role' in d && 'via' in d) {
    return <TypeInspector data={node.data as unknown as TypeNodeData} members={(d.members as MemberDto[] | undefined) ?? []} />;
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
function TypeInspector({ data, members }: { data: TypeNodeData; members: MemberDto[] }) {
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

// Ширина инспектора — до перезапуска приложения (sessionStorage, не localStorage).
const RIGHT_MIN = 240;
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
export function SystemView({ onExit }: { onExit?: () => void }) {
  const [graph, setGraph] = useState<SystemGraph | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lens, setLens] = useState<Lens>('architecture');
  const [runLayers, setRunLayers] = useState(true); // анализ кода: нарушения слоёв (architecture)
  const [runDrift, setRunDrift] = useState(true);   // анализ кода: дрейф от свода (convention)
  const [selected, setSelected] = useState<Node | null>(null);
  const [drill, setDrill] = useState<Node | null>(null); // сервис, раскрытый в слои (L1); null → L0
  const [drill2, setDrill2] = useState<string | null>(null); // слой, раскрытый в типы (L2); null → L1
  const [l1Map, setL1Map] = useState<GraphDto | null>(null); // тип-граф раскрытого сервиса (богатый L1)
  const [l1Loading, setL1Loading] = useState(false);
  const [edgeKinds, setEdgeKinds] = useState<ReadonlySet<string>>(new Set());
  const [minConfidence, setMinConfidence] = useState(0);
  const [hideIsolated, setHideIsolated] = useState(false);
  const [hideTests, setHideTests] = useState(false);
  const [problemsOpen, setProblemsOpen] = useState(false); // нижняя панель «Проблемы проекта»
  const [hiddenLayers, setHiddenLayers] = useState<ReadonlySet<string>>(new Set()); // скрытые слои (секция СЛОИ)
  const [leftOpen, setLeftOpen] = useState(true);   // панель ВИД
  const [rightOpen, setRightOpen] = useState(true);  // панель ИНСПЕКТОР
  // ширина инспектора: перетаскивание границы + «на всю ширину». Держим до перезапуска (sessionStorage).
  const [rightW, setRightW] = useState(readRightW);
  const [rightFull, setRightFull] = useState(readRightFull);
  const bodyRef = useRef<HTMLDivElement>(null);
  const [magnet, setMagnet] = useState(true); // магнит к сетке по инерции: медленно тянешь → притягивает
  const [loadedPath, setLoadedPath] = useState(''); // путь загруженного проекта — ключ раскладки
  const [branch, setBranch] = useState<string | null>(null); // git-ветка открытого проекта
  const [saved, setSaved] = useState(true);         // раскладка совпадает с сохранённой
  const [menuOpen, setMenuOpen] = useState(false);  // выпадающее меню проекта
  const [pickerOpen, setPickerOpen] = useState(false); // модалка выбора папки
  const [recents, setRecents] = useState<Recent[]>(() => loadRecents());
  const flowRef = useRef<FlowHandle>(null);
  const flagsRef = useRef({ layers: true, drift: true }); // текущие флаги анализа (для стабильного load)
  flagsRef.current = { layers: runLayers, drift: runDrift };

  const load = useCallback(async (rawPath: string) => {
    const path = rawPath.trim();
    if (!path) return;
    setLoading(true);
    setError(null);
    setSelected(null);
    setDrill(null);
    setDrill2(null);
    try {
      // какие код-анализы гнать — выбор пользователя (флаги drift/layers)
      const g = await fetchSystem({ path, drift: flagsRef.current.drift, layers: flagsRef.current.layers });
      setGraph(g);
      setLoadedPath(path);
      setEdgeKinds(new Set(edgeKindsOf(g))); // по умолчанию все виды рёбер включены
      setRecents((prev) => pushRecent(prev, { path, name: g.project, nodes: g.nodes.length, at: Date.now() }));
      void fetchGitBranch(path).then(setBranch); // git-ветка для статус-бара (не блокирует)
      try { localStorage.setItem('af_path', path); } catch { /* quota */ }
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      setGraph(null);
    } finally {
      setLoading(false);
    }
  }, []);

  // Выбрать, какие код-анализы гнать → пере-анализировать текущий проект с новыми флагами.
  const setAnalysis = useCallback((layers: boolean, drift: boolean) => {
    setRunLayers(layers);
    setRunDrift(drift);
    flagsRef.current = { layers, drift };
    if (loadedPath) void load(loadedPath);
  }, [loadedPath, load]);

  const closeProject = useCallback(() => {
    setGraph(null);
    setLoadedPath('');
    setBranch(null);
    setSelected(null);
    setDrill(null);
    setDrill2(null);
    try { localStorage.removeItem('af_path'); } catch { /* ignore */ }
  }, []);

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

  const toggleRightFull = useCallback(() => setRightFull((f) => !f), []);
  // Перетаскивание левой границы инспектора → ширина (снимает режим «на всю ширину»).
  const startRightResize = useCallback((e: ReactMouseEvent) => {
    e.preventDefault();
    const body = bodyRef.current;
    if (!body) return;
    const rect = body.getBoundingClientRect();
    const leftW = leftOpen ? 210 : 40;
    const max = Math.max(RIGHT_MIN, rect.width - leftW - 60);
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
  }, [leftOpen]);


  // Восстановить последний открытый проект при старте.
  useEffect(() => {
    let lastPath = '';
    try { lastPath = localStorage.getItem('af_path') ?? ''; } catch { /* ignore */ }
    if (lastPath) void load(lastPath);
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
  const laid = useMemo(() => gridLayout(fullFlow.nodes, fullFlow.edges), [fullFlow]);

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
    setL1Loading(true);
    setL1Map(null);
    fetchMap({ path: root })
      .then((m) => { if (alive) setL1Map(m); })
      .catch(() => { if (alive) setL1Map(null); })   // ошибка карты → откат на простой L1
      .finally(() => { if (alive) setL1Loading(false); });
    return () => { alive = false; };
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
  // L2: слой раскрыт в свои типы (плашки Фазы 1 + внутрислойные связи). Раскладка ELK — асинхронная.
  const [typeFlow, setTypeFlow] = useState<{ nodes: Node[]; edges: Edge[] }>({ nodes: [], edges: [] });
  useEffect(() => {
    if (!drill2 || !l1Map) { setTypeFlow({ nodes: [], edges: [] }); return; }
    let alive = true;
    void buildTypeFlow(l1Map, drill2).then((f) => { if (alive) setTypeFlow(f); });
    return () => { alive = false; };
  }, [drill2, l1Map]);
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
  const lensProblems = useMemo(() => problemsByLens(graph?.problems, lens), [graph, lens]);
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
                  reanalyze: () => void load(loadedPath),
                  save: () => flowRef.current?.save(),
                  reset: () => flowRef.current?.reset(),
                  exportJson,
                  closeProject,
                }}
                onBrowse={() => setPickerOpen(true)}
                onPick={(r) => void load(r.path)}
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
              {l1Loading
                ? <span className="crumbs__hint"> · загрузка слоёв…</span>
                : l1Map
                  ? <span className="crumbs__hint"> · двойной клик по слою → типы · <span className="crumbs__viol">красное = нарушение свода</span></span>
                  : <span className="crumbs__hint"> · сводка слоёв</span>}
            </>
          ) : (
            <><b>System</b> ▸ Service ▸ Layer ▸ Type ▸ Member</>
          )}
        </nav>
        <div className="sysview__right">
          {graph && (
            <span className={`saved ${saved ? 'saved--ok' : 'saved--dirty'}`}>
              {saved ? '✓ сохранено' : '● есть изменения'}
            </span>
          )}
          {onExit && <button className="sysview__exit" onClick={onExit}>← тип-граф (Ф1)</button>}
        </div>
      </header>

      <div className="sysview__body" ref={bodyRef}>
        <aside className={`panel panel--view ${leftOpen ? '' : 'is-collapsed'}`}>
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
            <div className="panel__hint">скрыть слабые/эвристические связи</div>
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
          <div className="sysview__flow">
            {graph ? (
              <HiddenLayersContext.Provider value={hiddenLayers}>
                <ReactFlowProvider>
                  <Flow
                    ref={flowRef}
                    flow={activeFlow}
                    magnet={magnet}
                    storageKey={drill ? '' : storageKey}
                    onDirty={setSaved}
                    selectedId={selected?.id ?? null}
                    onSelect={setSelected}
                    onDoubleClick={onDoubleClick}
                    onClear={() => setSelected(null)}
                  />
                </ReactFlowProvider>
              </HiddenLayersContext.Provider>
            ) : (
              <div className="sysview__empty">
                {loading ? 'анализирую…' : <>Проект не открыт. Нажми <b>«Открыть проект»</b> вверху слева.</>}
              </div>
            )}
          </div>
        </main>

        <aside
          className={`panel panel--inspector ${rightOpen ? '' : 'is-collapsed'}`}
          style={rightOpen ? { width: rightFull ? `calc(100% - ${leftOpen ? 210 : 40}px - 6px)` : rightW } : undefined}
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
                  : <div className="panel__empty">кликните узел</div>}
              </div>
            </>
          )}
        </aside>
      </div>

      {/* раскрывающийся список проблем (над полосой «ПРОБЛЕМЫ ПРОЕКТА») */}
      {graph && problemsOpen && (
        <div className="problems-drawer">
          <div className="pd__list">
            {lensProblems.length === 0 ? (
              <div className="panel__empty">нет проблем в этой линзе</div>
            ) : lensProblems.map((p) => (
              <button key={p.id} className={`pd-row sev-${p.severity}`} onClick={() => focusNode(p.location)}>
                <span className="pd-row__kind">{p.kind}</span>
                <span className="pd-row__title">{p.title}</span>
                <span className="pd-row__loc">{nameById.get(p.location) ?? p.location}</span>
              </button>
            ))}
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
          <span className="pb__toggle">
            {problemsOpen ? 'свернуть' : 'развернуть'}
            <Icon name={problemsOpen ? 'chevronDown' : 'chevronUp'} size={13} />
          </span>
        </button>
      )}

      <footer className="sysview__status">
        <span className="status__proj">◆ arch_forge</span>
        {branch && <span className="status__branch"><Icon name="branch" size={13} /> {branch}</span>}
        {graph && <span className="status__ok">✓ анализ актуален</span>}
        <span className="status__right">
          <span>● {counts.critical} · ⚠ {counts.warning} · ○ {counts.coverage} · дрейф {counts.info}</span>
          <span>{flow.nodes.length} узлов · {flow.edges.length} связей</span>
          <span>линза «{lens === 'architecture' ? 'реальные' : 'дрейф'}»: {lensProblems.length}</span>
        </span>
      </footer>

      {pickerOpen && (
        <FolderPicker
          initial={loadedPath || undefined}
          onSelect={(p) => { setPickerOpen(false); void load(p); }}
          onClose={() => setPickerOpen(false)}
        />
      )}
    </div>
  );
}
