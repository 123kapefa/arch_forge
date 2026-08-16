import { useContext } from 'react';
import { BaseEdge, useInternalNode, useStoreApi, type EdgeProps, type InternalNode } from '@xyflow/react';
import type { XY } from './layerFlow';
import { LayoutDirtyContext } from './layoutDirty';

type Rect = { x: number; y: number; w: number; h: number };

// Ортогонализация маршрута ELK: косой сегмент a→b → H→V→H (для статичного пути по точкам ELK).
function orthoPoints(pts: XY[]): XY[] {
  const out: XY[] = [pts[0]];
  for (let i = 1; i < pts.length; i++) {
    const a = pts[i - 1], b = pts[i];
    if (Math.abs(a.x - b.x) < 0.5 || Math.abs(a.y - b.y) < 0.5) out.push(b);
    else { const midX = (a.x + b.x) / 2; out.push({ x: midX, y: a.y }, { x: midX, y: b.y }, b); }
  }
  return out;
}

// Путь-ломаная со скруглением углов (радиус r). Точки уже ортогональны и в координатах потока.
function roundedPath(pts: XY[], r = 8): string {
  const p = pts.filter((pt, i) => i === 0 || Math.abs(pt.x - pts[i - 1].x) > 0.5 || Math.abs(pt.y - pts[i - 1].y) > 0.5);
  if (p.length < 2) return '';
  let d = `M ${p[0].x},${p[0].y}`;
  for (let i = 1; i < p.length - 1; i++) {
    const a = p[i - 1], b = p[i], c = p[i + 1];
    const inLen = Math.hypot(b.x - a.x, b.y - a.y) || 1;
    const outLen = Math.hypot(c.x - b.x, c.y - b.y) || 1;
    const ri = Math.min(r, inLen / 2, outLen / 2);
    const p1 = { x: b.x + ((a.x - b.x) / inLen) * ri, y: b.y + ((a.y - b.y) / inLen) * ri };
    const p2 = { x: b.x + ((c.x - b.x) / outLen) * ri, y: b.y + ((c.y - b.y) / outLen) * ri };
    d += ` L ${p1.x},${p1.y} Q ${b.x},${b.y} ${p2.x},${p2.y}`;
  }
  const last = p[p.length - 1];
  d += ` L ${last.x},${last.y}`;
  return d;
}

function rectOf(n: InternalNode): Rect {
  const p = n.internals.positionAbsolute;
  return { x: p.x, y: p.y, w: n.measured?.width ?? 200, h: n.measured?.height ?? 100 };
}
const moved = (a: XY, b: unknown): boolean => {
  const p = b as XY | undefined;
  return !p || Math.hypot(a.x - p.x, a.y - p.y) > 1.5;
};

function segHitsRect(a: XY, b: XY, r: Rect, pad = 6): boolean {
  const minx = Math.min(a.x, b.x) - pad, maxx = Math.max(a.x, b.x) + pad;
  const miny = Math.min(a.y, b.y) - pad, maxy = Math.max(a.y, b.y) + pad;
  return maxx > r.x && minx < r.x + r.w && maxy > r.y && miny < r.y + r.h;
}
function clearPath(pts: XY[], obstacles: Rect[]): boolean {
  for (let i = 1; i < pts.length; i++)
    for (const r of obstacles)
      if (segHitsRect(pts[i - 1], pts[i], r)) return false;
  return true;
}

/**
 * Обходной ортогональный маршрут (для перетащенного узла): точки крепления — по центрам сторон
 * (симметрично для симметрично стоящих блоков). Пробует прямой S-путь; задевает блок — обходит
 * канал сбоку/сверху-снизу за габариты всех препятствий. Возвращает ортогональную ломаную.
 */
