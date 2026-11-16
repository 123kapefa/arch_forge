// Паспорт системы: что за система по числам. Без React/DOM.

import type { SystemGraph } from '../../../shared/api';

export interface Passport {
  services: number;
  datastores: number;
  topics: number;
  externals: number;
  endpoints: number;
  edges: number;
  /** Имена слоёв по всем сервисам (уникальные, в порядке появления). */
  layers: string[];
}

export function passportOf(g: SystemGraph): Passport {
  const count = (kind: string) => g.nodes.filter((n) => n.kind === kind).length;
  const known = ['service', 'datastore', 'topic'];
  const layers = new Set<string>();
  for (const s of g.services ?? []) for (const l of s.layers) layers.add(l.name);
  return {
    services: count('service'),
    datastores: count('datastore'),
    topics: count('topic'),
    // всё незнакомое (в т.ч. external) честно относим к внешним
    externals: g.nodes.filter((n) => !known.includes(n.kind)).length,
    endpoints: g.nodes.reduce((sum, n) => sum + (n.endpoints?.length ?? 0), 0),
    edges: g.edges.length,
    layers: [...layers],
  };
}

export const isEmptyProject = (g: SystemGraph) => g.nodes.length === 0;
