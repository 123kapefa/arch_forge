import { BaseEdge, getSmoothStepPath, useInternalNode, type EdgeProps } from '@xyflow/react';
import { getEdgeParams } from './floating';
import type { XY } from './layerFlow';

// Ортогонализация маршрута ELK/dagre: косой сегмент a→b → H→V→H с вертикалью посередине.
function orthoPoints(pts: XY[]): XY[] {
  const out: XY[] = [pts[0]];
  for (let i = 1; i < pts.length; i++) {
    const a = pts[i - 1], b = pts[i];
    if (Math.abs(a.x - b.x) < 0.5 || Math.abs(a.y - b.y) < 0.5) {
      out.push(b);
    } else {
      const midX = (a.x + b.x) / 2;
      out.push({ x: midX, y: a.y }, { x: midX, y: b.y }, b);
    }
  }
  return out;
}

// Путь-ломаная со скруглением углов (радиус r). Точки уже ортогональны и в координатах потока.
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

const moved = (a: XY | undefined, b: unknown): boolean => {
  const pos = b as XY | undefined;
  return !a || !pos || Math.hypot(a.x - pos.x, a.y - pos.y) > 1.5;
};

/**
 * Ребро L1/L2: пока узлы стоят на местах раскладки — путь по маршруту ELK (обход карточек, прямые углы).
 * Если узел ПЕРЕТАЩИЛИ — рисуем живой floating-smoothstep по текущим позициям, чтобы линия шла за блоком.
 */
export function OrthoEdge({ id, source, target, data, style, markerEnd, sourceX, sourceY, targetX, targetY }: EdgeProps) {
  const src = useInternalNode(source);
  const tgt = useInternalNode(target);
  const pts = (data as { points?: XY[] } | undefined)?.points;

  // сдвинули ли хоть один узел от позиции раскладки (elkPos)
  const dragged = !src || !tgt
    || moved(src.internals.positionAbsolute, src.data?.elkPos)
    || moved(tgt.internals.positionAbsolute, tgt.data?.elkPos);

  if (pts && pts.length >= 2 && !dragged) {
    return <BaseEdge id={id} path={roundedPath(orthoPoints(pts))} style={style} markerEnd={markerEnd} />;
  }
  if (src && tgt) {
    const { sx, sy, tx, ty, sourcePos, targetPos } = getEdgeParams(src, tgt);
    const [path] = getSmoothStepPath({
      sourceX: sx, sourceY: sy, sourcePosition: sourcePos,
      targetX: tx, targetY: ty, targetPosition: targetPos, borderRadius: 8,
    });
    return <BaseEdge id={id} path={path} style={style} markerEnd={markerEnd} />;
  }
  const [path] = getSmoothStepPath({ sourceX, sourceY, targetX, targetY, borderRadius: 8 });
  return <BaseEdge id={id} path={path} style={style} markerEnd={markerEnd} />;
}
