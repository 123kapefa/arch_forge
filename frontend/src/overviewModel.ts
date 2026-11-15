// Чистая логика E8: стартовый обзор (триаж) и индекс поиска ⌘K над системным графом. Без React/DOM.

import type { Problem, ProblemItem, SystemGraph, SystemNode } from './system';
import type { GraphDto } from './api';
import { categoryOf, ownerByRoot } from './systemModel';

const SEV: Record<string, number> = { critical: 0, warning: 1, coverage: 2, info: 3 };
const HEALTH: Record<string, number> = { critical: 0, warning: 1, ok: 2 };
const sevRank = (s: string) => (Object.hasOwn(SEV, s) ? SEV[s] : 3);
const healthRank = (h?: string) => (h && Object.hasOwn(HEALTH, h) ? HEALTH[h] : 2);

export interface ServiceTriage {
  node: SystemNode;
  critical: number;
  warning: number;
  total: number;
}

/** Сервисы по убыванию «боли»: здоровье узла, затем critical/warning по проблемам, привязанным к узлу или к файлам его root. */
export function worstServices(graph: SystemGraph, limit = 5): ServiceTriage[] {
  const tally = new Map<string, { critical: number; warning: number; total: number }>();
  const bump = (id: string, sev: string, n: number) => {
    const t = tally.get(id) ?? { critical: 0, warning: 0, total: 0 };
    if (sev === 'critical') t.critical += n;
    else if (sev === 'warning') t.warning += n;
    t.total += n;
    tally.set(id, t);
  };
  const ids = new Set(graph.nodes.map((n) => n.id));
  for (const p of graph.problems ?? []) {
    if (p.severity === 'coverage' || p.severity === 'info') continue; // не «боль»
    const items = p.items ?? [];
    if (items.length > 0 && categoryOf(p) === 'convention') {
      for (const it of items) {
        const svc = ownerByRoot(graph.nodes, it.file);
        if (svc) bump(svc.id, p.severity, 1);
      }
    } else if (ids.has(p.location)) {
      bump(p.location, p.severity, Math.max(1, items.length));
    }
  }
  return graph.nodes
    .filter((n) => n.kind === 'service')
    .map((node) => ({ node, ...(tally.get(node.id) ?? { critical: 0, warning: 0, total: 0 }) }))
    .filter((s) => s.total > 0 || healthRank(s.node.health) < 2)
    .sort((a, b) =>
      healthRank(a.node.health) - healthRank(b.node.health) ||
      b.critical - a.critical || b.warning - a.warning ||
      a.node.name.localeCompare(b.node.name))
    .slice(0, limit);
}

/** Топ проблем: сперва архитектурные (кросс-сервисные), по суровости; дрейф от свода — после. */
export function topProblems(graph: SystemGraph, limit = 6): Problem[] {
  return [...(graph.problems ?? [])]
    .filter((p) => p.severity === 'critical' || p.severity === 'warning')
    .sort((a, b) =>
      (categoryOf(a) === 'convention' ? 1 : 0) - (categoryOf(b) === 'convention' ? 1 : 0) ||
      sevRank(a.severity) - sevRank(b.severity))
    .slice(0, limit);
}

export interface CoverageShare { marker: number; heuristic: number; unknown: number; total: number }

/** Покрытие анализа в процентах (по типам). total=0 → нули. */
export function coverageShare(graph: SystemGraph): CoverageShare {
  const c = graph.coverage;
  // total — число типов; без разбивки по источнику (структурный скан: все три = 0) процентов считать не из чего
  const split = (c?.marker ?? 0) + (c?.heuristic ?? 0) + (c?.unknown ?? 0);
  const total = split > 0 ? c?.total ?? 0 : 0;
  const pct = (n: number) => (total > 0 ? Math.round((n / total) * 100) : 0);
  return { marker: pct(c?.marker ?? 0), heuristic: pct(c?.heuristic ?? 0), unknown: pct(c?.unknown ?? 0), total };
}

