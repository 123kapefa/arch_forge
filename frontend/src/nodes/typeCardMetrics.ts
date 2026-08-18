// Метрики UML-карточки типа (L2) — общие для компонента TypeCard и раскладки layerFlow.
// Отдельным файлом, чтобы TypeCard.tsx экспортировал только компонент (react-refresh).

import type { MemberDto } from '../api';

export const TYPE_PREVIEW_MAX = 8;
export const TYPE_CARD_MIN_W = 230;
export const TYPE_CARD_MAX_W = 420;

// Приблизительная ширина символа (с запасом, чтобы текст не упирался): имя типа (15px bold),
// моноширинный член (13px SF Mono ≈ 0.6em).
const CH_NAME = 8.7;
const CH_MONO = 7.9;
const BUF = 6; // страховочный запас, px

// Высота карточки под раскладку (ELK/сетка) — должна совпадать с реальной по CSS.
export function typeCardHeight(memberCount: number): number {
  const HEAD = 56, BODY_PAD = 14, ROW = 22, MORE = 20;
  const preview = Math.min(memberCount, TYPE_PREVIEW_MAX);
  if (preview === 0) return HEAD;
  return HEAD + BODY_PAD + preview * ROW + (memberCount > TYPE_PREVIEW_MAX ? MORE : 0);
}

// Длина строки члена в карточке: свойство «Имя : Тип», метод/эндпоинт «Имя(параметры)».
function memberLineLen(m: MemberDto): number {
  if (m.kind === 'property' || m.kind === 'field') return m.name.length + (m.signature ? m.signature.length + 3 : 0);
  const params = m.signature?.match(/^\(([^)]*)\)/)?.[1] ?? '';
  return m.name.length + params.length + 2;
}

// Ширина карточки под содержимое: max(шапка с именем, самая длинная строка члена), в пределах [MIN, MAX].
// Так имена не режутся; лишь очень длинные сигнатуры упираются в MAX и получают многоточие.
export function typeCardWidth(name: string, members: MemberDto[]): number {
  const headW = 14 + 30 + 11 + name.length * CH_NAME + 10 + 9 + 14 + BUF; // иконка+отступы+имя+точка
  let bodyW = 0;
  for (const m of members.slice(0, TYPE_PREVIEW_MAX)) {
    bodyW = Math.max(bodyW, 14 + 8 + 8 + 10 + 8 + memberLineLen(m) * CH_MONO + 14 + BUF); // pad+«+»+mark+gaps+текст
  }
  return Math.round(Math.min(TYPE_CARD_MAX_W, Math.max(TYPE_CARD_MIN_W, headW, bodyW)));
}
