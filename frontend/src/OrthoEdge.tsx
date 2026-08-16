import { BaseEdge, useInternalNode, useStoreApi, type EdgeProps, type InternalNode } from '@xyflow/react';
import { routeEdge, type Rect, type XY, type EdgeLite } from './orthoRouter';

// Путь-ломаная со скруглением углов (радиус r). Точки ортогональны и в координатах потока.
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

/**
 * Ребро L0/L1/L2. В покое рисуется по ПРЕДРАССЧИТАННОМУ маршруту (data.route) — он получен централизованно
 * с пучковой развязкой (параллельные линии разведены). Пока свой узел перетаскивают — «живой» маршрут
 * routeEdge по текущим позициям (следует за блоком, без развязки; на отпускании пересчитается централизованно).
 */
export function OrthoEdge({ id, source, target, data, style, markerEnd, sourceX, sourceY, targetX, targetY }: EdgeProps) {
  const src = useInternalNode(source);
  const tgt = useInternalNode(target);
  const store = useStoreApi();
  const route = (data as { route?: XY[] } | undefined)?.route;
  const dragging = !!(src?.dragging || tgt?.dragging);

  if (route && route.length >= 2 && !dragging) {
    return <BaseEdge id={id} path={roundedPath(route)} style={style} markerEnd={markerEnd} />;
  }
  if (src && tgt) {
    const { nodeLookup, edges } = store.getState();
    const rects = new Map<string, Rect>();
    for (const n of nodeLookup.values()) rects.set(n.id, rectOf(n));
    const pts = routeEdge(id, source, target, rects, edges as EdgeLite[]);
    if (pts.length >= 2) return <BaseEdge id={id} path={roundedPath(pts)} style={style} markerEnd={markerEnd} />;
  }
  return <BaseEdge id={id} path={`M ${sourceX},${sourceY} L ${targetX},${targetY}`} style={style} markerEnd={markerEnd} />;
}
