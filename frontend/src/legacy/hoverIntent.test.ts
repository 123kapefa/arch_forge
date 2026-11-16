import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createHoverIntent, type HoverTarget } from './hoverIntent';

const A: HoverTarget = { kind: 'node', id: 'a' };
const B: HoverTarget = { kind: 'node', id: 'b' };
const E: HoverTarget = { kind: 'edge', id: 'e' };

describe('createHoverIntent', () => {
  const calls: (HoverTarget | null)[] = [];
  const mk = () => createHoverIntent({ onChange: (t) => calls.push(t), enterDelay: 150, leaveDelay: 100 });
  beforeEach(() => { vi.useFakeTimers(); calls.length = 0; });
  afterEach(() => { vi.useRealTimers(); });

  it('включается только после задержки', () => {
    const h = mk();
    h.enter(A);
    vi.advanceTimersByTime(149);
    expect(calls).toEqual([]);
    vi.advanceTimersByTime(1);
    expect(calls).toEqual([A]);
  });
  it('пролёт быстрее порога не вызывает ничего', () => {
    const h = mk();
    h.enter(A); vi.advanceTimersByTime(60); h.leave(); vi.advanceTimersByTime(60);
    h.enter(B); vi.advanceTimersByTime(60); h.leave(); vi.advanceTimersByTime(500);
    expect(calls).toEqual([]);
  });
  it('выключается с льготной задержкой; возврат внутри неё отменяет выключение', () => {
    const h = mk();
    h.enter(A); vi.advanceTimersByTime(150);
    h.leave(); vi.advanceTimersByTime(99);
    expect(calls).toEqual([A]);
    h.enter(A); vi.advanceTimersByTime(500);
    expect(calls).toEqual([A]); // не мигнуло
    h.leave(); vi.advanceTimersByTime(100);
    expect(calls).toEqual([A, null]);
  });
  it('переход на соседний элемент: подсветка держится до переключения, без промежуточного null', () => {
    const h = mk();
    h.enter(A); vi.advanceTimersByTime(150);
    h.leave(); vi.advanceTimersByTime(30);
    h.enter(B); vi.advanceTimersByTime(150);
    expect(calls).toEqual([A, B]);
  });
  it('узел → ребро тоже переключается без промежуточного выключения', () => {
    const h = mk();
    h.enter(A); vi.advanceTimersByTime(150);
    h.leave(); h.enter(E); vi.advanceTimersByTime(150);
    expect(calls).toEqual([A, E]);
  });
  it('suspend (drag/pan/zoom) сбрасывает сразу и блокирует наведение до resume', () => {
    const h = mk();
    h.enter(A); vi.advanceTimersByTime(150);
    h.suspend();
    expect(calls).toEqual([A, null]);
    h.enter(B); vi.advanceTimersByTime(500);
    expect(calls).toEqual([A, null]);
    h.resume(); h.enter(B); vi.advanceTimersByTime(150);
    expect(calls).toEqual([A, null, B]);
  });
  it('suspend во время ожидания включения отменяет его', () => {
    const h = mk();
    h.enter(A); vi.advanceTimersByTime(100); h.suspend(); vi.advanceTimersByTime(500);
    expect(calls).toEqual([]);
  });
});
