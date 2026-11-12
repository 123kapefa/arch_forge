import { CONFIDENCE_LABEL, confidenceLevel, matchLabel, relativeFile, shortFile, sortCalls, summarizeCalls, type HttpCall } from './edgeCalls';

export interface SelectedEdge {
  id: string;
  source: string;
  target: string;
  kind: string;
  confidence: number;
  via?: string;
  violation: boolean;
  calls?: HttpCall[];
}

const KIND_LABEL: Record<string, string> = {
  'http-call': 'HTTP-вызов', 'rw-db': 'доступ к БД', 'uses-bus': 'шина', publishes: 'публикует', subscribes: 'подписан',
};
const VIA_LABEL: Record<string, string> = { compose: 'из compose', config: 'из конфигурации', code: 'из кода', unknown: 'источник неизвестен' };

/** Инспектор связи: откуда → куда, чем подтверждена и список конкретных вызовов с источником (E3.1). */
export function EdgeInspector({ edge, names, scanRoot, callsRequested }: { edge: SelectedEdge; names: Record<string, string>; scanRoot: string; callsRequested: boolean }) {
  const from = names[edge.source] ?? edge.source;
  const to = names[edge.target] ?? edge.target;
  const calls = edge.calls ? sortCalls(edge.calls) : undefined;
  const lvl = confidenceLevel(edge.confidence);
  return (
    <div className="einspect">
      <div className="einspect__title">{from} <span aria-hidden>→</span> {to}</div>
      <div className="einspect__tags">
        <span className="einspect__tag">{KIND_LABEL[edge.kind] ?? edge.kind}</span>
        {edge.via && <span className="einspect__tag">{VIA_LABEL[edge.via] ?? edge.via}</span>}
        <span className={`einspect__tag einspect__conf--${lvl}`} title={`уверенность ${edge.confidence.toFixed(2)}`}>
          уверенность: {CONFIDENCE_LABEL[lvl]}
        </span>
        {edge.violation && <span className="einspect__tag einspect__tag--bad">нарушение</span>}
      </div>

      {edge.kind === 'http-call' ? (
        <>
          <div className="einspect__label">вызовы · {summarizeCalls(calls, callsRequested)}</div>
          {calls && calls.length > 0 && (
            <ul className="einspect__calls">
              {calls.map((c, i) => {
                const m = matchLabel(c.matched);
                const rel = relativeFile(c.file, scanRoot);
                const cl = confidenceLevel(c.confidence);
                return (
                  <li key={`${c.method}|${c.route}|${c.file ?? ''}|${c.line ?? ''}|${i}`} className="einspect__call">
                    <div className="einspect__req">
                      <span className="einspect__method">{c.method}</span>
                      <span className="einspect__route">{c.route}</span>
                    </div>
                    <div className="einspect__meta">
                      <span className={`einspect__match einspect__match--${m.tone}`}>{m.text}</span>
                      <span className={`einspect__conf--${cl}`}>{CONFIDENCE_LABEL[cl]}</span>
                    </div>
                    {rel && (
                      <div className="einspect__src">
                        <span className="einspect__path" title={rel}>{shortFile(rel)}</span>
                        {c.line ? <span className="einspect__line">:{c.line}</span> : null}
                        <button type="button" className="finspect__copy"
                          onClick={() => void navigator.clipboard?.writeText(c.file ?? '')}>копировать путь</button>
                      </div>
                    )}
                  </li>
                );
              })}
            </ul>
          )}
          {!callsRequested && <div className="finspect__hint">Вызовы запрашиваются вместе с анализом кода: включите «нарушения слоёв» или «дрейф от свода» на панели «Вид».</div>}
          {callsRequested && !calls && <div className="finspect__hint">Связь взята из compose или конфигурации; в коде абсолютных адресов этого сервиса не найдено (относительные адреса и именованные клиенты пока не разбираются).</div>}
          {calls && <div className="finspect__hint">Найдено статическим разбором кода, без запуска: динамически собранные адреса не определяются.</div>}
        </>
      ) : (
        <div className="finspect__hint">Для этого вида связи конкретных вызовов нет.</div>
      )}
    </div>
  );
}
