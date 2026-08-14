// Контракт IR — зеркалит ArchForge.Contracts (System.Text.Json Web = camelCase).

export interface GraphDto {
  project: string;
  nodes: NodeDto[];
  edges: EdgeDto[];
  coverage: CoverageDto;
  drift: string[];
}

export interface NodeDto {
  id: string;
  name: string;
  kind: string;   // class | record | interface | enum | struct
  layer: string;  // Domain | Application | Api | ...
  role: string;   // стереотип: Aggregate | Handler | Endpoint | ...
  via: string;    // marker | heuristic | unknown
  file: string;
  drift: string[];
}

export interface EdgeDto {
  id: string;
  source: string;
  target: string;
  kind: string;   // implements | holds | uses | ...
}

export interface CoverageDto {
  marker: number;
  heuristic: number;
  unknown: number;
  total: number;
}

export interface MapQuery {
  path: string;
  layer?: string;
  focus?: string;
}

/** GET /api/map (через сервис Map). */
export async function fetchMap(q: MapQuery): Promise<GraphDto> {
  const params = new URLSearchParams({ path: q.path });
  if (q.layer) params.set('layer', q.layer);
  if (q.focus) params.set('focus', q.focus);

  const res = await fetch(`/api/map?${params.toString()}`);
  if (!res.ok) {
    throw new Error(`Map ${res.status}: ${await res.text()}`);
  }
  return res.json() as Promise<GraphDto>;
}
