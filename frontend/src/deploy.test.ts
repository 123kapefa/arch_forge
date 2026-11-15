import { describe, it, expect } from 'vitest';
import type { SystemGraph, SystemNode } from './system';
import { startTiers, compareEnvs, prodEnvOf, rankRow } from './deploy';

const base = { coverage: { marker: 0, heuristic: 0, unknown: 0, total: 0 }, drift: [] };
const svc = (id: string, deploy?: SystemNode['deploy']): SystemNode => ({ id, name: id, kind: 'service', deploy });
const db = (id: string, deploy?: SystemNode['deploy']): SystemNode => ({ id, name: id, kind: 'datastore', deploy });
const g = (nodes: SystemNode[], edges: SystemGraph['edges'] = []): SystemGraph => ({ project: 'p', nodes, edges, ...base });
const pub = (host: string, container: string) => ({ container, host, published: true });

describe('startTiers', () => {
  it('зависимость от compose-ребра: БД — ярус 0, сервис — 1, зависящий от него — 2', () => {
    const t = startTiers(g([], [
      { id: '1', source: 'api', target: 'db', kind: 'rw-db', via: 'compose' },
      { id: '2', source: 'gw', target: 'api', kind: 'rw-db', via: 'compose' },
      { id: '3', source: 'gw', target: 'x', kind: 'http-call', via: 'config' }, // не compose — игнор
    ]));
    expect(t.get('db')).toBe(0);
    expect(t.get('api')).toBe(1);
    expect(t.get('gw')).toBe(2);
    expect(t.has('x')).toBe(false);
  });
  it('цикл не зависает', () => {
    const t = startTiers(g([], [
      { id: '1', source: 'a', target: 'b', kind: 'rw-db', via: 'compose' },
      { id: '2', source: 'b', target: 'a', kind: 'rw-db', via: 'compose' },
    ]));
    expect(t.size).toBe(2);
  });
});

describe('compareEnvs', () => {
  const dev = g([db('pg', { ports: [{ container: '5432', published: false }], hasHealthcheck: true, networks: ['n'] }), svc('api', { ports: [pub('8080', '80')], hasHealthcheck: true, networks: ['n'] })]);
  const prod = g([
    db('pg', { ports: [pub('5432', '5432')], hasHealthcheck: false, networks: ['n'] }),
    svc('api', { ports: [pub('8080', '80')], hasHealthcheck: true, networks: ['n'] }),
  ]);
  it('prodEnvOf: prod по имени, иначе последняя', () => {
    expect(prodEnvOf(['dev', 'prod', 'stage'])).toBe('prod');
    expect(prodEnvOf(['dev', 'stage'])).toBeNull(); // не гадаем по порядку списка
    expect(prodEnvOf(['default'])).toBeNull();
  });
  it('дифф: healthcheck потерян и порт БД наружу в prod → ломается; одинаковое не «меняется»', () => {
    const rows = compareEnvs([{ name: 'dev', graph: dev }, { name: 'prod', graph: prod }]);
    const pg = rows.find((r) => r.id === 'pg')!;
    expect(pg.changed).toEqual(['ports', 'healthcheck']);
    expect(pg.breaks.some((b) => b.includes('порт хранилища наружу'))).toBe(true);
    expect(pg.breaks.some((b) => b.includes('healthcheck'))).toBe(true);
    const api = rows.find((r) => r.id === 'api')!;
    expect(api.changed).toEqual([]);
    expect(api.breaks).toEqual([]);
    expect(rows[0].id).toBe('pg'); // «ломающиеся» сверху
  });
  it('узел есть во всех непрод-средах, но нет в prod → presence + «нет в prod»', () => {
    const only = g([svc('api', { ports: [], hasHealthcheck: true, networks: [] })]);
    const rows = compareEnvs([{ name: 'dev', graph: dev }, { name: 'stage', graph: dev }, { name: 'prod', graph: only }]);
    const pg = rows.find((r) => r.id === 'pg')!;
    expect(pg.changed).toContain('presence');
    expect(pg.breaks).toContain('нет в prod');
  });
  it('одна среда / пустые данные не падают', () => {
    expect(compareEnvs([])).toEqual([]);
    expect(compareEnvs([{ name: 'default', graph: g([svc('a')]) }])).toEqual([]);
  });
  it('порт хранилища наружу во ВСЕХ средах — не «поломка перехода» (зона детектора)', () => {
    const exp = (hc: boolean) => g([db('pg', { ports: [{ container: '5432', host: '5432', published: true }], hasHealthcheck: hc, networks: [] })]);
    const rows = compareEnvs([{ name: 'dev', graph: exp(true) }, { name: 'prod', graph: exp(true) }]);
    expect(rows.find((r) => r.id === 'pg')?.breaks ?? []).toEqual([]);
  });
  it('порты не опубликованы в prod — предупреждение, не «ломается»', () => {
    const rows = compareEnvs([
      { name: 'dev', graph: g([svc('api', { ports: [pub('8080', '80')], hasHealthcheck: true, networks: [] })]) },
      { name: 'prod', graph: g([svc('api', { ports: [{ container: '80', published: false }], hasHealthcheck: true, networks: [] })]) },
    ]);
    const api = rows[0];
    expect(api.breaks).toEqual([]);
    expect(api.warns).toEqual(['порты не опубликованы в prod']);
    expect(rankRow(api)).toBe(1);
  });
  it('узел только в dev (не во всех остальных средах): presence, но без «нет в prod»', () => {
    const withTool = g([svc('api', { ports: [], hasHealthcheck: true, networks: [] }), svc('mailhog', { ports: [pub('8025', '8025')], hasHealthcheck: false, networks: [] })]);
    const plain = g([svc('api', { ports: [], hasHealthcheck: true, networks: [] })]);
    const rows = compareEnvs([{ name: 'dev', graph: withTool }, { name: 'stage', graph: plain }, { name: 'prod', graph: plain }]);
    const m = rows.find((r) => r.id === 'mailhog')!;
    expect(m.changed).toContain('presence');
    expect(m.cells.prod.present).toBe(false);
    expect(m.breaks).toEqual([]);
  });
  it('rankRow: ломается < предупреждение/меняется < без отличий', () => {
    const r = (o: Partial<{ breaks: string[]; warns: string[]; changed: string[] }>) =>
      rankRow({ id: '', name: '', kind: '', cells: {}, changed: [], breaks: [], warns: [], ...o });
    expect(r({ breaks: ['x'] })).toBe(0);
    expect(r({ changed: ['ports'] })).toBe(1);
    expect(r({})).toBe(2);
  });
});

describe('startTiers — циклы', () => {
  it('цикл из трёх узлов: завершается, ярусы ограничены', () => {
    const t = startTiers(g([], [
      { id: '1', source: 'a', target: 'b', kind: 'rw-db', via: 'compose' },
      { id: '2', source: 'b', target: 'c', kind: 'rw-db', via: 'compose' },
      { id: '3', source: 'c', target: 'a', kind: 'rw-db', via: 'compose' },
    ]));
    expect(t.size).toBe(3);
    for (const v of t.values()) expect(v).toBeLessThanOrEqual(3);
  });
});
