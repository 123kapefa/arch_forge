import { Card } from '../../shared/ui';
import type { SystemGraph } from '../../shared/api';
import { passportOf } from './model';
import { MiniMapView } from './MiniMapView';
import styles from './PassportCard.module.css';

/** «Что это»: паспорт системы числами + мини-карта. */
export function PassportCard({ graph, query }: { graph: SystemGraph; query: string }) {
  const p = passportOf(graph);
  const rows: Array<[string, number]> = [
    ['Сервисы', p.services],
    ['Хранилища', p.datastores],
    ['Шины/очереди', p.topics],
    ['Внешние системы', p.externals],
    ['Endpoint-ы', p.endpoints],
    ['Связи', p.edges],
  ];
  return (
    <Card title="Что это">
      <MiniMapView graph={graph} query={query} />
      <dl className={styles.grid}>
        {rows.map(([name, n]) => (
          <div key={name} className={styles.cell}>
            <dt>{name}</dt>
            <dd>{n}</dd>
          </div>
        ))}
      </dl>
      <p className={styles.layers}>
        Слои: {p.layers.length > 0 ? p.layers.join(' · ') : 'не определены (разбор слоёв не выполнялся)'}
      </p>
    </Card>
  );
}
