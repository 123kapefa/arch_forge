import { isRouteErrorResponse, Link, useRouteError } from 'react-router';
import styles from './Fallback.module.css';

/** Error boundary маршрута: падение экрана не роняет оболочку целиком. */
export function RouteError() {
  const error = useRouteError();
  const message = isRouteErrorResponse(error)
    ? `${error.status} ${error.statusText}`
    : error instanceof Error ? error.message : 'Неизвестная ошибка';
  return (
    <section className={styles.box} role="alert" aria-labelledby="re-title">
      <h1 id="re-title" className={styles.title}>Что-то пошло не так</h1>
      <p className={styles.detail}>{message}</p>
      <p><Link to="/">Вернуться на старт</Link></p>
    </section>
  );
}
