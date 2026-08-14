import { describe, it, expect } from 'vitest';
import type { GraphDto } from './api';
import { filterGraph, toFlow } from './graph';

const sample = (): GraphDto => ({
  project: 'demo',
  nodes: [
    { id: 'D:A', name: 'A', kind: 'class', layer: 'Domain', role: 'Aggregate', via: 'marker', file: 'A.cs', drift: [] },
    { id: 'D:B', name: 'B', kind: 'class', layer: 'Domain', role: 'Entity', via: 'heuristic', file: 'B.cs', drift: [] },
    { id: 'I:C', name: 'C', kind: 'class', layer: 'Infrastructure', role: 'Repo', via: 'marker', file: 'C.cs', drift: [] },
    { id: 'T:t', name: 't', kind: 'class', layer: 'tests', role: 'X', via: 'unknown', file: 't.cs', drift: [] },
    { id: 'D:iso', name: 'iso', kind: 'class', layer: 'Domain', role: 'X', via: 'unknown', file: 'i.cs', drift: [] },
  ],
  edges: [
    { id: 'e1', source: 'D:A', target: 'D:B', kind: 'holds' },
    { id: 'e2', source: 'D:B', target: 'I:C', kind: 'uses' },
    { id: 'e3', source: 'D:A', target: 'T:t', kind: 'uses' },
  ],
  coverage: { marker: 2, heuristic: 1, unknown: 2, total: 5 },
  drift: [],
});

describe('filterGraph', () => {
  it('без фильтров — граф не меняется', () => {
    const g = filterGraph(sample(), { hideTests: false, hideIsolated: false });
    expect(g.nodes).toHaveLength(5);
    expect(g.edges).toHaveLength(3);
  });

  it('hideTests убирает tests-узлы и рёбра к ним', () => {
    const g = filterGraph(sample(), { hideTests: true, hideIsolated: false });
    expect(g.nodes.map((n) => n.id)).not.toContain('T:t');
    expect(g.edges.map((e) => e.id)).not.toContain('e3'); // e3 вело в T:t
    expect(g.edges).toHaveLength(2);
  });

  it('hideIsolated убирает узлы без рёбер', () => {
    const g = filterGraph(sample(), { hideTests: false, hideIsolated: true });
    expect(g.nodes.map((n) => n.id)).not.toContain('D:iso');
    expect(g.nodes).toHaveLength(4); // A,B,C,t связаны рёбрами
  });

  it('hideTests+hideIsolated: t убран → его ребро тоже; остаются связные A/B/C', () => {
    const g = filterGraph(sample(), { hideTests: true, hideIsolated: true });
    expect(g.nodes.map((n) => n.id).sort()).toEqual(['D:A', 'D:B', 'I:C']);
  });

  it('покрытие/проект честные — не пересчитываются на срезе', () => {
    const g = filterGraph(sample(), { hideTests: true, hideIsolated: true });
    expect(g.project).toBe('demo');
    expect(g.coverage.total).toBe(5);
  });

  it('не мутирует исходный граф', () => {
    const src = sample();
    filterGraph(src, { hideTests: true, hideIsolated: true });
    expect(src.nodes).toHaveLength(5);
    expect(src.edges).toHaveLength(3);
  });
});

describe('toFlow', () => {
  it('null → пусто', () => {
    expect(toFlow(null)).toEqual({ nodes: [], edges: [] });
  });

  it('маппит узлы/рёбра: type, label, конечные координаты', () => {
    const { nodes, edges } = toFlow(sample());
    expect(nodes).toHaveLength(5);
    expect(nodes.every((n) => n.type === 'type')).toBe(true);
    expect(edges).toHaveLength(3);
    expect(edges.find((e) => e.id === 'e1')?.label).toBe('holds');
    expect(nodes.every((n) => Number.isFinite(n.position.x))).toBe(true);
  });
});
