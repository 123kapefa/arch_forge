import type { Violation } from './lint';

/** Панель линта (P1-F): дрейф от свода + нарушения направления слоёв. Клик по нарушению → к узлу. */
export function LintPanel({ drift, violations, onClose, onGoTo }: {
  drift: string[];
  violations: Violation[];
  onClose: () => void;
  onGoTo: (id: string) => void;
}) {
  return (
    <aside className="lint" aria-label="Линт">
      <div className="lint__head">
        <span className="lint__title">Линт</span>
        <button className="insp__close" onClick={onClose} title="закрыть">✕</button>
      </div>

      <div className="lint__block">
        <div className="lint__k">⚠ Дрейф от свода · {drift.length}</div>
        {drift.length === 0
          ? <div className="lint__none">нет</div>
          : <ul className="lint__list">{drift.map((d, i) => <li key={i} className="lint__drift">{d}</li>)}</ul>}
      </div>

      <div className="lint__block">
        <div className="lint__k">↯ Направление слоёв · {violations.length}</div>
        {violations.length === 0
          ? <div className="lint__none">нарушений нет — зависимости идут внутрь</div>
          : (
            <ul className="lint__list">
              {violations.map((v) => (
                <li
                  key={v.id}
                  className="lint__viol"
                  role="button"
                  tabIndex={0}
                  onClick={() => onGoTo(v.source)}
                  onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onGoTo(v.source); } }}
                  title="показать узел"
                >
                  <b>{v.fromName}</b> <span className="lint__vl">{v.fromLayer}</span>
                  {' → '}
                  <b>{v.toName}</b> <span className="lint__vl">{v.toLayer}</span>
                </li>
              ))}
            </ul>
          )}
      </div>
    </aside>
  );
}
