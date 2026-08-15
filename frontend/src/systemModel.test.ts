import { describe, it, expect } from 'vitest';
import type { Problem, SystemGraph } from './system';
import {
  problemsByLens,
  worstSeverity,
  severityCounts,
  problemsAt,
  problemsUnder,
  nodesByKind,
  namespaceOf,
  serviceRootPath,
} from './systemModel';

const problems: Problem[] = [
  { id: 'p0', severity: 'warning', kind: 'shared-store', title: 'redis', location: 'sys/redis', category: 'architecture' },
  { id: 'p1', severity: 'warning', kind: 'layer-violation', title: 'слои', location: 'sys/backend', category: 'architecture' },
  { id: 'p2', severity: 'info', kind: 'drift', title: 'дрейф', location: 'sys/backend', category: 'convention' },
  { id: 'p3', severity: 'critical', kind: 'service-cycle', title: 'цикл', location: 'sys/a', category: 'architecture' },
  { id: 'p4', severity: 'coverage', kind: 'ambiguous-target', title: 'неоднозначно', location: 'sys/a' }, // без category
];

describe('problemsByLens', () => {
  it('convention содержит только дрейф', () => {
    expect(problemsByLens(problems, 'convention').map((p) => p.kind)).toEqual(['drift']);
  });
  it('architecture включает проблемы без category (обратная совместимость)', () => {
    const kinds = problemsByLens(problems, 'architecture').map((p) => p.kind);
    expect(kinds).toContain('ambiguous-target'); // category=null → architecture
    expect(kinds).not.toContain('drift');
  });
  it('пустой вход — пустой результат', () => {
    expect(problemsByLens(undefined, 'architecture')).toEqual([]);
  });
});

describe('worstSeverity', () => {
  it('critical суровее warning/coverage/info', () => {
    expect(worstSeverity(problems)).toBe('critical');
  });
  it('info мягче coverage', () => {
    expect(worstSeverity([
      { id: 'x', severity: 'info', kind: 'drift', title: '', location: 'l' },
      { id: 'y', severity: 'coverage', kind: 'k', title: '', location: 'l' },
    ])).toBe('coverage');
  });
  it('пусто → null', () => {
    expect(worstSeverity([])).toBeNull();
  });
  it('неизвестная severity мягче known — critical побеждает (fallback rank)', () => {
    expect(worstSeverity([
      { id: 'a', severity: 'wat', kind: 'k', title: '', location: 'l' },
      { id: 'b', severity: 'critical', kind: 'k', title: '', location: 'l' },
    ])).toBe('critical');
  });
});

describe('severityCounts', () => {
  it('считает по важности', () => {
    expect(severityCounts(problems)).toEqual({ critical: 1, warning: 2, coverage: 1, info: 1 });
  });
  it('игнорирует prototype-ключи severity (constructor/toString) — без мусорных ключей', () => {
    const ps: Problem[] = [
      { id: 'a', severity: 'constructor', kind: 'k', title: '', location: 'l' },
      { id: 'b', severity: 'toString', kind: 'k', title: '', location: 'l' },
      { id: 'c', severity: 'warning', kind: 'k', title: '', location: 'l' },
    ];
    expect(severityCounts(ps)).toEqual({ critical: 0, warning: 1, coverage: 0, info: 0 });
  });
});

describe('problemsAt / problemsUnder', () => {
  it('problemsAt — только точное совпадение id', () => {
    expect(problemsAt(problems, 'sys/redis').map((p) => p.id)).toEqual(['p0']);
    expect(problemsAt(problems, 'sys/backend').map((p) => p.id)).toEqual(['p1', 'p2']);
  });
  it('problemsUnder — namespace и префиксы узлов', () => {
    // per-service проблемы на sys/backend видны для узла sys/backend/catalog-api
    expect(problemsUnder(problems, 'sys/backend').map((p) => p.id)).toEqual(['p1', 'p2']);
    expect(problemsUnder(problems, 'sys').length).toBe(problems.length); // всё под корнем sys
  });
  it('problemsUnder не ловит чужой namespace по частичному совпадению имени', () => {
    const ps: Problem[] = [{ id: 'q', severity: 'warning', kind: 'k', title: '', location: 'orders' }];
    expect(problemsUnder(ps, 'order')).toEqual([]); // «order» ≠ «orders» и не «order/…»
  });
  it('пустой namespace не матчит по ведущему /', () => {
    const ps: Problem[] = [{ id: 'q', severity: 'warning', kind: 'k', title: '', location: '/abs' }];
    expect(problemsUnder(ps, '')).toEqual([]);
  });
});

describe('nodesByKind', () => {
  it('группирует узлы по виду', () => {
    const g: SystemGraph = {
      project: 'sys',
      nodes: [
        { id: 'a', name: 'a', kind: 'service' },
        { id: 'b', name: 'b', kind: 'service' },
        { id: 'db', name: 'db', kind: 'datastore' },
      ],
      edges: [],
      coverage: { marker: 0, heuristic: 0, unknown: 0, total: 0 },
      drift: [],
    };
    const by = nodesByKind(g);
    expect(by.service.map((n) => n.id)).toEqual(['a', 'b']);
    expect(by.datastore.map((n) => n.id)).toEqual(['db']);
  });
});

describe('namespaceOf / serviceRootPath (drill L0→L1)', () => {
  it('namespaceOf — префикс до последнего /', () => {
    expect(namespaceOf('product_site/backend/catalog-api')).toBe('product_site/backend');
    expect(namespaceOf('redis')).toBe(''); // id без /
  });
  it('serviceRootPath собирает абсолютный путь сервиса', () => {
    const node = { id: 'product_site/backend/catalog-api', name: 'catalog-api', kind: 'service' };
    expect(serviceRootPath('/ws', node)).toBe('/ws/product_site/backend');
    expect(serviceRootPath('/ws/', node)).toBe('/ws/product_site/backend'); // хвостовой / не двоится
  });
  it('узел без namespace → корень скана', () => {
    expect(serviceRootPath('/ws', { id: 'redis', name: 'redis', kind: 'datastore' })).toBe('/ws');
  });
});
