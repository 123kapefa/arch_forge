# Стандарты фронта

Решения и «почему» — в [adr/0016](adr/0016-frontend-v2-architecture.md). Здесь — как работать.

## Структура `frontend/src/`

```
app/        провайдеры (QueryClient), роутер (routes.tsx), оболочка (Shell), 404, error boundary
features/   по папке на экран: start, overview, problem, impact, map, slices, diff, report, states
shared/
  ui/       общие компоненты (без знания про экраны)
  api/      client.ts (fetch, ошибки, отмена), types.ts (типы ответов)
  lib/      чистые функции и хелперы
  styles/   tokens.css (токены, темы), global.css
legacy/     старая карта: заморожена, новых фич нет
test/       setup.ts, renderApp.tsx
```

## Правила

- Один экран — одна папка `features/<экран>/`. Экран **не импортирует другой экран**; общее — в `shared/`.
- `shared/` не знает про `features/` и `app/`; `features/` не знает про `app/`.
- Новый код не импортирует `legacy/`; исключение одно — `features/map/legacy-adapter.tsx`. Новых фич в `legacy/` нет: правим только баги, остальное разбираем по частям в `features/map`.
- **Файл ≤ 300 строк** в `app/features/shared` (без пустых строк и комментариев). Разрастается — разбейте на компоненты/хуки.
- **Никакого `dangerouslySetInnerHTML`**: имена сервисов, типов и файлов приходят из чужих репозиториев. Текст выводим только через React.
- **Ограничения `check-boundaries.mjs`:** видит только относительные импорты (`./`, `../`, в т.ч. динамический `import('…')`); алиасы (`@/…`), `require`, `import.meta.glob` не проверяются. **Алиасы вводить нельзя, не обновив скрипт** (и его тест `scripts/check-boundaries.test.mjs`).
- Проверяет всё это `npm run lint` (`oxlint` + `scripts/check-boundaries.mjs`); в CI — часть обязательных проверок.

## Как добавить экран

1. Папка `features/<имя>/` с `<Имя>Screen.tsx` (именованный экспорт; на экране — `<h1>`).
2. Маршрут в `app/routes.tsx` с `lazy: screen(() => import('…'), '<Имя>Screen')`.
3. Если экран в верхней панели — запись в `shared/lib/screens.ts`.
4. Тест экрана рядом (`*.test.tsx`) + проверка маршрута в `app/routes.test.tsx`.
5. Опишите поведение в `docs/product/` (правило `adr/0015`).

## Как писать компонент и хук

- Компонент — функция, один файл, стили рядом в `<Имя>.module.css`; цвета, отступы, радиусы — только из токенов (`var(--…)`), не литералами.
- Данные — хук `use<Что>` рядом с экраном на `useQuery`; запрос идёт через `shared/api` (`getJson`), `signal` берём из `queryFn`. Отмену (`AbortError`) не показываем как ошибку.
- Клиент `getJson`: в `query` значения `undefined` и `false` отбрасываются, `true` уходит строкой `"true"` (флаги бэка: `?drift=true`). Ошибки: `ApiError(status)`; таймаут — 408, обрыв соединения — status 0 («нет связи»), отмена — `AbortError`.
- Состояния загрузки, ошибки и пустоты — обязательны; ошибку показываем текстом, не молчим.
- Типы ответов — `shared/api/types.ts` (пока вручную, см. долг в ADR).

## Тесты

- Vitest + Testing Library. Старые тесты (`legacy/`) идут в node; тесту с DOM нужна первая строка `// @vitest-environment jsdom`.
- Роутинг и оболочка — через `test/renderApp.tsx` (memory-роутер + QueryClient).
- Ищем элементы по роли и имени (`getByRole`), не по классам. Сеть мокаем на границе `fetch`.
- Тяжёлое (`legacy-adapter`, карта) в тестах оболочки подменяем `vi.mock`.

## Доступность

- Семантика: `<header>`, `<nav aria-label>`, `<main>`, один `<h1>` на экран; кнопки — `<button>`, переходы — `<a>`/`<Link>`.
- Фокус видим (`:focus-visible` в `global.css`), клавиатурная навигация не ломается.
- Статус не только цветом: рядом текст или значок. Контраст текста ≥ 4.5:1 в обеих темах.
- Динамические сообщения (`role="status"`, `role="alert"`) для загрузки/ошибок.

## Команды

```bash
cd frontend
npm run dev     # Vite, прокси /api → Map (:5200)
npm run build   # tsc -b + vite build
npm run test    # vitest
npm run lint    # oxlint + проверка границ
```
