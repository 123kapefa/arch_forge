import { useCallback, useMemo, useState } from 'react';
import { ReactFlow, Background, Controls, MiniMap, type Node } from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import './system.css';

import { fetchSystem, type SystemGraph } from './system';
import { buildSystemFlow, type ServiceCardData, type InfraNodeData } from './systemFlow';
import { ServiceCardNode } from './nodes/ServiceCardNode';
import { InfraNode } from './nodes/InfraNode';
import { problemsByLens, severityCounts, type Lens } from './systemModel';

const nodeTypes = { serviceCard: ServiceCardNode, infra: InfraNode };

type NodeData = ServiceCardData | InfraNodeData;
const isCard = (d: NodeData): d is ServiceCardData => 'summary' in d;

function Inspector({ node, lens }: { node: Node; lens: Lens }) {
  const data = node.data as NodeData;
  const title = isCard(data) ? data.summary.name : data.node.name;
  const problems = problemsByLens(data.problems, lens);

  return (
    <div className="inspector">
      <div className="inspector__name">{title}</div>
      {isCard(data) && (
        <ul className="inspector__layers">
          {data.summary.layers.map((l) => (
            <li key={l.name} className={`health-${l.health}`}>
              <span>{l.name}</span>
              {l.note && <em>{l.note}</em>}
            </li>
          ))}
        </ul>
      )}
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

  const load = useCallback(async () => {
    const path = pathInput.trim();
    if (!path) return;
    setLoading(true);
    setError(null);
    setSelected(null);
    try {
      // код-линзы включены: нужны карточки со слоями + дрейф/нарушения
      setGraph(await fetchSystem({ path, drift: true, layers: true }));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      setGraph(null);
    } finally {
      setLoading(false);
    }
  }, [pathInput]);

  const { nodes, edges } = useMemo(
    () => (graph ? buildSystemFlow(graph) : { nodes: [], edges: [] }),
    [graph],
  );
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
            <div className="legend"><span className="legend__line legend__line--db" /> rw-db</div>
            <div className="legend"><span className="legend__line legend__line--http" /> http-call</div>
            <div className="legend"><span className="legend__line legend__line--bus" /> шина</div>
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
            <ReactFlow
              nodes={nodes}
              edges={edges}
              nodeTypes={nodeTypes}
              fitView
              minZoom={0.15}
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
