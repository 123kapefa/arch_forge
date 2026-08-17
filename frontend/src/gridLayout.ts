// Детерминированная раскладка L0: предсказуемые ряды, без органического разброса.
// Сверху gateway → ряды «юнитов» (core-сервис + его спутники под ним) → снизу общая инфра.
// Число колонок подбирается под ~16:10. Рёбра — простые ортогональные (RoutedEdge без маршрута → smoothstep).
import type { Edge, Node } from '@xyflow/react';
import { isInfra, type SystemNodeData } from './systemFlow';

const CORE_W = 236, SAT_W = 190, SAT_H = 54;
const GAP = 60;        // зазор внутри юнита (сервис ↔ спутники, спутник ↔ спутник)
const COL_GAP = 140;   // между юнитами по горизонтали
const ROW_GAP = 130;   // между рядами юнитов
const SAT_COLS = 2;    // спутники под сервисом в 2 колонки

const nameOf = (n: Node) => (n.data as SystemNodeData).node.name;
const kindOf = (n: Node) => (n.data as SystemNodeData).node.kind;
const layersOf = (n: Node) => (n.data as SystemNodeData).node.layers?.length ?? 0;
const norm = (s: string) => s.toLowerCase().replace(/[^a-z0-9]/g, '');
const coreH = (n: Node) => 44 + layersOf(n) * 22 + 30;

interface Unit { core: Node; sats: Node[]; }

function unitSize(u: Unit): { w: number; h: number } {
  const rows = Math.ceil(u.sats.length / SAT_COLS);
  const satsW = Math.min(u.sats.length, SAT_COLS) * (SAT_W + GAP) - GAP;
  const w = Math.max(CORE_W, u.sats.length > 0 ? satsW : CORE_W);
  const h = coreH(u.core) + (rows > 0 ? GAP + rows * (SAT_H + GAP) - GAP : 0);
  return { w, h };
}

// Разложить юнит в ячейке (ox,oy): core по центру сверху, спутники под ним в 2 колонки по центру.
function placeUnit(u: Unit, ox: number, oy: number, cellW: number, pos: Map<string, { x: number; y: number }>): void {
  const { w } = unitSize(u);
  const x0 = ox + (cellW - w) / 2; // центрируем юнит в ячейке
  pos.set(u.core.id, { x: x0 + (w - CORE_W) / 2, y: oy });
  const sy = oy + coreH(u.core) + GAP;
  u.sats.forEach((s, i) => {
    const col = i % SAT_COLS, row = Math.floor(i / SAT_COLS);
    const inRow = Math.min(u.sats.length - row * SAT_COLS, SAT_COLS);
    const rowW = inRow * (SAT_W + GAP) - GAP;
    pos.set(s.id, { x: x0 + (w - rowW) / 2 + col * (SAT_W + GAP), y: sy + row * (SAT_H + GAP) });
  });
}

export function gridLayout(nodes: Node[], edges: Edge[]): { nodes: Node[]; edges: Edge[] } {
  if (nodes.length === 0) return { nodes, edges };

  // Сквозной shared kernel — вне сервисной сетки: иначе он занимает сервисную ячейку и налезает на ручную
  // раскладку (пользователь сам расставил сервисы). Кладём отдельно, слева-сверху над сеткой.
  const sharedNode = nodes.find((n) => kindOf(n) === 'shared');
  const grid = sharedNode ? nodes.filter((n) => n.id !== sharedNode.id) : nodes;

  // core-сервисы (со слоями) — по алфавиту для стабильности
  const cores = grid
    .filter((n) => !isInfra(kindOf(n)) && layersOf(n) > 0)
    .sort((a, b) => nameOf(a).localeCompare(nameOf(b)));
  const bases = [...new Set(cores.map((c) => norm(nameOf(c)).replace(/api$/, '')).filter((b) => b.length >= 3))];
  const clusterOf = new Map<string, string>();
  for (const n of grid) {
    const nn = norm(nameOf(n));
    let best = '';
    for (const b of bases) if (b.length > best.length && nn.includes(b)) best = b;
    if (best) clusterOf.set(n.id, best);
  }

  const gateway = grid.find((n) => norm(nameOf(n)).includes('gateway'));
  const claimed = new Set<string>();
  const units: Unit[] = cores.map((core) => {
    const base = norm(nameOf(core)).replace(/api$/, '');
    const sats = grid.filter((n) => n.id !== core.id && clusterOf.get(n.id) === base);
    [core, ...sats].forEach((x) => claimed.add(x.id));
    return { core, sats };
  });
  const infra = grid.filter((n) => !claimed.has(n.id) && n.id !== gateway?.id);

  const cellW = Math.max(...units.map((u) => unitSize(u).w), CORE_W);
  const cellH = Math.max(...units.map((u) => unitSize(u).h), coreH(cores[0] ?? grid[0] ?? nodes[0]));

  // число колонок под ~16:10
  const gwBlock = 80 + ROW_GAP;
  const infraRowsH = infra.length > 0 ? SAT_H + ROW_GAP : 0;
  let cols = 3, bestDiff = Infinity;
  for (let c = 3; c <= 7; c++) {
    const rows = Math.ceil(units.length / c);
    const w = c * (cellW + COL_GAP) - COL_GAP;
    const h = gwBlock + rows * (cellH + ROW_GAP) - ROW_GAP + infraRowsH;
    const diff = Math.abs(w / h - 1.6);
    if (diff < bestDiff) { bestDiff = diff; cols = c; }
  }

  const pos = new Map<string, { x: number; y: number }>();
  const gridW = cols * (cellW + COL_GAP) - COL_GAP;

  // gateway — сверху по центру
  if (gateway) pos.set(gateway.id, { x: (gridW - CORE_W) / 2, y: 0 });

  // юниты — сетка cols×rows
  units.forEach((u, i) => {
    const col = i % cols, row = Math.floor(i / cols);
    const ox = col * (cellW + COL_GAP);
    const oy = gwBlock + row * (cellH + ROW_GAP);
    placeUnit(u, ox, oy, cellW, pos);
  });

  // общая инфра — ряд(ы) снизу, по центру
  const unitRows = Math.ceil(units.length / cols);
  const infraY = gwBlock + unitRows * (cellH + ROW_GAP);
  const perInfraRow = Math.max(1, Math.floor(gridW / (SAT_W + GAP)));
  infra.forEach((n, i) => {
    const col = i % perInfraRow, row = Math.floor(i / perInfraRow);
    const rowW = Math.min(infra.length - row * perInfraRow, perInfraRow) * (SAT_W + GAP) - GAP;
    pos.set(n.id, { x: (gridW - rowW) / 2 + col * (SAT_W + GAP), y: infraY + row * (SAT_H + GAP) });
  });

  // сквозной shared kernel — своя позиция слева-сверху, вне сервисной сетки (не конфликтует с ручной раскладкой)
  if (sharedNode) pos.set(sharedNode.id, { x: -(CORE_W + COL_GAP), y: 0 });

  const laidNodes = nodes.map((n) => ({ ...n, position: pos.get(n.id) ?? { x: 0, y: 0 } }));
  return { nodes: laidNodes, edges }; // маршруты не считаем → RoutedEdge рисует smoothstep
}
