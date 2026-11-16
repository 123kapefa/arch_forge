// Подгруппы «одиночек» экрана типов и подсчёт «используют». Чистые функции (без React/ELK).
// Признаки — только имеющиеся у узла: `role`, `kind`, число членов и имя-суффикс как запасной признак.

import type { OutsideSummary } from './typeLinks';

export type SubKey = 'validators' | 'endpoints' | 'constants' | 'records' | 'other';

export const SUB_ORDER: SubKey[] = ['validators', 'endpoints', 'constants', 'records', 'other'];
export const SUB_LABEL: Record<SubKey, string> = {
  validators: 'Валидаторы',
  endpoints: 'Endpoint-классы',
  constants: 'Справочники ошибок и констант',
  records: 'Записи и DTO',
  other: 'Прочее',
};

/** Группы больше этого порога по умолчанию свёрнуты (с превью имён). */
export const COLLAPSE_OVER = 12;
export const PREVIEW_NAMES = 6;

// Имена-справочники: статические классы ошибок/прав/констант. Запасной признак (роли у них нет, `unknown`).
const CONSTANTS_RE = /(Errors?|Permissions|Constants|Kinds|Roles|Types|Labels|Claims|Rules|Scopes)$/;

export interface GroupableNode { name: string; role?: string; kind?: string; members?: number }

/** К какой подгруппе относится тип: сначала по роли, затем по виду (record), затем по имени/пустому телу. */
export function subgroupOf(n: GroupableNode): SubKey {
  const role = n.role ?? '';
  if (role.replace(/\?$/, '') === 'Validator' || /Validator$/.test(n.name)) return 'validators';
  if (role === 'Endpoint' || /Endpoints?$/.test(n.name)) return 'endpoints';
  if (role === 'DTO' || role === 'Event' || role === 'Result' || n.kind === 'record') return 'records';
  if (n.kind !== 'interface' && n.kind !== 'enum' && (CONSTANTS_RE.test(n.name) || (n.kind === 'class' && (n.members ?? 0) === 0 && (!role || role === 'unknown')))) return 'constants';
  return 'other';
}

/** Ключ группы одиночек в раскладке/персисте. */
export const isoKey = (sub: SubKey) => `linked:${sub}`;
export const NONE_KEY = 'none';

export interface UsedBy { names: string[]; total: number }

/** Кто ссылается на тип ВНУТРИ слоя (слабые связи: сильных у одиночек нет по определению), имена по алфавиту. */
export function usedByOf(insideWeak: OutsideSummary | undefined): UsedBy {
  const names = [...new Set((insideWeak?.links ?? []).filter((l) => l.dir === 'in').map((l) => l.peerName))].sort((a, b) => a.localeCompare(b));
  return { names, total: names.length };
}

/** Строка карточки: «используют: A, B (+N)»; пусто → null. */
export function usedByLine(u: UsedBy, shown = 2): string | null {
  if (u.total === 0) return null;
  const head = u.names.slice(0, shown).join(', ');
  return u.total > shown ? `${head} (+${u.total - shown})` : head;
}

/** Число входящих связей типа (внутри слоя слабые + снаружи) — для порядка внутри группы. */
export function incomingCount(insideWeak: OutsideSummary | undefined, outside: OutsideSummary | undefined): number {
  return (insideWeak?.in ?? 0) + (outside?.in ?? 0);
}

/** Порядок в подгруппе: больше входящих — выше, затем по имени. */
export function compareByIncoming(a: { incoming: number; name: string }, b: { incoming: number; name: string }): number {
  return b.incoming - a.incoming || a.name.localeCompare(b.name);
}

/** Свёрнута ли группа: явный выбор пользователя важнее; иначе большие (> COLLAPSE_OVER) свёрнуты. */
export function isGroupCollapsed(state: Record<string, boolean>, key: string, size: number): boolean {
  return key in state ? state[key] : size > COLLAPSE_OVER;
}
