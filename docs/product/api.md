# API

Фронт ходит в **Map** (`:5200`); Map обращается к **Analysis** (`:5100`). Ответы — JSON (camelCase).

## Map
| Метод и путь | Назначение |
|---|---|
| `GET /api/map` | карта проекта (типы, связи, покрытие, дрейф) |
| `GET /api/system`, `POST /api/system` | картина системы: параметры `path`, `env`, `drift`, `layers`, `calls`; POST принимает профиль |
| `GET /api/imports` | прямые пакеты проекта: версии, кто использует, конфликты |
| `GET /api/fs`, `GET /api/git` | обзор папок для выбора проекта; сведения о git-репозитории |
| `GET /api/rules`, `GET /api/vocabulary` | каталог правил и словарь ролей с умолчаниями |
| `GET /api/profile?path=`, `PUT /api/profile?path=` | профиль проекта и привязка проекта к конфигурации |
| `GET/POST /api/profile/configs`, `GET/PUT/DELETE /api/profile/configs/{id}`, `POST /api/profile/configs/{id}/duplicate` | именованные конфигурации |
| `POST /api/profile/preview` | предпросмотр влияния: сколько находок даёт каждое правило при профиле, без сохранения |
| `GET /api/me` | кто вошёл: субъект, роли, права |
| `GET /api/auth/session`, `POST /api/auth/login`, `GET /api/auth/callback`, `POST /api/auth/logout` | сессия входа (только с этой машины) |

## Analysis
`GET /api/analysis` (типы проекта), `GET` и `POST /api/system`, `POST /api/system/preview`, `GET /api/imports`, `GET /api/rules`, `GET /api/vocabulary`. Прямые обращения закрыты: [Вход и сессия](auth.md).
