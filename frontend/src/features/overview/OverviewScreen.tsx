import { isEmptyProject } from './model';
import { OverviewContent } from './OverviewContent';
import { EmptyProject, Loading, LoadError, NoPath } from './States';
import { useOverview } from './useOverview';
import styles from './OverviewScreen.module.css';

/** Обзор: ответы, а не карта. Три вопроса — что не так, что это, насколько верить. */
export function OverviewScreen() {
  const { path, query, system } = useOverview();
  const g = system.data;
  return (
    <div className={styles.screen}>
      <header className={styles.head}>
        <h1 className={styles.title}>Обзор</h1>
        {g && <p className={styles.sub}>{g.project} · {g.nodes.length} узлов · {g.edges.length} связей</p>}
      </header>
      {!path && <NoPath />}
      {path && system.isPending && <Loading />}
      {path && system.isError && <LoadError message={system.error.message} onRetry={() => void system.refetch()} />}
      {g && (isEmptyProject(g) ? <EmptyProject /> : <OverviewContent graph={g} query={query} root={path} />)}
    </div>
  );
}
