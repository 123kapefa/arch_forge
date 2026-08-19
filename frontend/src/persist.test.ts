import { describe, it, expect, beforeEach } from 'vitest';
import type { SystemGraph } from './system';
import {
  readViewport, writeViewport, readBool, readNum, readGraphCache, writeGraphCache, gcacheKey,
} from './persist';

// Мини-реализация Web Storage для node-окружения (jsdom не подключён).
class MemStorage {
  private m = new Map<string, string>();
  get length() { return this.m.size; }
  key(i: number) { return [...this.m.keys()][i] ?? null; }
  getItem(k: string) { return this.m.has(k) ? this.m.get(k)! : null; }
  setItem(k: string, v: string) { this.m.set(k, String(v)); }
  removeItem(k: string) { this.m.delete(k); }
  clear() { this.m.clear(); }
}

beforeEach(() => {
  (globalThis as unknown as { sessionStorage: MemStorage }).sessionStorage = new MemStorage();
  (globalThis as unknown as { localStorage: MemStorage }).localStorage = new MemStorage();
});

const graph = (project = 'demo'): SystemGraph => ({
  project, nodes: [], edges: [], coverage: { marker: 0, heuristic: 0, unknown: 0, total: 0 }, drift: [],
});

describe('readNum', () => {
  it('возвращает сохранённое число не меньше min', () => {
    sessionStorage.setItem('k', '300');
    expect(readNum('k', 210, 170)).toBe(300);
  });
  it('число меньше min → дефолт', () => {
    sessionStorage.setItem('k', '100');
    expect(readNum('k', 210, 170)).toBe(210);
  });
  it('нет ключа → дефолт', () => {
    expect(readNum('missing', 210, 170)).toBe(210);
  });
  it('Infinity/NaN отсекаются → дефолт (не проходят как «>= min»)', () => {
    sessionStorage.setItem('inf', 'Infinity');
    sessionStorage.setItem('nan', 'oops');
    expect(readNum('inf', 210, 170)).toBe(210);
    expect(readNum('nan', 210, 170)).toBe(210);
  });
});

describe('readBool', () => {
  it("'1' → true, '0' → false", () => {
    sessionStorage.setItem('t', '1');
    sessionStorage.setItem('f', '0');
    expect(readBool('t', false)).toBe(true);
    expect(readBool('f', true)).toBe(false);
  });
  it('нет ключа → дефолт', () => {
    expect(readBool('missing', true)).toBe(true);
    expect(readBool('missing', false)).toBe(false);
  });
});

describe('readViewport', () => {
  it('round-trip валидного вьюпорта', () => {
    writeViewport('L', { x: 1.5, y: -2, zoom: 0.3 });
    expect(readViewport('L')).toEqual({ x: 1.5, y: -2, zoom: 0.3 });
  });
  it('нет ключа → null', () => {
    expect(readViewport('none')).toBeNull();
  });
  it('нечисловые/битые значения → null (не уводим камеру в NaN)', () => {
    localStorage.setItem('bad:vp', JSON.stringify({ x: 'a', y: 0, zoom: 1 }));
    localStorage.setItem('inf:vp', JSON.stringify({ x: 0, y: 0, zoom: Infinity }));
    localStorage.setItem('broken:vp', '{ not json');
    expect(readViewport('bad')).toBeNull();
    expect(readViewport('inf')).toBeNull(); // Infinity сериализуется как null → Number.isFinite=false
    expect(readViewport('broken')).toBeNull();
  });
});

describe('readGraphCache / writeGraphCache', () => {
  it('round-trip валидного графа под версионным ключом с флагами', () => {
    writeGraphCache('/p', true, false, graph('demo'));
    expect(sessionStorage.getItem(gcacheKey('/p', true, false))).not.toBeNull();
    expect(readGraphCache('/p', true, false)?.project).toBe('demo');
  });
  it('битый JSON → null', () => {
    sessionStorage.setItem(gcacheKey('/p', true, true), '{ broken');
    expect(readGraphCache('/p', true, true)).toBeNull();
  });
  it('неверная форма (нет nodes/edges) → null, а не в рендер', () => {
    sessionStorage.setItem(gcacheKey('/p', true, true), JSON.stringify({ project: 'x' }));
    expect(readGraphCache('/p', true, true)).toBeNull();
  });
  it('разные флаги дают разные ключи (нет коллизии кэша)', () => {
    expect(gcacheKey('/p', true, false)).not.toBe(gcacheKey('/p', false, true));
  });
  it('запись вытесняет прежние комбинации флагов ТОГО ЖЕ пути, но не другие проекты', () => {
    writeGraphCache('/p', true, true, graph());   // /p под флагами 11
    writeGraphCache('/other', true, true, graph()); // другой проект
    writeGraphCache('/p', false, false, graph());  // тот же путь, флаги 00 → должен вытеснить 11
    expect(sessionStorage.getItem(gcacheKey('/p', true, true))).toBeNull();   // старая комбинация ушла
    expect(sessionStorage.getItem(gcacheKey('/p', false, false))).not.toBeNull(); // свежая осталась
    expect(sessionStorage.getItem(gcacheKey('/other', true, true))).not.toBeNull(); // чужой проект не тронут
  });
  it('путь-суффикс не вызывает ложного вытеснения (двоеточие-разделитель)', () => {
    writeGraphCache('/foo/bar', true, true, graph());
    writeGraphCache('/x/foo/bar', true, true, graph()); // заканчивается на «/foo/bar», но это другой путь
    expect(sessionStorage.getItem(gcacheKey('/foo/bar', true, true))).not.toBeNull();
    expect(sessionStorage.getItem(gcacheKey('/x/foo/bar', true, true))).not.toBeNull();
  });
});
