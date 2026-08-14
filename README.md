# arch_forge

Инструмент статически разбирает чужой C#/.NET-проект (Clean Architecture, микросервисы) и строит интерактивную карту архитектуры — «плашки» типов и связи в стиле Unreal Blueprints. Разбор идёт через Roslyn и **не требует, чтобы таргет собирался**.

Замысел — [`docs/VISION.md`](docs/VISION.md), дорога — [`docs/ROADMAP.md`](docs/ROADMAP.md), карта репозитория и правила — [`AGENTS.md`](AGENTS.md).

**Статус: Фаза 1 «Видеть» — готова.** Разбор `путь → IR`, роли по convention-pack, дрейф, тип-граф, свой React-вьюер (drill по уровням, инспектор, импорты, линт-панель). Известные ограничения — в конце `docs/plan-phase-1.md`.

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

Открыть **http://localhost:5173**, вставить путь к разбираемому .NET-сервису и нажать «Разобрать».

> Семантика (транзитивное наследование, точные рёбра) работает, когда у таргета есть собранный `bin/`. Без него — синтаксический «пол» (`docs/adr/0008`).

## Проверки

```bash
# Бэк — сборка и тесты всего решения
dotnet build backend/ArchForge.slnx
dotnet test backend/services/Analysis/tests/ArchForge.Analysis.Tests
dotnet test backend/services/Map/tests/ArchForge.Map.Tests
```

```bash
# Фронт
cd frontend && npm run build && npm run lint && npm run test
```

## Структура

- `backend/` — сервисы `Analysis` (Roslyn) и `Map` (гейтвей) + `shared/{Contracts,SharedKernel}`. Clean Architecture по своду, декомпозиция — `docs/adr/0009`.
- `frontend/` — свой клиент на React + React Flow (`docs/adr/0010`).
- `docs/` — `VISION.md`, `ROADMAP.md`, ADR, `roslyn.md`, `agents/`.
- `.ai/` — skills, rules, hooks, agents (материализуются в `.claude/`).
