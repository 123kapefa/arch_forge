import { describe, expect, it } from 'vitest';
import { fallbackZ, routeOrtho, type Rect, type XY } from './orthoRouter';

const allOrtho = (pts: XY[]) => pts.every((p, i) => i === 0 || Math.abs(p.x - pts[i - 1].x) < 0.5 || Math.abs(p.y - pts[i - 1].y) < 0.5);

describe('routeOrtho', () => {
  const sP = { x: 0, y: 0 }, sS = { x: 18, y: 0 }, tP = { x: 2000, y: 1500 }, tS = { x: 1982, y: 1500 };
  it('обычный случай: ортогональный путь, огибает блок', () => {
    const wall: Rect = { x: 500, y: -200, w: 20, h: 400 };
    const pts = routeOrtho({ x: 0, y: 0 }, { x: 18, y: 0 }, { x: 1000, y: 0 }, { x: 982, y: 0 }, [wall]);
    expect(allOrtho(pts)).toBe(true);
    expect(pts.some((p) => p.y !== 0)).toBe(true);
  });
  it('искусственно большая сетка (много препятствий) → запасной путь без диагоналей', () => {
    const obs: Rect[] = [];
    for (let i = 0; i < 90; i++) obs.push({ x: 40 + i * 21, y: 30 + i * 16, w: 12, h: 10 });
    const pts = routeOrtho(sP, sS, tP, tS, obs);
    expect(allOrtho(pts)).toBe(true);
    expect(pts[0]).toEqual(sP);
    expect(pts[pts.length - 1]).toEqual(tP);
  });
  it('непроходимое препятствие (старт замурован) → запасной путь без диагоналей', () => {
    const box: Rect[] = [{ x: -400, y: -400, w: 800, h: 800 }]; // стаб старта внутри блока: пути нет
    const pts = routeOrtho({ x: 0, y: 0 }, { x: 18, y: 0 }, { x: 600, y: 300 }, { x: 582, y: 300 }, box);
    expect(allOrtho(pts)).toBe(true);
  });
  it('fallbackZ: Z-образный, все сегменты горизонтальные/вертикальные', () => {
    const pts = fallbackZ(sP, sS, tP, tS);
    expect(allOrtho(pts)).toBe(true);
    expect(pts.length).toBeGreaterThanOrEqual(4);
  });
});
