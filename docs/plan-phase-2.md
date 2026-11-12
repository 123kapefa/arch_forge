# План — Фаза 2 «Действовать (ИИ)» (декомпозиция)

Довести arch_forge от read-only карты (Фаза 1) до «визуального Claude Code»: выбрал плашку → инструкция → локальный ИИ-агент правит код в рабочей папке → **diff на апрув** → ре-анализ карты. Направление — `docs/adr/0005-ai-provider-adapter.md`; декомпозиция сервисов — `adr/0009`; фронт-граница — `adr/0010`. Каждый пункт — сквозной проверяемый вертикальный срез, как P1-A…H. Статусы — на 2026-08-14.

## Граф зависимостей вех

```
P2-0  спайк: headless-раннер + git-safety loop  ← снимает главный риск
  └─▶ P2-A  сервис Agent: /api/agent/run + контракт
        ├─▶ P2-B  Map-гейтвей: AgentClient + сборка контекст-бандла
        └─▶ P2-C  git-loop: pre-flight гарды + diff + rollback
              └─(B,C)─▶ P2-D  фронт: инструкция → diff → апрув/reject → ре-анализ
                          └─▶ P2-E  адаптеры: Codex / локальная модель / кастомный шаблон
P2-F  робастность + безопасность — сквозной, финализируется последним
```

## Архитектурные решения

- **Новый сервис `backend/services/Agent`** (Domain/Contracts/Core/Infrastructure/Web — как Analysis/Map), не расширение Map. adr/0009 предзаявляет `Agent (Фаза 2)`; это единственная **пишущая и исполняющая внешние команды** часть — изоляция локализует радиус поражения и security-ревью (Map остаётся чистым read-гейтвеем, `project-boundaries`).
- **Фронт дёргает Agent только через Map** (инвариант adr/0010: Map — единственная фронт-фейсинг точка, Vite-прокси `/api` → Map, без CORS). Map получает типизированный `AgentClient` (калька `AnalysisClient`) и тонкие pass-through `/api/agent/*`.
- **Ре-анализ после апрува — бесплатно**: фронт перезапрашивает `/api/map?path=…` (ноль изменений контракта). Agent сам Analysis не зовёт.
- **Ответственности**: Analysis — `путь → IR` (read); Map — проекции + сборка контекст-бандла + гейтвей к Agent; Agent — бандл → запуск провайдера → diff/apply/rollback.

## Контракт `IAgentRunner` (порт в `Agent.Domain`, адаптеры в `Agent.Infrastructure`)

```
IAgentRunner
    string ProviderId { get; }                              // "claude" | "codex" | "local" | "custom"
    Task<AgentRunResult> RunAsync(AgentRunRequest req, CancellationToken ct)
```

- **Вход**: `WorkingDirectory` (корень git-репо цели, валидируется), `Prompt`/`ContextBundle`, `Scope` (nodeId + файлы), `Timeout`.
- **Выход**: `Status` (`Succeeded|Failed|TimedOut|Cancelled`), `ChangedFiles` (из `git status --porcelain`, не из «слова» провайдера), `Diff` (unified), `Log`, `ExitCode`, `Error`.
- **Первый адаптер `ClaudeCliRunner`** (headless `claude -p`): безопасная сборка команды через **`ProcessStartInfo.ArgumentList`** (`UseShellExecute=false`, никакой shell-склейки — закрывает command injection); отмена/таймаут/kill-tree переиспользуют паттерн `CancellationToken` из P1-H (`WaitForExitAsync(ct)`, `Kill(entireProcessTree: true)`, linked-CTS). Тестируется фейком за портом; интеграция с реальным `claude` — gated (в CI бинаря нет).

## Контекст-бандл по плашке (собирается в Map)

Якорь узла (`NodeDto.File`) + прямые соседи по графу (та же выборка, что `GraphProjections.Focus`: имена+файлы) + текст правил слоя/свода для `Layer`/`Role` узла + инструкция пользователя. **Границы**: только файл узла + файлы прямых соседей + правила, не весь репо. Инвариант: наше приложение код наружу не шлёт; что читает/шлёт CLI пользователя — его BYO-выбор.

