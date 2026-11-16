import { Link } from 'react-router';
import { SCREENS } from '../../shared/lib/screens';
import styles from './StartScreen.module.css';

/** Старт: пока — оглавление экранов. Выбор папки и ход разбора появятся отдельным PR. */
export function StartScreen() {
  return (
    <section className={styles.start} aria-labelledby="start-title">
      <h1 id="start-title" className={styles.title}>Открыть проект</h1>
      <p className={styles.note}>Выбор папки и ход разбора ещё не сделаны. Пока можно перейти к экранам:</p>
      <ul className={styles.list}>
        {SCREENS.filter((s) => s.path !== '/').map((s) => (
          <li key={s.path}><Link to={s.example}>{s.title}</Link></li>
        ))}
      </ul>
    </section>
  );
}
