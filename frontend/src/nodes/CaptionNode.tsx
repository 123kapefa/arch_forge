import { useContext } from 'react';
import type { NodeProps, Node } from '@xyflow/react';
import { CaptionToggleContext } from '../captionContext';

export interface CaptionData {
  text: string;
  count: number;
  group: string;
  level?: number;       // 1 — заголовок раздела, 2 — подгруппа
  collapsed?: boolean;  // подгруппа свёрнута: карточки не показаны, ниже — превью имён
  preview?: string[];
  [key: string]: unknown;
}

/** Подпись группы на холсте L2: заголовок раздела или сворачиваемая подгруппа с превью имён. Сама не выбирается и не двигается. */
export function CaptionNode({ data }: NodeProps<Node<CaptionData, 'caption'>>) {
  const toggle = useContext(CaptionToggleContext);
  const sub = data.level === 2 || data.level === 1 && data.group !== 'section' && data.collapsed != null;
  if (data.group === 'section' || data.collapsed == null) {
    return (
      <div className={`tcaption tcaption--l${data.level ?? 1}`}>
        <span className="tcaption__text">{data.text}</span>
        <span className="tcaption__count">{data.count}</span>
      </div>
    );
  }
  return (
    <div className={`tcaption tcaption--l${sub ? 2 : 1} tcaption--sub`}>
      <button
        className="tcaption__btn nodrag nopan"
        aria-expanded={!data.collapsed}
        onClick={(e) => { e.stopPropagation(); toggle(data.group, !data.collapsed); }}
        title={data.collapsed ? 'развернуть группу' : 'свернуть группу'}
      >
        <span className="tcaption__chev" aria-hidden="true">{data.collapsed ? '▸' : '▾'}</span>
        <span className="tcaption__text">{data.text}</span>
        <span className="tcaption__count">{data.count}</span>
      </button>
      {data.collapsed && data.preview && (
        <div className="tcaption__preview">
          {data.preview.join(', ')}{data.count > data.preview.length ? ` … (+${data.count - data.preview.length})` : ''}
        </div>
      )}
    </div>
  );
}
