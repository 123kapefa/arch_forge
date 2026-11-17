import { useId, type ReactNode } from 'react';
import styles from './Card.module.css';

interface Props {
  title: string;
  /** Подзаголовок справа от названия (например, счётчик). */
  aside?: ReactNode;
  children: ReactNode;
}

/** Карточка-блок с заголовком h2; регион подписан заголовком (aria-labelledby). */
export function Card({ title, aside, children }: Props) {
  const id = useId();
  return (
    <section className={styles.card} aria-labelledby={id}>
      <header className={styles.head}>
        <h2 id={id} className={styles.title}>{title}</h2>
        {aside && <span className={styles.aside}>{aside}</span>}
      </header>
      <div className={styles.body}>{children}</div>
    </section>
  );
}
