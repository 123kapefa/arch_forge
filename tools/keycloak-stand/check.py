#!/usr/bin/env python3
"""Ручной интеграционный набор: настоящий Keycloak + запущенный Map (adr/0013). Только стандартная библиотека Python.

Перед запуском: стенд поднят (compose.yaml) и Map запущен так, как описано в README.md.
  python3 tools/keycloak-stand/check.py          # всё, включая обновление токена и отзыв (~2-3 минуты)
  python3 tools/keycloak-stand/check.py --fast   # без ожидания фоновой службы
"""
import json, re, sys, time, html, urllib.request, urllib.parse, urllib.error, http.cookiejar

MAP = 'http://localhost:5200'
KC = 'http://localhost:8280'
REALM = f'{KC}/realms/archforge'
FAST = '--fast' in sys.argv
fails = []


def check(name, ok, extra=''):
    print(('PASS ' if ok else 'FAIL ') + name + (f'  [{extra}]' if extra and not ok else ''))
    if not ok:
        fails.append(name)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *a, **k):
        return None


def new_jar():
    # Keycloak ставит cookie с Secure даже на http://localhost (браузеры считают localhost безопасным) — разрешаем и urllib
    return http.cookiejar.CookieJar(http.cookiejar.DefaultCookiePolicy(secure_protocols=('http', 'https')))


def opener(jar):
    return urllib.request.build_opener(NoRedirect, urllib.request.HTTPCookieProcessor(jar))


def call(op, method, url, data=None, headers=None):
    req = urllib.request.Request(url, data=data, method=method, headers=headers or {})
    try:
        r = op.open(req, timeout=20)
    except urllib.error.HTTPError as e:
        r = e
    return r.status if hasattr(r, 'status') else r.code, dict(r.headers), r.read().decode('utf-8', 'replace')


def session(op):
    s, _, b = call(op, 'GET', f'{MAP}/api/auth/session')
    return json.loads(b) if s == 200 else {'state': f'HTTP{s}'}


def probe_token(client='archforge-probe'):
    form = urllib.parse.urlencode({'grant_type': 'password', 'client_id': client, 'username': 'tester', 'password': 'tester'}).encode()
    s, _, b = call(urllib.request.build_opener(), 'POST', f'{REALM}/protocol/openid-connect/token', form,
                   {'Content-Type': 'application/x-www-form-urlencoded'})
    return json.loads(b)['access_token'] if s == 200 else None


def browser_login(op):
    """Проходит вход так же, как браузер: Map отдаёт адрес, Keycloak показывает форму, возврат на callback."""
    s, _, b = call(op, 'POST', f'{MAP}/api/auth/login', b'', {'Content-Length': '0'})
    if s != 200:
        return None, f'login HTTP {s}: {b[:200]}'
    url = json.loads(b)['url']
    kcjar = new_jar()
    kop = opener(kcjar)
    s, _, page = call(kop, 'GET', url)
    m = re.search(r'<form[^>]*id="kc-form-login"[^>]*action="([^"]+)"', page) or re.search(r'action="([^"]+login-actions/authenticate[^"]*)"', page)
    if s != 200 or not m:
        return None, f'нет формы входа (HTTP {s})'
    form = urllib.parse.urlencode({'username': 'tester', 'password': 'tester', 'credentialId': ''}).encode()
    s, h, _ = call(kop, 'POST', html.unescape(m.group(1)), form, {'Content-Type': 'application/x-www-form-urlencoded'})
    loc = h.get('Location') or h.get('location')
    if s != 302 or not loc or not loc.startswith(f'{MAP}/api/auth/callback'):
        return None, f'Keycloak не вернул на callback (HTTP {s}, {loc})'
    s, h, _ = call(op, 'GET', loc)   # op несёт cookie привязки входа
    return (h.get('Location') or h.get('location') or ''), f'callback HTTP {s}'


op = opener(new_jar())

# 0. стенд и Map доступны
try:
    call(op, 'GET', f'{REALM}/.well-known/openid-configuration')
    st = session(op)
except Exception as e:
    print('Стенд или Map недоступны:', e)
    sys.exit(2)
check('Map: режим разработки выключен, вход настроен', st['state'] != 'dev', st)
if st['state'] == 'dev':
    print('Запустите Map с Authentication__DevMode=false и Authority стенда (README.md).')
    sys.exit(2)

call(op, 'POST', f'{MAP}/api/auth/logout', b'', {'Content-Length': '0'})
check('до входа состояние none', session(op)['state'] == 'none')

# 1. вход по коду + PKCE
back, info = browser_login(op)
check('вход проходит до конца (возврат на ?login=ok)', bool(back) and 'login=ok' in back, f'{back} {info}')
s1 = session(op)
check('после входа состояние active, субъект есть', s1['state'] == 'active' and bool(s1.get('subject')), s1)
check('способ хранения сессии известен', s1.get('storage') in ('dpapi', 'keychain', 'file'), s1)

