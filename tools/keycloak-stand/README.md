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
