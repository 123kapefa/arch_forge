// Типы ответов описаны ВРУЧНУЮ по backend/shared/ArchForge.Contracts (System.Text.Json Web = camelCase).
// Бэкенд пока не отдаёт OpenAPI, поэтому `npm run api:types` недоступен — это технический долг (adr/0016).
// Описаны только поля, которые нужны новым экранам; расширяем по мере появления экранов.

export type Health = 'ok' | 'warning' | 'critical' | string;

export interface SystemEndpoint {
  method: string;
  route: string;
  source: string;
  type?: string;
  line?: number;
}

export interface SystemNode {
  id: string;
  name: string;
  /** service | datastore | topic | external (набор открыт) */
  kind: string;
  /** ok | warning | critical — по самой суровой проблеме на узле */
  health?: Health;
  endpoints?: SystemEndpoint[];
}

export interface SystemEdge {
  id: string;
  source: string;
  target: string;
  kind: string;
  /** compose | config | code | unknown — откуда известна связь */
  via?: string;
  /** 0..1 */
  confidence?: number;
  violation?: boolean;
}

/** Роли типов: по маркеру (точно), по эвристике, неизвестно. */
export interface SystemCoverage {
  marker: number;
  heuristic: number;
  unknown: number;
  total: number;
}

export interface ProblemItem {
  name: string;
  layer: string;
  note: string;
  file?: string;
  line?: number;
}

export interface Problem {
  id: string;
  /** critical | warning | coverage | info */
  severity: string;
  /** shared-db | service-cycle | layer-violation | drift | … */
  kind: string;
  title: string;
  /** id узла/ребра или namespace-префикс */
  location: string;
  /** Источник знания: compose | config | code | … */
  via?: string;
  fix?: string;
  /** architecture | convention (дрейф от свода); нет поля — architecture */
  category?: string;
  members?: string[];
  /** 0..1; нет поля — не применимо */
  confidence?: number;
  items?: ProblemItem[];
}

export interface LayerSummary {
  name: string;
  health: Health;
  types: number;
  note?: string;
}

export interface ServiceSummary {
  id: string;
  name: string;
  health: Health;
  layers: LayerSummary[];
  /** Нет у структурного скана и у старых бэков. */
  coverage?: SystemCoverage;
}

export interface SystemGraph {
  project: string;
  nodes: SystemNode[];
  edges: SystemEdge[];
  coverage: SystemCoverage;
  drift: string[];
  health?: Health;
  problems?: Problem[];
  services?: ServiceSummary[];
  /** Что пропущено при разборе — «карта неполная». */
  degradations?: string[];
}

export interface SystemQuery {
  path: string;
  drift?: boolean;
  layers?: boolean;
  env?: string;
  calls?: boolean;
  refresh?: boolean;
}
