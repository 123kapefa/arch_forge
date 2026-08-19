import { useContext } from 'react';
import { Handle, Position, type NodeProps, type Node } from '@xyflow/react';
import type { SystemNodeData } from '../systemFlow';
import { isInfra } from '../systemFlow';
import { namespaceOf } from '../systemModel';
import { repoColor } from '../theme';
import { HiddenLayersContext } from '../layersContext';
import { LensContext } from '../lensContext';

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
  const { node, health } = data;
  const infra = isInfra(node.kind);
  const shared = node.kind === 'shared'; // сквозной shared kernel — не деплой-сервис
  const accent = shared ? '#8a94a6' : infra ? KIND_ACCENT[node.kind] ?? '#7c8698' : repoColor(namespaceOf(node.id));
  const hiddenLayers = useContext(HiddenLayersContext);
  // подсветка по активной линзе (#70): арх — красный, свод/дрейф — жёлтый; узлы без попаданий линзы не красятся
  const { lens, hits } = useContext(LensContext);
  const lensHit = hits.get(node.id) ?? 0;
  const lensCls = lensHit > 0 ? ` lenshit lenshit--${lens}` : '';
  const lensBadge = lensHit > 0
    ? <span className={`badge ${lens === 'convention' ? 'badge--drift' : 'badge--arch'}`}>
        {lens === 'convention' ? '≈' : '⚠'} {lensHit}
      </span>
    : null;
  const layers = node.layers?.filter((l) => !hiddenLayers.has(l.name)); // секция «СЛОИ» прячет выбранные

  // Сервис (или сквозной shared kernel) со слоями → карточка «внутри — слои» (L0)
  if ((!infra || shared) && layers && layers.length > 0) {
    return (
      <div
        className={`svc-card health-${health}${shared ? ' svc-card--shared' : ''}${selected ? ' is-selected' : ''}${lensCls}`}
        style={{ borderLeft: `3px solid ${accent}` }}
      >
        <Handle type="target" position={Position.Top} />
        <div className="svc-card__head">
          {shared
            ? <span className="svc-card__cross" aria-hidden="true">⧉</span>
            : <span className="svc-card__dot" style={{ background: HEALTH_DOT[health] ?? HEALTH_DOT.ok }} />}
          <span className="svc-card__name" title={node.id}>{node.name}</span>
          {shared && <span className="svc-card__tag">сквозной</span>}
          {lensBadge}
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
        <div className="svc-card__foot">
          {node.endpoints && node.endpoints.length > 0 && <>⇄ {node.endpoints.length} endpoint(ов) · </>}
          двойной клик → слои (L1)
        </div>
        <Handle type="source" position={Position.Bottom} />
      </div>
    );
  }

  // Компактная плашка: инфра или сервис без распознанных слоёв
  return (
    <div
      className={`sys-node kind-${node.kind} health-${health}${selected ? ' is-selected' : ''}${lensCls}`}
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
      {lensBadge}
      <Handle type="source" position={Position.Bottom} />
    </div>
  );
}
