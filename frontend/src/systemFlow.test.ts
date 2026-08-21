import { describe, it, expect } from 'vitest';
import type { SystemGraph } from './system';
import { buildSystemFlow, edgeStyle, edgeWidth, isInfra, filterGraph, edgeKindsOf,
  isPortPublic, portExposure, portLabel, nodeHasExposedPort } from './systemFlow';
import type { SystemPort } from './system';

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

describe('filterGraph / edgeKindsOf', () => {
  const g: SystemGraph = {
    project: 's', coverage: { marker: 0, heuristic: 0, unknown: 0, total: 0 }, drift: [],
    nodes: [
      { id: 'a', name: 'a', kind: 'service' },
      { id: 'b', name: 'b', kind: 'service' },
      { id: 'iso', name: 'iso', kind: 'service' },
    ],
    edges: [
      { id: 'e1', source: 'a', target: 'b', kind: 'http-call', confidence: 0.8 },
      { id: 'e2', source: 'a', target: 'b', kind: 'rw-db', confidence: 0.4 },
    ],
  };

  it('edgeKindsOf — уникальные виды, сортировка', () => {
    expect(edgeKindsOf(g)).toEqual(['http-call', 'rw-db']);
  });
  it('фильтр по виду ребра', () => {
    const r = filterGraph(g, { edgeKinds: new Set(['http-call']), minConfidence: 0, hideIsolated: false });
    expect(r.edges.map((e) => e.kind)).toEqual(['http-call']);
  });
  it('порог confidence прячет слабые связи', () => {
    const r = filterGraph(g, { edgeKinds: new Set(['http-call', 'rw-db']), minConfidence: 0.5, hideIsolated: false });
    expect(r.edges.map((e) => e.id)).toEqual(['e1']); // 0.4 отсеян
  });
  it('без одиночек — узлы без связей убираются', () => {
    const r = filterGraph(g, { edgeKinds: new Set(['http-call', 'rw-db']), minConfidence: 0, hideIsolated: true });
    expect(r.nodes.map((n) => n.id).sort()).toEqual(['a', 'b']); // iso убран
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
    expect(edgeStyle('rw-db', false).stroke).toBe('#6fcf77');
    expect(edgeStyle('http-call', false).strokeDasharray).toBeDefined();
    expect(edgeStyle('rw-db', true).stroke).toBe('#e5484d');
  });
  it('толщина ребра растёт с уверенностью, нарушение не тоньше 2', () => {
    expect(edgeWidth(1)).toBeGreaterThan(edgeWidth(0.6));
    expect(edgeWidth(0.4)).toBeLessThan(edgeWidth(0.95));
    expect(edgeWidth(0.3, true)).toBeGreaterThanOrEqual(2); // слабое, но нарушение — всё равно заметно
  });
});

describe('deploy — выставленность портов (зеркалит backend ExposedPortDetector)', () => {
  const port = (p: Partial<SystemPort>): SystemPort =>
    ({ container: '5432', published: true, ...p });

  it('публичность: все интерфейсы (0.0.0.0 / :: / пусто / без host_ip) — да; localhost (v4/v6)/не опубликован — нет', () => {
    expect(isPortPublic(port({ hostIp: undefined }))).toBe(true);
    expect(isPortPublic(port({ hostIp: '' }))).toBe(true);        // пустая строка = все интерфейсы (как undefined)
    expect(isPortPublic(port({ hostIp: '0.0.0.0' }))).toBe(true);
    expect(isPortPublic(port({ hostIp: '::' }))).toBe(true);
    expect(isPortPublic(port({ hostIp: '127.0.0.1' }))).toBe(false);
    expect(isPortPublic(port({ hostIp: '::1' }))).toBe(false);    // IPv6 localhost
    expect(isPortPublic(port({ published: false }))).toBe(false);
  });

  it('классификация: хранилище наружу → exposed, сервис наружу → public, конкретный IP → bound, не опубликован → internal', () => {
    expect(portExposure(port({ hostIp: undefined }), 'datastore')).toBe('exposed');
    expect(portExposure(port({ hostIp: undefined }), 'service')).toBe('public');
    expect(portExposure(port({ hostIp: '127.0.0.1' }), 'datastore')).toBe('bound');
    expect(portExposure(port({ hostIp: '::1' }), 'datastore')).toBe('bound');        // IPv6 localhost — bound, не exposed
    expect(portExposure(port({ hostIp: '192.168.1.5' }), 'datastore')).toBe('bound'); // LAN-IP не теряется
    expect(portExposure(port({ published: false }), 'datastore')).toBe('internal');
  });

  it('portLabel: опубликованный → «host → container», эфемерный без host → «* → container», внутренний → container', () => {
    expect(portLabel(port({ host: '5432', hostIp: undefined }))).toBe('5432 → 5432');
    expect(portLabel(port({ host: '15432', hostIp: '127.0.0.1' }))).toBe('127.0.0.1:15432 → 5432');
    expect(portLabel(port({ host: undefined, hostIp: undefined }))).toBe('* → 5432'); // эфемерный: host назначит docker
    expect(portLabel(port({ published: false, host: undefined }))).toBe('5432');
  });

  it('nodeHasExposedPort: хранилище с портом наружу → true; сервис/без deploy → false', () => {
    expect(nodeHasExposedPort(
      { id: 'd', name: 'db', kind: 'datastore', deploy: { ports: [port({ hostIp: undefined })], hasHealthcheck: false, networks: [] } },
    )).toBe(true);
    expect(nodeHasExposedPort(
      { id: 's', name: 'api', kind: 'service', deploy: { ports: [port({ hostIp: undefined })], hasHealthcheck: false, networks: [] } },
    )).toBe(false); // сервис наружу — это public, не exposed
    expect(nodeHasExposedPort({ id: 'x', name: 'x', kind: 'datastore' })).toBe(false); // нет deploy
  });
});
