// Защита холста: проверка, что сохранённый вьюпорт вообще показывает содержимое, и что раскладка без NaN/Infinity.
// Чистые функции (без React Flow) — тестируются отдельно.

export interface Box { x: number; y: number; w: number; h: number }
export interface Vp { x: number; y: number; zoom: number }
export interface PosNode { position: { x: number; y: number }; width?: number | null; height?: number | null; measured?: { width?: number; height?: number } }

const DEF_W = 260, DEF_H = 120;

/** Описывающий прямоугольник узлов (по позиции и известному/типовому размеру). Нет конечных узлов → null. */
export function bboxOfNodes(nodes: readonly PosNode[]): Box | null {
  let x1 = Infinity, y1 = Infinity, x2 = -Infinity, y2 = -Infinity;
  for (const n of nodes) {
    const { x, y } = n.position;
    if (!Number.isFinite(x) || !Number.isFinite(y)) continue;
    const w = n.measured?.width ?? n.width ?? DEF_W, h = n.measured?.height ?? n.height ?? DEF_H;
    x1 = Math.min(x1, x); y1 = Math.min(y1, y); x2 = Math.max(x2, x + w); y2 = Math.max(y2, y + h);
  }
  return Number.isFinite(x1) ? { x: x1, y: y1, w: x2 - x1, h: y2 - y1 } : null;
}

/** Пересекается ли прямоугольник содержимого с видимой областью вьюпорта (flow-координаты = (screen - v.xy) / zoom). */
export function viewportShowsContent(vp: Vp, bbox: Box, size: { w: number; h: number }): boolean {
  if (![vp.x, vp.y, vp.zoom, size.w, size.h].every(Number.isFinite) || vp.zoom <= 0) return false;
  const vx1 = -vp.x / vp.zoom, vy1 = -vp.y / vp.zoom;
  const vx2 = vx1 + size.w / vp.zoom, vy2 = vy1 + size.h / vp.zoom;
  return bbox.x < vx2 && bbox.x + bbox.w > vx1 && bbox.y < vy2 && bbox.y + bbox.h > vy1;
}

/** Есть ли в раскладке узлы с нечисловыми координатами. */
export const hasNonFinitePositions = (nodes: readonly PosNode[]) => nodes.some((n) => !Number.isFinite(n.position.x) || !Number.isFinite(n.position.y));

/** Раскладка с нечисловыми координатами не применяется как есть: NaN/Infinity → 0 (лучше сдвинутая карточка, чем пустой холст). */
export function sanitizePositions<T extends PosNode>(nodes: T[]): T[] {
  if (!hasNonFinitePositions(nodes)) return nodes;
  return nodes.map((n) => (Number.isFinite(n.position.x) && Number.isFinite(n.position.y) ? n : { ...n, position: { x: Number.isFinite(n.position.x) ? n.position.x : 0, y: Number.isFinite(n.position.y) ? n.position.y : 0 } }));
}
