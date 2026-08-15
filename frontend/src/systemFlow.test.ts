import { describe, it, expect } from 'vitest';
import type { SystemGraph } from './system';
import { buildSystemFlow, edgeStyle } from './systemFlow';

const graph: SystemGraph = {
  project: 'sys',
  nodes: [
    { id: 'auth/api', name: 'api', kind: 'service' },
    { id: 'product/backend/catalog-api', name: 'catalog-api', kind: 'service' },
    { id: 'product/backend/cart-api', name: 'cart-api', kind: 'service' },
    { id: 'product/backend/redis', name: 'redis', kind: 'datastore' },
    { id: 'http://sms', name: 'sms', kind: 'external' },
  ],
  edges: [
    { id: 'e1', source: 'product/backend/catalog-api', target: 'product/backend/cart-api', kind: 'http-call' }, // внутри карточки
    { id: 'e2', source: 'product/backend/catalog-api', target: 'auth/api', kind: 'http-call' },                 // межсервисное
    { id: 'e3', source: 'product/backend/catalog-api', target: 'product/backend/redis', kind: 'rw-db' },        // сервис→инфра
    { id: 'e4', source: 'auth/api', target: 'http://sms', kind: 'http-call' },                                  // сервис→внешний
  ],
  coverage: { marker: 0, heuristic: 0, unknown: 0, total: 0 },
  drift: [],
  services: [
    { id: 'auth', name: 'AuthService', health: 'ok', layers: [] },
    { id: 'product/backend', name: 'Product', health: 'warning', layers: [] },
  ],
};

describe('buildSystemFlow — роллап compose → карточки .sln', () => {
  const { nodes, edges } = buildSystemFlow(graph);

  it('сервис-узлы сворачиваются в карточки, инфра/внешние остаются', () => {
    expect(nodes.filter((n) => n.type === 'serviceCard').map((n) => n.id).sort())
      .toEqual(['auth', 'product/backend']);
    expect(nodes.filter((n) => n.type === 'infra').map((n) => n.id).sort())
      .toEqual(['http://sms', 'product/backend/redis']);
  });

  it('петля внутри карточки убирается; межсервисные и инфра-рёбра — роллапятся', () => {
    const ids = edges.map((e) => `${e.source}->${e.target}`).sort();
    expect(ids).toEqual([
      'auth->http://sms',
      'product/backend->auth',
      'product/backend->product/backend/redis',
    ]);
  });
});

describe('edgeStyle', () => {
  it('цвет по виду ребра', () => {
    expect(edgeStyle('rw-db', false).stroke).toBe('#57c78a');
    expect(edgeStyle('http-call', false).stroke).toBe('#e08a3c');
  });
  it('http-call/шина — пунктир; rw-db — сплошной', () => {
    expect(edgeStyle('http-call', false).strokeDasharray).toBeDefined();
    expect(edgeStyle('rw-db', false).strokeDasharray).toBeUndefined();
  });
  it('нарушение → красный поверх вида', () => {
    expect(edgeStyle('rw-db', true).stroke).toBe('#e5534b');
  });
});
