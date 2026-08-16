// Ортогональный роутер по Hanan-сетке (Dijkstra со штрафом за повороты): гарантированно огибает блоки
// и минимизирует число поворотов (нет бессмысленных загибов). Используется для «живых» рёбер после
// перетаскивания узлов (L1/L2). Сетка строится из границ близких препятствий → малая, считается быстро.

export type Rect = { x: number; y: number; w: number; h: number };
export type XY = { x: number; y: number };
export type Side = 'T' | 'B' | 'L' | 'R';
export type EdgeLite = { id: string; source: string; target: string };

const MARGIN = 14;    // базовый отступ линий сетки от габаритов препятствия
const LANE_STEP = 7;  // разнос «полос»: у каждого ребра свой отступ → каналы не совпадают
const TURN = 40;      // штраф за поворот (в px пути)
const MAX_CELLS = 1200; // предохранитель: слишком большая сетка → прямой заход

/**
 * Маршрут sPort→tPort, огибая obstacles. Заходит в порты перпендикулярно через стабы (sStub/tStub).
 * lane — «полоса» ребра (0..): сдвигает отступ сетки, чтобы параллельные рёбра не ложились друг на друга.
 * Нет пути / сетка слишком большая → прямой L-заход [sPort, sStub, tStub, tPort].
 */
export function routeOrtho(sPort: XY, sStub: XY, tPort: XY, tStub: XY, obstacles: Rect[], lane = 0): XY[] {
  const margin = MARGIN + (lane % 5) * LANE_STEP;
  const pad = 90;
  const bx1 = Math.min(sStub.x, tStub.x) - pad, bx2 = Math.max(sStub.x, tStub.x) + pad;
  const by1 = Math.min(sStub.y, tStub.y) - pad, by2 = Math.max(sStub.y, tStub.y) + pad;
  const obs = obstacles.filter((o) => o.x - margin < bx2 && o.x + o.w + margin > bx1 && o.y - margin < by2 && o.y + o.h + margin > by1);

  const xset = new Set<number>([sStub.x, tStub.x]);
  const yset = new Set<number>([sStub.y, tStub.y]);
  for (const o of obs) { xset.add(o.x - margin); xset.add(o.x + o.w + margin); yset.add(o.y - margin); yset.add(o.y + o.h + margin); }
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

// --- порты, стороны, слоты (общие для дефолтной раскладки и «живого» ребра при перетаскивании) ---
const PORT_GAP = 18;
const clamp = (v: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, v));

export function sideFacing(me: Rect, other: Rect): Side {
  const dx = other.x + other.w / 2 - (me.x + me.w / 2);
  const dy = other.y + other.h / 2 - (me.y + me.h / 2);
  if (Math.abs(dy) >= Math.abs(dx)) return dy >= 0 ? 'B' : 'T';
  return dx >= 0 ? 'R' : 'L';
}
// слот ребра среди рёбер, выходящих из nodeId той же стороной
function slotOf(nodeId: string, edgeId: string, side: Side, edges: EdgeLite[], rects: Map<string, Rect>): { i: number; n: number } {
  const meR = rects.get(nodeId); if (!meR) return { i: 0, n: 1 };
  const peers: { id: string; key: number }[] = [];
  for (const e of edges) {
    const other = e.source === nodeId ? e.target : e.target === nodeId ? e.source : null;
    if (!other) continue;
    const oR = rects.get(other); if (!oR) continue;
    if (sideFacing(meR, oR) !== side) continue;
    peers.push({ id: e.id, key: side === 'T' || side === 'B' ? oR.x + oR.w / 2 : oR.y + oR.h / 2 });
  }
  peers.sort((a, b) => a.key - b.key || a.id.localeCompare(b.id));
  const i = peers.findIndex((p) => p.id === edgeId);
  return { i: i < 0 ? 0 : i, n: Math.max(1, peers.length) };
}
function portPoint(r: Rect, side: Side, i: number, n: number): XY {
  const off = (i - (n - 1) / 2) * PORT_GAP;
  if (side === 'T') return { x: clamp(r.x + r.w / 2 + off, r.x + 12, r.x + r.w - 12), y: r.y };
  if (side === 'B') return { x: clamp(r.x + r.w / 2 + off, r.x + 12, r.x + r.w - 12), y: r.y + r.h };
  if (side === 'L') return { x: r.x, y: clamp(r.y + r.h / 2 + off, r.y + 12, r.y + r.h - 12) };
  return { x: r.x + r.w, y: clamp(r.y + r.h / 2 + off, r.y + 12, r.y + r.h - 12) };
}
function alignPort(r: Rect, side: Side, partner: XY): XY {
  if (side === 'T') return { x: clamp(partner.x, r.x + 12, r.x + r.w - 12), y: r.y };
  if (side === 'B') return { x: clamp(partner.x, r.x + 12, r.x + r.w - 12), y: r.y + r.h };
  if (side === 'L') return { x: r.x, y: clamp(partner.y, r.y + 12, r.y + r.h - 12) };
  return { x: r.x + r.w, y: clamp(partner.y, r.y + 12, r.y + r.h - 12) };
}
function outPoint(p: XY, side: Side, d = 18): XY {
  if (side === 'T') return { x: p.x, y: p.y - d };
  if (side === 'B') return { x: p.x, y: p.y + d };
  if (side === 'L') return { x: p.x - d, y: p.y };
  return { x: p.x + d, y: p.y };
}
function hashLane(id: string): number {
  let h = 0;
  for (let i = 0; i < id.length; i++) h = (h * 31 + id.charCodeAt(i)) >>> 0;
  return h % 5;
}

