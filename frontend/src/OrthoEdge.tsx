import { useContext, useMemo } from 'react';
import { BaseEdge, useInternalNode, useStoreApi, type EdgeProps, type InternalNode } from '@xyflow/react';
import type { XY } from './layerFlow';
import { LayoutVersionContext } from './layoutVersion';
import { routeOrtho, type Rect } from './orthoRouter';

type Side = 'T' | 'B' | 'L' | 'R';

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
// Порт стороны выровнять по партнёру (сторона с ОДНИМ ребром → прямая линия вместо загиба).
function alignPort(r: Rect, side: Side, partner: XY): XY {
  if (side === 'T') return { x: clamp(partner.x, r.x + 12, r.x + r.w - 12), y: r.y };
  if (side === 'B') return { x: clamp(partner.x, r.x + 12, r.x + r.w - 12), y: r.y + r.h };
  if (side === 'L') return { x: r.x, y: clamp(partner.y, r.y + 12, r.y + r.h - 12) };
  return { x: r.x + r.w, y: clamp(partner.y, r.y + 12, r.y + r.h - 12) };
}
function outPoint(p: XY, side: Side, d = 18): XY {
  if (side === 'T') return { x: p.x, y: p.y - d };
  if (side === 'B') return { x: p.x, y: p.y + d };
  if (side === 'L') return { x: p.x - d, y: p.y };
  return { x: p.x + d, y: p.y };
}

/**
 * Ребро L0/L1/L2: ортогональный маршрут по Hanan-сетке (routeOrtho) по текущим позициям узлов — огибает
 * блоки, минимум поворотов, порты распределены, сторона с одним ребром выровнена (прямая). Так рисуются
 * ВСЕ линии (и дефолтная расстановка, и после перетаскивания). Маршрут мемоизирован: пересчёт только при
 * смене позиций своих узлов или версии раскладки (после drag-stop), поэтому наведение/выбор — без пересчёта.
 */
export function OrthoEdge({ id, source, target, style, markerEnd, sourceX, sourceY, targetX, targetY }: EdgeProps) {
  const src = useInternalNode(source);
  const tgt = useInternalNode(target);
  const store = useStoreApi();
  const version = useContext(LayoutVersionContext);
  const sPos = src?.internals.positionAbsolute;
  const tPos = tgt?.internals.positionAbsolute;

  const path = useMemo(() => {
    const { edges, nodeLookup } = store.getState();
    const sN = nodeLookup.get(source), tN = nodeLookup.get(target);
    if (!sN || !tN) return null;
    const sR = rectOf(sN), tR = rectOf(tN);
    const sSide = sideFacing(sR, tR), tSide = sideFacing(tR, sR);
    const ss = slot(source, id, sSide, edges, nodeLookup), ts = slot(target, id, tSide, edges, nodeLookup);
    let sPort = portPoint(sR, sSide, ss.i, ss.n);
    let tPort = portPoint(tR, tSide, ts.i, ts.n);
    if (ss.n === 1 && ts.n > 1) sPort = alignPort(sR, sSide, tPort);      // сторона с одним ребром → прямая
    else if (ts.n === 1 && ss.n > 1) tPort = alignPort(tR, tSide, sPort);
    const obstacles: Rect[] = [];
    for (const n of nodeLookup.values()) if (n.id !== source && n.id !== target) obstacles.push(rectOf(n));
    return roundedPath(routeOrtho(sPort, outPoint(sPort, sSide), tPort, outPoint(tPort, tSide), obstacles));
    // позиции узлов и версия раскладки — достаточный ключ; edges/nodeLookup читаем свежими из store
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id, source, target, sPos?.x, sPos?.y, tPos?.x, tPos?.y, version, store]);

  if (!path) return <BaseEdge id={id} path={`M ${sourceX},${sourceY} L ${targetX},${targetY}`} style={style} markerEnd={markerEnd} />;
  return <BaseEdge id={id} path={path} style={style} markerEnd={markerEnd} />;
}
