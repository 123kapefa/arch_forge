// Типы ответов описаны ВРУЧНУЮ по backend/shared/ArchForge.Contracts (System.Text.Json Web = camelCase).
// Бэкенд пока не отдаёт OpenAPI, поэтому `npm run api:types` недоступен — это технический долг (adr/0016).
// Описаны только поля, которые нужны новым экранам; расширяем по мере появления экранов.

export type Health = 'ok' | 'warning' | 'critical' | string;

export interface SystemNode {
  id: string;
  name: string;
  /** service | datastore | topic | external */
  kind: string;
}

export interface SystemEdge {
  id: string;
  source: string;
  target: string;
  kind: string;
}

export interface SystemCoverage {
  [key: string]: unknown;
}

export interface Problem {
  id?: string;
  severity: 'critical' | 'warning' | 'coverage' | 'info' | string;
  category?: 'architecture' | 'convention' | string;
  title?: string;
}

export interface SystemGraph {
  project: string;
  nodes: SystemNode[];
  edges: SystemEdge[];
  coverage: SystemCoverage;
  drift: string[];
  health?: Health;
  problems?: Problem[];
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
