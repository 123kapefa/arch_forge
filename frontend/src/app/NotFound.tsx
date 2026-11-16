import { Link } from 'react-router';
import styles from './Fallback.module.css';

export function NotFound() {
  return (
    <section className={styles.box} aria-labelledby="nf-title">
      <h1 id="nf-title" className={styles.title}>Страница не найдена</h1>
      <p>Такого адреса нет. <Link to="/">Вернуться на старт</Link></p>
    </section>
  );
}
