import { describe, it, expect } from 'vitest';
import { confidenceLevel, matchLabel, mergeCalls, sortCalls, summarizeCalls, relativeFile, shortFile, type HttpCall } from './edgeCalls';

const c = (o: Partial<HttpCall> = {}): HttpCall => ({ method: 'GET', route: '/api/orders', ...o });

describe('уровень доверия', () => {
  it.each([[0.9, 'high'], [0.8, 'high'], [0.6, 'medium'], [0.5, 'medium'], [0.49, 'low'], [0, 'low'], [undefined, 'medium']])('%s', (v, lvl) => {
    expect(confidenceLevel(v as number | undefined)).toBe(lvl);
  });
});

describe('сопоставление с endpoint-ом', () => {
  it('честно различает найден / не найден / не проверялось', () => {
    expect(matchLabel(true).tone).toBe('ok');
    expect(matchLabel(false)).toEqual({ text: 'endpoint не найден', tone: 'warn' });
    expect(matchLabel(undefined)).toEqual({ text: 'не проверялось', tone: 'idle' });
  });
});

describe('склейка вызовов', () => {
  it('нет данных — нет списка (не путать с пустым)', () => {
    expect(mergeCalls(undefined, undefined)).toBeUndefined();
    expect(mergeCalls([], undefined)).toEqual([]);
  });
  it('одинаковый вызов (метод, маршрут, место) не дублируется', () => {
    const a = [c({ file: 'A.cs', line: 3 })];
    const b = [c({ file: 'A.cs', line: 3 }), c({ file: 'A.cs', line: 9 })];
    expect(mergeCalls(a, b)).toHaveLength(2);
  });
  it('один маршрут из разных мест — разные вызовы', () => {
    expect(mergeCalls([c({ file: 'A.cs', line: 1 })], [c({ file: 'B.cs', line: 1 })])).toHaveLength(2);
  });
});

describe('порядок и сводка', () => {
  it('сортировка по маршруту, методу, месту', () => {
    const s = sortCalls([c({ route: '/b' }), c({ route: '/a', method: 'POST' }), c({ route: '/a', method: 'GET', line: 2 }), c({ route: '/a', method: 'GET', line: 1 })]);
    expect(s.map((x) => `${x.route} ${x.method} ${x.line ?? ''}`)).toEqual(['/a GET 1', '/a GET 2', '/a POST ', '/b GET ']);
  });
  it('исходный массив не меняется', () => {
    const src = [c({ route: '/b' }), c({ route: '/a' })];
    sortCalls(src);
    expect(src[0].route).toBe('/b');
  });
  it('сводка: не запрашивались / пусто / число и несопоставленные', () => {
    expect(summarizeCalls(undefined, false)).toBe('вызовы не запрашивались');
    expect(summarizeCalls(undefined, true)).toBe('конкретных вызовов в коде не найдено');
    expect(summarizeCalls(undefined)).toBe('конкретных вызовов в коде не найдено');
    expect(summarizeCalls([])).toBe('конкретных вызовов в коде не найдено');
    expect(summarizeCalls([c()])).toBe('1 вызов');
    expect(summarizeCalls([c(), c({ route: '/x' }), c({ route: '/y', matched: false })])).toBe('3 вызова · 1 без найденного endpoint');
    expect(summarizeCalls(Array.from({ length: 5 }, (_, i) => c({ route: `/${i}` })))).toBe('5 вызовов');
    expect(summarizeCalls(Array.from({ length: 11 }, (_, i) => c({ route: `/${i}` })))).toBe('11 вызовов');
  });
});

describe('путь файла', () => {
  it('хвост пути для узкой панели', () => {
    expect(shortFile('/p/orders/src/Orders.Api/Program.cs')).toBe('Orders.Api/Program.cs');
    expect(shortFile('/p/orders/src/Orders.Api/Program.cs', 1)).toBe('Program.cs');
    expect(shortFile('Program.cs')).toBe('Program.cs');
    expect(shortFile('C:\\p\\src\\A.cs')).toBe('src/A.cs');
  });
  it('внутри корня — относительный, иначе как есть', () => {
    expect(relativeFile('/p/sys/orders/A.cs', '/p/sys')).toBe('orders/A.cs');
    expect(relativeFile('/p/sys/orders/A.cs', '/p/sys/')).toBe('orders/A.cs');
    expect(relativeFile('/other/A.cs', '/p/sys')).toBe('/other/A.cs');
    expect(relativeFile(undefined, '/p')).toBeUndefined();
  });
});
