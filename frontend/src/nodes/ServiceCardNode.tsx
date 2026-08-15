import { Handle, Position, type NodeProps, type Node } from '@xyflow/react';
import type { ServiceCardData } from '../systemFlow';
import { severityCounts } from '../systemModel';

export type ServiceCardType = Node<ServiceCardData, 'serviceCard'>;

const HEALTH_DOT: Record<string, string> = { ok: '#57c78a', warning: '#e0a13c', critical: '#e5534b' };

// Индикатор здоровья слоя: ✓ ok · ⚠ warning · ● critical.
function layerMark(health: string): string {
  return health === 'critical' ? '●' : health === 'warning' ? '⚠' : '✓';
}

/** Карточка .sln-сервиса (L0): здоровье + слои с индикаторами. Двойной клик — drill в L1. */
export function ServiceCardNode({ data, selected }: NodeProps<ServiceCardType>) {
  const { summary, problems } = data;
  const counts = severityCounts(problems);

  return (
    <div className={`svc-card health-${summary.health}${selected ? ' is-selected' : ''}`}>
      <Handle type="target" position={Position.Top} />

      <div className="svc-card__head">
        <span className="svc-card__dot" style={{ background: HEALTH_DOT[summary.health] ?? HEALTH_DOT.ok }} />
        <span className="svc-card__name" title={summary.id}>{summary.name}</span>
        <span className="svc-card__badges">
          {counts.critical > 0 && <span className="badge badge--critical">● {counts.critical}</span>}
          {counts.warning > 0 && <span className="badge badge--warning">⚠ {counts.warning}</span>}
          {counts.coverage > 0 && <span className="badge badge--coverage">○ {counts.coverage}</span>}
        </span>
      </div>

      <div className="svc-card__layers">
        {summary.layers.map((l) => (
          <div key={l.name} className={`svc-layer health-${l.health}`} title={l.note ?? ''}>
            <span className="svc-layer__name">{l.name}</span>
            {l.note && <span className="svc-layer__note">{l.note}</span>}
            <span className="svc-layer__mark">{layerMark(l.health)}</span>
          </div>
        ))}
      </div>

      <div className="svc-card__foot">двойной клик → слои (L1)</div>

      <Handle type="source" position={Position.Bottom} />
    </div>
  );
}
