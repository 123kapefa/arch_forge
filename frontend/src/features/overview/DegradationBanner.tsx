import styles from './DegradationBanner.module.css';

/** Баннер при частичном разборе: «чисто ≠ проблем нет», с перечнем деградаций. */
export function DegradationBanner({ degradations }: { degradations: string[] }) {
  return (
    <div className={styles.banner} role="status">
      <strong>Разбор неполный: отсутствие находок не значит, что проблем нет.</strong>
      <ul className={styles.list}>
        {degradations.map((d) => <li key={d}>{d}</li>)}
      </ul>
    </div>
  );
}
