import { useContext } from 'react';
import { Handle, Position, type NodeProps, type Node } from '@xyflow/react';
import type { SystemNodeData } from '../systemFlow';
import { isInfra, nodeHasExposedPort, portExposure, portLabel } from '../systemFlow';
import { namespaceOf } from '../systemModel';
import { repoColor } from '../theme';
import { HiddenLayersContext } from '../layersContext';
import { LensContext } from '../lensContext';
import { DeployContext } from '../deployContext';
import type { SystemNode } from '../system';

export type SystemNodeType = Node<SystemNodeData, 'sys'>;

const HEALTH_DOT: Record<string, string> = { ok: '#6fcf77', warning: '#e0b24a', critical: '#e5484d' };
const KIND_ICON: Record<string, string> = { datastore: '▤', topic: '◈', external: '⊗' };
const KIND_LABEL: Record<string, string> = {
  service: 'сервис', datastore: 'хранилище', topic: 'шина', external: 'внешний',
};
const KIND_ACCENT: Record<string, string> = { datastore: '#6fcf77', topic: '#b98cff', external: '#7c8698' };

// индикатор слоя: ✓ ok · ⚠ warning · ● critical
const layerMark = (h: string) => (h === 'critical' ? '●' : h === 'warning' ? '⚠' : '✓');

const MAX_PORT_CHIPS = 3;

/** Deploy-оверлеи плашки (E3.2): ярус старта · порты (published/internal) · healthcheck · сети. Только в режиме Deploy. */
export function DeployStrip({ node }: { node: SystemNode }) {
  const dep = useContext(DeployContext);
  if (!dep) return null;
  const { overlays, tiers } = dep;
  const d = node.deploy;
  const tier = tiers.get(node.id);
  const ports = d?.ports ?? [];
  const shown = ports.slice(0, MAX_PORT_CHIPS);
  const hasAny = (overlays.start && tier !== undefined) || (overlays.ports && ports.length > 0)
    || (overlays.healthcheck && d) || (overlays.networks && (d?.networks.length ?? 0) > 0);
  if (!hasAny) return null;
  return (
    <div className="dep-strip" data-testid="deploy-strip">
      {overlays.start && tier !== undefined && (
        <span className="dep-chip dep-chip--start" title={`depends_on на хранилища: ярус ${tier + 1} (зависимости сервис→сервис не учитываются)`}>⛓ {tier + 1}</span>
      )}
      {overlays.ports && shown.map((p, i) => {
        const ex = portExposure(p, node.kind);
        return <span key={`${p.container}-${i}`} className={`dep-chip dep-chip--port dep-chip--${ex}`} title={portLabel(p)}>{portLabel(p)}</span>;
      })}
      {overlays.ports && ports.length > shown.length && (
        <span className="dep-chip" title={ports.slice(shown.length).map(portLabel).join(', ')}>+{ports.length - shown.length}</span>
      )}
      {overlays.healthcheck && d && (
        <span className={`dep-chip dep-chip--hc-${d.hasHealthcheck ? 'on' : 'off'}`} title={d.hasHealthcheck ? 'healthcheck задан' : 'healthcheck не задан'}>
          hc {d.hasHealthcheck ? '✓' : '—'}
        </span>
      )}
      {overlays.networks && d?.networks.map((net, i) => <span key={`${net}-${i}`} className="dep-chip dep-chip--net" title={`сеть ${net}`}>{net}</span>)}
    </div>
  );
}

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
        <DeployStrip node={node} />
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
        <span className="sys-node__sub">
          {KIND_LABEL[node.kind] ?? node.kind}
          {nodeHasExposedPort(node) && <span className="sys-node__flag" title="порт опубликован наружу (все интерфейсы)">⇱ наружу</span>}
        </span>
      </div>
      {infra && <span className="sys-node__dot" style={{ background: HEALTH_DOT[health] ?? HEALTH_DOT.ok }} />}
      {lensBadge}
      <DeployStrip node={node} />
      <Handle type="source" position={Position.Bottom} />
    </div>
  );
}
