import { describe, it, expect } from 'vitest';
import type { SystemGraph } from './system';
import { buildSystemFlow, edgeStyle, isInfra } from './systemFlow';

const graph: SystemGraph = {
  project: 'sys',
  nodes: [
    { id: 'p/catalog-api', name: 'catalog-api', kind: 'service', health: 'ok' },
    { id: 'p/cart-api', name: 'cart-api', kind: 'service', health: 'ok' },
    { id: 'p/redis', name: 'redis', kind: 'datastore', health: 'warning' },
    { id: 'http://sms', name: 'sms', kind: 'external' },
  ],
  edges: [
    { id: 'e1', source: 'p/catalog-api', target: 'p/cart-api', kind: 'http-call' }, // вызов между микросервисами — СОХРАНЯЕТСЯ
    { id: 'e2', source: 'p/catalog-api', target: 'p/redis', kind: 'rw-db' },
    { id: 'e3', source: 'p/cart-api', target: 'p/cart-api', kind: 'http-call' },       // петля — убираем
    { id: 'e4', source: 'p/cart-api', target: 'http://sms', kind: 'http-call' },
  ],
  coverage: { marker: 0, heuristic: 0, unknown: 0, total: 0 },
  drift: [],
  problems: [{ id: 'q', severity: 'warning', kind: 'shared-store', title: 'redis', location: 'p/redis', category: 'architecture' }],
};

describe('buildSystemFlow — микросервисы как узлы (без роллапа)', () => {
  const { nodes, edges } = buildSystemFlow(graph);

  it('все узлы графа присутствуют (микросервисы + инфра), тип sys', () => {
    expect(nodes.map((n) => n.id).sort()).toEqual(['http://sms', 'p/cart-api', 'p/catalog-api', 'p/redis']);
    expect(nodes.every((n) => n.type === 'sys')).toBe(true);
  });

  it('граф вызовов между микросервисами сохранён, петля убрана', () => {
    const ids = edges.map((e) => `${e.source}->${e.target}`).sort();
    expect(ids).toEqual([
      'p/cart-api->http://sms',
      'p/catalog-api->p/cart-api', // межсервисный вызов НЕ схлопнут
      'p/catalog-api->p/redis',
    ]);
  });

  it('здоровье и проблемы узла прокинуты', () => {
    const redis = nodes.find((n) => n.id === 'p/redis')!;
    expect((redis.data as { health: string }).health).toBe('warning');
    expect((redis.data as { problems: unknown[] }).problems).toHaveLength(1);
  });
});

describe('isInfra / edgeStyle', () => {
  it('вид узла', () => {
    expect(isInfra('datastore')).toBe(true);
    expect(isInfra('topic')).toBe(true);
    expect(isInfra('external')).toBe(true);
    expect(isInfra('service')).toBe(false);
  });
  it('цвет/пунктир ребра и красный при нарушении', () => {
    expect(edgeStyle('rw-db', false).stroke).toBe('#57c78a');
    expect(edgeStyle('http-call', false).strokeDasharray).toBeDefined();
    expect(edgeStyle('rw-db', true).stroke).toBe('#e5534b');
  });
});
