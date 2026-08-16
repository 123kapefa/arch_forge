import { type NodeProps, type Node } from '@xyflow/react';
import type { MemberChipData } from '../memberFlow';

export type MemberChipType = Node<MemberChipData, 'member'>;

// Маркер вида члена: эндпоинт ▪, метод ƒ, свойство ◇, поле •.
const KIND_MARK: Record<string, string> = { endpoint: '▪', method: 'ƒ', property: '◇', field: '•' };
const KIND_LABEL: Record<string, string> = { endpoint: 'endpoint', method: 'метод', property: 'свойство', field: 'поле' };

/** Узел L3: член типа. Эндпоинт — с HTTP-verb и маршрутом; метод/свойство/поле — с сигнатурой. */
export function MemberChip({ data, selected }: NodeProps<MemberChipType>) {
  const m = data.member;
  return (
    <div className={`member-chip kind-${m.kind}${selected ? ' is-selected' : ''}`}>
      <div className="member-chip__head">
        <span className="member-chip__mark">{KIND_MARK[m.kind] ?? '·'}</span>
        <span className="member-chip__name" title={m.name}>{m.name}</span>
        {m.httpMethod && <span className={`verb verb-${m.httpMethod}`}>{m.httpMethod}</span>}
      </div>
      {m.route && <div className="member-chip__route" title={m.route}>/{m.route.replace(/^\//, '')}</div>}
      {m.signature && !m.route && <div className="member-chip__sig" title={m.signature}>{m.signature}</div>}
      <div className="member-chip__kind">{KIND_LABEL[m.kind] ?? m.kind}{m.line ? ` · стр. ${m.line}` : ''}</div>
    </div>
  );
}