// ---------- Поиск ⌘K ----------

export type SearchKind = 'service' | 'datastore' | 'topic' | 'external' | 'endpoint' | 'type';

export interface SearchEntry {
  key: string;
  kind: SearchKind;
  label: string;        // что ищем и показываем
  sub?: string;         // уточнение (сервис / слой / файл)
  nodeId: string;       // узел системного графа, к которому ведёт результат
  item?: ProblemItem;   // для типа с проблемой — факт (переход «к плашке»)
  problem?: Problem;
}

/** Индекс: узлы системы, эндпоинты сервисов, типы из фактов проблем и (если сервис раскрыт) типы его карты. */
export function buildSearchIndex(graph: SystemGraph, l1Map?: GraphDto | null, drillId?: string | null): SearchEntry[] {
  const out: SearchEntry[] = [];
  for (const n of graph.nodes) {
    const kind = (['service', 'datastore', 'topic', 'external'].includes(n.kind) ? n.kind : 'external') as SearchKind;
    out.push({ key: `n:${n.id}`, kind, label: n.name, sub: n.kind, nodeId: n.id });
    for (const e of n.endpoints ?? []) {
      out.push({ key: `e:${n.id}:${e.method}:${e.route}:${e.line ?? ''}`, kind: 'endpoint', label: `${e.method} ${e.route || '/'}`, sub: `${n.name}${e.type ? ` · ${e.type}` : ''}`, nodeId: n.id });
    }
  }
  const seen = new Set<string>();
  for (const p of graph.problems ?? []) {
    for (const it of p.items ?? []) {
      if (!it.file) continue;
      const svc = ownerByRoot(graph.nodes, it.file);
      if (!svc) continue;
      const key = `t:${svc.id}:${it.name}:${it.file}`;
      if (seen.has(key)) continue;
      seen.add(key);
      out.push({ key, kind: 'type', label: it.name, sub: `${svc.name} · ${it.layer}`, nodeId: svc.id, item: it, problem: p });
    }
  }
  if (l1Map && drillId) {
    const svc = graph.nodes.find((n) => n.id === drillId);
    for (const t of l1Map.nodes) {
      const key = `t:${drillId}:${t.name}:${t.file ?? ''}`;
      if (seen.has(key)) continue;
      seen.add(key);
      out.push({ key, kind: 'type', label: t.name, sub: `${svc?.name ?? ''} · ${t.layer}`, nodeId: drillId, item: { name: t.name, layer: t.layer, note: '', file: t.file ?? undefined } });
    }
  }
  return out;
}

const KIND_ORDER: Record<string, number> = { service: 0, datastore: 1, topic: 1, external: 1, type: 2, endpoint: 3 };

/** Нечувствительный к регистру поиск: префикс слова > вхождение > по sub. Пустой запрос → сервисы/хранилища. */
export function searchEntries(index: SearchEntry[], query: string, limit = 30): SearchEntry[] {
  const q = query.trim().toLowerCase();
  if (!q) return index.filter((e) => e.kind === 'service' || e.kind === 'datastore' || e.kind === 'topic').slice(0, limit);
  const scored: { e: SearchEntry; s: number }[] = [];
  for (const e of index) {
    const l = e.label.toLowerCase();
    let s = -1;
    if (l === q) s = 0;
    else if (l.startsWith(q)) s = 1;
    else if (l.split(/[\s/_.\-{}]+/).some((w) => w.startsWith(q))) s = 2;
    else if (l.includes(q)) s = 3;
    else if (e.sub?.toLowerCase().includes(q)) s = 4;
    if (s >= 0) scored.push({ e, s });
  }
  return scored
    .sort((a, b) => a.s - b.s || (KIND_ORDER[a.e.kind] ?? 9) - (KIND_ORDER[b.e.kind] ?? 9) || a.e.label.localeCompare(b.e.label))
    .slice(0, limit)
    .map((x) => x.e);
}
