import { describe, expect, it } from 'vitest';
import { bboxOfNodes, hasNonFinitePositions, sanitizePositions, viewportShowsContent } from './viewportGuard';

const n = (x: number, y: number) => ({ position: { x, y }, width: 100, height: 50 });

describe('viewportGuard', () => {
  it('bbox узлов; нечисловые координаты пропускаются', () => {
    expect(bboxOfNodes([n(0, 0), n(200, 300)])).toEqual({ x: 0, y: 0, w: 300, h: 350 });
    expect(bboxOfNodes([n(NaN, 0), n(10, 10)])).toEqual({ x: 10, y: 10, w: 100, h: 50 });
    expect(bboxOfNodes([n(NaN, NaN)])).toBeNull();
    expect(bboxOfNodes([])).toBeNull();
  });
  it('вьюпорт показывает содержимое, если bbox пересекает видимую область', () => {
    const box = { x: 0, y: 0, w: 300, h: 300 };
    expect(viewportShowsContent({ x: 0, y: 0, zoom: 1 }, box, { w: 800, h: 600 })).toBe(true);
    expect(viewportShowsContent({ x: -250, y: -250, zoom: 1 }, box, { w: 800, h: 600 })).toBe(true);  // краем
    expect(viewportShowsContent({ x: -5000, y: 0, zoom: 1 }, box, { w: 800, h: 600 })).toBe(false);   // всё левее экрана
    expect(viewportShowsContent({ x: 0, y: 9000, zoom: 1 }, box, { w: 800, h: 600 })).toBe(false);    // всё выше экрана
    expect(viewportShowsContent({ x: 0, y: 0, zoom: 0.01 }, { x: 90000, y: 0, w: 10, h: 10 }, { w: 800, h: 600 })).toBe(false);
  });
  it('мусорный вьюпорт (NaN/zoom<=0) не показывает содержимое', () => {
    const box = { x: 0, y: 0, w: 10, h: 10 };
    expect(viewportShowsContent({ x: NaN, y: 0, zoom: 1 }, box, { w: 800, h: 600 })).toBe(false);
    expect(viewportShowsContent({ x: 0, y: 0, zoom: 0 }, box, { w: 800, h: 600 })).toBe(false);
  });
  it('раскладка с NaN/Infinity санируется, нормальная возвращается как есть', () => {
    const ok = [n(1, 2)];
    expect(sanitizePositions(ok)).toBe(ok);
    const bad = [n(NaN, 5), n(Infinity, -Infinity)];
    expect(hasNonFinitePositions(bad)).toBe(true);
    const fixed = sanitizePositions(bad);
    expect(hasNonFinitePositions(fixed)).toBe(false);
    expect(fixed[0].position).toEqual({ x: 0, y: 5 });
  });
});
