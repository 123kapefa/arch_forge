import { describe, it, expect, vi, afterEach } from 'vitest';
import { fetchWithTimeout, isAbortError } from './http';

// фейковый fetch, который «висит», пока его signal не оборвут → отражает реальный abort
function hangingFetch() {
  return (_url: string, init?: { signal?: AbortSignal }) =>
    new Promise<Response>((_res, rej) => {
      init?.signal?.addEventListener('abort', () => rej(new DOMException('aborted', 'AbortError')));
    });
}

afterEach(() => vi.unstubAllGlobals());

describe('fetchWithTimeout', () => {
  it('внешняя отмена (вытеснение) прерывает запрос AbortError-ом, а не текстом таймаута', async () => {
    vi.stubGlobal('fetch', hangingFetch());
    const external = new AbortController();
    let err: unknown;
    const p = fetchWithTimeout('/x', 10_000, external.signal).catch((e) => { err = e; });
    external.abort();
    await p;
    expect(isAbortError(err)).toBe(true); // именно AbortError → вызывающий его проглотит, не покажет ошибку
  });

  it('таймаут даёт понятную ошибку (не голый AbortError)', async () => {
    vi.stubGlobal('fetch', hangingFetch());
    await expect(fetchWithTimeout('/x', 1)).rejects.toThrow(/Превышено время ожидания/);
  });
});
