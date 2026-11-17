// Раскладка мини-карты: три колонки (хранилища/шины · сервисы · внешние). Детерминированная, без физики.

import type { SystemGraph } from '../../../shared/api';

export const MINI_W = 320;
export const NODE_W = 92;
export const NODE_H = 26;
const GAP = 12;
const PAD = 8;
export const MAX_NODES = 14;

export interface MiniNode { id: string; name: string; kind: string; health: string; x: number; y: number }
export interface MiniEdge { id: string; x1: number; y1: number; x2: number; y2: number }
export interface MiniMap { nodes: MiniNode[]; edges: MiniEdge[]; hidden: number; height: number }

const column = (kind: string) => (kind === 'service' ? 1 : kind === 'datastore' || kind === 'topic' ? 0 : 2);
const HEALTH_RANK: Record<string, number> = { critical: 0, warning: 1, ok: 2 };
const healthRank = (h?: string) => (h && Object.hasOwn(HEALTH_RANK, h) ? HEALTH_RANK[h] : 2);
// квоты по колонкам: иначе в большой системе все места займут сервисы одного вида
const QUOTA = [4, 7, 3];
const colX = [PAD, (MINI_W - NODE_W) / 2, MINI_W - NODE_W - PAD];

export function layoutMiniMap(g: SystemGraph, max = MAX_NODES): MiniMap {
  const degree = new Map<string, number>();
  for (const e of g.edges) {
    degree.set(e.source, (degree.get(e.source) ?? 0) + 1);
    degree.set(e.target, (degree.get(e.target) ?? 0) + 1);
  }
  // самые «больные», затем самые связанные — они важнее на схеме-превью
  const ordered = [...g.nodes].sort(
    (a, b) => healthRank(a.health) - healthRank(b.health) || (degree.get(b.id) ?? 0) - (degree.get(a.id) ?? 0) || a.name.localeCompare(b.name),
  );
  const taken = [0, 0, 0];
  const shown: typeof ordered = [];
  const rest: typeof ordered = [];
  for (const n of ordered) {
    const c = column(n.kind);
    if (shown.length < max && taken[c] < QUOTA[c]) { taken[c] += 1; shown.push(n); } else rest.push(n);
  }
  for (const n of rest) if (shown.length < max) shown.push(n); // свободные места — остальным
  const cols: typeof shown[] = [[], [], []];
  for (const n of shown) cols[column(n.kind)].push(n);
  const rows = Math.max(1, ...cols.map((c) => c.length));
  const height = PAD * 2 + rows * NODE_H + (rows - 1) * GAP;
  const nodes: MiniNode[] = [];
  cols.forEach((list, ci) => {
    // колонка центрируется по вертикали относительно самой длинной
    const offset = ((rows - list.length) * (NODE_H + GAP)) / 2;
    list.forEach((n, i) => {
      nodes.push({ id: n.id, name: n.name, kind: n.kind, health: n.health ?? 'ok', x: colX[ci], y: PAD + offset + i * (NODE_H + GAP) });
    });
  });
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const edges: MiniEdge[] = [];
  for (const e of g.edges) {
    const a = byId.get(e.source);
    const b = byId.get(e.target);
    if (a && b) edges.push({ id: e.id, x1: a.x + NODE_W / 2, y1: a.y + NODE_H / 2, x2: b.x + NODE_W / 2, y2: b.y + NODE_H / 2 });
  }
  return { nodes, edges, hidden: g.nodes.length - shown.length, height };
}
