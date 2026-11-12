import { describe, it, expect, vi, afterEach } from 'vitest';
import { describeSession, daysWord, parseStatus, loginResult, withoutLoginParam, isSafeLoginUrl, startLogin, fetchSession, logout, type SessionStatus } from './session';

const st = (o: Partial<SessionStatus>): SessionStatus =>
  ({ state: 'active', subject: 'u1', graceEndsAt: null, daysLeft: null, warn: false, storage: 'file', ...o });

afterEach(() => vi.unstubAllGlobals());

describe('склонение дней', () => {
  it.each([[1, '1 день'], [2, '2 дня'], [4, '4 дня'], [5, '5 дней'], [11, '11 дней'], [14, '14 дней'], [21, '21 день'], [0, '0 дней']])('%i', (n, w) => {
    expect(daysWord(n)).toBe(w);
  });
});

describe('показ статуса', () => {
  it('нет данных — нейтрально, без действий', () => {
    const v = describeSession(null);
    expect(v.tone).toBe('idle'); expect(v.canLogin).toBe(false); expect(v.canLogout).toBe(false);
  });
  it('режим разработки помечен и без входа/выхода', () => {
    const v = describeSession(st({ state: 'dev', subject: 'dev' }));
    expect(v.tone).toBe('dev'); expect(v.label).toBe('Режим разработки');
    expect(v.canLogin).toBe(false); expect(v.canLogout).toBe(false);
  });
  it('вошёл: имя и выход', () => {
    const v = describeSession(st({}));
    expect(v.tone).toBe('ok'); expect(v.label).toBe('u1'); expect(v.canLogout).toBe(true); expect(v.canLogin).toBe(false);
  });
  it('без связи, срок большой — спокойно, показывает остаток и дату', () => {
    const v = describeSession(st({ state: 'offline', daysLeft: 10, graceEndsAt: '2026-11-22T10:00:00Z' }));
    expect(v.tone).toBe('ok'); expect(v.detail).toContain('10 дней'); expect(v.canLogin).toBe(false);
  });
  it('предупреждение за 3 дня: тон warn и можно войти заново', () => {
    const v = describeSession(st({ state: 'offline', daysLeft: 3, warn: true }));
    expect(v.tone).toBe('warn'); expect(v.detail).toContain('3 дня'); expect(v.canLogin).toBe(true); expect(v.canLogout).toBe(true);
  });
  it('срок вышел и часы сдвинуты — нужен вход, выйти нечем', () => {
    for (const state of ['expired', 'clock'] as const) {
      const v = describeSession(st({ state, subject: null }));
      expect(v.tone).toBe('bad'); expect(v.canLogin).toBe(true); expect(v.canLogout).toBe(false);
    }
    expect(describeSession(st({ state: 'clock' })).detail).toContain('Часы');
  });
  it('не вошёл', () => {
    const v = describeSession(st({ state: 'none', subject: null }));
    expect(v.label).toBe('Войти'); expect(v.canLogin).toBe(true);
  });
  it('отрицательный остаток не показываем', () => {
    expect(describeSession(st({ state: 'offline', daysLeft: -2, warn: true })).detail).toContain('0 дней');
  });
});

describe('разбор ответа', () => {
  it('чужая форма и незнакомое состояние не дают входа', () => {
    expect(parseStatus(null)).toBeNull();
    expect(parseStatus('x')).toBeNull();
    expect(parseStatus({ state: 'admin', subject: 5 })).toMatchObject({ state: 'none', subject: null });
  });
  it('поля приводятся по типам', () => {
    expect(parseStatus({ state: 'offline', subject: 'a', graceEndsAt: '2026-01-01T00:00:00Z', daysLeft: 3, warn: true, storage: 'keychain' }))
      .toEqual({ state: 'offline', subject: 'a', graceEndsAt: '2026-01-01T00:00:00Z', daysLeft: 3, warn: true, storage: 'keychain' });
    expect(parseStatus({ state: 'active', warn: 'yes' })!.warn).toBe(false);
  });
});

describe('возврат с сервера входа', () => {
  it('login=ok|failed, остальное игнорируется', () => {
    expect(loginResult('?login=ok')).toBe('ok');
    expect(loginResult('?a=1&login=failed')).toBe('failed');
    expect(loginResult('?login=evil')).toBeNull();
    expect(loginResult('')).toBeNull();
  });
  it('параметр убирается, прочее остаётся', () => {
    expect(withoutLoginParam('?login=ok')).toBe('');
    expect(withoutLoginParam('?a=1&login=ok')).toBe('?a=1');
  });
  it('адрес входа — только http(s)', () => {
    expect(isSafeLoginUrl('https://kc.local/auth?x=1')).toBe(true);
    expect(isSafeLoginUrl('http://localhost:8080/a')).toBe(true);
    for (const bad of ['javascript:alert(1)', 'data:text/html,x', '//evil', 'не адрес', '', null, 5]) expect(isSafeLoginUrl(bad)).toBe(false);
  });
});

const reply = (status: number, body: unknown) => vi.fn().mockResolvedValue({ ok: status < 400, status, json: async () => body });

describe('вызовы API', () => {
  it('статус читается и приводится', async () => {
    vi.stubGlobal('fetch', reply(200, { state: 'active', subject: 'u' }));
    expect((await fetchSession()).state).toBe('active');
  });
  it('ошибка статуса — исключение', async () => {
    vi.stubGlobal('fetch', reply(403, {}));
    await expect(fetchSession()).rejects.toThrow('403');
  });
  it('вход: POST и адрес', async () => {
    const f = reply(200, { url: 'https://kc/auth' });
    vi.stubGlobal('fetch', f);
    expect(await startLogin()).toBe('https://kc/auth');
    expect(f.mock.calls[0][0]).toBe('/api/auth/login');
    expect(f.mock.calls[0][1].method).toBe('POST');
  });
  it('вход: текст ошибки сервера и небезопасный адрес', async () => {
    vi.stubGlobal('fetch', reply(503, { error: 'сервер входа недоступен' }));
    await expect(startLogin()).rejects.toThrow('сервер входа недоступен');
    vi.stubGlobal('fetch', reply(200, { url: 'javascript:alert(1)' }));
    await expect(startLogin()).rejects.toThrow('адрес');
  });
  it('выход: POST, ошибка пробрасывается', async () => {
    const f = reply(204, null);
    vi.stubGlobal('fetch', f);
    await logout();
    expect(f.mock.calls[0][1].method).toBe('POST');
    vi.stubGlobal('fetch', reply(403, null));
    await expect(logout()).rejects.toThrow('403');
  });
});
