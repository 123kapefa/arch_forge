import { Handle, Position, type NodeProps, type Node } from '@xyflow/react';
import type { SystemNodeData } from '../systemFlow';
import { isInfra } from '../systemFlow';
import { namespaceOf } from '../systemModel';
import { repoColor } from '../theme';

export type SystemNodeType = Node<SystemNodeData, 'sys'>;

const HEALTH_DOT: Record<string, string> = { ok: '#6fcf77', warning: '#e0b24a', critical: '#e5484d' };
const KIND_ICON: Record<string, string> = { datastore: '▤', topic: '◈', external: '⊗' };
const KIND_LABEL: Record<string, string> = {
  service: 'сервис', datastore: 'хранилище', topic: 'шина', external: 'внешний',
};
const KIND_ACCENT: Record<string, string> = { datastore: '#6fcf77', topic: '#b98cff', external: '#7c8698' };

// индикатор слоя: ✓ ok · ⚠ warning · ● critical
const layerMark = (h: string) => (h === 'critical' ? '●' : h === 'warning' ? '⚠' : '✓');

/** Узел L0: сервис (карточка СО слоями, если они есть) или компактная плашка (инфра / сервис без слоёв). */
export function SystemNodeView({ data, selected }: NodeProps<SystemNodeType>) {
  const { node, health, problems } = data;
  const infra = isInfra(node.kind);
  const accent = infra ? KIND_ACCENT[node.kind] ?? '#7c8698' : repoColor(namespaceOf(node.id));
  const layers = node.layers;

  // Сервис со слоями → карточка «внутри сервиса — слои» (L0)
  if (!infra && layers && layers.length > 0) {
    return (
      <div
        className={`svc-card health-${health}${selected ? ' is-selected' : ''}`}
        style={{ borderLeft: `3px solid ${accent}` }}
      >
        <Handle type="target" position={Position.Top} />
        <div className="svc-card__head">
          <span className="svc-card__dot" style={{ background: HEALTH_DOT[health] ?? HEALTH_DOT.ok }} />
          <span className="svc-card__name" title={node.id}>{node.name}</span>
          {problems.length > 0 && <span className="badge badge--warning">⚠ {problems.length}</span>}
        </div>
        <div className="svc-card__layers">
          {layers.map((l) => (
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

  // Компактная плашка: инфра или сервис без распознанных слоёв
  return (
    <div
      className={`sys-node kind-${node.kind} health-${health}${selected ? ' is-selected' : ''}`}
      style={{ borderLeft: `3px solid ${accent}` }}
    >
      <Handle type="target" position={Position.Top} />
      {infra ? (
        <span className="sys-node__icon">{KIND_ICON[node.kind] ?? '□'}</span>
      ) : (
        <span className="sys-node__dot" style={{ background: HEALTH_DOT[health] ?? HEALTH_DOT.ok }} />
      )}
      <div className="sys-node__body">
        <span className="sys-node__name" title={node.id}>{node.name}</span>
        <span className="sys-node__sub">{KIND_LABEL[node.kind] ?? node.kind}</span>
      </div>
      {infra && <span className="sys-node__dot" style={{ background: HEALTH_DOT[health] ?? HEALTH_DOT.ok }} />}
      {problems.length > 0 && <span className="sys-node__badge">⚠ {problems.length}</span>}
      <Handle type="source" position={Position.Bottom} />
    </div>
  );
}
