# arch_forge

Инструмент статически разбирает чужой C#/.NET-проект (Clean Architecture, микросервисы) и строит интерактивную карту архитектуры: «плашки» типов, сервисы, базы и связи в стиле Unreal Blueprints. Разбор идёт через Roslyn и не требует, чтобы разбираемый проект собирался.

## Документация

- **[Что умеет продукт](docs/product/README.md)** — возможности, [запуск](docs/product/getting-started.md), карта, правила, профили, вход, API.
- **[Техническая область](docs/tech/README.md)** — дорога, замысел, решения (ADR), идеи, справочники.
- [`AGENTS.md`](AGENTS.md) — карта репозитория и правила для ИИ-агентов.

## Быстрый старт

Нужны .NET SDK 10 и Node.js 20+. Три процесса (Analysis `:5100`, Map `:5200`, фронт `:5173`) — команды в [запуске](docs/product/getting-started.md). Затем откройте http://localhost:5173 и выберите папку с проектом.

## Структура

- `backend/` — сервисы `Analysis` (Roslyn) и `Map` (гейтвей, профили, сессия входа) + `shared/{Contracts,SharedKernel}`; решение `backend/ArchForge.slnx`.
- `frontend/` — клиент на React + React Flow.
- `docs/` — две области: `product/` и `tech/`.
- `tools/keycloak-stand/` — тестовый Keycloak и проверки входа.
- `.ai/` — skills, rules, hooks, agents для ИИ-агентов (материализуются в `.claude/`).
