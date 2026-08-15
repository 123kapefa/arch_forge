import { BaseEdge, getSmoothStepPath, useInternalNode, type EdgeProps } from '@xyflow/react';
import { getEdgeParams } from './floating';

/**
 * Floating-ребро: цепляется к грани плашки, обращённой к цели (низ/верх/лево/право), и рисует
 * ортогональный путь между этими точками. В детерминированной сетке это убирает петли и въезд не с той стороны.
 */
export function RoutedEdge({ id, source, target, style, markerEnd }: EdgeProps) {
  const src = useInternalNode(source);
  const tgt = useInternalNode(target);
  if (!src || !tgt) return null;

  const { sx, sy, tx, ty, sourcePos, targetPos } = getEdgeParams(src, tgt);
  const [path] = getSmoothStepPath({
    sourceX: sx, sourceY: sy, sourcePosition: sourcePos,
    targetX: tx, targetY: ty, targetPosition: targetPos,
    borderRadius: 10,
  });
  return <BaseEdge id={id} path={path} style={style} markerEnd={markerEnd} />;
}
