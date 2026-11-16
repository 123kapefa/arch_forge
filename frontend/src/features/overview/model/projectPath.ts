// Путь проекта: `?path=` в URL, иначе `af_path` из localStorage (тот же ключ, что у старой карты).

export const PATH_KEY = 'af_path';

export function resolveProjectPath(search: URLSearchParams, read: () => string | null = readStored): string | null {
  const fromUrl = search.get('path')?.trim();
  if (fromUrl) return fromUrl;
  const stored = read()?.trim();
  return stored || null;
}

function readStored(): string | null {
  try {
    return localStorage.getItem(PATH_KEY);
  } catch {
    return null; // приватный режим / запрет доступа
  }
}
