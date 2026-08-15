import { Handle, Position, type NodeProps, type Node } from '@xyflow/react';
import type { InfraNodeData } from '../systemFlow';

export type InfraNodeType = Node<InfraNodeData, 'infra'>;

const HEALTH_DOT: Record<string, string> = { ok: '#57c78a', warning: '#e0a13c', critical: '#e5534b' };
const KIND_ICON: Record<string, string> = { datastore: '▤', topic: '◈', external: '⊗' };
const KIND_LABEL: Record<string, string> = { datastore: 'хранилище', topic: 'шина', external: 'внешний' };

/** Инфра-узел L0: хранилище / топик-шина / внешний узел. */
export function InfraNode({ data, selected }: NodeProps<InfraNodeType>) {
  const { node, health, problems } = data;

  return (
    <div className={`infra-node kind-${node.kind} health-${health}${selected ? ' is-selected' : ''}`}>
      <Handle type="target" position={Position.Top} />

      <span className="infra-node__icon">{KIND_ICON[node.kind] ?? '□'}</span>
      <div className="infra-node__body">
        <span className="infra-node__name" title={node.id}>{node.name}</span>
        <span className="infra-node__kind">{KIND_LABEL[node.kind] ?? node.kind}</span>
      </div>
      <span className="infra-node__dot" style={{ background: HEALTH_DOT[health] ?? HEALTH_DOT.ok }} />
      {problems.length > 0 && <span className="infra-node__badge">⚠ {problems.length}</span>}

      <Handle type="source" position={Position.Bottom} />
    </div>
  );
}
