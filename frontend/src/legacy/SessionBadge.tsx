import { useCallback, useEffect, useRef, useState } from 'react';
import { describeSession, fetchSession, loginResult, logout, startLogin, withoutLoginParam, type SessionStatus } from './session';
import { isAbortError } from './http';

const POLL_MS = 60_000;

const STORAGE_NOTE: Record<string, string> = {
  dpapi: 'Сессия хранится зашифрованной, ключ защищён Windows (DPAPI).',
  keychain: 'Сессия хранится зашифрованной, ключ — в связке ключей macOS.',
  file: 'Сессия хранится зашифрованной, ключ — в файле с доступом только вам.',
};

/** Кто вошёл, срок автономной работы и вход/выход (adr/0013). Токенов не видит — только статус от Map. */
export function SessionBadge() {
  const [status, setStatus] = useState<SessionStatus | null>(null);
  const [failed, setFailed] = useState(false);
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<{ kind: 'ok' | 'bad'; text: string } | null>(null);
  const ctl = useRef<AbortController | null>(null);

  const refresh = useCallback(async () => {
    ctl.current?.abort();
    const c = new AbortController();
    ctl.current = c;
    try {
      setStatus(await fetchSession(c.signal));
      setFailed(false);
    } catch (e) {
      if (!isAbortError(e)) setFailed(true); // прежний статус не затираем — связь могла мигнуть
    }
  }, []);

  // Возврат с сервера входа: Map приводит на «?login=ok|failed». Показываем итог и убираем след из адреса.
  useEffect(() => {
    const r = loginResult(window.location.search);
    if (!r) return;
    setNote(r === 'ok' ? { kind: 'ok', text: 'Вход выполнен' } : { kind: 'bad', text: 'Войти не удалось. Попробуйте ещё раз.' });
    const clean = withoutLoginParam(window.location.search);
    window.history.replaceState(null, '', window.location.pathname + clean + window.location.hash);
  }, []);

  useEffect(() => {
    void refresh();
    const t = window.setInterval(() => void refresh(), POLL_MS);
    const onFocus = () => void refresh();
    window.addEventListener('focus', onFocus);
    return () => { window.clearInterval(t); window.removeEventListener('focus', onFocus); ctl.current?.abort(); };
  }, [refresh]);

  useEffect(() => {
    if (!note) return;
    const t = window.setTimeout(() => setNote(null), 6000);
    return () => window.clearTimeout(t);
  }, [note]);

  const view = describeSession(status);
  const label = failed && !status ? 'Вход: нет данных' : view.label;
  const tone = failed && !status ? 'idle' : view.tone;

  const login = async () => {
    setBusy(true); setNote(null);
    try { window.location.assign(await startLogin()); } // уходим на сервер входа; вернёмся на «?login=…»
    catch (e) { setNote({ kind: 'bad', text: e instanceof Error ? e.message : 'Не удалось начать вход' }); setBusy(false); }
  };
  const out = async () => {
    setBusy(true); setNote(null);
    try { await logout(); await refresh(); setOpen(false); }
    catch (e) { setNote({ kind: 'bad', text: e instanceof Error ? e.message : 'Не удалось выйти' }); }
    finally { setBusy(false); }
  };

  return (
    <div className="sess">
      <button className={`sess__btn sess__btn--${tone}`} onClick={() => setOpen((o) => !o)} aria-expanded={open} title={view.detail}>
        <span className="sess__dot" aria-hidden />{label}
        {tone === 'warn' && <span className="sess__sr"> — скоро нужен вход</span>}
      </button>
      {note && <span className={`sess__note sess__note--${note.kind}`} role="status">{note.text}</span>}
      {open && (
        <>
          <div className="pm-backdrop" onClick={() => setOpen(false)} />
          <div className="sess__pop" role="dialog" aria-label="Вход">
            <div className="sess__title">{label}</div>
            <p className="sess__text">{failed && !status ? 'Не удалось получить состояние входа: сервис Map недоступен.' : view.detail}</p>
            {status && status.state !== 'dev' && status.state !== 'none' && STORAGE_NOTE[status.storage] && (
              <p className="sess__hint">{STORAGE_NOTE[status.storage]}</p>
            )}
            <div className="sess__actions">
              {view.canLogin && <button className="sess__act sess__act--main" disabled={busy} onClick={() => void login()}>Войти</button>}
              {view.canLogout && <button className="sess__act" disabled={busy} onClick={() => void out()}>Выйти</button>}
              <button className="sess__act" onClick={() => void refresh()} disabled={busy}>Обновить</button>
            </div>
          </div>
        </>
      )}
    </div>
  );
}
