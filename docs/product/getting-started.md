# Запуск

## Требования
- **.NET SDK 10**
- **Node.js 20+** (для фронта)

## Три процесса
Порты фиксируются явно: фронт и Map по умолчанию рассчитывают на `5100` и `5200`.

```bash
# 1. Analysis — движок анализа
ASPNETCORE_URLS=http://localhost:5100 \
  dotnet run --project backend/services/Analysis/src/ArchForge.Analysis.Web
```
```bash
# 2. Map — гейтвей к Analysis (Analysis:BaseUrl по умолчанию http://localhost:5100)
ASPNETCORE_URLS=http://localhost:5200 \
  dotnet run --project backend/services/Map/src/ArchForge.Map.Web
```
```bash
# 3. Фронт — Vite на :5173, проксирует /api на Map
cd frontend && npm install && npm run dev
```

Откройте **http://localhost:5173**, нажмите «Открыть проект» и выберите папку: один сервис или целую систему (папка с несколькими сервисами и `docker-compose*.yml`). Среда (dev/stage/prod) переключается в верхней панели.

## Вход
В окружении `Development` (по умолчанию при `dotnet run`) включён режим разработки: вход не нужен, запросы идут от тестового пользователя `dev`, в шапке виден значок «Режим разработки». Для работы с сервером входа см. [Вход и сессия](auth.md).

## Семантика и «пол»
Если у разбираемого проекта есть собранная папка `bin/`, анализатор использует семантику Roslyn: наследование, дженерики, точные связи. Без `bin/` работает синтаксический «пол»: структура и связи по тексту кода, с пометкой о том, что точность ниже.

## Проверки
```bash
dotnet build backend/ArchForge.slnx
dotnet test backend/ArchForge.slnx
cd frontend && npm run build && npm run lint && npm run test
```
Два теста Analysis читают локальный репозиторий `auth_service` (путь в `ARCHFORGE_AUTH_SERVICE`) и красные, пока он в текущем состоянии; без этого репозитория они пропускаются (см. [известные проблемы](../tech/agents/known-issues.md)).
