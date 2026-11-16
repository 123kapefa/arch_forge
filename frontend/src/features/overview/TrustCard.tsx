import { Card } from '../../shared/ui';
import { pctLabel, type CoverageShare, type Trust } from './model';
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

const legend = (s: CoverageShare) =>
  `точно (по маркеру) ${pctLabel(s, 'marker')} · эвристика ${pctLabel(s, 'heuristic')} · неизвестно ${pctLabel(s, 'unknown')}`;

/** «Насколько верить»: доли покрытия разбора и деградации; «чисто» не выдаётся за «проблем нет». */
export function TrustCard({ trust }: { trust: Trust }) {
  const { overall, rows, degradations } = trust;
  return (
    <Card title="Насколько верить">
      {overall ? (
        <>
          <Bar marker={overall.marker} heuristic={overall.heuristic} unknown={overall.unknown} label={legend(overall)} />
          <p className={styles.legend}>{legend(overall)} <span className={styles.muted}>(из {overall.sum} типов с известным источником)</span></p>
          {overall.mismatch && (
            <p className={styles.legend}>Бэк сообщил {overall.total} типов, а с разбивкой по источнику — {overall.sum}: доли считаны от {overall.sum}.</p>
          )}
          {trust.servicesCovered && (
            <p className={styles.legend}>
              Покрытие — по разобранным сервисам: {trust.servicesCovered.covered} из {trust.servicesCovered.total}. Неразобранные в доли не входят.
            </p>
          )}
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
              <Bar marker={r.marker} heuristic={r.heuristic} unknown={r.unknown} label={`${r.name}: ${legend(r)}`} />
              <span className={styles.unk}>неизв. {pctLabel(r, 'unknown')}</span>
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
      <p className={styles.note}>«Чисто» не значит «проблем нет»: проверено только то, что удалось разобрать, и оценка относится к разобранным сервисам.</p>
    </Card>
  );
}
