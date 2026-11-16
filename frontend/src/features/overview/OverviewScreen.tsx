import { useSearchParams } from 'react-router';
import { StubScreen } from '../../shared/ui';
import { useSystem } from './useSystem';

/** Заглушка + пример использования useSystem: `/overview?path=<папка проекта>` подтягивает граф. */
export function OverviewScreen() {
  const [search] = useSearchParams();
  const path = search.get('path');
  const system = useSystem(path ? { path } : null);

  return (
    <>
      <StubScreen title="Обзор" description="Паспорт системы: сервисы, хранилища, находки, покрытие." />
      {path && (
        <p style={{ textAlign: 'center' }} role="status">
          {system.isPending && 'Разбираю проект…'}
          {system.isError && `Не удалось получить граф: ${system.error.message}`}
          {system.data && `${system.data.project}: узлов ${system.data.nodes.length}, связей ${system.data.edges.length}`}
        </p>
      )}
    </>
  );
}
