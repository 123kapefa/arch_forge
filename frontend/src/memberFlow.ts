// L3 «члены типа»: раскрытие типа в его члены (эндпоинты/методы/свойства/поля) по тип-графу сервиса.
// Члены не связаны между собой → это не граф, а ровная сетка «чипов», отсортированных по виду:
// endpoint → method → property → field (у эндпоинта виден HTTP-verb и маршрут).

import type { Edge, Node } from '@xyflow/react';
import type { GraphDto, MemberDto } from './api';

export interface MemberChipData {
  member: MemberDto;
  [key: string]: unknown;
}

const KIND_ORDER: Record<string, number> = { endpoint: 0, method: 1, property: 2, field: 3 };
const MW = 230;
const MH = 62;
const GAP_X = 20;
const GAP_Y = 16;

/** Имя+члены типа по его id из тип-графа сервиса. */
export function membersOf(map: GraphDto, typeId: string): { name: string; kind: string; members: MemberDto[] } {
  const t = map.nodes.find((n) => n.id === typeId);
  return { name: t?.name ?? typeId, kind: t?.kind ?? '', members: t?.members ?? [] };
}

/** L3-доска: чипы членов ровной сеткой, отсортированы по виду (endpoint→method→property→field), затем по строке/имени. */
export function buildMemberFlow(map: GraphDto, typeId: string): { nodes: Node[]; edges: Edge[] } {
  const { members } = membersOf(map, typeId);
  const sorted = [...members].sort(
    (a, b) =>
      (KIND_ORDER[a.kind] ?? 9) - (KIND_ORDER[b.kind] ?? 9) ||
      (a.line ?? 0) - (b.line ?? 0) ||
      a.name.localeCompare(b.name),
  );
  const cols = Math.min(6, Math.max(3, Math.round(Math.sqrt(sorted.length * 1.6)) || 3));
  const nodes: Node[] = sorted.map((m, i) => ({
    id: `member:${m.id}`,
    type: 'member',
    position: { x: (i % cols) * (MW + GAP_X), y: Math.floor(i / cols) * (MH + GAP_Y) },
    data: { member: m } satisfies MemberChipData,
  }));
  return { nodes, edges: [] };
}
