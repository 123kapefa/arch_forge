import type { SystemGraph } from '../../shared/api';
import { findingsOf, trustOf } from './model';
import { DegradationBanner } from './DegradationBanner';
import { FindingsCard } from './FindingsCard';
import { ImpactPlaceholder } from './ImpactPlaceholder';
import { PassportCard } from './PassportCard';
import { TrustCard } from './TrustCard';
import styles from './OverviewContent.module.css';

/** Раскладка экрана для непустого проекта: слева «что не так», справа «что это» и «насколько верить». */
export function OverviewContent({ graph, query, root }: { graph: SystemGraph; query: string; root: string | null }) {
  const trust = trustOf(graph);
  return (
    <>
      {trust.degradations.length > 0 && <DegradationBanner degradations={trust.degradations} />}
      <div className={styles.cols}>
        <div className={styles.col}>
          <FindingsCard findings={findingsOf(graph)} query={query} clean={trust.cleanIsNotSafe} root={root} />
          <ImpactPlaceholder />
        </div>
        <div className={styles.col}>
          <PassportCard graph={graph} query={query} />
          <TrustCard trust={trust} />
        </div>
      </div>
    </>
  );
}
