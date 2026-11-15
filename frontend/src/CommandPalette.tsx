// E8: поиск ⌘K — сервис / хранилище / тип / endpoint; Enter ведёт к узлу на карте.

import { useEffect, useMemo, useRef, useState } from 'react';
import { searchEntries, type SearchEntry } from './overviewModel';

const KIND_LABEL: Record<string, string> = {
  service: 'сервис', datastore: 'хранилище', topic: 'топик', external: 'внешний', endpoint: 'endpoint', type: 'тип',
};

export function CommandPalette({ index, onPick, onClose }: {
  index: SearchEntry[];
  onPick: (e: SearchEntry) => void;
  onClose: () => void;
}) {
  const [q, setQ] = useState('');
  const [cur, setCur] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);
  const results = useMemo(() => searchEntries(index, q), [index, q]);
  useEffect(() => { inputRef.current?.focus(); }, []);
  useEffect(() => { setCur(0); }, [q]);

  // window-слушатель: работает, даже если фокус ушёл из поля (клик по списку и т.п.)
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { e.preventDefault(); onClose(); }
      else if (e.key === 'ArrowDown') { e.preventDefault(); setCur((c) => Math.min(c + 1, results.length - 1)); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); setCur((c) => Math.max(c - 1, 0)); }
      else if (e.key === 'Enter' && results[cur]) { e.preventDefault(); onPick(results[cur]); }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [results, cur, onPick, onClose]);

  return (
    <div className="ck-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div className="ck" role="dialog" aria-label="Поиск по системе">
        <input ref={inputRef} className="ck__input" value={q} onChange={(e) => setQ(e.target.value)}
          placeholder="Сервис, тип или endpoint…" aria-label="Поиск" />
        <ul className="ck__list" role="listbox">
          {results.length === 0 && <li className="ck__empty">Ничего не найдено</li>}
          {results.map((r, i) => (
            <li key={r.key} role="option" aria-selected={i === cur}>
              <button className={`ck__row${i === cur ? ' is-cur' : ''}`} onMouseEnter={() => setCur(i)} onClick={() => onPick(r)}>
                <span className="ck__kind">{KIND_LABEL[r.kind] ?? r.kind}</span>
                <span className="ck__label">{r.label}</span>
                {r.sub && <span className="ck__sub">{r.sub}</span>}
              </button>
            </li>
          ))}
        </ul>
        <div className="ck__hint">↑↓ выбор · Enter перейти · Esc закрыть</div>
      </div>
    </div>
  );
}
