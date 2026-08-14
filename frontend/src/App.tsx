import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ReactFlow, Background, Controls, MiniMap } from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import './App.css';

import { fetchMap, type GraphDto, type CoverageDto } from './api';
import { filterGraph, toFlow, toLayerFlow } from './graph';
import { TypeNode, type TypeNodeData } from './nodes/TypeNode';
import { LayerNode, type LayerNodeData } from './nodes/LayerNode';
import { Inspector } from './Inspector';
import { layerColor } from './theme';

const nodeTypes = { type: TypeNode, layer: LayerNode };
const DEFAULT_PATH = ''; // без хардкода машины разработчика — путь вводит пользователь
type Mode = 'layers' | 'types';

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
  const [pathInput, setPathInput] = useState(DEFAULT_PATH);
  const [loadedPath, setLoadedPath] = useState('');
  const [graph, setGraph] = useState<GraphDto | null>(null);
  const [mode, setMode] = useState<Mode>('layers'); // уровень: обзор слоёв / типы (меняется при приходе графа)
  const [layer, setLayer] = useState<string | null>(null);
  const [focus, setFocus] = useState<string | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [hideTests, setHideTests] = useState(false);
  const [hideIsolated, setHideIsolated] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const reqSeq = useRef(0);

  // path/layer/focus передаются явно на вызове — нет стейл-замыканий. reqSeq отбрасывает устаревшие
  // ответы (гонка). mode переключаем ТОЛЬКО когда пришёл соответствующий граф — иначе на миг
  // отрисовался бы чужой уровень. При ошибке граф НЕ обнуляем — держим последний рабочий.
  const load = useCallback(async (p: string, nextLayer: string | null, nextFocus: string | null, resultMode: Mode) => {
    const id = ++reqSeq.current;
    setLoading(true);
    setError(null);
    try {
      const g = await fetchMap({ path: p, layer: nextLayer || undefined, focus: nextFocus || undefined });
      if (id !== reqSeq.current) return;
      setGraph(g);
      setMode(resultMode);
    } catch (e) {
      if (id !== reqSeq.current) return;
      setError(String(e));
    } finally {
      if (id === reqSeq.current) setLoading(false);
    }
  }, []);

  // «Разобрать»: грузим весь проект и встаём на обзор слоёв.
  const analyze = () => {
    const p = pathInput.trim();
    if (!p) return;
    setLoadedPath(p);
    setLayer(null);
    setFocus(null);
    setSelectedId(null);
    load(p, null, null, 'layers');
  };

  // Провал в слой (обзор → типы) и подъём назад к обзору.
  const drillLayer = (l: string) => {
    setLayer(l);
    setFocus(null);
    setSelectedId(null);
    load(loadedPath, l, null, 'types');
  };
  const backToLayers = useCallback(() => {
    if (mode === 'layers' && layer === null && !loading) return; // уже на обзоре — не дёргаем сеть
    setLayer(null);
    setFocus(null);
    setSelectedId(null);
    load(loadedPath, null, null, 'layers');
  }, [mode, layer, loading, loadedPath, load]);

  // Esc: закрыть инспектор, иначе подняться к обзору слоёв. Без побочки внутри апдейтера setState.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      if (selectedId) setSelectedId(null);
      else if (mode === 'types') backToLayers();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [mode, selectedId, backToLayers]);

  const view = useMemo(
    () => (graph ? filterGraph(graph, { hideTests, hideIsolated }) : null),
    [graph, hideTests, hideIsolated],
  );
  // Обзор — из полного graph (карта слоёв не зависит от тумблеров); типы — из отфильтрованного view.
  const { nodes, edges } = useMemo(
    () => (mode === 'layers' ? toLayerFlow(graph) : toFlow(view)),
    [mode, graph, view],
  );
  const layers = useMemo(
    () => (graph ? [...new Set(graph.nodes.map((n) => n.layer))].sort() : []),
    [graph],
  );
  // Свечение выбранного узла ведём от нашего selectedId, а не от внутреннего selection React Flow.
  const displayNodes = useMemo(
    () => nodes.map((n) => ({ ...n, selected: n.id === selectedId })),
    [nodes, selectedId],
  );
  // Инспектор — только на уровне типов; узел резолвим из вида по id (уйдёт из вида → панель закроется).
  const selected = useMemo(
    () => (mode === 'types' && view && selectedId ? view.nodes.find((n) => n.id === selectedId) ?? null : null),
    [mode, view, selectedId],
  );

  // Меняется при любой смене видимого набора → ремоунт ReactFlow → повторный fitView.
  const fitKey = [mode, layer ?? 'all', focus ?? '', hideTests, hideIsolated, nodes.length, edges.length].join('|');
  const emptyView = graph !== null && nodes.length === 0;
  const focusName = focus ? (graph?.nodes.find((n) => n.id === focus)?.name ?? focus) : null;

  return (
    <div className="app">
      <header className="bar">
        <input
          className="bar__path"
          value={pathInput}
          onChange={(e) => setPathInput(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter') analyze(); }}
          placeholder="путь к .NET проекту (папка solution)"
          spellCheck={false}
        />
        <button onClick={analyze} disabled={loading || !pathInput.trim()}>
          {loading ? '…' : 'Разобрать'}
        </button>

        {graph && (
          <>
            <nav className="crumbs">
              <button className="crumbs__home" onClick={backToLayers} title="к обзору слоёв">
                {graph.project}
              </button>
              {mode === 'types' && (
                <>
                  <span className="crumbs__sep">›</span>
                  <span className="crumbs__cur">{layer ?? 'все типы'}</span>
                  {focusName && <><span className="crumbs__sep">›</span><span className="crumbs__cur">{focusName}</span></>}
                </>
              )}
            </nav>

            <select
              value={mode === 'types' ? (layer ?? '') : ''}
              onChange={(e) => {
                const v = e.target.value || null;
                if (v) drillLayer(v); else backToLayers();
              }}
            >
              <option value="">обзор слоёв</option>
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
                onClick={() => { setFocus(null); load(loadedPath, layer, null, 'types'); }}
                title="сбросить фокус"
              >
                ✕ focus: {focusName}
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
          <div className="hint">Укажи путь к .NET-проекту и нажми «Разобрать». Сверху — обзор слоёв; клик по слою — провал в его типы.</div>
        )}
        {emptyView && (
          <div className="hint">Под текущие фильтры ничего не попало — ослабь «без tests»/«без одиночек» или смени слой.</div>
        )}
        <ReactFlow
          key={fitKey}
          nodes={displayNodes}
          edges={edges}
          nodeTypes={nodeTypes}
          fitView
          fitViewOptions={{ padding: 0.15 }}
          minZoom={0.1}
          onlyRenderVisibleElements
          onNodeClick={(_, n) => {
            if (mode === 'layers') drillLayer((n.data as LayerNodeData).layer);
            else setSelectedId(n.id);
          }}
          onPaneClick={() => setSelectedId(null)}
          proOptions={{ hideAttribution: true }}
        >
          <Background gap={20} color="#20262f" />
          <Controls />
          <MiniMap
            pannable
            zoomable
            nodeColor={(n) => layerColor((n.data as TypeNodeData | LayerNodeData).layer)}
            maskColor="rgba(13,17,23,0.7)"
          />
        </ReactFlow>

        {selected && view && (
          <Inspector
            node={selected}
            graph={view}
            onClose={() => setSelectedId(null)}
            onFocus={(id) => { setFocus(id); load(loadedPath, layer, id, 'types'); }}
          />
        )}
      </div>
    </div>
  );
}
