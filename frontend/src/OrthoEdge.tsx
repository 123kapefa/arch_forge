import { BaseEdge, type EdgeProps } from '@xyflow/react';
import type { XY } from './layerFlow';

// Путь-ломаная по точкам маршрута со скруглением углов (радиус r). Точки уже в координатах потока.
function roundedPath(pts: XY[], r = 7): string {
  if (pts.length < 2) return '';
  if (pts.length === 2) return `M ${pts[0].x},${pts[0].y} L ${pts[1].x},${pts[1].y}`;
  let d = `M ${pts[0].x},${pts[0].y}`;
  for (let i = 1; i < pts.length - 1; i++) {
    const a = pts[i - 1], b = pts[i], c = pts[i + 1];
    const inLen = Math.hypot(b.x - a.x, b.y - a.y) || 1;
    const outLen = Math.hypot(c.x - b.x, c.y - b.y) || 1;
    const ri = Math.min(r, inLen / 2, outLen / 2);
    const p1 = { x: b.x + ((a.x - b.x) / inLen) * ri, y: b.y + ((a.y - b.y) / inLen) * ri };
    const p2 = { x: b.x + ((c.x - b.x) / outLen) * ri, y: b.y + ((c.y - b.y) / outLen) * ri };
    d += ` L ${p1.x},${p1.y} Q ${b.x},${b.y} ${p2.x},${p2.y}`;
  }
  const last = pts[pts.length - 1];
  d += ` L ${last.x},${last.y}`;
  return d;
}

/**
 * Ребро L2: рисуется по точкам маршрута, которые посчитал dagre (обходят узлы, идут по каналам между
 * рангами) — поэтому нет каши из линий поверх плашек. Нет точек (fallback) → прямая source→target.
 */
export function OrthoEdge({ id, data, style, markerEnd, sourceX, sourceY, targetX, targetY }: EdgeProps) {
  const pts = (data as { points?: XY[] } | undefined)?.points;
  const path = pts && pts.length >= 2
    ? roundedPath(pts)
    : `M ${sourceX},${sourceY} L ${targetX},${targetY}`;
  return <BaseEdge id={id} path={path} style={style} markerEnd={markerEnd} />;
}
