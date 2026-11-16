// Сессия входа (adr/0013): тип статуса от Map, чистая логика показа и вызовы API.
// Токены сюда не попадают никогда — Map отдаёт только статус.

import { fetchWithTimeout } from './http';

export type SessionState = 'none' | 'active' | 'offline' | 'expired' | 'clock' | 'dev';

/** GET /api/auth/session. */
export interface SessionStatus {
  state: SessionState;
  subject: string | null;
  graceEndsAt: string | null; // конец автономного срока (ISO)
  daysLeft: number | null;
  warn: boolean;              // осталось ≤ WarnDays
  storage: string;            // как хранится сессия: none | dpapi | keychain | file
}

export type Tone = 'idle' | 'ok' | 'warn' | 'bad' | 'dev';

export interface SessionView {
  tone: Tone;
  label: string;        // коротко, в шапку
  detail: string;       // пояснение для подсказки/меню
  canLogin: boolean;
  canLogout: boolean;
}

const STATES: readonly SessionState[] = ['none', 'active', 'offline', 'expired', 'clock', 'dev'];

/** «1 день, 2 дня, 5 дней». */
export function daysWord(n: number): string {
  const a = Math.abs(n) % 100;
  const b = a % 10;
  if (a >= 11 && a <= 14) return `${n} дней`;
  if (b === 1) return `${n} день`;
  if (b >= 2 && b <= 4) return `${n} дня`;
  return `${n} дней`;
}

function dateOf(iso: string | null): string | null {
  if (!iso) return null;
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? null : d.toLocaleDateString('ru-RU', { day: 'numeric', month: 'long' });
}

/** Что показать по статусу. Незнакомое состояние от сервера считаем «не вошёл»: не выдаём чужое за вход. */
export function describeSession(s: SessionStatus | null): SessionView {
  if (!s) return { tone: 'idle', label: 'Вход…', detail: 'Состояние входа ещё не получено', canLogin: false, canLogout: false };
  const who = s.subject ?? 'пользователь';
  const left = s.daysLeft != null ? Math.max(0, s.daysLeft) : null;
  const until = dateOf(s.graceEndsAt);
  switch (s.state) {
    case 'dev':
      return { tone: 'dev', label: 'Режим разработки', detail: 'Вход отключён: все запросы идут от тестового пользователя. Только для локальной разработки.', canLogin: false, canLogout: false };
    case 'active':
      return {
        tone: s.warn ? 'warn' : 'ok', label: who,
        detail: s.warn && left != null ? `Вход выполнен. Войдите заново в течение ${daysWord(left)}.` : 'Вход выполнен',
        canLogin: false, canLogout: true,
      };
    case 'offline': {
      const tail = left != null ? ` Автономная работа ещё ${daysWord(left)}${until ? ` (до ${until})` : ''}.` : '';
      return {
        tone: s.warn ? 'warn' : 'ok', label: `${who} · без связи`,
        detail: `Нет связи с сервером входа, работаем по сохранённой сессии.${tail}${s.warn ? ' Скоро понадобится вход.' : ''}`,
        canLogin: s.warn, canLogout: true,
      };
    }
    case 'expired':
      return { tone: 'bad', label: 'Нужен вход', detail: 'Срок сессии вышел. Войдите заново.', canLogin: true, canLogout: false };
    case 'clock':
      return { tone: 'bad', label: 'Нужен вход', detail: 'Часы компьютера сдвинуты назад, сессии нельзя доверять. Проверьте время и войдите заново.', canLogin: true, canLogout: false };
    default:
      return { tone: 'idle', label: 'Войти', detail: 'Вход не выполнен', canLogin: true, canLogout: false };
  }
}

/** Приводит ответ сервера к SessionStatus; чужую форму отбрасывает. */
export function parseStatus(raw: unknown): SessionStatus | null {
  if (!raw || typeof raw !== 'object') return null;
  const r = raw as Record<string, unknown>;
  const state = STATES.includes(r.state as SessionState) ? (r.state as SessionState) : 'none';
  return {
    state,
    subject: typeof r.subject === 'string' ? r.subject : null,
    graceEndsAt: typeof r.graceEndsAt === 'string' ? r.graceEndsAt : null,
    daysLeft: typeof r.daysLeft === 'number' ? r.daysLeft : null,
    warn: r.warn === true,
    storage: typeof r.storage === 'string' ? r.storage : 'none',
  };
}

/** Результат возврата с сервера входа: Map редиректит на `?login=ok|failed`. */
export function loginResult(search: string): 'ok' | 'failed' | null {
  const v = new URLSearchParams(search).get('login');
  return v === 'ok' || v === 'failed' ? v : null;
}

/** Убирает `login` из строки запроса, остальное сохраняет (строка адреса не хранит служебный след). */
export function withoutLoginParam(search: string): string {
  const p = new URLSearchParams(search);
  p.delete('login');
  const s = p.toString();
  return s ? `?${s}` : '';
}

/** Переходим только на http(s): ответ сервера не должен увести страницу на javascript: и подобное. */
export function isSafeLoginUrl(url: unknown): url is string {
  if (typeof url !== 'string') return false;
  try { const u = new URL(url); return u.protocol === 'https:' || u.protocol === 'http:'; } catch { return false; }
}

export class SessionApiError extends Error {}

const TIMEOUT_MS = 10_000;

export async function fetchSession(signal?: AbortSignal): Promise<SessionStatus> {
  const res = await fetchWithTimeout('/api/auth/session', TIMEOUT_MS, signal);
  if (!res.ok) throw new SessionApiError(`Состояние входа недоступно (${res.status})`);
  const s = parseStatus(await res.json());
  if (!s) throw new SessionApiError('Неожиданный ответ о состоянии входа');
  return s;
}

/** Просит у Map адрес входа; саму навигацию делает вызывающий. */
export async function startLogin(): Promise<string> {
  const res = await fetchWithTimeout('/api/auth/login', TIMEOUT_MS, undefined, { method: 'POST' });
  let body: { url?: unknown; error?: unknown } = {};
  try { body = await res.json(); } catch { /* пустое тело */ }
  if (!res.ok) throw new SessionApiError(typeof body.error === 'string' ? body.error : `Не удалось начать вход (${res.status})`);
  if (!isSafeLoginUrl(body.url)) throw new SessionApiError('Сервер вернул неподходящий адрес входа');
  return body.url;
}

export async function logout(): Promise<void> {
  const res = await fetchWithTimeout('/api/auth/logout', TIMEOUT_MS, undefined, { method: 'POST' });
  if (!res.ok) throw new SessionApiError(`Не удалось выйти (${res.status})`);
}
