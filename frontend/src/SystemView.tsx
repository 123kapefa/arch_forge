import {
  forwardRef, useCallback, useEffect, useImperativeHandle, useMemo, useRef, useState,
} from 'react';
import {
  ReactFlow, ReactFlowProvider, useReactFlow, useNodesState, useEdgesState,
  Background, Controls, MiniMap, type Edge, type Node, type NodeChange,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import './system.css';

import { fetchSystem, type SystemGraph } from './system';
import { buildSystemFlow, filterFlow, edgeKindsOf, edgeStyle, type SystemNodeData } from './systemFlow';
import { gridLayout } from './gridLayout';
import { RoutedEdge } from './RoutedEdge';
import { SystemNodeView } from './nodes/SystemNodeView';
import { problemsByLens, severityCounts, namespaceOf, type Lens } from './systemModel';
import { repoColor } from './theme';
import { FolderPicker } from './FolderPicker';
import { Icon, type IconName } from './Icon';

const nodeTypes = { sys: SystemNodeView };
const edgeTypes = { routed: RoutedEdge };
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
  onClear: () => void;
}>(function Flow({ flow, magnet, storageKey, onDirty, selectedId, onSelect, onClear }, ref) {
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
      elevateEdgesOnSelect
      snapToGrid={magnet && snapNow}
      snapGrid={[GRID, GRID]}
      onNodeDragStart={() => { last.current = null; }}
      onNodeDrag={onNodeDrag}
      onNodeDragStop={() => { last.current = null; setSnapNow(false); }}
      onNodeMouseEnter={(_, n) => setHoverId(n.id)}
      onNodeMouseLeave={() => setHoverId(null)}
      onNodeClick={(_, n) => onSelect(n)}
      onPaneClick={onClear}
    >
      <Background gap={GRID} />
      <Controls />
      <MiniMap pannable zoomable nodeColor="#3a4658" maskColor="rgba(11,15,23,0.6)" />
    </ReactFlow>
  );
});

const KIND_LABEL: Record<string, string> = {
  service: 'сервис', datastore: 'хранилище', topic: 'шина', external: 'внешний',
};

function Inspector({ node, graph, lens, onFocus }: {
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
  const [selected, setSelected] = useState<Node | null>(null);
  const [edgeKinds, setEdgeKinds] = useState<ReadonlySet<string>>(new Set());
  const [minConfidence, setMinConfidence] = useState(0);
  const [hideIsolated, setHideIsolated] = useState(false);
  const [hideTests, setHideTests] = useState(false);
  const [problemsOpen, setProblemsOpen] = useState(false); // нижняя панель «Проблемы проекта»
  const [leftOpen, setLeftOpen] = useState(true);   // панель ВИД
  const [rightOpen, setRightOpen] = useState(true);  // панель ИНСПЕКТОР
  const [magnet, setMagnet] = useState(true); // магнит к сетке по инерции: медленно тянешь → притягивает
  const [loadedPath, setLoadedPath] = useState(''); // путь загруженного проекта — ключ раскладки
  const [saved, setSaved] = useState(true);         // раскладка совпадает с сохранённой
  const [menuOpen, setMenuOpen] = useState(false);  // выпадающее меню проекта
  const [pickerOpen, setPickerOpen] = useState(false); // модалка выбора папки
  const [recents, setRecents] = useState<Recent[]>(() => loadRecents());
  const flowRef = useRef<FlowHandle>(null);

  const load = useCallback(async (rawPath: string) => {
    const path = rawPath.trim();
    if (!path) return;
    setLoading(true);
    setError(null);
    setSelected(null);
    try {
      // код-линзы включены: нужны проблемы слоёв/дрейфа + сводка сервисов (для drill)
      const g = await fetchSystem({ path, drift: true, layers: true });
      setGraph(g);
      setLoadedPath(path);
      setEdgeKinds(new Set(edgeKindsOf(g))); // по умолчанию все виды рёбер включены
      setRecents((prev) => pushRecent(prev, { path, name: g.project, nodes: g.nodes.length, at: Date.now() }));
      try { localStorage.setItem('af_path', path); } catch { /* quota */ }
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      setGraph(null);
    } finally {
      setLoading(false);
    }
  }, []);

  const closeProject = useCallback(() => {
    setGraph(null);
    setLoadedPath('');
    setSelected(null);
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
        <nav className="crumbs"><b>System</b> ▸ Service ▸ Layer ▸ Type ▸ Member</nav>
        <div className="sysview__right">
          {graph && (
            <span className={`saved ${saved ? 'saved--ok' : 'saved--dirty'}`}>
              {saved ? '✓ сохранено' : '● есть изменения'}
            </span>
          )}
          {onExit && <button className="sysview__exit" onClick={onExit}>← тип-граф (Ф1)</button>}
        </div>
      </header>

      <div className="sysview__body">
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
              <ReactFlowProvider>
                <Flow
                  ref={flowRef}
                  flow={flow}
                  magnet={magnet}
                  storageKey={storageKey}
                  onDirty={setSaved}
                  selectedId={selected?.id ?? null}
                  onSelect={setSelected}
                  onClear={() => setSelected(null)}
                />
              </ReactFlowProvider>
            ) : (
              <div className="sysview__empty">
                {loading ? 'анализирую…' : <>Проект не открыт. Нажми <b>«Открыть проект»</b> вверху слева.</>}
              </div>
            )}
          </div>
        </main>

        <aside className={`panel panel--inspector ${rightOpen ? '' : 'is-collapsed'}`}>
          {!rightOpen ? (
            <button className="panel__collapsed" onClick={() => setRightOpen(true)} title="Показать инспектор">
              <Icon name="chevronLeft" size={16} />
              <span className="panel__vtitle">ИНСПЕКТОР</span>
            </button>
          ) : (
            <>
              <button className="panel__header" onClick={() => setRightOpen(false)}>
                ИНСПЕКТОР<Icon name="chevronRight" size={15} />
              </button>
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
        <span className={`status__health health-${graph?.health ?? 'none'}`}>{graph?.health ?? '—'}</span>
        {graph && <span className="status__proj">{graph.project}</span>}
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
