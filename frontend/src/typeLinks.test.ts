import { describe, expect, it } from 'vitest';
import type { GraphDto, NodeDto } from './api';
import { buildTypeFlow } from './layerFlow';
import { frameworkEntry, isStrongKind, isolatedGroup, outsideBadge, outsideLinksByType } from './typeLinks';

const node = (id: string, layer: string, role = 'unknown'): NodeDto =>
  ({ id, name: id.toUpperCase(), kind: 'class', layer, role, via: 'heuristic', file: `${id}.cs`, drift: [] });
const map = (nodes: NodeDto[], edges: [string, string, string][]): GraphDto => ({
  project: 'p', nodes, drift: [], coverage: { marker: 0, heuristic: 0, unknown: 0, total: nodes.length },
  edges: edges.map(([source, target, kind], i) => ({ id: `e${i}`, source, target, kind })),
});

describe('outsideLinksByType', () => {
  const g = map(
    [node('a', 'App'), node('b', 'App'), node('d', 'Domain'), node('c', 'Contracts'), node('z', 'App')],
    [['a', 'b', 'uses'], ['a', 'd', 'holds'], ['c', 'a', 'implements'], ['a', 'd', 'refs'], ['d', 'c', 'uses']],
  );
  const res = outsideLinksByType(g, 'App');
  it('считает только рёбра через границу слоя, сворачивая пары', () => {
    expect(res.get('a')!.links).toHaveLength(2); // →D (holds+refs свёрнуты) и ←C
    expect(res.get('a')!.out).toBe(1);
    expect(res.get('a')!.in).toBe(1);
    expect(res.get('a')!.layers).toEqual(['Contracts', 'Domain']);
    expect(res.get('a')!.links.find((l) => l.peerId === 'd')!.kinds).toEqual(['holds', 'refs']);
  });
  it('внутрислойные и чужие рёбра не считаются; без внешних связей — нет записи', () => {
    expect(res.has('b')).toBe(false);
    expect(res.has('z')).toBe(false);
    expect(res.has('d')).toBe(false);
  });
  it('слабая связь = только неизвестные/refs виды; неизвестный вид не падает', () => {
    const w = outsideLinksByType(map([node('a', 'App'), node('d', 'Domain')], [['a', 'd', 'refs'], ['d', 'a', 'mystery']]), 'App').get('a')!;
    expect(w.links.every((l) => l.weak)).toBe(true);
    expect(res.get('a')!.links.find((l) => l.peerId === 'c')!.weak).toBe(false);
  });
});

describe('isolatedGroup / outsideBadge', () => {
  it('делит одиночек на «только вне слоя» и «без связей»', () => {
    const g = map([node('a', 'App'), node('d', 'Domain'), node('q', 'App')], [['a', 'd', 'uses']]);
    const res = outsideLinksByType(g, 'App');
    expect(isolatedGroup(res.get('a'))).toBe('outsideOnly');
    expect(isolatedGroup(res.get('q'))).toBe('none');
  });
  it('бейдж: число и слои', () => {
    const res = outsideLinksByType(map([node('a', 'App'), node('d', 'Domain')], [['a', 'd', 'uses']]), 'App');
    expect(outsideBadge(res.get('a'))!.text).toBe('вне слоя: 1');
    expect(outsideBadge(undefined)).toBeNull();
  });
});

describe('frameworkEntry', () => {
  it('по роли узла', () => {
    expect(frameworkEntry('EF migration', 'X')).toMatch(/миграция/);
    expect(frameworkEntry('EF snapshot', 'X')).toMatch(/снимок/);
    expect(frameworkEntry('Validator', 'X')).toMatch(/валидатор/);
  });
  it('по имени — только слабый признак, с пометкой', () => {
    expect(frameworkEntry('unknown', 'DependencyInjection')).toMatch(/по имени/);
    expect(frameworkEntry('unknown', 'AuthRegistration')).toMatch(/по имени/);
  });
  it('обычный тип — null (ничего не выдумываем)', () => {
    expect(frameworkEntry('DTO', 'UserDto')).toBeNull();
    expect(frameworkEntry(undefined, 'Foo')).toBeNull();
  });
});

describe('слабые связи (refs)', () => {
  it('сильные виды — известный набор, refs и неизвестные — слабые', () => {
    for (const k of ['uses', 'holds', 'implements', 'handles', 'raises']) expect(isStrongKind(k)).toBe(true);
    expect(isStrongKind('refs')).toBe(false);
    expect(isStrongKind('mystery')).toBe(false);
  });
  it('внутрислойные слабые связи попадают в список inside, сильные — нет', () => {
    const g = map([node('a', 'App'), node('b', 'App'), node('c', 'App')], [['a', 'b', 'refs'], ['b', 'c', 'uses']]);
    const inside = outsideLinksByType(g, 'App', 'inside');
    expect(inside.get('a')!.out).toBe(1);
    expect(inside.get('b')!.in).toBe(1);
    expect(inside.has('c')).toBe(false);
  });
  it('одиночка только с refs (внутри слоя) — не «вообще без связей»', () => {
    const g = map([node('a', 'App'), node('b', 'App'), node('q', 'App')], [['a', 'b', 'refs']]);
    const inside = outsideLinksByType(g, 'App', 'inside');
    const out = outsideLinksByType(g, 'App');
    expect(isolatedGroup(out.get('a'), inside.get('a'))).toBe('outsideOnly');
    expect(isolatedGroup(out.get('q'), inside.get('q'))).toBe('none');
  });
  it('buildTypeFlow: refs не попадают в раскладку, но возвращаются отдельно; refs-узел — в блоке одиночек', async () => {
    const g = map(
      [node('a', 'App'), node('b', 'App'), node('v', 'App'), node('w', 'App'), node('d', 'Domain')],
      [['a', 'b', 'uses'], ['v', 'a', 'refs'], ['w', 'd', 'refs']],
    );
    const f = await buildTypeFlow(g, 'App');
    expect(f.edges.map((e) => e.source + e.target)).toEqual(['ab']);
    expect(f.weakEdges).toHaveLength(1);
    const captions = f.nodes.filter((n) => n.type === 'caption');
    expect(captions).toHaveLength(1); // v и w — оба «связи только вне слоя или слабые», «вообще без связей» пуст
    const v = f.nodes.find((n) => n.id === 'v')!;
    const a = f.nodes.find((n) => n.id === 'a')!;
    expect(v.position.y).toBeGreaterThan(a.position.y); // v в блоке одиночек под схемой, а не в раскладке
  });
});
