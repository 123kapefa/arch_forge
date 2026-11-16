import { describe, expect, it } from 'vitest';
import type { Problem, SystemGraph } from '../../../shared/api';
import { countBySeverity, evidenceOf, rankFindings, relativeTo } from './findings';
import { layoutMiniMap } from './miniMap';
import { passportOf } from './passport';
import { resolveProjectPath } from './projectPath';
import { shareOf, trustOf } from './trust';

const p = (id: string, severity: string, extra: Partial<Problem> = {}): Problem => ({ id, severity, kind: 'k', title: id, location: 'x', ...extra });
const graph = (extra: Partial<SystemGraph> = {}): SystemGraph => ({
  project: 'demo', nodes: [], edges: [], coverage: { marker: 0, heuristic: 0, unknown: 0, total: 0 }, drift: [], ...extra,
});

describe('rankFindings', () => {
  it('серьёзность → уверенность → архитектура раньше дрейфа', () => {
    const ranked = rankFindings([
      p('info', 'info'),
      p('warn-low', 'warning', { confidence: 0.4 }),
      p('crit-drift', 'critical', { category: 'convention', confidence: 0.9 }),
      p('warn-high', 'warning', { confidence: 0.9 }),
      p('crit', 'critical', { confidence: 0.9 }),
      p('warn-none', 'warning'),
    ]);
    expect(ranked.map((x) => x.id)).toEqual(['crit', 'crit-drift', 'warn-high', 'warn-low', 'warn-none', 'info']);
  });
  it('неизвестная серьёзность — в конец, исходный массив не меняется', () => {
    const input = [p('a', 'weird'), p('b', 'info')];
    expect(rankFindings(input).map((x) => x.id)).toEqual(['b', 'a']);
    expect(input[0].id).toBe('a');
  });
  it('считает по серьёзностям', () => {
    expect(countBySeverity([p('a', 'critical'), p('b', 'info'), p('c', 'weird')])).toEqual({ critical: 1, warning: 0, coverage: 0, info: 1, total: 3 });
  });
});

describe('evidenceOf', () => {
  it('файл:строка из первого факта с путём, уверенность в %', () => {
    const e = evidenceOf(p('a', 'warning', { kind: 'layer-violation', via: 'code', confidence: 0.857, items: [{ name: 'n', layer: 'l', note: '' }, { name: 'm', layer: 'l', note: '', file: 'A.cs', line: 12 }] }));
    expect(e).toEqual({ location: 'A.cs:12', rule: 'layer-violation', confidencePct: 86, source: 'code' });
  });
  it('нет данных — null, ничего не выдумываем', () => {
    expect(evidenceOf(p('a', 'info'))).toEqual({ location: null, rule: 'k', confidencePct: null, source: null });
  });
});

describe('relativeTo', () => {
  it('срезает корень проекта, чужие пути не трогает', () => {
    expect(relativeTo('/r/p/src/A.cs', '/r/p/')).toBe('src/A.cs');
    expect(relativeTo('/other/A.cs', '/r/p')).toBe('/other/A.cs');
    expect(relativeTo('/r/p/A.cs')).toBe('/r/p/A.cs');
  });
});

describe('trust', () => {
  it('shareOf: без разбивки по источнику — null', () => {
    expect(shareOf({ marker: 0, heuristic: 0, unknown: 0, total: 10 })).toBeNull();
    expect(shareOf(undefined)).toBeNull();
    expect(shareOf({ marker: 1, heuristic: 5, unknown: 4, total: 10 })).toMatchObject({ marker: 10, heuristic: 50, unknown: 40 });
  });
  it('деградации → partial; чисто при частичном разборе помечается как не безопасное', () => {
    const t = trustOf(graph({ degradations: ['DI не прочитан', '  '] }));
    expect(t.level).toBe('partial');
    expect(t.degradations).toEqual(['DI не прочитан']);
    expect(t.cleanIsNotSafe).toBe(true);
  });
  it('полный разбор без находок — можно верить', () => {
    const t = trustOf(graph({ coverage: { marker: 5, heuristic: 3, unknown: 2, total: 10 } }));
    expect(t.level).toBe('full');
    expect(t.cleanIsNotSafe).toBe(false);
  });
  it('нет покрытия и деградаций — unknown', () => {
    expect(trustOf(graph()).level).toBe('unknown');
  });
  it('строки по сервисам: больше неизвестного — выше', () => {
    const cov = (u: number) => ({ marker: 10 - u, heuristic: 0, unknown: u, total: 10 });
    const t = trustOf(graph({ services: [
      { id: 'a', name: 'a', health: 'ok', layers: [], coverage: cov(1) },
      { id: 'b', name: 'b', health: 'ok', layers: [], coverage: cov(8) },
      { id: 'c', name: 'c', health: 'ok', layers: [] },
    ] }));
    expect(t.rows.map((r) => r.id)).toEqual(['b', 'a']);
  });
});

describe('passport / miniMap / path', () => {
  const g = graph({
    nodes: [
      { id: 's1', name: 's1', kind: 'service', endpoints: [{ method: 'GET', route: '/', source: 'controller' }] },
      { id: 'd1', name: 'db', kind: 'datastore' },
      { id: 'e1', name: 'ext', kind: 'external' },
      { id: 'z', name: 'z', kind: 'strange' },
    ],
    edges: [{ id: 'e', source: 's1', target: 'd1', kind: 'rw-db' }],
    services: [{ id: 's1', name: 's1', health: 'ok', layers: [{ name: 'API', health: 'ok', types: 1 }, { name: 'Domain', health: 'ok', types: 1 }] }],
  });
  it('паспорт', () => {
    expect(passportOf(g)).toMatchObject({ services: 1, datastores: 1, externals: 2, endpoints: 1, edges: 1, layers: ['API', 'Domain'] });
  });
  it('мини-карта: лимит узлов и скрытые', () => {
    const m = layoutMiniMap(g, 2);
    expect(m.nodes.map((n) => n.id).sort()).toEqual(['d1', 's1']);
    expect(m.hidden).toBe(2);
    const many = graph({ nodes: Array.from({ length: 30 }, (_, i) => ({ id: `s${i}`, name: `s${i}`, kind: 'service' })) });
    expect(layoutMiniMap(many).nodes).toHaveLength(14);
    expect(layoutMiniMap(many).hidden).toBe(16);
    expect(m.edges).toHaveLength(1);
  });
  it('путь: URL приоритетнее localStorage; пусто → null', () => {
    expect(resolveProjectPath(new URLSearchParams('path=/a'), () => '/b')).toBe('/a');
    expect(resolveProjectPath(new URLSearchParams(''), () => ' /b ')).toBe('/b');
    expect(resolveProjectPath(new URLSearchParams('path=%20'), () => null)).toBeNull();
  });
});
