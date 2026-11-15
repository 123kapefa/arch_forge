import { useMemo } from 'react';
import { compareEnvs, prodEnvOf, type CompareRow, type EnvGraph } from './deploy';

const CHANGE_LABEL: Record<string, string> = {
  ports: 'порты', healthcheck: 'healthcheck', networks: 'сети', presence: 'наличие',
};

function Cell({ row, env, diff }: { row: CompareRow; env: string; diff: boolean }) {
  const c = row.cells[env];
  if (!c.present) return <td className="cmp__cell--absent">нет в среде</td>;
  return (
    <td className={diff ? 'cmp__cell--diff' : undefined}>
      <div className="cmp__line">
        {c.ports.length === 0 ? <span className="dep-chip">нет портов</span>
          : c.ports.map((p, i) => <span key={`${p.label}-${i}`} className={`dep-chip dep-chip--${p.exposure}`}>{p.label}</span>)}
      </div>
      <div className="cmp__line">
        {c.healthcheck !== null && (
          <span className={`dep-chip dep-chip--hc-${c.healthcheck ? 'on' : 'off'}`}>hc {c.healthcheck ? '✓' : '—'}</span>
        )}
        {c.networks.map((n, i) => <span key={`${n}-${i}`} className="dep-chip dep-chip--net">{n}</span>)}
      </div>
    </td>
  );
}

/** Таблица «Сравнение сред» (E3.3): строка = сервис/хранилище, колонка = среда. Подсветка «меняется» и «ломается к prod». */
export function CompareView({ envs, loading, errors, onClose }: {
  envs: EnvGraph[]; loading: boolean; errors: string[]; onClose: () => void;
}) {
  const rows = useMemo(() => compareEnvs(envs), [envs]);
  const names = envs.map((e) => e.name);
  const prod = prodEnvOf(names);
  const breaking = rows.filter((r) => r.breaks.length > 0).length;
  const changing = rows.filter((r) => r.changed.length > 0).length;
  return (
    <div className="cmp" data-testid="compare-view">
      <div className="cmp__head">
        <span className="cmp__title">Сравнение сред</span>
        <span>{names.join(' · ') || '—'}</span>
        {rows.length > 0 && <span>меняется: {changing}{prod ? ` · ломается в ${prod}: ${breaking}` : ''}</span>}
        <button className="sysview__nav" onClick={onClose}>← к карте</button>
      </div>
      {loading && <div className="cmp__empty">загружаю среды…</div>}
      {errors.map((e) => <div key={e} className="cmp__err">{e}</div>)}
      {!loading && names.length > 1 && !prod && (
        <p className="cmp__note">Среды <code>prod</code> нет — показываем только отличия между средами, без пометок «ломается».</p>
      )}
      {!loading && names.length === 1 && (
        <p className="cmp__note">В проекте одна среда — сравнивать не с чем. Добавьте <code>docker-compose.&lt;env&gt;.yml</code> (dev/stage/prod).</p>
      )}
      {!loading && names.length > 1 && rows.length === 0 && errors.length === 0 && (
        <div className="cmp__empty">В compose нет портов, сетей и healthcheck — сравнивать нечего.</div>
      )}
      {rows.length > 0 && (
        <table className="cmp__table">
          <thead>
            <tr>
              <th>Узел</th>
              {names.map((n) => <th key={n} className={n === prod ? 'cmp__prod' : undefined}>{n}{n === prod ? ' (prod)' : ''}</th>)}
              <th>Отличия</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.id} className={r.breaks.length ? 'cmp__row--breaks' : r.changed.length || r.warns.length ? 'cmp__row--changed' : undefined}>
                <th scope="row" className="cmp__name">{r.name}<span className="cmp__kind">{r.kind}</span></th>
                {names.map((n) => <Cell key={n} row={r} env={n} diff={r.changed.length > 0} />)}
                <td>
                  {r.breaks.map((b) => <div key={b} className="cmp__line"><span className="cmp__tag cmp__tag--break">ломается: {b}</span></div>)}
                  {r.warns.map((w) => <div key={w} className="cmp__line"><span className="cmp__tag cmp__tag--change">внимание: {w}</span></div>)}
                  {r.changed.length > 0 && (
                    <div className="cmp__line">
                      <span className="cmp__tag cmp__tag--change">меняется: {r.changed.map((c) => CHANGE_LABEL[c] ?? c).join(', ')}</span>
                    </div>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