## Diff → апрув → ре-анализ

- **Diff** — git в целевом репо: `git diff` (дерево vs HEAD), `git status --porcelain` → точный `ChangedFiles`.
- **Апрув** — правки уже на диске; апрув = перезапрос `/api/map?path=…` → ре-анализ. Коммит **не** авто (за пользователем, `git-workflow`).
- **Reject/rollback** — `/api/agent/rollback`: `git restore` изменённых + точечное удаление созданных прогоном untracked по porcelain-списку (не слепой `git clean`).
- **Требование git-репо** (нюанс adr/0005): pre-flight `git rev-parse --is-inside-work-tree`; не git → блок с сообщением (+ опц. явно-согласованный `git init`). MVP требует **чистое дерево** до прогона, иначе diff/rollback затрут незакоммиченную работу пользователя.

## Безопасность (критично — исполнение внешних команд, правящих чужой код)

Command injection → только `ArgumentList`. Произвольные пути → канонизация + аллоу-лист (= анализируемый путь), traversal отклоняется. Permission-posture → запуск инициирует пользователь + git diff-gate как страховка (правки обратимы) + права держит CLI пользователя. Изоляция → `WorkingDirectory`=цель, свои секреты/env в дочерний процесс не инжектим, таймаут+kill-tree, ресурс-капы (P1-H). Egress → только через CLI пользователя; лог провайдера может содержать код → локальный, никуда не шлём/не пишем в репо. Свод-правила: `secrets-config` (BYO, токенов не храним), `project-boundaries` (Agent отдельно от read-путей), `git-workflow` (не авто-коммит/пуш).

## Вехи

### P2-0 — Спайк: headless-раннер + git-safety loop (де-риск) — ✅
`IAgentRunner`+`ClaudeCliRunner`+`GitWorkspace`+`ProcessRunner` в scratch-git-репо: run→unified-diff→чистый rollback; таймаут/отмена убивают дерево процессов. Трогает только `Agent.Domain`/`Agent.Infrastructure`+тесты (ни Map, ни фронта).

**Результат спайка:**
- **Механическая петля доказана** (10 детерминированных тестов): безопасный запуск (`ArgumentList`, без shell), отмена и таймаут убивают дерево процессов, `GitWorkspace` снимает diff (включая новые файлы через intent-to-add) и делает точечный откат к чистому дереву, pre-flight отклоняет не-git и грязное дерево.
- **Запуск провайдера доказан**: реальный `claude -p … --dangerously-skip-permissions` в scratch-репо стартует, мы корректно захватываем stdout/exit-code; флаг прав принят (парсинг прошёл).
- **Ключевая находка — авторизация BYO**: свежий headless-`claude` в изолированном окружении вернул `exit 1` + «Not logged in» (auth интерактивной сессии не наследуется дочерним процессом). Петля отработала честно: провайдер упал → правок нет → дерево чистое, статус `Failed`, лог несёт причину. **Требование Фазы 2**: сервис Agent запускается там, где провайдерный CLI пользователя уже залогинен (BYO из adr/0005); таксономия ошибок P2-A/P2-F обязана внятно показывать «провайдер не авторизован». Реальное поведение permission-флага проверяется в залогиненном окружении (открыто, петлю не блокирует).

### P2-A — Сервис Agent: `/api/agent/run` (sync MVP) + контракт
Поднять сервис по своду; вертикальный срез `{workingDir, providerId, bundle/prompt, scope}` → `ClaudeCliRunner` → `AgentRunResult`. Provider-registry (DI по `providerId`). Новый `ArchForge.Agent.Contracts` (`AgentClient`); фронт-фейсинг DTO — в shared `ArchForge.Contracts`. **DoD**: `dotnet test` зелёный; плохой путь/инъекция отклонены; Claude за портом.

### P2-B — Map-гейтвей: `AgentClient` + `/api/agent/*` + сборка бандла
Map получает `AgentClient` (`AddHttpClient<AgentClient>` в `Registration.cs`), тонкие `/api/agent/run|rollback|providers`; собирает `ContextBundle` из IR + инструкция и форвардит. **DoD**: `/api/agent/run` работает только через Map; бандл scoped; тесты зелёные.

