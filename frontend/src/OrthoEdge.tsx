import { BaseEdge, getSmoothStepPath, Position, type EdgeProps } from '@xyflow/react';
import type { XY } from './layerFlow';

// Ортогонализация маршрута dagre: точки dagre — контрольные точки сплайна (диагонали). Каждый косой
// сегмент a→b заменяем на H→V→H с вертикалью посередине (в канале между рангами) — линия выходит из
// источника и входит в цель горизонтально, повороты прямые. Соседние сегменты стыкуются горизонталью.
function orthoPoints(pts: XY[]): XY[] {
  const out: XY[] = [pts[0]];
  for (let i = 1; i < pts.length; i++) {
    const a = pts[i - 1], b = pts[i];
    if (Math.abs(a.x - b.x) < 0.5 || Math.abs(a.y - b.y) < 0.5) {
      out.push(b); // сегмент уже ортогонален
    } else {
      const midX = (a.x + b.x) / 2;
      out.push({ x: midX, y: a.y }, { x: midX, y: b.y }, b);
    }
  }
  return out;
}

// Путь-ломаная со скруглением углов (радиус r). Точки уже в координатах потока и ортогональны.
function roundedPath(pts: XY[], r = 6): string {
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

/**
 * Ребро L2: путь по маршруту dagre (обходит узлы по каналам между рангами), но ортогонализированный —
 * прямые углы, как на L0. Нет точек (fallback) → стандартный smoothstep source→target.
 */
export function OrthoEdge({ id, data, style, markerEnd, sourceX, sourceY, targetX, targetY }: EdgeProps) {
  const pts = (data as { points?: XY[] } | undefined)?.points;
  if (pts && pts.length >= 2) {
    return <BaseEdge id={id} path={roundedPath(orthoPoints(pts))} style={style} markerEnd={markerEnd} />;
  }
  const [path] = getSmoothStepPath({
    sourceX, sourceY, sourcePosition: Position.Right,
    targetX, targetY, targetPosition: Position.Left, borderRadius: 6,
  });
  return <BaseEdge id={id} path={path} style={style} markerEnd={markerEnd} />;
}
