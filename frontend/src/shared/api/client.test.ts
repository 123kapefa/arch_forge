import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, getJson, isAbortError } from './client';

afterEach(() => vi.unstubAllGlobals());

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });

describe('getJson', () => {
  it('ходит на /api, собирает query и пропускает пустые значения', async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({ ok: true }));
    vi.stubGlobal('fetch', fetchMock);
    const res = await getJson<{ ok: boolean }>('/system', { query: { path: 'a b', drift: true, layers: false, env: undefined } });
    expect(res).toEqual({ ok: true });
    expect(fetchMock.mock.calls[0][0]).toBe('/api/system?path=a+b&drift=true');
  });

  it('не-2xx → ApiError с сообщением сервера', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(json({ error: 'нет такой папки' }, 404)));
    const err = await getJson('/system').catch((e) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect(err).toMatchObject({ status: 404, message: 'нет такой папки' });
  });

  it('HTML/стектрейс в теле ошибки не попадает в message', async () => {
    const html = '<html><script>alert(1)</script>System.NullReferenceException at Foo.Bar() in /src/Foo.cs:line 12</html>';
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(html, { status: 500, headers: { 'Content-Type': 'text/html' } })));
    const err = (await getJson('/system').catch((e) => e)) as Error;
    expect(err).toMatchObject({ status: 500, message: 'Ошибка запроса (500)' });
    expect(err.message).not.toMatch(/script|Exception|Foo/);
  });

  it('поле error из JSON усекается до 300 символов', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(json({ error: 'x'.repeat(1000) }, 400)));
    const err = (await getJson('/system').catch((e) => e)) as Error;
    expect(err.message).toHaveLength(300);
  });

  it('внешняя отмена пробрасывается как AbortError (не ошибка для показа)', async () => {
    vi.stubGlobal('fetch', vi.fn((_u: string, init: RequestInit) => new Promise((_r, rej) => {
      init.signal!.addEventListener('abort', () => rej(new DOMException('aborted', 'AbortError')));
    })));
    const ctrl = new AbortController();
    const p = getJson('/system', { signal: ctrl.signal });
    ctrl.abort();
    expect(isAbortError(await p.catch((e) => e))).toBe(true);
  });

  it('таймаут → ApiError 408', async () => {
    vi.stubGlobal('fetch', vi.fn((_u: string, init: RequestInit) => new Promise((_r, rej) => {
      init.signal!.addEventListener('abort', () => rej(new DOMException('aborted', 'AbortError')));
    })));
    const err = await getJson('/system', { timeoutMs: 5 }).catch((e) => e);
    expect(err).toMatchObject({ name: 'ApiError', status: 408 });
  });

  it('обрыв соединения (TypeError от fetch) → ApiError 0 «нет связи»', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')));
    const err = await getJson('/system').catch((e) => e);
    expect(err).toMatchObject({ name: 'ApiError', status: 0 });
    expect((err as Error).message).toMatch(/Нет связи/);
  });
});
