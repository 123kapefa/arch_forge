import type { NodeProps, Node } from '@xyflow/react';

export interface CaptionData {
  text: string;
  count: number;
  group: string;
  [key: string]: unknown;
}

/** Подпись группы на холсте L2 (напр. «Связи только вне слоя · 23»): не выбирается и не двигается. */
export function CaptionNode({ data }: NodeProps<Node<CaptionData, 'caption'>>) {
  return (
    <div className={`tcaption tcaption--${data.group}`}>
      <span className="tcaption__text">{data.text}</span>
      <span className="tcaption__count">{data.count}</span>
    </div>
  );
}
