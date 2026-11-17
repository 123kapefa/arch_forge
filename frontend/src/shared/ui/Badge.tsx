import type { ReactNode } from 'react';
import styles from './Badge.module.css';

export type BadgeTone = 'error' | 'warning' | 'ok' | 'unknown' | 'accent';

interface Props {
  tone: BadgeTone;
  /** Значок: статус не должен передаваться только цветом. */
  icon?: string;
  children: ReactNode;
}

/** Метка статуса: цвет + значок + текст. */
export function Badge({ tone, icon, children }: Props) {
  return (
    <span className={`${styles.badge} ${styles[tone]}`}>
      {icon && <span aria-hidden="true">{icon}</span>}
      {children}
    </span>
  );
}
