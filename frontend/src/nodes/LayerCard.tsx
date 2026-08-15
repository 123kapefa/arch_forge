import { Handle, Position, type NodeProps, type Node } from '@xyflow/react';
import { layerColor } from '../theme';
import type { LayerSummary } from '../system';

export type LayerCardType = Node<{ layer: LayerSummary }, 'layer'>;

const mark = (h: string) => (h === 'critical' ? '●' : h === 'warning' ? '⚠' : '✓');

/** Узел L1: слой сервиса (имя + число типов + здоровье + заметка). Раскрывается в типы (L2). */
export function LayerCard({ data, selected }: NodeProps<LayerCardType>) {
  const l = data.layer;
  return (
    <div
      className={`layer-card health-${l.health}${selected ? ' is-selected' : ''}`}
      style={{ borderLeft: `3px solid ${layerColor(l.name)}` }}
    >
      <Handle type="target" position={Position.Top} />
      <div className="layer-card__head">
        <span className="layer-card__name">{l.name}</span>
        <span className="layer-card__mark">{mark(l.health)}</span>
      </div>
      <div className="layer-card__meta">
        {l.types} тип(ов){l.note ? ` · ${l.note}` : ''}
      </div>
      <div className="layer-card__foot">двойной клик → типы (L2)</div>
      <Handle type="source" position={Position.Bottom} />
    </div>
  );
}
