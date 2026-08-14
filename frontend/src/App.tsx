import { useCallback, useMemo, useState } from 'react';
import {
  ReactFlow, Background, Controls, MiniMap,
  MarkerType, type Node, type Edge,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import './App.css';

import { fetchMap, type GraphDto, type CoverageDto } from './api';
import { layout } from './layout';
import { TypeNode, type TypeNodeData } from './nodes/TypeNode';
import { layerColor } from './theme';

const nodeTypes = { type: TypeNode };
const DEFAULT_PATH = '/Users/123kapefa/Documents/projects/microservices/auth_service';

type Filters = { hideTests: boolean; hideIsolated: boolean };

/** Клиентская фильтрация вида — мгновенно, без запроса. Покрытие не трогаем: оно честное. */
function filterGraph(g: GraphDto, f: Filters): GraphDto {
  let nodes = g.nodes;
  if (f.hideTests) nodes = nodes.filter((n) => n.layer.toLowerCase() !== 'tests');
  const ids = new Set(nodes.map((n) => n.id));
  let edges = g.edges.filter((e) => ids.has(e.source) && ids.has(e.target));
  if (f.hideIsolated) {
    const linked = new Set<string>();
    edges.forEach((e) => { linked.add(e.source); linked.add(e.target); });
    nodes = nodes.filter((n) => linked.has(n.id));
  }
  return { ...g, nodes, edges };
}

function toFlow(g: GraphDto | null): { nodes: Node[]; edges: Edge[] } {
  if (!g) return { nodes: [], edges: [] };
  const nodes: Node[] = g.nodes.map((n) => ({
    id: n.id,
    type: 'type',
    position: { x: 0, y: 0 },
    data: { name: n.name, kind: n.kind, role: n.role, layer: n.layer, via: n.via, drift: n.drift },
  }));
  const edges: Edge[] = g.edges.map((e) => ({
    id: e.id,
    source: e.source,
    target: e.target,
    label: e.kind,
    markerEnd: { type: MarkerType.ArrowClosed },
  }));
  return { nodes: layout(nodes, edges), edges };
}

function Coverage({ nodeCount, edgeCount, coverage, drift }: {
  nodeCount: number; edgeCount: number; coverage: CoverageDto; drift: string[];
}) {
  const t = Math.max(coverage.total, 1);
  const pct = (n: number) => `${((n / t) * 100).toFixed(0)}%`;
  return (
    <div className="cov">
      <span>{nodeCount} узлов · {edgeCount} связей</span>
      <span className="cov__bar">
        <span className="cov__seg cov__seg--marker" style={{ width: pct(coverage.marker) }} title={`маркер ${coverage.marker}`} />
        <span className="cov__seg cov__seg--heuristic" style={{ width: pct(coverage.heuristic) }} title={`эвристика ${coverage.heuristic}`} />
        <span className="cov__seg cov__seg--unknown" style={{ width: pct(coverage.unknown) }} title={`unknown ${coverage.unknown}`} />
      </span>
      {drift.length > 0 && <span className="cov__drift" title={drift.join('\n')}>⚠ {drift.length} дрейф</span>}
    </div>
  );
}

export default function App() {
  const [path, setPath] = useState(DEFAULT_PATH);
  const [graph, setGraph] = useState<GraphDto | null>(null);
  const [layer, setLayer] = useState<string | null>(null);
  const [focus, setFocus] = useState<string | null>(null);
  const [hideTests, setHideTests] = useState(false);
  const [hideIsolated, setHideIsolated] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  // Явные параметры, чтобы не зависеть от асинхронного setState.
  const load = useCallback(
    async (over: { layer?: string | null; focus?: string | null } = {}) => {
      const nextLayer = 'layer' in over ? over.layer : layer;
      const nextFocus = 'focus' in over ? over.focus : focus;
      setLoading(true);
      setError(null);
      try {
        const g = await fetchMap({ path, layer: nextLayer || undefined, focus: nextFocus || undefined });
        setGraph(g);
      } catch (e) {
        setError(String(e));
        setGraph(null);
      } finally {
        setLoading(false);
      }
    },
    [path, layer, focus],
  );

  const view = useMemo(
    () => (graph ? filterGraph(graph, { hideTests, hideIsolated }) : null),
    [graph, hideTests, hideIsolated],
  );
  const { nodes, edges } = useMemo(() => toFlow(view), [view]);
  const layers = useMemo(
    () => (graph ? [...new Set(graph.nodes.map((n) => n.layer))].sort() : []),
    [graph],
  );

  // Меняется при любой смене видимого набора → ремоунт ReactFlow → повторный fitView.
  const fitKey = [layer ?? 'all', focus ?? '', hideTests, hideIsolated, nodes.length, edges.length].join('|');

  return (
    <div className="app">
      <header className="bar">
        <input
          className="bar__path"
          value={path}
          onChange={(e) => setPath(e.target.value)}
          placeholder="путь к .NET проекту"
          spellCheck={false}
        />
        <button
          onClick={() => { setLayer(null); setFocus(null); load({ layer: null, focus: null }); }}
          disabled={loading}
        >
          {loading ? '…' : 'Разобрать'}
        </button>

        {graph && (
          <>
            <select
              value={layer ?? ''}
              onChange={(e) => {
                const v = e.target.value || null;
                setLayer(v); setFocus(null);
                load({ layer: v, focus: null });
              }}
            >
              <option value="">все слои</option>
              {layers.map((l) => <option key={l} value={l}>{l}</option>)}
            </select>

            <label className="tog">
              <input type="checkbox" checked={hideTests} onChange={(e) => setHideTests(e.target.checked)} />
              без tests
            </label>
            <label className="tog">
              <input type="checkbox" checked={hideIsolated} onChange={(e) => setHideIsolated(e.target.checked)} />
              без одиночек
            </label>

            {focus && (
              <button
                className="bar__focus"
                onClick={() => { setFocus(null); load({ focus: null }); }}
                title="сбросить фокус"
              >
                ✕ focus: {graph.nodes.find((n) => n.id === focus)?.name ?? focus}
              </button>
            )}

            {view && (
              <Coverage
                nodeCount={view.nodes.length}
                edgeCount={view.edges.length}
                coverage={graph.coverage}
                drift={graph.drift}
              />
            )}
          </>
        )}
      </header>

      {error && <div className="err">{error}</div>}

      <div className="canvas">
        {!graph && !error && (
          <div className="hint">Укажи путь и нажми «Разобрать». Клик по плашке — фокус на её связях.</div>
        )}
        <ReactFlow
          key={fitKey}
          nodes={nodes}
          edges={edges}
          nodeTypes={nodeTypes}
          fitView
          fitViewOptions={{ padding: 0.15 }}
          minZoom={0.1}
          onNodeClick={(_, n) => { setFocus(n.id); load({ focus: n.id }); }}
          proOptions={{ hideAttribution: true }}
        >
          <Background gap={20} color="#20262f" />
          <Controls />
          <MiniMap
            pannable
            zoomable
            nodeColor={(n) => layerColor((n.data as TypeNodeData).layer)}
            maskColor="rgba(13,17,23,0.7)"
          />
        </ReactFlow>
      </div>
    </div>
  );
}
