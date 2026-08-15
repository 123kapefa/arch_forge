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
import { buildSystemFlow, filterGraph, edgeKindsOf, edgeStyle, type SystemNodeData } from './systemFlow';
import { SystemNodeView } from './nodes/SystemNodeView';
import { problemsByLens, severityCounts, type Lens } from './systemModel';

const nodeTypes = { sys: SystemNodeView };
const GRID = 20; // шаг сетки: точки фона + шаг магнита
// Порог скорости указателя (px/мс): медленнее (малая инерция) → магнит к сетке; быстрее → свободно.
// Низкий порог = магнит только при явно медленном, «прицельном» движении, а не при обычном перетаскивании.
const MAGNET_SPEED = 0.15;

export type FlowHandle = { save: () => void; reset: () => void };

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
  onSelect: (n: Node) => void;
  onClear: () => void;
}>(function Flow({ flow, magnet, storageKey, onDirty, onSelect, onClear }, ref) {
  const [nodes, setNodes, onNodesChange] = useNodesState(flow.nodes);
  const [edges, setEdges, onEdgesChange] = useEdgesState(flow.edges);
  const [snapNow, setSnapNow] = useState(false); // притяжение активно прямо сейчас (низкая скорость)
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

  useEffect(() => {
    setNodes(applySaved(flow.nodes));
    setEdges(flow.edges);
    onDirty(true); // после загрузки/восстановления раскладка считается сохранённой
    if (flow.nodes.length === 0) return;
    const t = setTimeout(() => rf.fitView({ padding: 0.2 }), 60);
    return () => clearTimeout(t);
  }, [flow, applySaved, setNodes, setEdges, rf, onDirty]);

  useImperativeHandle(ref, () => ({
    save() {
      if (!storageKey) return;
      const pos: Record<string, { x: number; y: number }> = {};
      for (const n of nodesRef.current) pos[n.id] = n.position;
      try { localStorage.setItem(storageKey, JSON.stringify(pos)); } catch { /* quota */ }
      onDirty(true);
    },
    reset() {
      if (storageKey) { try { localStorage.removeItem(storageKey); } catch { /* ignore */ } }
      setNodes(flow.nodes); // вернуть вычисленную раскладку (без сохранённых позиций)
      onDirty(true);
      setTimeout(() => rf.fitView({ padding: 0.2 }), 60);
    },
  }), [storageKey, flow, setNodes, rf, onDirty]);

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

  return (
    <ReactFlow
      nodes={nodes}
      edges={edges}
      onNodesChange={handleNodesChange}
      onEdgesChange={onEdgesChange}
      nodeTypes={nodeTypes}
      minZoom={0.08}
      snapToGrid={magnet && snapNow}
      snapGrid={[GRID, GRID]}
      onNodeDragStart={() => { last.current = null; }}
      onNodeDrag={onNodeDrag}
      onNodeDragStop={() => { last.current = null; setSnapNow(false); }}
      onNodeClick={(_, n) => onSelect(n)}
      onPaneClick={onClear}
    >
      <Background gap={GRID} />
      <Controls />
      <MiniMap pannable zoomable />
    </ReactFlow>
  );
});

const KIND_LABEL: Record<string, string> = {
  service: 'сервис', datastore: 'хранилище', topic: 'шина', external: 'внешний',
};

