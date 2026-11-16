import { useState } from 'react';
import { Link } from 'react-router';
import { Badge, Card } from '../../shared/ui';
import type { Problem } from '../../shared/api';
import { evidenceOf, isConvention, severityOf } from './model';
import { SeverityBadge } from './SeverityBadge';
import styles from './FindingsCard.module.css';

const TOP = 8;

interface Props { findings: Problem[]; query: string; clean: boolean; root: string | null }

/** «Что не так»: находки по серьёзности, у каждой — доказательство и переход на /problems/:id. */
export function FindingsCard({ findings, query, clean, root }: Props) {
  const [all, setAll] = useState(false);
  const shown = all ? findings : findings.slice(0, TOP);
  return (
    <Card title="Что не так" aside={findings.length > 0 ? `${findings.length} по важности` : undefined}>
      {findings.length === 0 ? (
        <p className={styles.empty}>
          Находок нет.{' '}
          {clean && 'Это не значит, что проблем нет: часть системы не разобрана — см. «Насколько верить».'}
        </p>
      ) : (
        <ul className={styles.list}>
          {shown.map((p) => (
            <FindingRow key={p.id} p={p} query={query} root={root} />
          ))}
        </ul>
      )}
      {findings.length > TOP && (
        <button type="button" className={styles.more} onClick={() => setAll((v) => !v)} aria-expanded={all}>
          {all ? 'Свернуть' : `Показать все (${findings.length})`}
        </button>
      )}
    </Card>
  );
}

function FindingRow({ p, query, root }: { p: Problem; query: string; root: string | null }) {
  const ev = evidenceOf(p, root);
  return (
    <li className={styles.item}>
      <Link to={`/problems/${encodeURIComponent(p.id)}${query}`} className={styles.link}>
        <span className={styles.badges}>
          <SeverityBadge severity={severityOf(p)} />
          {isConvention(p) && <Badge tone="accent">Свод</Badge>}
        </span>
        <span className={styles.main}>
          <span className={styles.title}>{p.title}</span>
          <span className={styles.evidence}>
            {ev.location ?? 'файл не указан'} · правило {ev.rule}
            {ev.source ? ` · источник ${ev.source}` : ''}
          </span>
        </span>
        <span className={styles.conf}>
          {ev.confidencePct === null ? 'уверенность не указана' : `уверенность ${ev.confidencePct}%`}
        </span>
      </Link>
    </li>
  );
}
