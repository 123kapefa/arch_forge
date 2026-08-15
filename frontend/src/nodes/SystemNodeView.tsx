import { Handle, Position, type NodeProps, type Node } from '@xyflow/react';
import type { SystemNodeData } from '../systemFlow';
import { isInfra } from '../systemFlow';

export type SystemNodeType = Node<SystemNodeData, 'sys'>;

const HEALTH_DOT: Record<string, string> = { ok: '#57c78a', warning: '#e0a13c', critical: '#e5534b' };
const KIND_ICON: Record<string, string> = { datastore: '▤', topic: '◈', external: '⊗' };
const KIND_LABEL: Record<string, string> = {
  service: 'сервис', datastore: 'хранилище', topic: 'шина', external: 'внешний',
};

/** Узел L0: compose-микросервис или инфра (хранилище/шина/внешний) — компактная плашка со здоровьем. */
export function SystemNodeView({ data, selected }: NodeProps<SystemNodeType>) {
  const { node, health, problems } = data;
  const infra = isInfra(node.kind);

  return (
    <div className={`sys-node kind-${node.kind} health-${health}${selected ? ' is-selected' : ''}`}>
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
