import { describe, it, expect } from 'vitest';
import type { GraphDto } from './api';
import { buildCodeUrl, nodeDeps } from './nodeInfo';

describe('buildCodeUrl', () => {
  it('macOS абсолютный путь — один слэш', () => {
    expect(buildCodeUrl('/Users/x/Foo.cs')).toBe('vscode://file/Users/x/Foo.cs');
  });

  it('Windows-путь: диск сохраняется, слэш добавляется', () => {
    expect(buildCodeUrl('C:\\repo\\Foo.cs')).toBe('vscode://file/C:/repo/Foo.cs');
  });

  it('спецсимволы # и пробел percent-encode (иначе ломается ссылка)', () => {
    expect(buildCodeUrl('/repo/Ticket#42/My File.cs'))
      .toBe('vscode://file/repo/Ticket%2342/My%20File.cs');
  });

  it('% и ? в пути кодируются', () => {
    expect(buildCodeUrl('/a/b%c?.cs')).toBe('vscode://file/a/b%25c%3F.cs');
  });
});

describe('nodeDeps', () => {
  const g: GraphDto = {
    project: 'x',
    coverage: { marker: 0, heuristic: 0, unknown: 0, total: 0 },
    drift: [],
    nodes: [],
    edges: [
      { id: 'e1', source: 'A', target: 'B', kind: 'uses' },
      { id: 'e2', source: 'C', target: 'A', kind: 'holds' },
      { id: 'e3', source: 'A', target: 'D', kind: 'raises' },
    ],
  };

  it('разделяет исходящие и входящие', () => {
    const { outgoing, incoming } = nodeDeps(g, 'A');
    expect(outgoing.map((e) => e.id)).toEqual(['e1', 'e3']);
    expect(incoming.map((e) => e.id)).toEqual(['e2']);
  });
});