### P2-C — Git-loop: pre-flight гарды + diff + rollback
`GitWorkspace`: `is-git`, чистое дерево/baseline HEAD, точный post-run `ChangedFiles`, `rollback` к baseline, обработка не-git цели. **DoD**: грязное дерево отклонено с сообщением; rollback точный; без потери данных.

### P2-D — Фронт: инструкция → diff → апрув/reject → ре-анализ
Из `Inspector` (узел выбран) → «Поручить ИИ» → инструкция + выбор провайдера → `/api/agent/run` → панель diff → Апрув (перезапрос `/api/map`) / Reject (`/api/agent/rollback`). Переиспользует `fetchWithTimeout`. **DoD**: полный цикл в браузере на scratch-репо; ре-анализ после апрува.

### P2-E — Адаптеры: Codex / локальная модель / кастомный шаблон
Остальные адаптеры за `IAgentRunner`; provider-registry+конфиг; фронт-пикер. Кастомный шаблон — токены `{repo}`/`{prompt}` через `ArgumentList`. **DoD**: BYO-агностично; Claude + минимум ещё один доказаны.

### P2-F — Робастность и безопасность (сквозной)
Таймауты/kill-tree везде; ресурс-капы; таксономия ошибок; нет утечки секретов/кода в логах; аллоу-лист путей; финализация permission-posture; сквозная отмена (abort фронта → Map → Agent → kill). Зеркалит P1-H. **DoD**: permission-posture задокументирован; cancel доказан end-to-end; проход `@security-reviewer`.

## Контракт и совместимость

Новый проект `ArchForge.Agent.Contracts` (`AgentClient` + транспорт). Фронт-фейсинг DTO — в shared `ArchForge.Contracts` (camelCase), зеркалятся в `frontend/src/api.ts`: `AgentRunRequestDto`, `AgentRunResultDto`, `ChangedFileDto`, `AgentStatus`, `ProviderDto`, `ContextBundleDto`, `RollbackRequestDto`. Всё **аддитивно** — `/api/map`, `/api/imports`, `GraphDto/NodeDto/EdgeDto/ImportsDto` не меняются → клиенты Фазы 1 работают (`api-contracts`).

## Риски и открытые вопросы (закрыть до/в P2-0)

1. **Permission-posture headless** (главный) — сколько автономии давать `claude -p`; ставка на git diff-gate вместо авто-апрува. Де-рискуется в P2-0.
2. **Чистое дерево до прогона** — требовать clean git-tree (или авто-`stash`)? Риск затереть незакоммиченную работу.
3. **Не-git цель** — отказ vs предложить `git init` (инвазивно)?
4. **Долгие прогоны** — sync request/response (MVP, с таймаутом) vs job-модель (poll/stream)?
5. **Конкурентность** — один прогон на цель за раз (lock)?
6. **Egress** — наш сервер код не шлёт; провайдер пользователя может (BYO). Полностью локальный путь — адаптер локальной модели.
7. **CLI в CI** — `claude`-бинаря нет → интеграция gated/мокается; порт даёт юнит-тесты с фейком.

## Рекомендация: начать с P2-0

Главный неизвестный риск Фазы 2 — можно ли **надёжно и безопасно** гнать headless-CLI, чтобы он правил реальный репо, а мы получали **обратимый diff** с работающим permission-posture и без command injection. Остальное (гейтвей, контракт, UI, ре-анализ) — кальки паттернов Фазы 1. Прямой аналог того, как Фаза 1 первым делом сняла риск «семантика на несобираемом проекте» (P0). Если headless-права нельзя сделать безопасно/эргономично — меняется вся UX Фазы 2, и это дешевле узнать на однофайловом спайке.

## Не входит в Фазу 2
Дифф/дрейф архитектуры между коммитами/PR, арх-линты в CI, шаринг (Фаза 3); собственный облачный ИИ-шлюз + метеринг/монетизация (позже, ещё один адаптер).
