# Стенд Keycloak для проверки входа

Настоящий Keycloak 26.4 (Docker) для проверки входа arch_forge по ADR 0013: код + PKCE, обновление токена, выход, отзыв сессии, токен в `Authorization`. Не заменяет быстрые тесты на фиктивном сервере входа (`dotnet test`), а дополняет их.

**Только для локальной проверки.** Порт открыт лишь на `127.0.0.1`, пароли тестовые (`admin/admin`, пользователь `tester/tester`). Не используйте как боевой.

## Что внутри
- realm `archforge`, токен живёт 120 с (чтобы обновление было видно за пару минут);
- публичный клиент `archforge-desktop` (PKCE S256, redirect `http://localhost:5200/api/auth/callback`) с claims как в `auth_service`: `aud=archforge-api`, `role`, `permissions`;
- пользователь `tester` (роль `user`, право `archforge.use`);
- служебные клиенты `archforge-probe` и `archforge-probe-noaud` (вход по паролю) — только чтобы получать токены для проверки приёма в `Authorization`.

## Запуск
```bash
docker compose -f tools/keycloak-stand/compose.yaml up -d
```
Map против стенда (папка данных — временная, чтобы не трогать вашу сессию):
```bash
ASPNETCORE_ENVIRONMENT=Development Authentication__DevMode=false \
Authentication__Authority=http://localhost:8280/realms/archforge Authentication__Audience=archforge-api \
Authentication__AllowInsecureMetadata=true Auth__Directory=/tmp/af-stand \
dotnet run --project backend/services/Map/src/ArchForge.Map.Web --urls http://localhost:5200
```
Проверки (нужен только Python 3):
```bash
python3 tools/keycloak-stand/check.py          # всё, ~2–3 минуты (ждёт фоновую службу Map)
python3 tools/keycloak-stand/check.py --fast   # без обновления и отзыва
```
Остановка и сброс (нужен после правки realm): `docker compose -f tools/keycloak-stand/compose.yaml down -v`.

Вручную: фронт (`npm run dev`) с теми же настройками Map; в шапке — «Войти», форма Keycloak, возврат с «Вход выполнен».

## Что проверяет `check.py`
Вход до конца; состояние `active`; claims роли и права; токен Keycloak принят, испорченная подпись и токен без аудитории — 401; callback без cookie привязки не даёт входа; выход; фоновое обновление сдвигает автономный срок; отзыв сессии на сервере входа (`invalid_grant`) стирает сессию.

Не покрыто: автономная работа без связи с Keycloak и истечение 14 дней (покрыто быстрыми тестами с подменой часов).

## Проверка на стенде `auth_service`

Тот же `check.py` проходит и против настоящего `auth_service` (его Keycloak с realm проекта). Проверено на Keycloak 26.4 из `microservices/auth_service`.

Подготовка (в репозитории `auth_service`, стенд: `docker compose up -d --wait keycloak`):
```bash
DEV=1 scripts/project/create-project.sh archforge "arch_forge" http://localhost:8081
python3 scripts/project/add-app.py archforge archforge "arch_forge" --default-role user --web http://localhost:5200
python3 scripts/project/add-admin.py archforge 79990001111 --email tester@archforge.test --password 'Test1234!'   # только DEV
```
`--web http://localhost:5200` создаёт публичный клиент `archforge-web` (PKCE) с возвратом `http://localhost:5200/*`.

Map против него:
```bash
ASPNETCORE_ENVIRONMENT=Development Authentication__DevMode=false \
Authentication__Authority=http://localhost:8180/realms/archforge Authentication__Audience=archforge \
Authentication__AllowInsecureMetadata=true Auth__ClientId=archforge-web Auth__Directory=/tmp/af-stand \
dotnet run --project backend/services/Map/src/ArchForge.Map.Web --urls http://localhost:5200
```
Проверки (пароль админа Keycloak — из `.env` `auth_service`, нужен только для шага «отзыв»):
```bash
AF_KC=http://localhost:8180 AF_USER=79990001111 AF_PASS='Test1234!' AF_PROBE=0 AF_ADMIN_PASS=<KEYCLOAK_ADMIN_PASSWORD> \
  python3 tools/keycloak-stand/check.py
```
Токен доступа в `auth_service` живёт 15 минут, Map обновляет его за 2 минуты до конца, поэтому шаг «обновление» дождётся не за 3 минуты. Для проверки временно поставьте realm `accessTokenLifespan=120` (Admin API) и верните 900.

Права приложения объявляются отдельно (после `add-app.py`):
```bash
AF_KC=http://localhost:8180 AF_REALM=archforge AF_ADMIN_PASS=<KEYCLOAK_ADMIN_PASSWORD> python3 tools/keycloak-stand/declare-app-rights.py
```
Скрипт заводит право `archforge.use` и кладёт его в роль `user`; `check.py` тогда видит право в `/api/me` (по умолчанию `AF_PERMISSION=archforge.use`, для проверки без прав — пусто).

Что подтверждено: вход по коду + PKCE клиентом `archforge-web` с экраном входа темы `auth_service`; `aud` = код приложения (`archforge`); роль приложения приходит в claim `role`; ротация refresh-токена (в realm `revokeRefreshToken=true`) переживается; отзыв сессии стирает сессию. Права (`permissions`) приходят после `declare-app-rights.py`; без него роль `user` пуста.
