import { BaseEdge, getSmoothStepPath, useInternalNode, type EdgeProps, type InternalNode } from '@xyflow/react';
import type { RoutePoint } from './elkLayout';

const EPS = 1.5; // допуск: узел «на дефолтной позиции» (совпадает с ELK) → используем маршрут ELK

// Узел стоит на своей ELK-позиции? (подвинули → маршрут устарел, откатываемся к smoothstep)
function atElk(node: InternalNode | undefined): boolean {
  const elk = node?.data?.elk as RoutePoint | undefined;
  const p = node?.internals.positionAbsolute;
  if (!elk || !p) return false;
  return Math.abs(p.x - elk.x) < EPS && Math.abs(p.y - elk.y) < EPS;
}

function dist(a: RoutePoint, b: RoutePoint): number { return Math.hypot(a.x - b.x, a.y - b.y); }

// Ортогональная ломаная ELK со скруглёнными углами.
function roundedPath(pts: RoutePoint[], r: number): string {
  if (pts.length < 2) return '';
  let d = `M ${pts[0].x} ${pts[0].y}`;
  for (let i = 1; i < pts.length - 1; i++) {
    const p = pts[i], a = pts[i - 1], b = pts[i + 1];
    const r1 = Math.min(r, dist(a, p) / 2), r2 = Math.min(r, dist(p, b) / 2);
    const c1 = { x: p.x - Math.sign(p.x - a.x) * r1, y: p.y - Math.sign(p.y - a.y) * r1 };
    const c2 = { x: p.x + Math.sign(b.x - p.x) * r2, y: p.y + Math.sign(b.y - p.y) * r2 };
    d += ` L ${c1.x} ${c1.y} Q ${p.x} ${p.y} ${c2.x} ${c2.y}`;
  }
  const last = pts[pts.length - 1];
  d += ` L ${last.x} ${last.y}`;
  return d;
}

/** Ребро с маршрутом ELK (обход плашек). Если оба узла на дефолтных позициях — рисуем маршрут; иначе smoothstep. */
export function RoutedEdge({
  id, source, target, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, style, markerEnd, data,
}: EdgeProps) {
  const src = useInternalNode(source);
  const tgt = useInternalNode(target);
  const points = (data as { points?: RoutePoint[] } | undefined)?.points;
  const useRoute = !!points && points.length >= 2 && atElk(src) && atElk(tgt);
  const path = useRoute
    ? roundedPath(points, 12)
    : getSmoothStepPath({ sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, borderRadius: 10 })[0];
  return <BaseEdge id={id} path={path} style={style} markerEnd={markerEnd} />;
}
