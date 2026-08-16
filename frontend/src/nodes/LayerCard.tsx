import { Handle, Position, type NodeProps, type Node } from '@xyflow/react';
import { layerColor } from '../theme';
import type { LayerCardData } from '../layerFlow';

export type LayerCardType = Node<LayerCardData, 'layer'>;

const mark = (h: string) => (h === 'critical' ? '●' : h === 'warning' ? '⚠' : '✓');

// Стереотип типа → маркер (грубо по роли из тип-графа): контроллер/эндпоинт ▪, обработчик/сервис ▸,
// агрегат/сущность/событие ◆, DTO/запрос □, репозиторий/контекст ▫, прочее ·.
function roleMark(role: string): string {
  const r = role.toLowerCase();
  if (r.includes('endpoint') || r.includes('controller')) return '▪';
  if (r.includes('handler') || r.includes('service') || r.includes('provider') || r.includes('usecase')) return '▸';
  if (r.includes('aggregate') || r.includes('entity') || r.includes('event') || r.includes('valueobject')) return '◆';
  if (r.includes('dto') || r.includes('request') || r.includes('response') || r.includes('contract')) return '□';
  if (r.includes('repository') || r.includes('context') || r.includes('config')) return '▫';
  return '·';
}

/** Узел L1: слой сервиса — имя, число типов, здоровье, превью типов и предупреждения. Раскрывается в типы (L2). */
export function LayerCard({ data, selected }: NodeProps<LayerCardType>) {
  const { name, health, count, preview, warnings, notes } = data;
  const hidden = preview ? count - preview.length : 0;
  return (
    <div
      className={`layer-card health-${health}${selected ? ' is-selected' : ''}`}
      style={{ borderLeft: `3px solid ${layerColor(name)}` }}
    >
      <Handle type="target" position={Position.Top} />
      <div className="layer-card__head">
        <span className="layer-card__name">{name}</span>
        <span className="layer-card__mark">{mark(health)}</span>
      </div>
      <div className="layer-card__meta">{count} тип(ов)</div>

      {preview && preview.length > 0 && (
        <ul className="layer-card__types">
          {preview.map((t) => (
            <li key={t.name} className="layer-card__type" title={t.role}>
              <span className="layer-card__typemark">{roleMark(t.role)}</span>
              <span className="layer-card__typename">{t.name}</span>
            </li>
          ))}
          {hidden > 0 && <li className="layer-card__more">+ ещё {hidden}</li>}
        </ul>
      )}

      {((warnings && warnings.length > 0) || (notes && notes.length > 0)) && (
        <div className="layer-card__warns">
          {warnings?.map((w) => (
            <div key={w} className="layer-card__warn">⚠ {w} · нарушение свода</div>
          ))}
          {notes?.map((n) => (
            <div key={n} className="layer-card__note">{n}</div>
          ))}
        </div>
      )}

      <div className="layer-card__foot">двойной клик → типы (L2)</div>
      <Handle type="source" position={Position.Bottom} />
    </div>
  );
}
