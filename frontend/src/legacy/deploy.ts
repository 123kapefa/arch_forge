// Чистая логика видов Deploy и «Сравнение сред» (E3.2/E3.3, #89). Без React/DOM — тестируемо.
// Данные — из контракта системного графа (SystemNode.deploy + рёбра via=compose); бэкенд не трогаем.

import type { SystemGraph, SystemNode } from './system';
import { isInfra, portExposure, portLabel, type PortExposure } from './systemFlow';

// ---------- Порядок старта ----------

/**
 * Ярус по `depends_on` на ХРАНИЛИЩА (бэкенд отдаёт такие compose-рёбра; сервис→сервис нет): 0 — стартует первым,
 * 1 — после него и т.д. Берутся рёбра via=compose (source зависит от target). Узлы без compose-зависимостей и без зависимых в карту не попадают.
 * Циклы не зависают: глубина считается итеративно с ограничением по числу узлов.
 */
export function startTiers(g: SystemGraph): Map<string, number> {
  const deps = new Map<string, Set<string>>(); // узел → от чего зависит
  const involved = new Set<string>();
  for (const e of g.edges) {
    if (e.via !== 'compose' || e.source === e.target) continue;
    (deps.get(e.source) ?? deps.set(e.source, new Set()).get(e.source)!).add(e.target);
    involved.add(e.source); involved.add(e.target);
  }
  const tier = new Map<string, number>();
  for (const id of involved) tier.set(id, 0);
  // релаксация: ярус = 1 + макс. ярус зависимостей; не более N проходов (защита от цикла)
  for (let pass = 0; pass < involved.size; pass++) {
    let changed = false;
    for (const [id, ds] of deps) {
      for (const d of ds) {
        const want = (tier.get(d) ?? 0) + 1;
        if (want > (tier.get(id) ?? 0) && want <= involved.size) { tier.set(id, want); changed = true; }
      }
    }
    if (!changed) break;
  }
  return tier;
}

// ---------- Сравнение сред ----------

export interface CellPort { label: string; exposure: PortExposure }

export interface EnvCell {
  present: boolean;            // узел есть в этой среде
  ports: CellPort[];           // порты с классификацией (красим конкретный порт, не всю ячейку)
  exposed: boolean;            // у хранилища хоть один порт торчит на все интерфейсы
  published: number;           // сколько портов опубликовано наружу
  healthcheck: boolean | null; // null — узла нет / нет deploy-данных
  networks: string[];
}

export interface CompareRow {
  id: string;
  name: string;
  kind: string;
  cells: Record<string, EnvCell>; // по имени среды
  changed: string[];              // что отличается между средами: ports | healthcheck | networks | presence
  breaks: string[];               // что «ломается» при переходе в prod (человекочитаемо)
  warns: string[];                // предупреждения (не поломка): напр. порты не опубликованы в prod
}

export interface EnvGraph { name: string; graph: SystemGraph }

const EMPTY: EnvCell = { present: false, ports: [], exposed: false, published: 0, healthcheck: null, networks: [] };

function cellOf(n: SystemNode | undefined): EnvCell {
  if (!n) return EMPTY;
  const d = n.deploy;
  const ports = d?.ports ?? [];
  const exposed = ports.some((p) => portExposure(p, n.kind) === 'exposed');
  return {
    present: true,
    ports: ports.map((p) => ({ label: portLabel(p), exposure: portExposure(p, n.kind) })),
    exposed,
    published: ports.filter((p) => p.published).length,
    healthcheck: d ? d.hasHealthcheck : null,
    networks: d?.networks ?? [],
  };
}

const sameSet = (a: string[], b: string[]) => a.length === b.length && [...a].sort().join('\0') === [...b].sort().join('\0');
const labels = (c: EnvCell) => c.ports.map((p) => p.label);

/** Боевая среда для «ломается в prod»: только среда с именем `prod`/`production`. Нет такой → null (не гадаем по порядку). */
export function prodEnvOf(names: string[]): string | null {
  return names.find((n) => /^prod(uction)?$/i.test(n)) ?? null;
}

/** Приоритет строки при сортировке: 0 — ломается, 1 — предупреждение/меняется, 2 — без отличий. */
export const rankRow = (r: CompareRow) => (r.breaks.length ? 0 : r.warns.length || r.changed.length ? 1 : 2);

/** Строки таблицы сравнения: только сервисы и инфра (external/shared не деплоятся — пропускаем). */
export function compareEnvs(envs: EnvGraph[]): CompareRow[] {
  if (envs.length === 0) return [];
  const byEnv = new Map(envs.map((e) => [e.name, new Map(e.graph.nodes.map((n) => [n.id, n]))]));
  const ids = new Map<string, SystemNode>();
  for (const e of envs) for (const n of e.graph.nodes) {
    if (n.kind === 'external' || n.kind === 'shared') continue;
    if (!ids.has(n.id)) ids.set(n.id, n);
  }
  const names = envs.map((e) => e.name);
  const prod = prodEnvOf(names);
  const rows: CompareRow[] = [];
  for (const [id, first] of ids) {
    const cells: Record<string, EnvCell> = {};
    for (const nm of names) cells[nm] = cellOf(byEnv.get(nm)?.get(id));
    const list = names.map((nm) => cells[nm]);
    const present = list.filter((c) => c.present);
    // у «чистого» кода-сервиса без compose-данных (все deploy пустые) сравнивать нечего
    const hasAny = present.some((c) => c.ports.length > 0 || c.networks.length > 0 || c.healthcheck !== null);
    if (!hasAny && present.length === list.length) continue;

    const changed: string[] = [];
    if (present.length !== list.length) changed.push('presence');
    if (present.some((c) => !sameSet(labels(c), labels(present[0])))) changed.push('ports');
    if (present.some((c) => c.healthcheck !== present[0].healthcheck)) changed.push('healthcheck');
    if (present.some((c) => !sameSet(c.networks, present[0].networks))) changed.push('networks');

    const breaks: string[] = [];
    const warns: string[] = [];
    if (prod) {
      const p = cells[prod];
      const allOthers = names.filter((nm) => nm !== prod).map((nm) => cells[nm]);
      const others = allOthers.filter((c) => c.present);
      // пропал в prod, хотя есть во ВСЕХ остальных средах (dev-only инструменты вроде mailhog — норма)
      if (!p.present && allOthers.length > 0 && allOthers.every((c) => c.present)) breaks.push('нет в ' + prod);
      if (p.present) {
        // «ломается» — только то, что появляется при переходе в prod; наружу во ВСЕХ средах — зона детектора exposed-datastore
        if (p.exposed && !others.some((c) => c.exposed)) breaks.push(`порт хранилища наружу в ${prod}`);
        if (p.healthcheck === false && others.some((c) => c.healthcheck === true)) breaks.push(`потерян healthcheck в ${prod}`);
        if (first.kind !== 'datastore' && p.published === 0 && others.some((c) => c.published > 0)) warns.push(`порты не опубликованы в ${prod}`);
      }
    }
    rows.push({ id, name: first.name, kind: first.kind, cells, changed, breaks, warns });
  }
  // сначала «ломающиеся», затем «меняющиеся», затем остальные; внутри — сервисы раньше инфры, по имени
    return rows.sort((a, b) => rankRow(a) - rankRow(b) || Number(isInfra(a.kind)) - Number(isInfra(b.kind)) || a.name.localeCompare(b.name));
}

export type { PortExposure };
