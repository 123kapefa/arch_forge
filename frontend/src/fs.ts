// Обзор папок на диске сервера (`GET /api/fs`, через Map) — для пикера «Выбрать папку».
import { fetchWithTimeout } from './http';

export interface FsEntry {
  name: string;
  path: string;
  project: boolean; // похоже на корень системы/сервиса (.sln / compose / src)
}
export interface FsListing {
  path: string;
  parent: string | null; // null → выше некуда (корень диска / список дисков)
  entries: FsEntry[];
  drives: string[];      // при path=/ на Windows — список дисков (C:/, D:/…)
}

const TIMEOUT_MS = 15_000;

/** Текущая git-ветка открытого проекта (`GET /api/git`) — для статус-бара. null → нет репо/detached. */
export async function fetchGitBranch(path: string): Promise<string | null> {
  try {
    const res = await fetchWithTimeout(`/api/git?path=${encodeURIComponent(path)}`, 8000);
    if (!res.ok) return null;
    const d = (await res.json()) as { branch?: string | null };
    return d.branch ?? null;
  } catch {
    return null;
  }
}

export async function browseFs(path?: string): Promise<FsListing> {
  const q = path ? `?path=${encodeURIComponent(path)}` : '';
  const res = await fetchWithTimeout(`/api/fs${q}`, TIMEOUT_MS);
  if (!res.ok) {
    const body = await res.text();
    let message = body;
    try {
      const parsed = JSON.parse(body) as { error?: string };
      if (parsed?.error) message = parsed.error;
    } catch {
      /* тело не JSON */
    }
    throw new Error(message || `Ошибка обзора папок (${res.status})`);
  }
  return res.json() as Promise<FsListing>;
}
