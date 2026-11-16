import { Card } from '../../shared/ui';
import type { Trust } from './model';
import styles from './TrustCard.module.css';

function Bar({ marker, heuristic, unknown, label }: { marker: number; heuristic: number; unknown: number; label: string }) {
  return (
    <div className={styles.bar} role="img" aria-label={label}>
      <span className={styles.m} style={{ width: `${marker}%` }} />
      <span className={styles.h} style={{ width: `${heuristic}%` }} />
      <span className={styles.u} style={{ width: `${unknown}%` }} />
    </div>
  );
}

const legend = (s: { marker: number; heuristic: number; unknown: number }) =>
  `точно (по маркеру) ${s.marker}% · эвристика ${s.heuristic}% · неизвестно ${s.unknown}%`;

/** «Насколько верить»: доли покрытия разбора и деградации; «чисто» не выдаётся за «проблем нет». */
export function TrustCard({ trust }: { trust: Trust }) {
  const { overall, rows, degradations } = trust;
  return (
    <Card title="Насколько верить">
      {overall ? (
        <>
          <Bar {...overall} label={legend(overall)} />
          <p className={styles.legend}>{legend(overall)} <span className={styles.muted}>(из {overall.total} типов)</span></p>
        </>
      ) : (
        <p className={styles.legend}>
          Доли покрытия неизвестны: роли типов не разбирались кодом (анализ кода выключен или это структурный скан).
        </p>
      )}
      {rows.length > 1 && (
        <ul className={styles.rows} aria-label="Покрытие по сервисам">
          {rows.map((r) => (
            <li key={r.id} className={styles.row}>
              <span className={styles.name}>{r.name}</span>
              <Bar {...r} label={`${r.name}: ${legend(r)}`} />
              <span className={styles.unk}>неизв. {r.unknown}%</span>
            </li>
          ))}
        </ul>
      )}
      {degradations.length > 0 ? (
        <>
          <h3 className={styles.sub}>Что не разобралось</h3>
          <ul className={styles.degr}>
            {degradations.map((d) => <li key={d}>{d}</li>)}
          </ul>
        </>
      ) : (
        <p className={styles.legend}>Бэк не сообщил о пропусках при разборе.</p>
      )}
      <p className={styles.note}>«Чисто» не значит «проблем нет»: проверено только то, что удалось разобрать.</p>
    </Card>
  );
}
