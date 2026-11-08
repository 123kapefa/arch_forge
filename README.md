# arch_forge

Инструмент статически разбирает чужой C#/.NET-проект (Clean Architecture, микросервисы) и строит интерактивную карту архитектуры — «плашки» типов и связи в стиле Unreal Blueprints. Разбор идёт через Roslyn и **не требует, чтобы таргет собирался**.

Замысел — [`docs/VISION.md`](docs/VISION.md), дорога — [`docs/ROADMAP.md`](docs/ROADMAP.md), карта репозитория и правила — [`AGENTS.md`](AGENTS.md).

**Статус (2026-11-08):** Фаза 1 «Видеть» выпущена в `main`. В `dev` (не выпущено) — Фаза 1.5 «Полный разбор» и большая часть Фазы 1.6 «Картина системы по средам»: карта микросервисов из docker-compose и кода (сервисы, БД, шина, внешние системы), среды dev/stage/prod, call-graph, детекторы проблем (общая БД, циклы, БД наружу, дрейф, нарушения слоёв), drill System → Service → Layer → Type. Что осталось — `docs/roadmap-epics.md`.

## Требования

- **.NET SDK 10**
- **Node.js 20+** (для фронта)

## Как запустить

Три процесса: **Analysis** (движок Roslyn, `:5100`) → **Map** (гейтвей к Analysis, `:5200`) → **фронт** (Vite, `:5173`, проксирует `/api` на Map). Порты фиксируются через `ASPNETCORE_URLS` — фронт и Map по умолчанию рассчитывают именно на `5100`/`5200`.

```bash
# 1. Analysis — движок анализа, на :5100
ASPNETCORE_URLS=http://localhost:5100 \
  dotnet run --project backend/services/Analysis/src/ArchForge.Analysis.Web
```

```bash
# 2. Map — гейтвей к Analysis, на :5200 (Analysis:BaseUrl по умолчанию http://localhost:5100)
ASPNETCORE_URLS=http://localhost:5200 \
  dotnet run --project backend/services/Map/src/ArchForge.Map.Web
```

```bash
# 3. Фронт — Vite на :5173, проксирует /api → Map
cd frontend && npm install && npm run dev
```

Открыть **http://localhost:5173** и выбрать папку с проектом: одним сервисом или целой системой (папка с несколькими сервисами и `docker-compose*.yml`). Среда выбирается переключателем в топбаре (`?env=`).

Свод правил проекта можно дополнить файлом `archforge.conventions.json` (или `.archforge/conventions.json`) в корне разбираемого проекта — пример и список секций в окне «Свод конвенций» в приложении.

> Семантика (транзитивное наследование, точные рёбра) работает, когда у таргета есть собранный `bin/`. Без него — синтаксический «пол» (`docs/adr/0008`).

## Проверки

```bash
# Бэк — сборка и тесты всего решения (из корня нужно указывать решение явно)
dotnet build backend/ArchForge.slnx
dotnet test backend/ArchForge.slnx
```

> Два теста Analysis (`Reproduces_spike_coverage_on_auth_service`, `Semantic_pack_leaves_no_unknown_on_auth_service`) читают локальный репозиторий `auth_service` (путь — `ARCHFORGE_AUTH_SERVICE`) и сейчас красные из-за изменений в нём; без этого репозитория они пропускаются. Подробности — `docs/agents/known-issues.md`.

```bash
# Фронт
cd frontend && npm run build && npm run lint && npm run test
```

## Структура

- `backend/` — сервисы `Analysis` (Roslyn) и `Map` (гейтвей) + `shared/{Contracts,SharedKernel}`. Clean Architecture по своду, декомпозиция — `docs/adr/0009`.
- `frontend/` — свой клиент на React + React Flow (`docs/adr/0010`).
- `docs/` — `VISION.md`, `ROADMAP.md`, ADR, `roslyn.md`, `agents/`.
- `.ai/` — skills, rules, hooks, agents (материализуются в `.claude/`).
