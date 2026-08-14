import { describe, it, expect } from 'vitest';
import type { GraphDto } from './api';
import { aggregateLayers } from './graph';

const g: GraphDto = {
  project: 'demo',
  coverage: { marker: 0, heuristic: 0, unknown: 0, total: 0 },
  drift: [],
  nodes: [
    { id: 'D:A', name: 'A', kind: 'class', layer: 'Domain', role: '', via: 'marker', file: '', drift: [] },
    { id: 'D:B', name: 'B', kind: 'class', layer: 'Domain', role: '', via: 'marker', file: '', drift: [] },
    { id: 'I:C', name: 'C', kind: 'class', layer: 'Infrastructure', role: '', via: 'marker', file: '', drift: [] },
    { id: 'T:t', name: 't', kind: 'class', layer: 'tests', role: '', via: 'unknown', file: '', drift: [] },
  ],
  edges: [
    { id: 'e1', source: 'D:A', target: 'D:B', kind: 'holds' }, // внутри Domain — не считается
    { id: 'e2', source: 'D:B', target: 'I:C', kind: 'uses' },  // Domain → Infrastructure
    { id: 'e3', source: 'D:A', target: 'I:C', kind: 'uses' },  // Domain → Infrastructure (вес 2)
    { id: 'e4', source: 'D:A', target: 'T:t', kind: 'uses' },  // Domain → tests
  ],
};

describe('aggregateLayers', () => {
  it('считает типы по слоям (сортировка по имени)', () => {
    const { layers } = aggregateLayers(g);
    expect(layers).toEqual([
      { layer: 'Domain', count: 2 },
      { layer: 'Infrastructure', count: 1 },
      { layer: 'tests', count: 1 },
    ]);
  });

  it('межслойные связи с весом; внутрислойные отброшены', () => {
    const { links } = aggregateLayers(g);
    expect(links).toContainEqual({ source: 'Domain', target: 'Infrastructure', weight: 2 });
    expect(links).toContainEqual({ source: 'Domain', target: 'tests', weight: 1 });
    expect(links).toHaveLength(2); // Domain→Domain (e1) не в счёт
  });
});
