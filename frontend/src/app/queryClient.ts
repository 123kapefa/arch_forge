import { QueryClient } from '@tanstack/react-query';
import { ApiError } from '../shared/api';

/** Ошибки 4xx повторять бессмысленно (неверный путь/параметры); сетевые и 5xx — один повтор. */
function retry(failureCount: number, error: unknown): boolean {
  if (error instanceof ApiError && error.status >= 400 && error.status < 500) return false;
  return failureCount < 1;
}

export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 60_000, // разбор тяжёлый, меняется только по явному «пере-анализировать»
        gcTime: 10 * 60_000,
        refetchOnWindowFocus: false,
        retry,
      },
    },
  });
}
