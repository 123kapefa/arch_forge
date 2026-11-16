import { Link } from 'react-router';
import type { SystemGraph } from '../../shared/api';
import { layoutMiniMap, MINI_W, NODE_H, NODE_W } from './model';
import styles from './MiniMapView.module.css';

const HEALTH_LABEL: Record<string, string> = { ok: 'ок', warning: 'предупреждение', critical: 'критично' };

/** Мини-карта: схема системы, целиком кликабельна → /map. Имена выводятся только как текст (React экранирует). */
export function MiniMapView({ graph, query }: { graph: SystemGraph; query: string }) {
  const m = layoutMiniMap(graph);
  return (
    <Link to={`/map${query}`} className={styles.link} aria-label="Открыть карту">
      <svg viewBox={`0 0 ${MINI_W} ${m.height}`} className={styles.svg} role="img" aria-label={`Мини-карта: ${graph.nodes.length} узлов, ${graph.edges.length} связей`}>
        {m.edges.map((e) => (
          <line key={e.id} x1={e.x1} y1={e.y1} x2={e.x2} y2={e.y2} className={styles.edge} />
        ))}
        {m.nodes.map((n) => (
          <g key={n.id} transform={`translate(${n.x} ${n.y})`}>
            <title>{`${n.name} · ${n.kind} · ${HEALTH_LABEL[n.health] ?? n.health}`}</title>
            <rect width={NODE_W} height={NODE_H} rx="6" className={`${styles.node} ${styles[n.health] ?? ''}`} />
            <text x={NODE_W / 2} y={NODE_H / 2 + 4} textAnchor="middle" className={styles.label}>
              {n.name.length > 13 ? `${n.name.slice(0, 12)}…` : n.name}
            </text>
          </g>
        ))}
      </svg>
      {m.hidden > 0 && <span className={styles.more}>и ещё {m.hidden} на карте</span>}
      <span className={styles.cta}>Открыть карту →</span>
    </Link>
  );
}
