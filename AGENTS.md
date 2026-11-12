# arch_forge — руководство для AI-агента

Короткая карта проекта. Не заменяет документацию и не хранит весь контекст.
`AGENTS.md` отвечает на четыре вопроса: что за проект, где его части, как проверять, какие правила нельзя нарушать.

## Проект

arch_forge — инструмент, который статически разбирает чужие C#/.NET-проекты на микросервисах (чистая архитектура) и строит визуальную карту архитектуры («плашки» и связи в стиле Unreal Blueprints). Замысел и дорога — в `docs/tech/VISION.md` и `docs/tech/roadmap/ROADMAP.md`.

Стек: бэк — **.NET/C#** по своду (Clean Architecture; сервисы `Analysis` + `Map`), движок анализа — **Roslyn** (`docs/tech/reference/roslyn.md`); фронт — **свой клиент** на React + React Flow (`@xyflow/react`; `docs/tech/adr/0010`). Декомпозиция — `docs/tech/adr/0009-dotnet-roslyn-microservices.md`. archlens (Node/TS) — только референс идей.
Трекер задач: GitHub Project #2 (`github.com/users/123kapefa/projects/2`); статусы эпиков — `docs/tech/roadmap/epics.md`, вехи — `docs/tech/roadmap/ROADMAP.md`; ветки/PR — `docs/tech/agents/issue-tracker.md`.
Статус: Фаза 1 выпущена в `main`; Фазы 1.5/1.6 (картина системы по средам) — в `dev`, не выпущены.

## Структура

- `backend/` — API и доменная логика.
- `frontend/` — пользовательский интерфейс.
- `docs/` — проектная документация (CONTEXT.md, ADR, docs/agents).
- `.ai/` — skills, rules, hooks, agents, mcp и references.

## Перед завершением

- Тесты: `dotnet test backend/ArchForge.slnx` (из корня без указания решения не находит проект). Два теста на внешний `auth_service` известно красные — `docs/tech/agents/known-issues.md`.
- Линт/сборка: `dotnet build backend/ArchForge.slnx`.
- Фронт (если менялся `frontend/`): `npm run build`, `npm run test`, `npm run lint`.
- При изменении API проверить клиентов и контракт.
- Изменилось поведение — обновить описание в `docs/product/` в том же PR (`docs/tech/adr/0015`). Идеи и заделы — в `docs/tech/ideas/`, решения — в ADR, планы — в `docs/tech/roadmap/`.

## Hard rules

- Не менять уже применённые миграции — только новой миграцией.
- Не пушить напрямую в защищённые ветки (main, master) — только через PR.
- Не добавлять секреты в Git.
- Менять только области, необходимые задаче (см. `.ai/rules/`).

## Workflows (`.ai/skills/`)

- Новая функциональность → `feature`.
- Баг → `fix-bug`.
- Ревью готового diff → `code-review`.
- Неясная задача → `router`.

## Локальный контекст

- `CONTEXT.md` — общий язык проекта (термины, сущности, инварианты).
- `docs/tech/adr/` — существенные архитектурные решения.
- `docs/tech/agents/` — настройки конкретного репозитория (трекер, домен, известные проблемы).

Правила `.ai/rules/` авто-загружаются в Claude. Подробности «зачем» — в методичке (`.ai/references/`).
