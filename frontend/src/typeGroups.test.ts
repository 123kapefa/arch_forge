import { describe, expect, it } from 'vitest';
import { COLLAPSE_OVER, compareByIncoming, incomingCount, isGroupCollapsed, subgroupOf, usedByLine, usedByOf } from './typeGroups';
import type { OutsideSummary } from './typeLinks';

const sum = (links: { dir: 'in' | 'out'; peerName: string }[]): OutsideSummary => ({
  links: links.map((l, i) => ({ peerId: 'p' + i, peerName: l.peerName, peerLayer: 'App', dir: l.dir, kinds: ['refs'], weak: true })),
  out: links.filter((l) => l.dir === 'out').length, in: links.filter((l) => l.dir === 'in').length, layers: ['App'],
});

describe('subgroupOf', () => {
  it('по роли', () => {
    expect(subgroupOf({ name: 'X', role: 'Validator', kind: 'class' })).toBe('validators');
    expect(subgroupOf({ name: 'X', role: 'Validator?', kind: 'class' })).toBe('validators');
    expect(subgroupOf({ name: 'X', role: 'Endpoint', kind: 'class' })).toBe('endpoints');
    expect(subgroupOf({ name: 'X', role: 'Event', kind: 'record' })).toBe('records');
  });
  it('запасной признак — имя/вид', () => {
    expect(subgroupOf({ name: 'ReturnUrlValidator', role: 'unknown', kind: 'class', members: 2 })).toBe('validators');
    expect(subgroupOf({ name: 'MapEndpoint', role: 'unknown', kind: 'class', members: 1 })).toBe('endpoints');
    expect(subgroupOf({ name: 'UserEndpoints', role: 'unknown', kind: 'class', members: 3 })).toBe('endpoints');
    expect(subgroupOf({ name: 'AdminErrors', role: 'unknown', kind: 'class', members: 16 })).toBe('constants');
    expect(subgroupOf({ name: 'Users', role: 'unknown', kind: 'class', members: 0 })).toBe('constants'); // вложенный контейнер без членов
    expect(subgroupOf({ name: 'ClientInfo', role: 'unknown', kind: 'record', members: 2 })).toBe('records');
    expect(subgroupOf({ name: 'Registration', role: 'unknown', kind: 'class', members: 1 })).toBe('other');
    expect(subgroupOf({ name: 'IFoo', role: 'unknown', kind: 'interface', members: 0 })).toBe('other');
  });
});

describe('usedBy', () => {
  const s = sum([{ dir: 'in', peerName: 'Zed' }, { dir: 'in', peerName: 'Abc' }, { dir: 'out', peerName: 'Q' }, { dir: 'in', peerName: 'Mid' }]);
  it('только входящие, по алфавиту, со счётчиком', () => {
    const u = usedByOf(s);
    expect(u.names).toEqual(['Abc', 'Mid', 'Zed']);
    expect(u.total).toBe(3);
    expect(usedByLine(u)).toBe('Abc, Mid (+1)');
    expect(usedByLine(usedByOf(sum([{ dir: 'in', peerName: 'A' }])))).toBe('A');
    expect(usedByLine(usedByOf(undefined))).toBeNull();
  });
  it('входящие = внутри слоя + снаружи; порядок: больше входящих, затем имя', () => {
    expect(incomingCount(s, sum([{ dir: 'in', peerName: 'E' }]))).toBe(4);
    const items = [{ name: 'B', incoming: 1 }, { name: 'A', incoming: 1 }, { name: 'C', incoming: 5 }];
    expect(items.sort(compareByIncoming).map((i) => i.name)).toEqual(['C', 'A', 'B']);
  });
});

describe('isGroupCollapsed', () => {
  it('большие свёрнуты по умолчанию, явный выбор важнее', () => {
    expect(isGroupCollapsed({}, 'k', COLLAPSE_OVER + 1)).toBe(true);
    expect(isGroupCollapsed({}, 'k', COLLAPSE_OVER)).toBe(false);
    expect(isGroupCollapsed({ k: false }, 'k', 50)).toBe(false);
    expect(isGroupCollapsed({ k: true }, 'k', 2)).toBe(true);
  });
});