function Inspector({ node, lens }: { node: Node; lens: Lens }) {
  const data = node.data as SystemNodeData;
  const problems = problemsByLens(data.problems, lens);

  return (
    <div className="inspector">
      <div className="inspector__name">{data.node.name}</div>
      <div className="inspector__kind">{KIND_LABEL[data.node.kind] ?? data.node.kind}</div>
      <div className="inspector__sub">проблемы ({lens === 'architecture' ? 'реальные' : 'дрейф'})</div>
      {problems.length === 0 ? (
        <div className="panel__empty">нет</div>
      ) : (
        <ul className="inspector__problems">
          {problems.map((p) => (
            <li key={p.id} className={`sev-${p.severity}`}>
              <b>{p.kind}</b> {p.title}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/** L0 System Overview: карточки-сервисы + инфра + рёбра, панель «Вид», инспектор, статус-бар. */
export function SystemView({ onExit }: { onExit?: () => void }) {
  const [pathInput, setPathInput] = useState('');
  const [graph, setGraph] = useState<SystemGraph | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lens, setLens] = useState<Lens>('architecture');
  const [selected, setSelected] = useState<Node | null>(null);
  const [edgeKinds, setEdgeKinds] = useState<ReadonlySet<string>>(new Set());
  const [minConfidence, setMinConfidence] = useState(0);
  const [hideIsolated, setHideIsolated] = useState(false);
  const [magnet, setMagnet] = useState(true); // магнит к сетке по инерции: медленно тянешь → притягивает
  const [loadedPath, setLoadedPath] = useState(''); // путь загруженного проекта — ключ раскладки
  const [saved, setSaved] = useState(true);         // раскладка совпадает с сохранённой
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
      try { localStorage.setItem('af_path', path); } catch { /* quota */ }
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      setGraph(null);
    } finally {
      setLoading(false);
    }
  }, []);

  // Восстановить последний открытый проект при старте.
  useEffect(() => {
    let lastPath = '';
    try { lastPath = localStorage.getItem('af_path') ?? ''; } catch { /* ignore */ }
    if (lastPath) { setPathInput(lastPath); void load(lastPath); }
  }, [load]);

  // ⌘S / Ctrl+S — сохранить раскладку.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 's') {
        e.preventDefault();
        flowRef.current?.save();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  const view = useMemo(
    () => (graph ? filterGraph(graph, { edgeKinds, minConfidence, hideIsolated }) : null),
    [graph, edgeKinds, minConfidence, hideIsolated],
  );
  const flow = useMemo(
    () => (view ? buildSystemFlow(view) : { nodes: [], edges: [] }),
    [view],
  );
  const allEdgeKinds = useMemo(() => (graph ? edgeKindsOf(graph) : []), [graph]);
  const toggleEdgeKind = (k: string) =>
    setEdgeKinds((prev) => {
      const next = new Set(prev);
      if (next.has(k)) next.delete(k); else next.add(k);
      return next;
    });
  const counts = useMemo(() => severityCounts(graph?.problems), [graph]);
  const lensProblems = useMemo(() => problemsByLens(graph?.problems, lens), [graph, lens]);
  const storageKey = loadedPath ? `af_layout:${loadedPath}` : '';

  return (
    <div className="sysview">
      <header className="sysview__top">
        <span className="crumb crumb--root">{graph?.project ?? 'система'}</span>
        <nav className="crumbs"><b>System</b> ▸ Service ▸ Layer ▸ Type ▸ Member</nav>
        <div className="sysview__right">
          {graph && (
            <>
              <span className={`saved ${saved ? 'saved--ok' : 'saved--dirty'}`}>
                {saved ? '✓ сохранено' : '● есть изменения'}
              </span>
              <button className="sysview__btn" onClick={() => flowRef.current?.save()} title="Сохранить раскладку (⌘S)">
                💾 Сохранить
              </button>
              <button className="sysview__btn" onClick={() => flowRef.current?.reset()} title="Сбросить раскладку">
                ↺ Сброс
              </button>
            </>
          )}
          {onExit && <button className="sysview__exit" onClick={onExit}>← тип-граф (Ф1)</button>}
        </div>
      </header>

      <div className="sysview__body">
        <aside className="panel panel--view">
          <div className="panel__title">ВИД</div>
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
              <input type="checkbox" checked={magnet} onChange={(e) => setMagnet(e.target.checked)} />
              магнит к сетке
            </label>
            <div className="panel__hint">медленно тянешь — притягивает к точкам, быстро — свободно</div>
          </div>
        </aside>

        <main className="sysview__board">
          <form className="sysview__load" onSubmit={(e) => { e.preventDefault(); void load(pathInput); }}>
            <input
              value={pathInput}
              onChange={(e) => setPathInput(e.target.value)}
              placeholder="путь к корню системы (workspace)"
              spellCheck={false}
            />
            <button type="submit" disabled={loading}>{loading ? '…' : 'система'}</button>
          </form>
          {error && <div className="sysview__error">{error}</div>}
          <div className="sysview__flow">
            <ReactFlowProvider>
              <Flow
                ref={flowRef}
                flow={flow}
                magnet={magnet}
                storageKey={storageKey}
                onDirty={setSaved}
                onSelect={setSelected}
                onClear={() => setSelected(null)}
              />
            </ReactFlowProvider>
          </div>
        </main>

        <aside className="panel panel--inspector">
          <div className="panel__title">ИНСПЕКТОР</div>
          {selected ? <Inspector node={selected} lens={lens} /> : <div className="panel__empty">кликните узел</div>}
        </aside>
      </div>

      <footer className="sysview__status">
        <span className={`status__health health-${graph?.health ?? 'none'}`}>{graph?.health ?? '—'}</span>
        <span>● {counts.critical} · ⚠ {counts.warning} · ○ {counts.coverage} · дрейф {counts.info}</span>
        <span>{flow.nodes.length} узлов · {flow.edges.length} связей</span>
        <span className="status__lens">
          линза «{lens === 'architecture' ? 'реальные проблемы' : 'дрейф'}»: {lensProblems.length}
        </span>
      </footer>
    </div>
  );
}
