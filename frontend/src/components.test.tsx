import { describe, it, expect } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import { CompareView } from './CompareView';
import { DeployStrip } from './nodes/SystemNodeView';
import { DeployContext } from './deployContext';
import type { SystemGraph, SystemNode } from './system';

const base = { coverage: { marker: 0, heuristic: 0, unknown: 0, total: 0 }, drift: [] };
const g = (nodes: SystemNode[]): SystemGraph => ({ project: 'p', nodes, edges: [], ...base });
const noop = () => {};

describe('CompareView', () => {
  it('одна среда → подсказка «сравнивать не с чем»', () => {
    const html = renderToStaticMarkup(<CompareView envs={[{ name: 'default', graph: g([]) }]} loading={false} errors={[]} onClose={noop} />);
    expect(html).toContain('одна среда');
  });
  it('нет prod → поясняет, что пометок «ломается» не будет', () => {
    const html = renderToStaticMarkup(<CompareView envs={[{ name: 'dev', graph: g([]) }, { name: 'stage', graph: g([]) }]} loading={false} errors={[]} onClose={noop} />);
    expect(html).toContain('prod</code> нет');
  });
  it('таблица: порт БД наружу красится по порту, «ломается» и колонка prod', () => {
    const pgDev: SystemNode = { id: 'pg', name: 'pg', kind: 'datastore', deploy: { ports: [{ container: '5432', published: false }], hasHealthcheck: true, networks: [] } };
    const pgProd: SystemNode = { id: 'pg', name: 'pg', kind: 'datastore', deploy: { ports: [{ container: '5432', host: '5432', published: true }, { container: '9187', published: false }], hasHealthcheck: true, networks: [] } };
    const html = renderToStaticMarkup(<CompareView envs={[{ name: 'dev', graph: g([pgDev]) }, { name: 'prod', graph: g([pgProd]) }]} loading={false} errors={[]} onClose={noop} />);
    expect(html).toContain('ломается: порт хранилища наружу в prod');
    expect(html).toContain('dep-chip--exposed');
    expect(html).toContain('dep-chip--internal'); // второй порт той же ячейки не покрашен красным
    expect(html).toContain('(prod)');
  });
  it('загрузка и ошибки среды отображаются', () => {
    const html = renderToStaticMarkup(<CompareView envs={[]} loading errors={['Среда «x»: boom']} onClose={noop} />);
    expect(html).toContain('загружаю среды');
    expect(html).toContain('boom');
  });
});

describe('DeployStrip', () => {
  const node: SystemNode = { id: 'api', name: 'api', kind: 'service', deploy: { ports: [{ container: '80', host: '8080', published: true }], hasHealthcheck: false, networks: ['front'] } };
  const all = { ports: true, networks: true, healthcheck: true, start: true };
  const render = (ctx: Parameters<typeof DeployContext.Provider>[0]['value'], n = node) =>
    renderToStaticMarkup(<DeployContext.Provider value={ctx}><DeployStrip node={n} /></DeployContext.Provider>);

  it('вне режима Deploy ничего не рисует', () => expect(render(null)).toBe(''));
  it('порты, hc и сети; ярус — если есть', () => {
    const html = render({ overlays: all, tiers: new Map([['api', 1]]) });
    expect(html).toContain('8080');
    expect(html).toContain('hc —');
    expect(html).toContain('front');
    expect(html).toContain('⛓ 2');
  });
  it('выключенные оверлеи и пустой узел → пусто', () => {
    expect(render({ overlays: { ports: false, networks: false, healthcheck: false, start: false }, tiers: new Map() })).toBe('');
    expect(render({ overlays: all, tiers: new Map() }, { id: 'x', name: 'x', kind: 'service' })).toBe('');
  });
  it('больше трёх портов сворачиваются в +N', () => {
    const many: SystemNode = { ...node, deploy: { ports: [1, 2, 3, 4, 5].map((i) => ({ container: String(i), published: false })), hasHealthcheck: true, networks: [] } };
    expect(render({ overlays: all, tiers: new Map() }, many)).toContain('+2');
  });
});
