// Цвет слоя: курируем известные, остальным — стабильный цвет из строки.
const KNOWN: Record<string, string> = {
  domain: '#e0b341',
  application: '#4aa3df',
  contracts: '#9b8cff',
  infrastructure: '#e5734d',
  persistence: '#e5734d',
  api: '#57c78a',
  web: '#57c78a',
  shared: '#8a94a6',
  sharedkernel: '#8a94a6',
};

export function layerColor(layer: string): string {
  const key = layer.toLowerCase();
  if (KNOWN[key]) return KNOWN[key];
  let h = 0;
  for (const ch of layer) h = (h * 31 + ch.charCodeAt(0)) % 360;
  return `hsl(${h} 55% 60%)`;
}

// Цвет репозитория (.sln) — «цвет плашки = тип проекта» (прототип): микросервисы одного репо
// красятся одинаково. Палитра по прототипу + стабильный хеш для остальных.
const REPO_PALETTE = ['#6c8cff', '#a87bff', '#4fb6c6', '#e0b24a', '#e57ba0', '#5fc6b0', '#c98cff'];
export function repoColor(namespace: string): string {
  let h = 0;
  for (const ch of namespace) h = (h * 31 + ch.charCodeAt(0)) >>> 0;
  return REPO_PALETTE[h % REPO_PALETTE.length];
}
