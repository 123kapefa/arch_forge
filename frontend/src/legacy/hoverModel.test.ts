import { describe, expect, it } from 'vitest';
import { buildAdjacency, edgeTooltip, highlightFor } from './hoverModel';

const edges = [
  { id: 'e1', source: 'a', target: 'b' },
  { id: 'e2', source: 'c', target: 'a' },
  { id: 'e3', source: 'b', target: 'c' },
  { id: 'e4', source: 'x', target: 'y' },
  { id: 'e5', source: 'z', target: 'z' },
];
const adj = buildAdjacency(edges);

describe('highlightFor', () => {
  it('узел: входящие и исходящие рёбра + соседи, сам узел главный', () => {
    const h = highlightFor(adj, { nodeId: 'a' });
    expect([...h.edgeIds].sort()).toEqual(['e1', 'e2']);
    expect([...h.nodeIds].sort()).toEqual(['b', 'c']);
    expect([...h.mainIds]).toEqual(['a']);
  });
  it('ребро: оно само и два конца; соседей нет', () => {
    const h = highlightFor(adj, { edgeId: 'e4' });
    expect([...h.edgeIds]).toEqual(['e4']);
    expect([...h.mainIds].sort()).toEqual(['x', 'y']);
    expect(h.nodeIds.size).toBe(0);
  });
  it('петля не делает узел соседом самого себя', () => {
    const h = highlightFor(adj, { nodeId: 'z' });
    expect([...h.nodeIds]).toEqual([]);
    expect([...h.edgeIds]).toEqual(['e5']);
  });
  it('нет цели / неизвестные id — пусто', () => {
    expect(highlightFor(adj, {}).edgeIds.size).toBe(0);
    expect(highlightFor(adj, { edgeId: 'nope' }).mainIds.size).toBe(0);
    expect(highlightFor(adj, { nodeId: 'nope' }).edgeIds.size).toBe(0);
  });
  it('ребро приоритетнее узла', () => {
    expect([...highlightFor(adj, { nodeId: 'a', edgeId: 'e4' }).edgeIds]).toEqual(['e4']);
  });
});

describe('edgeTooltip', () => {
  it('вид, источник и уверенность', () => {
    expect(edgeTooltip({ kind: 'refs', via: 'semantic', confidence: 0.5 })).toBe('refs · источник: semantic · уверенность 0.50');
    expect(edgeTooltip({ kind: 'uses' })).toBe('uses');
    expect(edgeTooltip({ kind: 'http-call', origin: { evidence: 'GetAsync("/x")', file: 'A.cs', line: 7 } })).toBe('http-call · GetAsync("/x") · A.cs:7');
    expect(edgeTooltip(undefined)).toBe('');
  });
});
