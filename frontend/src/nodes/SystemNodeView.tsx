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
// акцент-бордер: сервис — по репозиторию (тип проекта); инфра — по виду.
const KIND_ACCENT: Record<string, string> = { datastore: '#6fcf77', topic: '#b98cff', external: '#7c8698' };

/** Узел L0: compose-микросервис или инфра (хранилище/шина/внешний) — компактная плашка со здоровьем. */
export function SystemNodeView({ data, selected }: NodeProps<SystemNodeType>) {
  const { node, health, problems } = data;
  const infra = isInfra(node.kind);
  const accent = infra ? KIND_ACCENT[node.kind] ?? '#7c8698' : repoColor(namespaceOf(node.id));

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
