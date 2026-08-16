// Метрики UML-карточки типа (L2) — общие для компонента TypeCard и раскладки layerFlow.
// Отдельным файлом, чтобы TypeCard.tsx экспортировал только компонент (react-refresh).

export const TYPE_CARD_W = 264;
export const TYPE_PREVIEW_MAX = 8;

// Высота карточки под раскладку (ELK/сетка) — должна совпадать с реальной по CSS.
export function typeCardHeight(memberCount: number): number {
  const HEAD = 56, BODY_PAD = 14, ROW = 22, MORE = 20;
  const preview = Math.min(memberCount, TYPE_PREVIEW_MAX);
  if (preview === 0) return HEAD;
  return HEAD + BODY_PAD + preview * ROW + (memberCount > TYPE_PREVIEW_MAX ? MORE : 0);
}
