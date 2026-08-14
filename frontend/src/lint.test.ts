import { describe, it, expect } from 'vitest';
import type { GraphDto, NodeDto } from './api';
import { layerRank, isLayerDirectionViolation, layerViolations } from './lint';

describe('layerRank', () => {
  it('Domain ближе к ядру, чем Application/Infrastructure/Api', () => {
    expect(layerRank('Domain')).toBeLessThan(layerRank('Application'));
    expect(layerRank('Application')).toBeLessThan(layerRank('Infrastructure'));
    expect(layerRank('Infrastructure')).toBeLessThan(layerRank('Api'));
  });
  it('неизвестный слой — средний ранг; регистронезависимо', () => {
    expect(layerRank('WAT')).toBe(1);
    expect(layerRank('domain')).toBe(0);
  });
});

describe('isLayerDirectionViolation', () => {
  it('зависимость внутрь — не нарушение; наружу — нарушение', () => {
    expect(isLayerDirectionViolation('Infrastructure', 'Domain')).toBe(false);
    expect(isLayerDirectionViolation('Api', 'Domain')).toBe(false);
    expect(isLayerDirectionViolation('Domain', 'Infrastructure')).toBe(true);
    expect(isLayerDirectionViolation('Application', 'Api')).toBe(true);
  });
  it('tests игнорируется в обе стороны (частая причина ложных нарушений)', () => {
    expect(isLayerDirectionViolation('tests', 'Infrastructure')).toBe(false);
    expect(isLayerDirectionViolation('tests', 'Api')).toBe(false);
    expect(isLayerDirectionViolation('Domain', 'tests')).toBe(false);
  });
});

describe('layerViolations', () => {
  const mk = (id: string, layer: string): NodeDto =>
    ({ id, name: id, kind: 'class', layer, role: '', via: 'marker', file: '', drift: [] });
  const g: GraphDto = {
    project: 'x',
    coverage: { marker: 0, heuristic: 0, unknown: 0, total: 0 },
    drift: [],
    nodes: [mk('Dom', 'Domain'), mk('Inf', 'Infrastructure'), mk('Api', 'Api'), mk('T', 'tests')],
    edges: [
      { id: 'ok', source: 'Inf', target: 'Dom', kind: 'uses' },      // Infra→Domain: внутрь — ок
      { id: 'bad', source: 'Dom', target: 'Inf', kind: 'uses' },     // Domain→Infra: наружу — нарушение
      { id: 'apiok', source: 'Api', target: 'Dom', kind: 'uses' },   // Api→Domain: ок
      { id: 'testedge', source: 'T', target: 'Inf', kind: 'uses' },  // с tests — игнор
    ],
  };

  it('ловит зависимость наружу (Domain→Infrastructure)', () => {
    const v = layerViolations(g);
    expect(v.map((x) => x.id)).toEqual(['bad']);
    expect(v[0]).toMatchObject({ fromLayer: 'Domain', toLayer: 'Infrastructure' });
  });

  it('связи внутрь и с tests не нарушают', () => {
    const ids = layerViolations(g).map((x) => x.id);
    expect(ids).not.toContain('ok');
    expect(ids).not.toContain('apiok');
    expect(ids).not.toContain('testedge');
  });
});
