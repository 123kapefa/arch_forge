import { describe, it, expect } from 'vitest';
import type { SystemGraph } from './system';
import { worstServices, topProblems, coverageShare, buildSearchIndex, searchEntries } from './overviewModel';

const g: SystemGraph = {
  project: 'p',
  nodes: [
    { id: 's/a', name: 'Alpha', kind: 'service', health: 'critical', root: '/r/a', endpoints: [{ method: 'GET', route: '/orders', source: 'controller', type: 'OrdersController' }] },
    { id: 's/b', name: 'Beta', kind: 'service', health: 'ok', root: '/r/b' },
    { id: 's/c', name: 'Gamma', kind: 'service', health: 'warning', root: '/r/c' },
    { id: 's/db', name: 'Postgres', kind: 'datastore' },
  ],
  edges: [],
  coverage: { marker: 6, heuristic: 3, unknown: 1, total: 10 },
  drift: [],
  problems: [
    { id: '1', severity: 'critical', kind: 'service-cycle', title: 'цикл', location: 's/a', category: 'architecture' },
    { id: '2', severity: 'warning', kind: 'shared-db', title: 'общая БД', location: 's/db', category: 'architecture' },
    { id: '3', severity: 'warning', kind: 'drift', title: 'дрейф', location: 's', category: 'convention',
      items: [{ name: 'OrderDto', layer: 'API', note: '', file: '/r/c/Dto/OrderDto.cs' }] },
    { id: '4', severity: 'info', kind: 'x', title: 'инфо', location: 's/b' },
  ],
};

describe('worstServices', () => {
  it('сортирует по здоровью и числу проблем, скрывает здоровые без проблем', () => {
    const r = worstServices(g).map((s) => s.node.name);
    expect(r).toEqual(['Alpha', 'Gamma']);
  });
  it('дрейф приписывается владельцу файла', () => {
    expect(worstServices(g).find((s) => s.node.name === 'Gamma')?.warning).toBe(1);
  });
});

describe('topProblems', () => {
  it('архитектурные раньше дрейфа, info отсекается', () => {
    expect(topProblems(g).map((p) => p.id)).toEqual(['1', '2', '3']);
  });
});

describe('coverageShare', () => {
  it('считает проценты', () => {
    expect(coverageShare(g)).toEqual({ marker: 60, heuristic: 30, unknown: 10, total: 10 });
  });
  it('нет разбивки (структурный скан) → нули', () => {
    expect(coverageShare({ ...g, coverage: { marker: 0, heuristic: 0, unknown: 0, total: 43 } }).total).toBe(0);
  });
  it('total=0 → нули', () => {
    expect(coverageShare({ ...g, coverage: { marker: 0, heuristic: 0, unknown: 0, total: 0 } }).marker).toBe(0);
  });
});

describe('поиск', () => {
  const idx = buildSearchIndex(g);
  it('находит сервис, endpoint и тип из проблем', () => {
    expect(searchEntries(idx, 'alp')[0].nodeId).toBe('s/a');
    expect(searchEntries(idx, 'orders').some((e) => e.kind === 'endpoint' && e.nodeId === 's/a')).toBe(true);
    const t = searchEntries(idx, 'orderdto').find((e) => e.kind === 'type');
    expect(t?.nodeId).toBe('s/c');
  });
  it('пустой запрос → сервисы и хранилища', () => {
    expect(searchEntries(idx, '').every((e) => ['service', 'datastore', 'topic'].includes(e.kind))).toBe(true);
  });
  it('точное совпадение выше вхождения', () => {
    expect(searchEntries(idx, 'beta')[0].label).toBe('Beta');
  });
});
