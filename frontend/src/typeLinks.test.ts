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
    expect(captions.map((c) => c.id)).toEqual(['caption:section:Связи только вне слоя или слабые (refs)', 'caption:linked:constants']); // (классы без членов — контейнеры) раздел + подгруппа; «без связей» пуст
    const v = f.nodes.find((n) => n.id === 'v')!;
    const a = f.nodes.find((n) => n.id === 'a')!;
    expect(v.position.y).toBeGreaterThan(a.position.y); // v в блоке одиночек под схемой, а не в раскладке
  });
});

describe('buildTypeFlow: подгруппы одиночек', () => {
  const mk = (id: string, role = 'unknown', kind = 'class'): NodeDto => ({ ...node(id, 'App', role), kind });
  const many = (n: number) => Array.from({ length: n }, (_, i) => mk(`v${String(i).padStart(2, '0')}`, 'Validator'));
  const base = [mk('hub'), mk('rec', 'unknown', 'record'), mk('lone'), ...many(13)];
  // каждый валидатор и запись ссылаются через refs на hub (слабо) → одиночки с «используют»
  const edgesTo = base.filter((n) => n.id !== 'hub' && n.id !== 'lone').map((n): [string, string, string] => ['hub', n.id, 'refs']);

  it('делит по ролям; большая группа (>12) свёрнута: карточек нет, в подписи превью', async () => {
    const f = await buildTypeFlow(map(base, edgesTo), 'App');
    const cap = f.nodes.find((n) => n.id === 'caption:linked:validators')!;
    expect((cap.data as { collapsed: boolean }).collapsed).toBe(true);
    expect((cap.data as { count: number }).count).toBe(13);
    expect((cap.data as { preview: string[] }).preview!.length).toBeLessThanOrEqual(6);
    expect(f.nodes.some((n) => n.id === 'v00')).toBe(false);
    expect(f.nodes.some((n) => n.id === 'rec')).toBe(true); // малая группа записей развёрнута
    expect(f.nodes.some((n) => n.id === 'caption:none')).toBe(true); // lone — вообще без связей
  });
  it('явное разворачивание из персиста важнее порога; порядок: больше входящих, затем имя', async () => {
    const g = map(base, [...edgesTo, ['v05', 'v07', 'refs'], ['v06', 'v07', 'refs']]);
    const f = await buildTypeFlow(g, 'App', undefined, { 'linked:validators': false });
    const vs = f.nodes.filter((n) => /^v\d\d$/.test(n.id));
    expect(vs).toHaveLength(13);
    const ys = vs.map((n) => n.position.y);
    const first = vs.sort((a, b) => a.position.y - b.position.y || a.position.x - b.position.x)[0];
    expect(first.id).toBe('v07'); // у v07 три входящих (hub, v05, v06) — выше остальных
    expect(Math.min(...ys)).toBeGreaterThan(0);
    const d = first.data as { usedBy: { names: string[]; total: number } };
    expect(d.usedBy.total).toBe(3);
  });
});

describe('сворачивание подгруппы не двигает связную часть', () => {
  const mk2 = (id: string, role = 'unknown', kind = 'class'): NodeDto => ({ ...node(id, 'App', role), kind });
  const vals = Array.from({ length: 5 }, (_, i) => mk2(`v${i}`, 'Validator'));
  const g = map(
    [mk2('a'), mk2('b'), mk2('hub'), mk2('rec', 'unknown', 'record'), ...vals],
    [['a', 'b', 'uses'], ...vals.map((v): [string, string, string] => ['hub', v.id, 'refs']), ['hub', 'rec', 'refs']],
  );
  it('позиции связной части и подгрупп выше свёрнутой не меняются, нижние смещаются вверх', async () => {
    const open = await buildTypeFlow(g, 'App', undefined, { 'linked:validators': false });
    const shut = await buildTypeFlow(g, 'App', undefined, { 'linked:validators': true });
    const pos = (f: typeof open, id: string) => f.nodes.find((n) => n.id === id)!.position;
    expect(pos(shut, 'a')).toEqual(pos(open, 'a'));
    expect(pos(shut, 'b')).toEqual(pos(open, 'b'));
    expect(pos(shut, 'caption:linked:validators' as string)).toEqual(pos(open, 'caption:linked:validators' as string));
    expect(shut.nodes.some((n) => n.id === 'v0')).toBe(false);
    expect(pos(shut, 'rec').y).toBeLessThan(pos(open, 'rec').y); // «Записи» ниже валидаторов — поднялись на высоту свёрнутой группы
    expect(open.nodes.every((n) => Number.isFinite(n.position.x) && Number.isFinite(n.position.y))).toBe(true);
  });
});
