import { Handle, Position, type NodeProps, type Node } from '@xyflow/react';
import { layerColor } from '../theme';

export type LayerNodeData = { layer: string; count: number };
export type LayerNodeType = Node<LayerNodeData, 'layer'>;

/** Узел обзорного уровня — слой целиком (P1-D). Клик → провал в типы слоя. */
export function LayerNode({ data, selected }: NodeProps<LayerNodeType>) {
  const color = layerColor(data.layer);
  return (
    <div className={`layernode${selected ? ' is-selected' : ''}`} style={{ borderColor: color }}>
      <Handle type="target" position={Position.Left} />
      <div className="layernode__name" style={{ color }}>{data.layer}</div>
      <div className="layernode__count">{data.count} типов</div>
      <Handle type="source" position={Position.Right} />
    </div>
  );
}
