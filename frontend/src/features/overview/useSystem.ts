import { useQuery } from '@tanstack/react-query';
import { getJson } from '../../shared/api';
import type { SystemGraph, SystemQuery } from '../../shared/api';

/** Ключ кэша: параметры запроса целиком — разные линзы дают разные ответы. */
export const systemKey = (q: SystemQuery) => ['system', q] as const;

/** GET /api/system. Без `path` запрос не идёт (enabled=false). Отмену передаёт TanStack Query. */
export function useSystem(q: SystemQuery | null) {
  return useQuery({
    queryKey: q ? systemKey(q) : ['system', null],
    queryFn: ({ signal }) => {
      const { path, ...flags } = q!;
      return getJson<SystemGraph>('/system', { signal, query: { path, ...flags } });
    },
    enabled: q !== null && q.path.length > 0,
  });
}
