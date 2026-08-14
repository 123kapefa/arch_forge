import { Handle, Position, type NodeProps, type Node } from '@xyflow/react';
import { layerColor } from '../theme';

export type TypeNodeData = {
  name: string;
  kind: string;
  role: string;
  layer: string;
  via: string;
  drift: string[];
};

export type TypeNodeType = Node<TypeNodeData, 'type'>;

/** «Плашка» типа — стереотип, слой, способ классификации, дрейф. */
export function TypeNode({ data, selected }: NodeProps<TypeNodeType>) {
  const color = layerColor(data.layer);
  const hasDrift = data.drift?.length > 0;

  return (
    <div
      className={`plashka via-${data.via}${selected ? ' is-selected' : ''}`}
      style={{ borderTopColor: color }}
    >
      <Handle type="target" position={Position.Left} />

      <div className="plashka__head" style={{ color }}>
        <span className="plashka__layer">{data.layer}</span>
        <span className="plashka__kind">{data.kind}</span>
        {hasDrift && (
          <span className="plashka__drift" title={data.drift.join('\n')}>
            ⚠ {data.drift.length}
          </span>
        )}
      </div>

      <div className="plashka__name" title={data.name}>{data.name}</div>

      <div className="plashka__foot">
        <span className="plashka__role">{data.role}</span>
        <span className="plashka__via">{data.via}</span>
      </div>

      <Handle type="source" position={Position.Right} />
    </div>
  );
}
