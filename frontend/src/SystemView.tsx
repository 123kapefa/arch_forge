import { useCallback, useMemo, useState } from 'react';
import { ReactFlow, Background, Controls, MiniMap, type Node } from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import './system.css';

import { fetchSystem, type SystemGraph } from './system';
import { buildSystemFlow, filterGraph, edgeKindsOf, edgeStyle, type SystemNodeData } from './systemFlow';
import { SystemNodeView } from './nodes/SystemNodeView';
import { problemsByLens, severityCounts, type Lens } from './systemModel';

const nodeTypes = { sys: SystemNodeView };

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

  const load = useCallback(async () => {
    const path = pathInput.trim();
    if (!path) return;
    setLoading(true);
    setError(null);
    setSelected(null);
    try {
      // код-линзы включены: нужны проблемы слоёв/дрейфа + сводка сервисов (для drill)
      const g = await fetchSystem({ path, drift: true, layers: true });
      setGraph(g);
      setEdgeKinds(new Set(edgeKindsOf(g))); // по умолчанию все виды рёбер включены
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      setGraph(null);
    } finally {
      setLoading(false);
    }
  }, [pathInput]);

  const view = useMemo(
    () => (graph ? filterGraph(graph, { edgeKinds, minConfidence, hideIsolated }) : null),
    [graph, edgeKinds, minConfidence, hideIsolated],
  );
  const { nodes, edges } = useMemo(
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

  return (
    <div className="sysview">
      <header className="sysview__top">
        <span className="crumb crumb--root">{graph?.project ?? 'система'}</span>
        <nav className="crumbs"><b>System</b> ▸ Service ▸ Layer ▸ Type ▸ Member</nav>
        {onExit && <button className="sysview__exit" onClick={onExit}>← тип-граф (Ф1)</button>}
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
          </div>
        </aside>

        <main className="sysview__board">
          <form className="sysview__load" onSubmit={(e) => { e.preventDefault(); void load(); }}>
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
            {/* key по набору узлов/рёбер → ремоунт + fitView при загрузке/фильтре (переумещение) */}
            <ReactFlow
              key={`${nodes.length}|${edges.length}`}
              nodes={nodes}
              edges={edges}
              nodeTypes={nodeTypes}
              fitView
              fitViewOptions={{ padding: 0.15 }}
              minZoom={0.1}
              onNodeClick={(_, n) => setSelected(n)}
              onPaneClick={() => setSelected(null)}
            >
              <Background />
              <Controls />
              <MiniMap pannable zoomable />
            </ReactFlow>
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
        <span>{nodes.length} узлов · {edges.length} связей</span>
        <span className="status__lens">
          линза «{lens === 'architecture' ? 'реальные проблемы' : 'дрейф'}»: {lensProblems.length}
        </span>
      </footer>
    </div>
  );
}
