import { Link } from 'react-router';
import styles from './States.module.css';

export function NoPath() {
  return (
    <div className={styles.state}>
      <p className={styles.lead}>Проект не выбран.</p>
      <p className={styles.text}>
        Обзор строится по папке проекта. Откройте проект на карте — выбор папки пока там, — затем вернитесь сюда.
        Либо добавьте <code>?path=&lt;папка проекта&gt;</code> к адресу.
      </p>
      <Link to="/map" className={styles.cta}>Открыть карту и выбрать проект</Link>
    </div>
  );
}

export function Loading() {
  return (
    <div role="status" aria-busy="true" aria-label="Разбираю проект…" className={styles.skeleton}>
      <span className={styles.srOnly}>Разбираю проект…</span>
      <div className={styles.bone} />
      <div className={styles.cols}>
        <div className={styles.bone} />
        <div className={styles.bone} />
      </div>
    </div>
  );
}

export function LoadError({ message, onRetry }: { message: string; onRetry: () => void }) {
  return (
    <div role="alert" className={styles.state}>
      <p className={styles.lead}>Не удалось получить обзор.</p>
      <p className={styles.text}>{message}</p>
      <button type="button" className={styles.cta} onClick={onRetry}>Повторить</button>
    </div>
  );
}

export function EmptyProject() {
  return (
    <div className={styles.state}>
      <p className={styles.lead}>В проекте не найдено ни одного сервиса.</p>
      <p className={styles.text}>
        Анализатор ищет решения .NET и compose-файлы. Вероятно, указана не корневая папка проекта или в ней нет
        .sln и docker-compose.yml. Проверьте путь.
      </p>
      <Link to="/map" className={styles.cta}>Выбрать другой проект</Link>
    </div>
  );
}
