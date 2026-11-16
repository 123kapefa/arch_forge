import { describe, it, expect, vi, afterEach } from 'vitest';
import { fetchSystem } from './system';

const okGraph = {
  project: 'sys', nodes: [], edges: [],
  coverage: { marker: 0, heuristic: 0, unknown: 0, total: 0 }, drift: [],
};

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

function mockFetch(impl: (url: string) => Response): string[] {
  const urls: string[] = [];
  vi.stubGlobal('fetch', vi.fn((url: string) => {
    urls.push(url);
    return Promise.resolve(impl(url));
  }));
  return urls;
}

afterEach(() => vi.unstubAllGlobals());

describe('fetchSystem query', () => {
  it('без флагов — только path (drift/layers отсутствуют)', async () => {
    const urls = mockFetch(() => jsonResponse(okGraph));
    await fetchSystem({ path: '/ws' });
    expect(urls[0]).toContain('path=%2Fws');
    expect(urls[0]).not.toContain('drift');
    expect(urls[0]).not.toContain('layers');
  });

  it('drift/layers добавляются только при true', async () => {
    const urls = mockFetch(() => jsonResponse(okGraph));
    await fetchSystem({ path: '/ws', drift: true, layers: true });
    expect(urls[0]).toContain('drift=true');
    expect(urls[0]).toContain('layers=true');
  });
});

describe('fetchSystem ошибки/успех', () => {
  it('извлекает {error} из JSON-тела', async () => {
    mockFetch(() => jsonResponse({ error: 'путь не найден' }, 400));
    await expect(fetchSystem({ path: '/x' })).rejects.toThrow('путь не найден');
  });

  it('не-JSON тело → текст как есть', async () => {
    mockFetch(() => new Response('boom', { status: 500 }));
    await expect(fetchSystem({ path: '/x' })).rejects.toThrow('boom');
  });

  it('успех → граф', async () => {
    mockFetch(() => jsonResponse(okGraph));
    const g = await fetchSystem({ path: '/ws' });
    expect(g.project).toBe('sys');
  });
});