/** Маршрут одного ребра по текущим прямоугольникам (порты + огибание). Пусто → []. */
export function routeEdge(id: string, source: string, target: string, rects: Map<string, Rect>, edges: EdgeLite[]): XY[] {
  const sR = rects.get(source), tR = rects.get(target);
  if (!sR || !tR) return [];
  const sSide = sideFacing(sR, tR), tSide = sideFacing(tR, sR);
  const ss = slotOf(source, id, sSide, edges, rects), ts = slotOf(target, id, tSide, edges, rects);
  let sPort = portPoint(sR, sSide, ss.i, ss.n);
  let tPort = portPoint(tR, tSide, ts.i, ts.n);
  if (ss.n === 1 && ts.n > 1) sPort = alignPort(sR, sSide, tPort);
  else if (ts.n === 1 && ss.n > 1) tPort = alignPort(tR, tSide, sPort);
  const obstacles: Rect[] = [];
  for (const [nid, r] of rects) if (nid !== source && nid !== target) obstacles.push(r);
  const lane = ss.i + ts.i + hashLane(id);
  return routeOrtho(sPort, outPoint(sPort, sSide), tPort, outPoint(tPort, tSide), obstacles, lane);
}

/** Маршрутизация ВСЕХ рёбер + пучковая развязка наложенных параллельных сегментов. */
export function routeAll(rects: Map<string, Rect>, edges: EdgeLite[]): Map<string, XY[]> {
  const routed = new Map<string, XY[]>();
  for (const e of edges) {
    const p = routeEdge(e.id, e.source, e.target, rects, edges);
    if (p.length >= 2) routed.set(e.id, p);
  }
  nudgeBundles(routed);
  return routed;
}

const BUNDLE_TOL = 4;    // порог «совпадающих» линий
const BUNDLE_MINOV = 10; // мин. перекрытие, чтобы считать наложением
const BUNDLE_GAP = 9;    // разнос полос при развязке

// Развести наложенные параллельные интерьерные сегменты по полосам. Сдвиг сегмента перпендикулярно
// тянет соседние (перпендикулярные) сегменты за общую точку — ортогональность сохраняется, порты не двигаются.
function nudgeBundles(routed: Map<string, XY[]>): void {
  type Seg = { id: string; i: number; horiz: boolean; pos: number; lo: number; hi: number; far: number };
  const segs: Seg[] = [];
  for (const [id, pts] of routed) {
    for (let i = 1; i < pts.length - 2; i++) { // интерьерные (не первый/последний — они у портов)
      const a = pts[i], b = pts[i + 1];
      const far = pts[0].x + pts[0].y + pts[pts.length - 1].x + pts[pts.length - 1].y;
      if (Math.abs(a.y - b.y) < 1 && Math.abs(a.x - b.x) > 1) segs.push({ id, i, horiz: true, pos: a.y, lo: Math.min(a.x, b.x), hi: Math.max(a.x, b.x), far });
      else if (Math.abs(a.x - b.x) < 1 && Math.abs(a.y - b.y) > 1) segs.push({ id, i, horiz: false, pos: a.x, lo: Math.min(a.y, b.y), hi: Math.max(a.y, b.y), far });
    }
  }
  const parent = segs.map((_, i) => i);
  const find = (x: number): number => (parent[x] === x ? x : (parent[x] = find(parent[x])));
  for (let i = 0; i < segs.length; i++) for (let j = i + 1; j < segs.length; j++) {
    const a = segs[i], b = segs[j];
    if (a.horiz !== b.horiz || Math.abs(a.pos - b.pos) > BUNDLE_TOL) continue;
    if (Math.min(a.hi, b.hi) - Math.max(a.lo, b.lo) > BUNDLE_MINOV) parent[find(i)] = find(j);
  }
  const groups = new Map<number, number[]>();
  for (let i = 0; i < segs.length; i++) { const r = find(i); (groups.get(r) ?? groups.set(r, []).get(r)!).push(i); }

  const off = new Map<string, Map<number, XY>>();
  const addOff = (id: string, idx: number, dx: number, dy: number) => {
    let m = off.get(id); if (!m) { m = new Map(); off.set(id, m); }
    const cur = m.get(idx) ?? { x: 0, y: 0 }; cur.x += dx; cur.y += dy; m.set(idx, cur);
  };
  for (const members of groups.values()) {
    if (members.length < 2) continue;
    const ms = members.map((mi) => segs[mi]).sort((a, b) => a.far - b.far || a.id.localeCompare(b.id));
    ms.forEach((s, k) => {
      const d = (k - (ms.length - 1) / 2) * BUNDLE_GAP;
      if (s.horiz) { addOff(s.id, s.i, 0, d); addOff(s.id, s.i + 1, 0, d); }
      else { addOff(s.id, s.i, d, 0); addOff(s.id, s.i + 1, d, 0); }
    });
  }
  for (const [id, m] of off) {
    const pts = routed.get(id)!;
    for (const [idx, d] of m) pts[idx] = { x: pts[idx].x + d.x, y: pts[idx].y + d.y };
  }
}
