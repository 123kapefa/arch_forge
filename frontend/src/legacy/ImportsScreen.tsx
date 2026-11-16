import { useMemo, useState } from 'react';
import type { ImportsDto, PackageDto } from './api';
import { Icon } from './Icon';

// Русская плюрализация: 1 проект / 2–4 проекта / 5+ проектов.
function plural(n: number, one: string, few: string, many: string): string {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return few;
  return many;
}

type Sort = 'usage' | 'name';
const NO_PACKAGES: PackageDto[] = []; // стабильная ссылка — чтобы useMemo не пересчитывался вхолостую

/** Экран «Импорты»: прямые пакеты проекта — версии, кто использует, конфликты версий. */
export function ImportsScreen({ imports, loading }: { imports: ImportsDto | null; loading: boolean }) {
  const [query, setQuery] = useState('');
  const [sort, setSort] = useState<Sort>('usage');
  const [expanded, setExpanded] = useState<string | null>(null);

  const packages = imports?.packages ?? NO_PACKAGES;
  const conflicts = useMemo(() => packages.filter((p) => p.versionConflict).length, [packages]);

  const rows = useMemo(() => {
    const q = query.trim().toLowerCase();
    const filtered = q ? packages.filter((p) => p.name.toLowerCase().includes(q)) : packages;
    const byName = (a: PackageDto, b: PackageDto) => a.name.localeCompare(b.name);
    const byUsage = (a: PackageDto, b: PackageDto) => b.usedBy.length - a.usedBy.length || byName(a, b);
    // конфликты всегда наверху, затем — по выбранной сортировке
    return [...filtered].sort((a, b) =>
      Number(b.versionConflict) - Number(a.versionConflict) || (sort === 'usage' ? byUsage(a, b) : byName(a, b)));
  }, [packages, query, sort]);

  return (
    <div className="imports-screen">
      <div className="imports-screen__head">
        <div className="imports-screen__title">
          <Icon name="box" size={18} />
          <span>Импорты</span>
          {imports && <span className="imports-screen__count">{packages.length} прям. пакет(ов)</span>}
          {conflicts > 0 && <span className="imports-screen__conf">⚠ {conflicts} конфликт(ов) версий</span>}
        </div>
        <div className="imports-screen__tools">
          <input
            className="imports-screen__search"
            placeholder="поиск пакета…"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
          <div className="imports-screen__sort">
            <button className={sort === 'usage' ? 'is-on' : ''} onClick={() => setSort('usage')}>по использованию</button>
            <button className={sort === 'name' ? 'is-on' : ''} onClick={() => setSort('name')}>по имени</button>
          </div>
        </div>
      </div>

      <div className="imports-screen__body">
        {loading && <div className="imports-screen__note">загрузка импортов…</div>}
        {!loading && imports && packages.length === 0 && <div className="imports-screen__note">прямых пакетов не найдено</div>}
        {!loading && !imports && <div className="imports-screen__note">откройте проект, чтобы увидеть импорты</div>}

        {!loading && imports && packages.length > 0 && (
          <>
            <div className="imp-row imp-row--head">
              <span>Пакет</span>
              <span>Версия</span>
              <span>Используют</span>
            </div>
            <ul className="imports-screen__list">
              {rows.map((p) => {
                const open = expanded === p.name;
                return (
                  <li key={p.name} className={`imp-item${p.versionConflict ? ' is-conflict' : ''}`}>
                    <button className="imp-row" onClick={() => setExpanded(open ? null : p.name)}>
                      <span className="imp-name" title={p.name}>
                        <Icon name={open ? 'chevronDown' : 'chevronRight'} size={13} />
                        {p.name}
                      </span>
                      <span className="imp-ver">
                        {p.versions.join(', ') || '—'}
                        {p.versionConflict && <span className="imp-badge">конфликт</span>}
                      </span>
                      <span className="imp-used">{p.usedBy.length} {plural(p.usedBy.length, 'проект', 'проекта', 'проектов')}</span>
                    </button>
                    {open && (
                      <ul className="imp-usedby">
                        {p.usedBy.map((u) => <li key={u}>{u}</li>)}
                      </ul>
                    )}
                  </li>
                );
              })}
              {rows.length === 0 && <li className="imports-screen__note">ничего не найдено по «{query}»</li>}
            </ul>
          </>
        )}
      </div>
    </div>
  );
}
