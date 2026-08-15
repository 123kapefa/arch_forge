import { useCallback, useEffect, useState } from 'react';
import { browseFs, type FsListing } from './fs';

/**
 * Пикер папки: навигация по диску сервера (через /api/fs) — как в HowToUseIt.
 * Клик по папке — зайти внутрь; двойной клик — сразу выбрать; «↑ вверх» — к родителю;
 * «диски» — к списку дисков (Windows); путь-строку можно править (Enter — перейти).
 */
export function FolderPicker({ initial, onSelect, onClose }: {
  initial?: string;
  onSelect: (path: string) => void;
  onClose: () => void;
}) {
  const [listing, setListing] = useState<FsListing | null>(null);
  const [draft, setDraft] = useState(initial ?? '');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const go = useCallback(async (path?: string) => {
    setLoading(true);
    setError(null);
    try {
      const l = await browseFs(path);
      setListing(l);
      setDraft(l.path);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void go(initial); }, [go, initial]);

  const atDrives = listing?.path === '/';

  return (
    <div className="fp-overlay" onClick={onClose}>
      <div className="fp" onClick={(e) => e.stopPropagation()}>
        <div className="fp__head">
          <span className="fp__title">Выбрать папку проекта</span>
          <button className="fp__x" onClick={onClose} title="Закрыть">✕</button>
        </div>

        <div className="fp__bar">
          <button
            className="fp__up" onClick={() => listing?.parent != null && void go(listing.parent)}
            disabled={listing?.parent == null || loading} title="На уровень выше"
          >↑ вверх</button>
          <button className="fp__up" onClick={() => void go('/')} disabled={loading} title="К списку дисков">💽 диски</button>
          <input
            className="fp__path" value={draft} spellCheck={false}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); void go(draft.trim()); } }}
          />
        </div>

        {error && <div className="fp__error">{error}</div>}

        <div className="fp__list">
          {loading && <div className="fp__muted">…</div>}
          {!loading && listing?.drives.map((d) => (
            <button key={d} className="fp__row" onClick={() => void go(d)} onDoubleClick={() => onSelect(d)} title={d}>
              <span className="fp__ic">💽</span>
              <span className="fp__name">{d}</span>
            </button>
          ))}
          {!loading && listing?.entries.map((e) => (
            <button key={e.path} className="fp__row" onClick={() => void go(e.path)} onDoubleClick={() => onSelect(e.path)} title={e.path}>
              <span className="fp__ic">📁</span>
              <span className="fp__name">{e.name}</span>
              {e.project && <span className="fp__badge">проект</span>}
            </button>
          ))}
          {!loading && listing && listing.entries.length === 0 && listing.drives.length === 0 && (
            <div className="fp__muted">вложенных папок нет — можно выбрать текущую</div>
          )}
        </div>

        <div className="fp__foot">
          <span className="fp__hint">двойной клик по папке — сразу выбрать</span>
          <button className="fp__cancel" onClick={onClose}>Отмена</button>
          <button
            className="fp__ok" onClick={() => listing && onSelect(listing.path)}
            disabled={!listing || loading || atDrives}
          >Выбрать эту папку</button>
        </div>
      </div>
    </div>
  );
}