# 2. запрос от локального пользователя и с токеном
s, _, b = call(op, 'GET', f'{MAP}/api/me')
me = json.loads(b) if s == 200 else {}
check('/api/me по локальной сессии: 200, не режим разработки', s == 200 and me.get('devMode') is False, f'{s} {b[:120]}')
check('/api/me: роль и право из claims Keycloak', 'user' in me.get('roles', []) and 'archforge.use' in me.get('permissions', []), me)

tok = probe_token()
hdr = {'Authorization': f'Bearer {tok}'} if tok else {}
anon = opener(new_jar())
s, _, b = call(anon, 'GET', f'{MAP}/api/me', headers=hdr)
check('токен Keycloak в Authorization принимается', bool(tok) and s == 200, f'{s} {b[:120]}')
s, _, _ = call(anon, 'GET', f'{MAP}/api/me', headers={'Authorization': f'Bearer {tok}x'} if tok else {})
check('испорченная подпись отклоняется (401)', s == 401, s)
bad = probe_token('archforge-probe-noaud')
s, _, _ = call(anon, 'GET', f'{MAP}/api/me', headers={'Authorization': f'Bearer {bad}'} if bad else {})
check('токен без нужной аудитории отклоняется (401)', bool(bad) and s == 401, s)

# 3. привязка входа к браузеру: чужой callback не подменяет аккаунт
call(op, 'POST', f'{MAP}/api/auth/logout', b'', {'Content-Length': '0'})
other = opener(new_jar())
s, _, b = call(op, 'POST', f'{MAP}/api/auth/login', b'', {'Content-Length': '0'})
url = json.loads(b)['url']
kop = opener(new_jar())
_, _, page = call(kop, 'GET', url)
m = re.search(r'action="([^"]+login-actions/authenticate[^"]*)"', page)
_, h, _ = call(kop, 'POST', html.unescape(m.group(1)), urllib.parse.urlencode({'username': 'tester', 'password': 'tester'}).encode(),
               {'Content-Type': 'application/x-www-form-urlencoded'})
loc = h.get('Location') or h.get('location') or ''
_, h2, _ = call(other, 'GET', loc)   # без cookie привязки
check('callback без cookie привязки не даёт входа', 'login=failed' in (h2.get('Location') or h2.get('location') or '') and session(op)['state'] == 'none')

# 4. выход
browser_login(op)
call(op, 'POST', f'{MAP}/api/auth/logout', b'', {'Content-Length': '0'})
check('выход: состояние none', session(op)['state'] == 'none')

if FAST:
    print('--fast: обновление токена и отзыв сессии пропущены')
else:
    # 5. фоновое обновление продлевает автономный срок
    browser_login(op)
    first = session(op).get('graceEndsAt')
    print('Жду фоновое обновление токена (до 3 минут)…')
    moved = False
    for _ in range(36):
        time.sleep(5)
        cur = session(op)
        if cur['state'] == 'active' and cur.get('graceEndsAt') and cur['graceEndsAt'] != first:
            moved = True
            break
    check('обновление токена прошло, автономный срок сдвинулся', moved, session(op))

    # 6. отзыв доступа на сервере входа стирает сессию (invalid_grant)
    adm = urllib.parse.urlencode({'grant_type': 'password', 'client_id': 'admin-cli', 'username': 'admin', 'password': 'admin'}).encode()
    s, _, b = call(urllib.request.build_opener(), 'POST', f'{KC}/realms/master/protocol/openid-connect/token', adm,
                   {'Content-Type': 'application/x-www-form-urlencoded'})
    at = json.loads(b)['access_token']
    a = {'Authorization': f'Bearer {at}'}
    plain = urllib.request.build_opener()
    _, _, ub = call(plain, 'GET', f'{KC}/admin/realms/archforge/users?username=tester&exact=true', headers=a)
    uid = json.loads(ub)[0]['id']
    s, _, _ = call(plain, 'POST', f'{KC}/admin/realms/archforge/users/{uid}/logout', b'', {**a, 'Content-Length': '0'})
    check('сессии пользователя на сервере входа завершены', s == 204, s)
    print('Жду, пока Map заметит отзыв (до 3 минут)…')
    gone = False
    for _ in range(36):
        time.sleep(5)
        if session(op)['state'] in ('none', 'expired'):
            gone = True
            break
    check('после отзыва Map стёр сессию (нужен вход)', gone, session(op))

print()
print('ИТОГ:', 'всё прошло' if not fails else f'не прошло: {len(fails)} — ' + '; '.join(fails))
sys.exit(1 if fails else 0)
