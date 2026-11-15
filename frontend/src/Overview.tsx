// E8: стартовый экран-обзор (триаж) — «где болит», прежде чем смотреть карту.
// Данные — только из уже загруженного SystemGraph (новых запросов нет).

import { useEffect, useMemo } from 'react';
import type { Problem, SystemGraph } from './system';
import { coverageShare, topProblems, worstServices } from './overviewModel';

const SEV_ICON: Record<string, string> = { critical: '●', warning: '⚠', coverage: '○', info: 'ℹ' };

export function Overview({ graph, onService, onProblem, onSearch, onClose }: {
  graph: SystemGraph;
  onService: (id: string) => void;
  onProblem: (p: Problem) => void;
  onSearch: () => void;
  onClose: () => void;
}) {
  const worst = useMemo(() => worstServices(graph), [graph]);
  const top = useMemo(() => topProblems(graph), [graph]);
  const cov = useMemo(() => coverageShare(graph), [graph]);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);
  const degr = graph.degradations ?? [];
  const nameOf = (loc: string) => graph.nodes.find((n) => n.id === loc)?.name;

  return (
    <div className="ov-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div className="ov" role="dialog" aria-label="Обзор проекта">
        <header className="ov__head">
          <h2>Обзор · {graph.project}</h2>
          <button className="ov__search" onClick={onSearch}>Поиск <kbd>⌘K</kbd></button>
          <button className="ov__close" onClick={onClose} aria-label="Закрыть обзор">×</button>
        </header>
        <div className="ov__grid">
          <section className="ov__card">
            <h3>Худшие сервисы</h3>
            {worst.length === 0 ? <p className="ov__ok">Проблемных сервисов нет</p> : (
              <ul>
                {worst.map((s) => (
                  <li key={s.node.id}>
                    <button className="ov__row" onClick={() => onService(s.node.id)}>
                      <span className={`ov__dot ov__dot--${s.node.health ?? 'ok'}`} />
                      <span className="ov__name">{s.node.name}</span>
                      <span className="ov__meta">
                        {s.critical > 0 && <>● {s.critical} </>}{s.warning > 0 && <>⚠ {s.warning}</>}
                        {s.total === 0 && (s.node.health ?? '')}
                      </span>
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </section>
          <section className="ov__card">
            <h3>Топ проблем</h3>
            {top.length === 0 ? <p className="ov__ok">Критичных проблем нет</p> : (
              <ul>
                {top.map((p) => (
                  <li key={p.id}>
                    <button className="ov__row" onClick={() => onProblem(p)}>
                      <span className={`ov__sev ov__sev--${p.severity}`}>{SEV_ICON[p.severity] ?? '•'}</span>
                      <span className="ov__name">{p.title}</span>
                      <span className="ov__meta">{nameOf(p.location) ?? p.kind}</span>
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </section>
          <section className="ov__card">
            <h3>Покрытие анализа</h3>
            {cov.total === 0 ? <p className="ov__ok">Нет данных: типы ещё не разбирались кодом (включите анализ кода)</p> : (
              <>
                <div className="ov__bar" title={`по маркеру ${cov.marker}% · эвристика ${cov.heuristic}% · неизвестно ${cov.unknown}%`}>
                  <span className="ov__bar-m" style={{ width: `${cov.marker}%` }} />
                  <span className="ov__bar-h" style={{ width: `${cov.heuristic}%` }} />
                  <span className="ov__bar-u" style={{ width: `${cov.unknown}%` }} />
                </div>
                <p className="ov__legend">по маркеру {cov.marker}% · эвристика {cov.heuristic}% · неизвестно {cov.unknown}% <span>(из {cov.total} типов)</span></p>
              </>
            )}
          </section>
          <section className="ov__card">
            <h3>Что не разобралось</h3>
            {degr.length === 0 ? <p className="ov__ok">Всё разобрано, карта полная</p> : (
              <ul className="ov__degr">{degr.map((d) => <li key={d}>⚠ {d}</li>)}</ul>
            )}
          </section>
        </div>
      </div>
    </div>
  );
}
