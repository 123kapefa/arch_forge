import styles from './StubScreen.module.css';

interface Props {
  title: string;
  /** Что за экран и откуда макет — одной строкой. */
  description?: string;
  /** Параметры маршрута, показываем для наглядности (например, id проблемы). */
  params?: Record<string, string | undefined>;
}

/** Заглушка экрана, который ещё не сделан. Заменяется реальным экраном отдельным PR. */
export function StubScreen({ title, description, params }: Props) {
  const entries = Object.entries(params ?? {}).filter(([, v]) => v !== undefined);
  return (
    <section className={styles.stub} aria-labelledby="stub-title">
      <h1 id="stub-title" className={styles.title}>{title}</h1>
      <p className={styles.badge}>Экран ещё не сделан</p>
      {description && <p className={styles.description}>{description}</p>}
      {entries.length > 0 && (
        <dl className={styles.params}>
          {entries.map(([k, v]) => (
            <div key={k}><dt>{k}</dt><dd>{v}</dd></div>
          ))}
        </dl>
      )}
    </section>
  );
}
