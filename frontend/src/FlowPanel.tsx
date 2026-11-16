import { useEffect, useState } from 'react';
import { InspectorFrame } from './InspectorFrame';
import { fetchFlow, KIND_ICON, STATUS_LABEL, type Flow } from './flow';
import { isAbortError } from './http';

/**
 * Панель «Поток» (E3.4): цепочка вызовов от выбранного сервиса. Статический вероятный happy-path — «без рантайма».
 * Подсвеченный путь на карте задаёт родитель через onFlow (набор рёбер считается из Flow).
 */
export function FlowPanel({ entry, entryName, path, env, onFlow, onClose, onFocus }: {
  entry: string; entryName: string; path: string; env?: string;
  onFlow: (f: Flow | null) => void; onClose: () => void; onFocus?: (id: string) => void;
}) {
  const [flow, setFlow] = useState<Flow | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [depth, setDepth] = useState(6);

  useEffect(() => {
    const ac = new AbortController();
    setFlow(null); setError(null); onFlow(null);
    fetchFlow({ path, entry, env, maxDepth: depth }, ac.signal)
      .then((f) => { setFlow(f); onFlow(f); })
      .catch((e) => { if (!isAbortError(e)) setError(e instanceof Error ? e.message : String(e)); });
    return () => ac.abort();
    // onFlow — стабильный setState родителя
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [path, entry, env, depth]);

  return (
    <InspectorFrame what="ПОТОК" className="flowp" title={entryName}
      subtitle={<span className="einspect__tags"><span className="einspect__tag">реконструировано по коду, без рантайма</span></span>}>
      <div className="flowp__bar">
        <label className="flowp__depth">глубина
          <select value={depth} onChange={(e) => setDepth(Number(e.target.value))}>
            {[2, 3, 4, 6, 8, 12].map((d) => <option key={d} value={d}>{d}</option>)}
          </select>
        </label>
        <button type="button" className="flowp__close" onClick={onClose}>скрыть поток</button>
      </div>

      {!flow && !error && <div className="inspector__block panel__empty">строю цепочку…</div>}
      {error && <div className="inspector__block finspect__hint" role="alert">Не удалось построить поток: {error}</div>}

      {flow && (
        <>
          {flow.steps.length === 0 ? (
            <div className="inspector__block panel__empty">исходящих вызовов у сервиса не найдено</div>
          ) : (
            <ol className="flowp__steps" aria-label="Шаги потока">
              {flow.steps.map((s) => {
                const hint = STATUS_LABEL[s.status];
                return (
                  <li key={s.index} className={`flowp__step flowp__step--${s.status}`} style={{ paddingLeft: 6 + (s.depth - 1) * 14 }}>
                    <span className="flowp__ico" title={s.sync === true ? 'синхронно' : s.sync === false ? 'асинхронно' : ''}>{KIND_ICON[s.kind] ?? '·'}</span>
                    <span className="flowp__label">{s.label}</span>
                    {onFocus
                      ? <button type="button" className="uinsp__link flowp__to" onClick={() => onFocus?.(s.to)} title="Перейти к узлу">{s.toName}</button>
                      : <span className="flowp__to">{s.toName}</span>}
                    {s.sync === false && <span className="einspect__tag">async</span>}
                    {hint && <span className="flowp__flag">{hint}</span>}
                    {s.note && <span className="flowp__note">{s.note}</span>}
                    {s.calls && s.calls.length > 1 && (
                      <ul className="flowp__calls">
                        {s.calls.map((c, i) => <li key={i}>{c.method} {c.route}{c.matched === false ? ' · не найден' : ''}</li>)}
                      </ul>
                    )}
                  </li>
                );
              })}
            </ol>
          )}
          {flow.truncated && <div className="inspector__block flowp__warn">Поток усечён лимитом — цепочка продолжается дальше. Увеличьте глубину.</div>}
          <ul className="flowp__notes">
            {flow.notes.map((n, i) => <li key={i} className="finspect__hint">{n}</li>)}
          </ul>
        </>
      )}
    </InspectorFrame>
  );
}
