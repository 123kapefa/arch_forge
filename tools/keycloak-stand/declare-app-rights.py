#!/usr/bin/env python3
"""Объявляет в Keycloak проекта auth_service права и роль по умолчанию arch_forge (adr/0013).

Источник правил — `ArchForgeRights` в backend/shared/ArchForge.SharedKernel/Auth/AuthOptions.cs: скрипт читает оттуда код
приложения, право и роль, поэтому расхождения быть не может. Модель — как у auth_service (docs/keycloak/04a-roles.md):
право — роль клиента приложения с атрибутами kind=permission, source=code; роль — составная роль клиента (kind=role),
в составе — права. Скрипт можно запускать повторно.

  AF_KC=http://localhost:8180 AF_REALM=archforge AF_ADMIN_USER=admin AF_ADMIN_PASS=<пароль мастер-админки> \\
    python3 tools/keycloak-stand/declare-app-rights.py

Приложение (клиент `archforge`) и роль `user` должны уже существовать: их создаёт add-app.py из auth_service.
Боевое объявление делает каталог приложения (AddAppCatalog из Platform.Authentication) — когда пакеты будут доступны.
"""
import json, os, re, sys, urllib.error, urllib.parse, urllib.request
from pathlib import Path

KC = os.environ.get('AF_KC', 'http://localhost:8180')
REALM = os.environ.get('AF_REALM', 'archforge')
SRC = Path(__file__).resolve().parents[2] / 'backend/shared/ArchForge.SharedKernel/Auth/AuthOptions.cs'


def constant(text, name):
    m = re.search(r'public const string %s = "([^"]+)";' % name, text)
    if not m:
        sys.exit(f'В {SRC} не найдена константа {name} (ArchForgeRights)')
    return m.group(1)


text = SRC.read_text(encoding='utf-8')
APP, USE, ROLE = constant(text, 'App'), constant(text, 'Use'), constant(text, 'DefaultRole')
PERMISSIONS = {USE: 'Пользоваться arch_forge: открывать проекты, смотреть карту, настраивать профили'}


def call(token, method, path, body=None, ok=(200, 201, 204)):
    data = None if body is None else json.dumps(body, ensure_ascii=False).encode()
    req = urllib.request.Request(f'{KC}{path}', data=data, method=method,
                                 headers={'Authorization': f'Bearer {token}', 'Content-Type': 'application/json'})
    try:
        with urllib.request.urlopen(req, timeout=20) as r:
            t = r.read().decode()
            return json.loads(t) if t else None
    except urllib.error.HTTPError as e:
        if e.code in ok:
            return None
        sys.exit(f'{method} {path}: {e.code} {e.read().decode()[:300]}')


form = urllib.parse.urlencode({'grant_type': 'password', 'client_id': 'admin-cli', 'username': os.environ.get('AF_ADMIN_USER', 'admin'),
                               'password': os.environ.get('AF_ADMIN_PASS', 'admin')}).encode()
try:
    token = json.load(urllib.request.urlopen(urllib.request.Request(f'{KC}/realms/master/protocol/openid-connect/token', data=form), timeout=20))['access_token']
except Exception as e:
    sys.exit(f'Нет токена мастер-админки ({e}). Задайте AF_ADMIN_PASS.')

base = f'/admin/realms/{REALM}'
clients = call(token, 'GET', f'{base}/clients?clientId={APP}')
if not clients:
    sys.exit(f'В realm {REALM} нет приложения «{APP}»: сначала add-app.py из auth_service')
cid = clients[0]['id']

for name, description in PERMISSIONS.items():
    role = {'name': name, 'description': description, 'attributes': {'kind': ['permission'], 'source': ['code']}}
    existing = call(token, 'GET', f'{base}/clients/{cid}/roles/{urllib.parse.quote(name)}', ok=(404,))
    if existing is None:
        call(token, 'POST', f'{base}/clients/{cid}/roles', role)
        print(f'право {name}: создано')
    else:
        call(token, 'PUT', f'{base}/clients/{cid}/roles/{urllib.parse.quote(name)}', {**existing, 'description': description, 'attributes': role['attributes']})
        print(f'право {name}: уже есть, описание и атрибуты выровнены')

if call(token, 'GET', f'{base}/clients/{cid}/roles/{ROLE}', ok=(404,)) is None:
    sys.exit(f'В приложении «{APP}» нет роли «{ROLE}»: add-app.py ... --default-role {ROLE}')
have = {c['name'] for c in call(token, 'GET', f'{base}/clients/{cid}/roles/{ROLE}/composites') or []}
add = [call(token, 'GET', f'{base}/clients/{cid}/roles/{urllib.parse.quote(p)}') for p in PERMISSIONS if p not in have]
if add:
    call(token, 'POST', f'{base}/clients/{cid}/roles/{ROLE}/composites', add)
    print(f'роль {ROLE}: добавлены права {[r["name"] for r in add]}')
else:
    print(f'роль {ROLE}: все права уже в составе')
