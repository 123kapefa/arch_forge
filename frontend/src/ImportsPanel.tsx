import type { ImportsDto } from './api';

// Русская плюрализация: 1 проект / 2–4 проекта / 5+ проектов.
function plural(n: number, one: string, few: string, many: string): string {
  const mod10 = n % 10;
  const mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return one;
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return few;
  return many;
}

/** Панель импортов (P1-G): прямые библиотеки — версии, кто использует, конфликты версий. */
export function ImportsPanel({ imports, loading, onClose }: {
  imports: ImportsDto | null;
  loading: boolean;
  onClose: () => void;
}) {
  return (
    <aside className="imports" aria-label="Импорты">
      <div className="imports__head">
        <span className="imports__title">📦 Импорты{imports ? ` · ${imports.packages.length}` : ''}</span>
        <button className="insp__close" onClick={onClose} title="закрыть">✕</button>
      </div>

      {loading && <div className="imports__none">загрузка…</div>}
      {!loading && imports && imports.packages.length === 0 && (
        <div className="imports__none">прямых пакетов не найдено</div>
      )}
      {!loading && imports && imports.packages.length > 0 && (
        <ul className="imports__list">
          {imports.packages.map((p) => (
            <li key={p.name} className={`imports__pkg${p.versionConflict ? ' is-conflict' : ''}`}>
              <div className="imports__name" title={p.name}>{p.name}</div>
              <div className="imports__meta">
                <span className="imports__ver">{p.versions.join(', ') || '—'}</span>
                <span className="imports__used" title={p.usedBy.join('\n')}>
                  {p.usedBy.length} {plural(p.usedBy.length, 'проект', 'проекта', 'проектов')}
                </span>
                {p.versionConflict && <span className="imports__conflict">⚠ конфликт версий</span>}
              </div>
            </li>
          ))}
        </ul>
      )}
    </aside>
  );
}