function orthoAvoid(src: Rect, tgt: Rect, obstacles: Rect[]): XY[] {
  const OUT = 28;
  const sCx = src.x + src.w / 2, sCy = src.y + src.h / 2;
  const tCx = tgt.x + tgt.w / 2, tCy = tgt.y + tgt.h / 2;
  const vertical = Math.abs(tCy - sCy) >= Math.abs(tCx - sCx);
  const cands: XY[][] = [];

  if (vertical) {
    const down = tCy >= sCy;
    const s = { x: sCx, y: down ? src.y + src.h : src.y };
    const t = { x: tCx, y: down ? tgt.y : tgt.y + tgt.h };
    const midY = (s.y + t.y) / 2;
    cands.push([s, { x: s.x, y: midY }, { x: t.x, y: midY }, t]); // прямой S
    const rx = Math.max(src.x + src.w, tgt.x + tgt.w, ...obstacles.map((o) => o.x + o.w)) + OUT;
    const lx = Math.min(src.x, tgt.x, ...obstacles.map((o) => o.x)) - OUT;
    cands.push([{ x: src.x + src.w, y: sCy }, { x: rx, y: sCy }, { x: rx, y: tCy }, { x: tgt.x + tgt.w, y: tCy }]); // обход справа
    cands.push([{ x: src.x, y: sCy }, { x: lx, y: sCy }, { x: lx, y: tCy }, { x: tgt.x, y: tCy }]); // обход слева
  } else {
    const right = tCx >= sCx;
    const s = { x: right ? src.x + src.w : src.x, y: sCy };
    const t = { x: right ? tgt.x : tgt.x + tgt.w, y: tCy };
    const midX = (s.x + t.x) / 2;
    cands.push([s, { x: midX, y: s.y }, { x: midX, y: t.y }, t]); // прямой
    const by = Math.max(src.y + src.h, tgt.y + tgt.h, ...obstacles.map((o) => o.y + o.h)) + OUT;
    const ty = Math.min(src.y, tgt.y, ...obstacles.map((o) => o.y)) - OUT;
    cands.push([{ x: sCx, y: src.y + src.h }, { x: sCx, y: by }, { x: tCx, y: by }, { x: tCx, y: tgt.y + tgt.h }]); // обход снизу
    cands.push([{ x: sCx, y: src.y }, { x: sCx, y: ty }, { x: tCx, y: ty }, { x: tCx, y: tgt.y }]); // обход сверху
  }
  return cands.find((c) => clearPath(c, obstacles)) ?? cands[0];
}

/**
 * Ребро L1/L2: узлы на местах раскладки — путь по маршруту ELK. Узел перетащили — обходной
 * ортогональный маршрут по текущим позициям (обходит блоки, симметричен), линия идёт за блоком.
 */
export function OrthoEdge({ id, source, target, data, style, markerEnd, sourceX, sourceY, targetX, targetY }: EdgeProps) {
  const src = useInternalNode(source);
  const tgt = useInternalNode(target);
  const store = useStoreApi();
  const layoutDirty = useContext(LayoutDirtyContext);
  const pts = (data as { points?: XY[] } | undefined)?.points;

  // Раскладку трогали (любой узел двигали) → маршруты ELK устарели, все рёбра идут на живой обход.
  const dragged = layoutDirty || !src || !tgt
    || moved(src.internals.positionAbsolute, src.data?.elkPos)
    || moved(tgt.internals.positionAbsolute, tgt.data?.elkPos);

  if (pts && pts.length >= 2 && !dragged) {
    return <BaseEdge id={id} path={roundedPath(orthoPoints(pts))} style={style} markerEnd={markerEnd} />;
  }
  if (src && tgt) {
    const obstacles: Rect[] = [];
    for (const n of store.getState().nodeLookup.values())
      if (n.id !== source && n.id !== target) obstacles.push(rectOf(n));
    return <BaseEdge id={id} path={roundedPath(orthoAvoid(rectOf(src), rectOf(tgt), obstacles))} style={style} markerEnd={markerEnd} />;
  }
  return <BaseEdge id={id} path={`M ${sourceX},${sourceY} L ${targetX},${targetY}`} style={style} markerEnd={markerEnd} />;
}
