import { useContext } from 'react';
import { BaseEdge, useInternalNode, useStoreApi, type EdgeProps, type InternalNode } from '@xyflow/react';
import type { XY } from './layerFlow';
import { LayoutDirtyContext } from './layoutDirty';

type Rect = { x: number; y: number; w: number; h: number };
type Side = 'T' | 'B' | 'L' | 'R';

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
const nodeMoved = (a: XY, b: unknown): boolean => {
  const p = b as XY | undefined;
  return !p || Math.hypot(a.x - p.x, a.y - p.y) > 1.5;
};
const clamp = (v: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, v));

// Сторона узла me, обращённая к other (по доминирующему направлению между центрами).
function sideFacing(me: Rect, other: Rect): Side {
  const dx = other.x + other.w / 2 - (me.x + me.w / 2);
  const dy = other.y + other.h / 2 - (me.y + me.h / 2);
  if (Math.abs(dy) >= Math.abs(dx)) return dy >= 0 ? 'B' : 'T';
  return dx >= 0 ? 'R' : 'L';
}
// Слот ребра среди рёбер, выходящих из nodeId той же стороной (для распределения портов).
function slot(nodeId: string, edgeId: string, side: Side, edges: { id: string; source: string; target: string }[],
  lookup: Map<string, InternalNode>): { i: number; n: number } {
  const me = lookup.get(nodeId); if (!me) return { i: 0, n: 1 };
  const meR = rectOf(me);
  const peers: { id: string; key: number }[] = [];
  for (const e of edges) {
    const other = e.source === nodeId ? e.target : e.target === nodeId ? e.source : null;
    if (!other) continue;
    const on = lookup.get(other); if (!on) continue;
    const oR = rectOf(on);
    if (sideFacing(meR, oR) !== side) continue;
    peers.push({ id: e.id, key: side === 'T' || side === 'B' ? oR.x + oR.w / 2 : oR.y + oR.h / 2 });
  }
  peers.sort((a, b) => a.key - b.key || a.id.localeCompare(b.id));
  const i = peers.findIndex((p) => p.id === edgeId);
  return { i: i < 0 ? 0 : i, n: Math.max(1, peers.length) };
}
// Точка-порт на стороне с разносом по слоту (чтобы рёбра не сходились в одну точку).
function portPoint(r: Rect, side: Side, i: number, n: number): XY {
  const off = (i - (n - 1) / 2) * 16;
  if (side === 'T') return { x: clamp(r.x + r.w / 2 + off, r.x + 12, r.x + r.w - 12), y: r.y };
  if (side === 'B') return { x: clamp(r.x + r.w / 2 + off, r.x + 12, r.x + r.w - 12), y: r.y + r.h };
  if (side === 'L') return { x: r.x, y: clamp(r.y + r.h / 2 + off, r.y + 12, r.y + r.h - 12) };
  return { x: r.x + r.w, y: clamp(r.y + r.h / 2 + off, r.y + 12, r.y + r.h - 12) };
}
function outPoint(p: XY, side: Side, d = 18): XY {
  if (side === 'T') return { x: p.x, y: p.y - d };
  if (side === 'B') return { x: p.x, y: p.y + d };
  if (side === 'L') return { x: p.x - d, y: p.y };
  return { x: p.x + d, y: p.y };
}

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
 * Обходной ортогональный маршрут (перетащенный узел): выход из точки-порта (разнесены по слоту, не
 * сходятся в одну точку), «стаб» наружу, затем прямой S-путь; задевает блок — обход канала сбоку/сверху
 * за габариты препятствий. Возвращает ортогональную ломаную.
 */
function orthoAvoid(s: XY, sSide: Side, t: XY, tSide: Side, src: Rect, tgt: Rect, obstacles: Rect[]): XY[] {
  const OUT = 28;
  const s1 = outPoint(s, sSide), t1 = outPoint(t, tSide);
  const vertical = sSide === 'T' || sSide === 'B';
  const cands: XY[][] = [];
  if (vertical) {
    const midY = (s1.y + t1.y) / 2;
    cands.push([s, s1, { x: s1.x, y: midY }, { x: t1.x, y: midY }, t1, t]); // прямой S
    const rx = Math.max(src.x + src.w, tgt.x + tgt.w, ...obstacles.map((o) => o.x + o.w)) + OUT;
    const lx = Math.min(src.x, tgt.x, ...obstacles.map((o) => o.x)) - OUT;
    for (const cx of [rx, lx]) cands.push([s, s1, { x: cx, y: s1.y }, { x: cx, y: t1.y }, t1, t]);
  } else {
    const midX = (s1.x + t1.x) / 2;
    cands.push([s, s1, { x: midX, y: s1.y }, { x: midX, y: t1.y }, t1, t]);
    const by = Math.max(src.y + src.h, tgt.y + tgt.h, ...obstacles.map((o) => o.y + o.h)) + OUT;
    const ty = Math.min(src.y, tgt.y, ...obstacles.map((o) => o.y)) - OUT;
    for (const cy of [by, ty]) cands.push([s, s1, { x: s1.x, y: cy }, { x: t1.x, y: cy }, t1, t]);
  }
  return cands.find((c) => clearPath(c, obstacles)) ?? cands[0];
}

/**
 * Ребро L1/L2: узлы на местах раскладки — маршрут ELK. Узел двигали — обходной ортогональный маршрут
 * по текущим позициям с распределением портов (обходит блоки, симметричен, не сходится в одну точку).
 */
export function OrthoEdge({ id, source, target, data, style, markerEnd, sourceX, sourceY, targetX, targetY }: EdgeProps) {
  const src = useInternalNode(source);
  const tgt = useInternalNode(target);
  const store = useStoreApi();
  const layoutDirty = useContext(LayoutDirtyContext);
  const pts = (data as { points?: XY[] } | undefined)?.points;

  const dragged = layoutDirty || !src || !tgt
    || nodeMoved(src.internals.positionAbsolute, src.data?.elkPos)
    || nodeMoved(tgt.internals.positionAbsolute, tgt.data?.elkPos);

  if (pts && pts.length >= 2 && !dragged) {
    return <BaseEdge id={id} path={roundedPath(orthoPoints(pts))} style={style} markerEnd={markerEnd} />;
  }
  if (src && tgt) {
    const { edges, nodeLookup } = store.getState();
    const sR = rectOf(src), tR = rectOf(tgt);
    const sSide = sideFacing(sR, tR), tSide = sideFacing(tR, sR);
    const ss = slot(source, id, sSide, edges, nodeLookup), ts = slot(target, id, tSide, edges, nodeLookup);
    const sPort = portPoint(sR, sSide, ss.i, ss.n), tPort = portPoint(tR, tSide, ts.i, ts.n);
    const obstacles: Rect[] = [];
    for (const n of nodeLookup.values()) if (n.id !== source && n.id !== target) obstacles.push(rectOf(n));
    return <BaseEdge id={id} path={roundedPath(orthoAvoid(sPort, sSide, tPort, tSide, sR, tR, obstacles))} style={style} markerEnd={markerEnd} />;
  }
  return <BaseEdge id={id} path={`M ${sourceX},${sourceY} L ${targetX},${targetY}`} style={style} markerEnd={markerEnd} />;
}
