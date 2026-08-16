import { Handle, Position, type NodeProps, type Node } from '@xyflow/react';
import { layerColor } from '../theme';
import type { MemberDto } from '../api';
import { TYPE_PREVIEW_MAX, typeCardWidth } from './typeCardMetrics';

export interface TypeCardData {
  name: string;
  kind: string;
  role: string;
  layer: string;
  via: string;
  drift?: string[];
  members?: MemberDto[];
  [key: string]: unknown;
}
export type TypeCardType = Node<TypeCardData, 'type'>;

// Маркер вида члена (как в референсе): свойство/поле ▸, метод ƒ, эндпоинт ▪.
const MARK: Record<string, string> = { endpoint: '▪', method: 'ƒ', property: '▸', field: '▸' };

// Текст строки члена: свойство «Имя : Тип», метод/эндпоинт «Имя(параметры)».
function memberText(m: MemberDto): string {
  if (m.kind === 'property' || m.kind === 'field') return m.signature ? `${m.name} : ${m.signature}` : m.name;
  const params = m.signature?.match(/^\(([^)]*)\)/)?.[1] ?? '';
  return `${m.name}(${params})`;
}

/** Узел L2: тип как UML-карточка — шапка (иконка, имя, «вид» · роль, статус) + превью членов. */
export function TypeCard({ data, selected }: NodeProps<TypeCardType>) {
  const color = layerColor(data.layer);
  const members = [...(data.members ?? [])].sort((a, b) => (a.line ?? 0) - (b.line ?? 0));
  const preview = members.slice(0, TYPE_PREVIEW_MAX);
  const hidden = members.length - preview.length;
  const health = (data.drift?.length ?? 0) > 0 ? 'warning' : 'ok';
  const roleTail = data.role && data.role !== 'unknown' ? ` · ${data.role}` : '';
  const width = typeCardWidth(data.name, members);

  return (
    <div className={`tcard health-${health}${selected ? ' is-selected' : ''}`} style={{ width }}>
      <Handle type="target" position={Position.Left} />
      <div className="tcard__head">
        <span className="tcard__icon" style={{ background: `${color}26`, color }}>
          <svg width="13" height="13" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
            <path d="M12 2l10 10-10 10L2 12z" />
          </svg>
        </span>
        <div className="tcard__title">
          <div className="tcard__name" title={data.name}>{data.name}</div>
          <div className="tcard__meta">«{data.kind}»{roleTail}</div>
        </div>
        <span className={`tcard__dot health-${health}`} />
      </div>
      {preview.length > 0 && (
        <div className="tcard__body">
          {preview.map((m) => (
            <div key={m.id} className={`tmember kind-${m.kind}`} title={memberText(m)}>
              <span className="tmember__vis">+</span>
              <span className="tmember__mark">{MARK[m.kind] ?? '·'}</span>
              <span className="tmember__text">{memberText(m)}</span>
            </div>
          ))}
          {hidden > 0 && <div className="tmember tmember--more">+ ещё {hidden}</div>}
        </div>
      )}
      <Handle type="source" position={Position.Right} />
    </div>
  );
}
