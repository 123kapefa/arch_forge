import { useSearchParams } from 'react-router';
import { useSystem } from './useSystem';
import { resolveProjectPath } from './model';

/** Путь проекта (?path= → localStorage) + загрузка системы с код-линзами: находки и покрытие приходят только с ними. */
export function useOverview() {
  const [search] = useSearchParams();
  const path = resolveProjectPath(search);
  const system = useSystem(path ? { path, drift: true, layers: true, calls: true } : null);
  const query = path ? `?path=${encodeURIComponent(path)}` : '';
  return { path, query, system };
}
