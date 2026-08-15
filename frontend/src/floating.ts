// Floating-рёбра: точка стыковки — на той грани плашки, что смотрит на другую плашку
// (низ/верх/лево/право), а не фиксированная «ручка». Убирает петли/въезд не с той стороны в сетке.
// Формула пересечения — стандартный приём React Flow (floating edges).
import { Position, type InternalNode } from '@xyflow/react';

interface Pt { x: number; y: number; }

function size(n: InternalNode): { w: number; h: number } {
  return { w: n.measured?.width ?? 190, h: n.measured?.height ?? 54 };
}

// Точка на границе `node`, направленная к центру `other`.
function intersection(node: InternalNode, other: InternalNode): Pt {
  const { w: nw, h: nh } = size(node);
  const { w: ow, h: oh } = size(other);
  const np = node.internals.positionAbsolute;
  const op = other.internals.positionAbsolute;
  const w = nw / 2, h = nh / 2;
  const x2 = np.x + w, y2 = np.y + h;
  const x1 = op.x + ow / 2, y1 = op.y + oh / 2;
  const xx1 = (x1 - x2) / (2 * w) - (y1 - y2) / (2 * h);
  const yy1 = (x1 - x2) / (2 * w) + (y1 - y2) / (2 * h);
  const a = 1 / (Math.abs(xx1) + Math.abs(yy1) || 1);
  const xx3 = a * xx1, yy3 = a * yy1;
  return { x: w * (xx3 + yy3) + x2, y: h * (-xx3 + yy3) + y2 };
}

function side(node: InternalNode, p: Pt): Position {
  const { w } = size(node);
  const np = node.internals.positionAbsolute;
  const px = Math.round(p.x - np.x);
  const py = Math.round(p.y - np.y);
  if (px <= 1) return Position.Left;
  if (px >= Math.round(w) - 1) return Position.Right;
  if (py <= 1) return Position.Top;
  return Position.Bottom;
}

export function getEdgeParams(source: InternalNode, target: InternalNode): {
  sx: number; sy: number; tx: number; ty: number; sourcePos: Position; targetPos: Position;
} {
  const sp = intersection(source, target);
  const tp = intersection(target, source);
  return { sx: sp.x, sy: sp.y, tx: tp.x, ty: tp.y, sourcePos: side(source, sp), targetPos: side(target, tp) };
}
