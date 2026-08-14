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
