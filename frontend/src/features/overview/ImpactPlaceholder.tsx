import { Card } from '../../shared/ui';
import styles from './States.module.css';

/** «Менять осторожно»: данных о радиусе взрыва в API пока нет — честная заглушка, ничего не выдумываем. */
export function ImpactPlaceholder() {
  return (
    <Card title="Менять осторожно">
      <p className={styles.text}>
        Какие типы заденет правка — появится после экрана «Влияние». Сейчас бэк этих данных не отдаёт.
      </p>
    </Card>
  );
}
