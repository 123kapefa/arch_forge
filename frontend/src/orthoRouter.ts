// Ортогональный роутер по Hanan-сетке (Dijkstra со штрафом за повороты): гарантированно огибает блоки
// и минимизирует число поворотов (нет бессмысленных загибов). Используется для «живых» рёбер после
// перетаскивания узлов (L1/L2). Сетка строится из границ близких препятствий → малая, считается быстро.

export type Rect = { x: number; y: number; w: number; h: number };
type XY = { x: number; y: number };

const MARGIN = 14; // отступ линий сетки от габаритов препятствия
const TURN = 40;   // штраф за поворот (в px пути)
const MAX_CELLS = 1200; // предохранитель: слишком большая сетка → прямой заход

/**
 * Маршрут sPort→tPort, огибая obstacles. Заходит в порты перпендикулярно через стабы (sStub/tStub).
 * Нет пути / сетка слишком большая → прямой L-заход [sPort, sStub, tStub, tPort].
 */
export function routeOrtho(sPort: XY, sStub: XY, tPort: XY, tStub: XY, obstacles: Rect[]): XY[] {
  const pad = 90;
  const bx1 = Math.min(sStub.x, tStub.x) - pad, bx2 = Math.max(sStub.x, tStub.x) + pad;
  const by1 = Math.min(sStub.y, tStub.y) - pad, by2 = Math.max(sStub.y, tStub.y) + pad;
  const obs = obstacles.filter((o) => o.x - MARGIN < bx2 && o.x + o.w + MARGIN > bx1 && o.y - MARGIN < by2 && o.y + o.h + MARGIN > by1);

  const xset = new Set<number>([sStub.x, tStub.x]);
  const yset = new Set<number>([sStub.y, tStub.y]);
  for (const o of obs) { xset.add(o.x - MARGIN); xset.add(o.x + o.w + MARGIN); yset.add(o.y - MARGIN); yset.add(o.y + o.h + MARGIN); }
  const X = [...xset].sort((a, b) => a - b);
  const Y = [...yset].sort((a, b) => a - b);
  const nx = X.length, ny = Y.length;
  if (nx * ny > MAX_CELLS) return [sPort, sStub, tStub, tPort];
  const ixOf = new Map(X.map((v, i) => [v, i]));
  const iyOf = new Map(Y.map((v, i) => [v, i]));

  const insideObs = (x: number, y: number) => obs.some((o) => x > o.x + 0.5 && x < o.x + o.w - 0.5 && y > o.y + 0.5 && y < o.y + o.h - 0.5);
  const freeH = (a: number, b: number, r: number) => !insideObs((X[a] + X[b]) / 2, Y[r]);
  const freeV = (r: number, a: number, b: number) => !insideObs(X[r], (Y[a] + Y[b]) / 2);

  const Sx = ixOf.get(sStub.x)!, Sy = iyOf.get(sStub.y)!;
  const Gx = ixOf.get(tStub.x)!, Gy = iyOf.get(tStub.y)!;

  // Dijkstra по состояниям (ix, iy, dir): dir 0=старт, 1=гориз, 2=верт.
  const NS = nx * ny * 3;
  const dist = new Float64Array(NS).fill(Infinity);
  const prev = new Int32Array(NS).fill(-1);
  const sid = (Sx * ny + Sy) * 3;
  dist[sid] = 0;
  const pq: [number, number][] = [[0, sid]];
  const pop = () => { let bi = 0; for (let i = 1; i < pq.length; i++) if (pq[i][0] < pq[bi][0]) bi = i; return pq.splice(bi, 1)[0]; };

  let goal = -1;
  while (pq.length) {
    const [c, id] = pop();
    if (c > dist[id]) continue;
    const dir = id % 3;
    const rest = (id - dir) / 3;
    const ix = Math.floor(rest / ny), iy = rest % ny;
    if (ix === Gx && iy === Gy) { goal = id; break; }
    const push = (nix: number, niy: number, ndir: number, seg: number) => {
      const nid = (nix * ny + niy) * 3 + ndir;
      const cost = c + seg + (dir !== 0 && dir !== ndir ? TURN : 0);
      if (cost < dist[nid]) { dist[nid] = cost; prev[nid] = id; pq.push([cost, nid]); }
    };
    if (ix + 1 < nx && freeH(ix, ix + 1, iy)) push(ix + 1, iy, 1, X[ix + 1] - X[ix]);
    if (ix - 1 >= 0 && freeH(ix - 1, ix, iy)) push(ix - 1, iy, 1, X[ix] - X[ix - 1]);
    if (iy + 1 < ny && freeV(ix, iy, iy + 1)) push(ix, iy + 1, 2, Y[iy + 1] - Y[iy]);
    if (iy - 1 >= 0 && freeV(ix, iy - 1, iy)) push(ix, iy - 1, 2, Y[iy] - Y[iy - 1]);
  }
  if (goal < 0) return [sPort, sStub, tStub, tPort];

  const chain: XY[] = [];
  for (let id = goal; id >= 0; id = prev[id]) {
    const dir = id % 3, rest = (id - dir) / 3;
    chain.push({ x: X[Math.floor(rest / ny)], y: Y[rest % ny] });
  }
  chain.reverse();
  return simplify([sPort, ...chain, tPort]);
}

// Убрать коллинеарные и совпадающие точки.
function simplify(pts: XY[]): XY[] {
  const out: XY[] = [];
  for (const p of pts) {
    if (out.length && Math.abs(out[out.length - 1].x - p.x) < 0.5 && Math.abs(out[out.length - 1].y - p.y) < 0.5) continue;
    if (out.length >= 2) {
      const a = out[out.length - 2], b = out[out.length - 1];
      const collinear = (Math.abs(a.x - b.x) < 0.5 && Math.abs(b.x - p.x) < 0.5) || (Math.abs(a.y - b.y) < 0.5 && Math.abs(b.y - p.y) < 0.5);
      if (collinear) { out[out.length - 1] = p; continue; }
    }
    out.push(p);
  }
  return out;
}
