# План — Фаза 1.5 «Полный разбор» (декомпозиция)

> **Документ исторический (фаза выполнена, PR #16–#48 в `dev`).** Актуальные статусы — `docs/roadmap-epics.md` (Фаза 1.6) и `docs/ROADMAP.md`. Статусы ниже — на 2026-08-15 и не обновляются.

Вставка **между** Фазой 1 (read-only карта, released) и Фазой 2 (ИИ-правки). Цель — поднять глубину с «типы внутри одного сервиса» до **системной карты микросервисов**: `корень системы → сервисы → слои → типы → члены`, со связями между сервисами и инфрой, где **главный сигнал — ПРОБЛЕМЫ** (структура — лишь скелет). Каждый пункт — сквозной проверяемый вертикальный срез, как P1-A…H. Направление — `docs/VISION.md`, декомпозиция — `adr/0009`, честность — `adr/0008`, фронт-граница — `adr/0010`. Статусы — на 2026-08-15.

## Северная звезда: проблемы, а не счётчики

Ценность карты — **показать, что не так** (это подтвердил разбор archlens: он ушёл вперёд по структуре, но детекции проблем у него нет вообще). Структура (сервисы/слои/типы/члены) — скелет; поверх него — слой **проблем**, и он главный:

- **из Фазы 1, поднять на уровень системы**: дрейф от свода · нарушения направления слоёв · покрытие (`unknown`-роли);
- **новые системные анти-паттерны** (видны только на уровне системы): **общая БД** (сервис пишет в хранилище чужого сервиса) · **цикл сервисов** (взаимные `http-call`) · **висячее событие** (`topic` только с `publishes` или только с `subscribes`) · обход шлюза / нарушение границ контекста;
- **rollup здоровья по уровням**: на каждом уровне «горит» число проблем этого scope → сразу видно, куда проваливаться; клик по проблеме → провал к узлу/ребру + инспектор «почему и как чинить» (`Via` + якорь на код);
- **честность (adr/0008)**: проблему показываем, только если видно в манифесте/коде; консервативные пороги (см. `confidence` ниже); ничего не выдумываем.

## Де-риск-таргет

`/Users/123kapefa/Documents/projects/product-site-workspace` (KalmykovGroup) — реальный мульти-сервисный .NET-воркспейс:
- 3 сервиса по своду: `auth-service`, `loyalty-service`, `product_site/backend` (каждый со своим `.slnx/.sln` + `docker-compose`).
- Общий kernel `Kalmykov.Shared.*`; OpenAPI во всех (`OpenApiExtensions.cs`).
- **Нет единого system-compose** (`microservices_management` — мониторинг). Сервисы обнаруживаются по подпапкам; межсервисная топология — из `nginx-proxmox` (reverse-proxy) + кросс-сервисных HTTP-клиентов (`SmsMicroserviceProvider`) + событий (`SettingsChangedEvent`, проект `Loyalty.Communication`).
- Шум для исключения: `.claude/worktrees/`, `product_site_backup_*`, `product_site_dbdumps`, `node_modules`, `bin/obj`.
- Обратная совместимость проверяется на одиночном `microservices/auth_service` (Фаза 1).

## Ключевой тезис (определяет всю форму)

Бóльшая часть «глубины» берётся **не из Roslyn, а из манифестов и конфига**: per-service `docker-compose` → инфра, образы, порты, `depends_on`; `appsettings`/compose-env/DI → `rw-db` (строки подключения), `publishes/subscribes` (события/шина), `http-call` (base URL клиентов + маршруты `nginx`); Roslyn остаётся **ядром** пер-сервисного тип-графа и даёт **члены** (эндпоинты статически). Значит Фаза 1.5 — **мульти-источниковый разбор**: к портам `ITypeSource`/`IImportSource` добавляются новые порты уровня «система», над пер-сервисными графами появляется системный IR, а поверх IR — **детектор проблем**.

## Ставка: «система = тоже `GraphDto`»

Точка входа решает уровень по тому, что нашла discovery:
- корень — система (несколько сервисов / compose) → **системный `GraphDto`**: узлы `service|datastore|topic`, рёбра `http-call|rw-db|publishes|subscribes`;
- корень — один сервис → **сегодняшнее поведение без изменений** (наведение на `auth_service` даёт ровно Фазу 1).

Иерархию несут **аддитивные опциональные поля** узла, а не новый формат → переиспользуем весь конвейер вьюера. Навигация (drill = re-fetch `/api/map?path=…`): **System** → сервис → **Layer** → **Type** → **Member**. Отвергнуто: отдельный `SystemDto` — ломает переиспользование вьюера, второй контракт; масштаб решаем drill-ом, а не форматом.

## Заимствования из archlens (идеи, не код)

archlens — референс-репозиторий (Node/regex, чужой орг); arch_forge — свой .NET/Roslyn. Берём **подходы**, реализуем сами. Что берём:

1. **Confidence на рёбрах** — у каждого ребра `Confidence` (0..1) + `Source` (файл) + `Via`/label. Шкала по источнику: `compose depends_on`≈1.0, `ProjectReference`≈1.0, `Services__X`/appsettings≈0.9, события из кода≈0.85, HTTP/БД по URL≈0.7, конвенция/эвристика≈0.5. **Дедуп «код > объявление»**: декларативный `depends` убираем, если для пары есть реальная код-связь. Даёт честность **и порог для проблем** (нарушение рисуем только при уверенной связи).
2. **Провенанс портов/секретов** — связь может быть литералом, ключом env (`${KEY}` → **внешний узел с ключом**, `managed`, не «пустота») или внутренним портом (`ASPNETCORE_URLS`/`EXPOSE`). Вливаем в `Via`.
3. **Маршрутизация рёбер** (фронт) — ортогональные линии с обходом блоков + раздвижка наложений в дорожки. Перепишем в наш React-Flow-клиент по мотивам (модуль чистый, тестируемый).

## Информационная архитектура (оболочка-прототип)

Фич много → canvas показывает **только структуру текущего scope**, всё остальное — в сворачиваемой хроме. Пять механизмов разделения:

1. **Семантический зум (drill)** — главный разделитель: canvas всегда на одном уровне, крошки = где ты.
2. **Панели (док, сворачиваемые)** — Инспектор (справа, контекст выбранного), Проблемы (снизу, свёрнута в бейдж `⛔2 ⚠4`), Импорты (за кнопкой).
3. **Тумблеры «Вид» (слева)** — что показывать на canvas: слои, **виды рёбер** (☑http-call ☐calls), **порог `confidence`** (скрыть слабые связи), tests/одиночки, **контур окружения**.
4. **Сворачивание в узле** — плашка сервиса ▾ → слои; контроллер ▾ → эндпоинты. Детали без ухода с уровня.
5. **Модалки** — endpoint «как swagger», полное объяснение проблемы. Глубокое и не-пространственное.

Принцип: плотность крутится тумблерами, глубина — зумом, никогда «всё сразу».

## Расширения контракта `ArchForge.Contracts` (строго аддитивно, `api-contracts`)

Только **новые опциональные поля** (nullable, `JsonIgnoreCondition.WhenWritingNull`) и **новые значения** свободных полей. `/api/map`, `/api/analysis`, `/api/imports` и формы Фазы 1 не ломаются. Зеркалим в `frontend/src/api.ts`.

- **`NodeDto.kind`** — новые значения: `service · datastore · topic · member · external`.
- **`NodeDto`** — опц.: `Service` (id владельца), `Parent` (member→тип, тип→слой/сервис), `Level` (`system|service|layer|type|member`), `Members` (`IReadOnlyList<MemberDto>?`), `Health` (`ok|warning|critical`), `ProblemCount`. `File` расширяется до «якорь на источник» (для инфры — `docker-compose.yml#Ln`).
- **`EdgeDto.kind`** — новые значения: `http-call · publishes · subscribes · rw-db · calls`.
- **`EdgeDto`** — опц.: `Via` (`compose|config|code|unknown`), **`Confidence`** (0..1), `Source` (файл), `Violation` (bool) + `Problem` (id).
- **`ProblemDto`** (новый) — `{ Id, Severity (critical|warning|coverage), Kind (drift|layer-violation|unknown-role|shared-db|service-cycle|dangling-event|…), Title, Location (nodeId|edgeId), Via, Fix }`. `GraphDto.Problems: IReadOnlyList<ProblemDto>?` + `GraphDto.Health`/summary.
- **`MemberDto`** (новый) — `{ Id, Name, Kind (endpoint|method|property|field), Signature, HttpMethod?, Route?, File, Line }`.
- **Окружения — только форма контракта (реализация позже)**: `Environment`/топология/инстанс/профиль как опц. поля/DTO, зарезервированы, но в Фазе 1.5 не заполняются.
- **Покрытие**: `CoverageDto` не меняем; на системном уровне агрегируем честно (per-service + суммарный).

## Источники и их разбор (порты/адаптеры в духе `ITypeSource`)

Новые порты — в `Analysis.Core`; адаптеры-манифесты — в новом инфра-проекте `ArchForge.Analysis.Infrastructure.Manifests` (парсинг compose/appsettings — **не** Roslyn; `project-boundaries`). Детектор проблем — чистый, поверх собранного IR (как `TypeClassifier`/линт Фазы 1).

| Порт (Core) | Адаптер | Даёт | Источник |
|---|---|---|---|
| `IServiceDiscoverySource` | `FolderDiscoverySource` (осн.), `ComposeDiscoverySource` | набор сервисов (имя, root, entrypoint) | скан подпапок: `.slnx/.sln` + `*.API/*.Web` + свой compose |
| `IManifestSource` | `ComposeManifestSource` | образы, `${VAR:-def}`, порты, volumes→storage, `depends_on` | per-service `docker-compose*.yml` |
| `IServiceConfigSource` | `AppSettingsConfigSource` + Roslyn-DI-скан + `NginxRouteSource` | `rw-db`, `publishes/subscribes`, `http-call` (+ `Confidence`/`Via`) | appsettings, compose-env, DI, `nginx-proxmox` |
| `IEndpointSource` | `RoslynEndpointSource` (+ опц. `OpenApiFileSource`) | члены-эндпоинты | Roslyn (атрибуты/`MapGet`/маркер) |
| `IProblemDetector` | `SystemLintRules` | дрейф/слои/покрытие (из Фазы 1) + анти-паттерны (общая БД/цикл/висячее событие) | собранный IR |

**Тиры деградации/честности**: **пол** — сервисы (discovery) + инфра/`depends_on` (всегда) → **конфиг-рёбра** (`rw-db`) → **код-рёбра** (`publishes/http-call`). Каждое ребро несёт `Via`+`Confidence`. Не видно — не рисуем.

**Эндпоинты — статически из кода** (Roslyn), опц. закоммиченный `openapi.json`. Рантайм-скан/генерация `/openapi/v1.json` (docker+`dotnet run`, как у archlens) — **вне** Фазы 1.5: чужой сервис не запускаем (`adr/0002`).

## Мульти-сервисное обнаружение

Корень = папка пользователя. Приоритет: **подпапки-сервисы** (осн. для таргета) → единый `docker-compose` (если есть) → один сервис (обратная совместимость). Масштаб/робастность — переиспользуем P1-H (изоляция по сервисам, отмена/таймауты, ограниченный параллелизм, капы, фильтр шума).

## Граф зависимостей вех

```
P1.5-0  спайк: discovery → системный GraphDto + rw-db + ОДНА реальная проблема (общая БД)  ← де-риск
  └─▶ P1.5-A  контракт (аддитивно: confidence/provenance/problems/health/env-модель) + зеркало
        ├─▶ P1.5-B  discovery-порт
        │     └─▶ P1.5-C  per-service compose → инфра
        │           └─▶ P1.5-D  config/DI/nginx → рёбра + confidence + provenance
        │                 └─▶ P1.5-E  сборка системного IR + Map (+ dedup код>объявление)
        │                       ├─▶ P1.5-P  детекция проблем + rollup здоровья  ← дифференциатор
        │                       └─▶ P1.5-F  члены (endpoints из Roslyn)
        │                             (P,F) ─▶ P1.5-G  фронт: зум + оболочка + панель проблем + маршрутизация рёбер
P1.5-H  робастность мульти-сервиса — сквозной, финализируется последним
```

## Вехи

### P1.5-0 — Спайк системного разбора (де-риск) — начать отсюда
На реальном `product-site-workspace`: `FolderDiscoverySource` находит 3 сервиса, per-service compose (инфра) + строки подключения → системный `GraphDto` (узлы `service|datastore`, рёбра `rw-db` с `Via`/`Confidence`) → и **всплывает хотя бы одна реальная системная проблема** (напр. общая БД). Без фронта. **Трогает**: `Analysis.Core` + новый `…Infrastructure.Manifests` + тесты.
**DoD**: узлы/рёбра **честные** (совпадают с реальностью таргета); каждое ребро несёт `Via`+`Confidence`; **обнаружена реальная проблема или честно показано, что её нет**; наведение на одиночный `auth_service` = прежний результат; `dotnet test` зелёный.

### P1.5-A — Контракт системного IR (аддитивно) — ✅ бэкенд
`Graph.cs`: kinds `service/datastore/topic/member/external`, edges `http-call/publishes/subscribes/rw-db/calls`, опц. `Service/Parent/Level/Members/Health`, `Via`+`Confidence`+`Violation`+`Problem` на ребре, `MemberDto`, `ProblemDto` + `Problems`/`Health` на графе, зарезервированный `EnvironmentDto`. **Зеркало `api.ts` перенесено в P1.5-G** (чтобы не пересекаться с фронт-работой; контракт аддитивен → фронт Фазы 1 работает без изменений). **DoD**: тест обратной совместимости (старый JSON Фазы 1 десериализуется; новые поля null; не пишутся при сериализации) ✔; `dotnet build` зелёный ✔ (`npm run build` — вместе с зеркалом в P1.5-G).

### P1.5-B — Обнаружение сервисов (discovery-порт)
`IServiceDiscoverySource` + `FolderDiscoverySource` + `ComposeDiscoverySource`; приоритет подпапки→compose→single. Domain: `SystemModel`. **DoD**: на таргете 3 сервиса (без шума); на `auth_service` → 1 сервис; тесты на оба.

### P1.5-C — Per-service compose → инфра-узлы
Образы, `${VAR:-default}`, порты, `volumes`→storage, `depends_on`; классификация app vs `datastore` vs `topic`. **DoD**: инфра совпадает с compose таргета; неизвестный образ → `unknown`, не выдумываем; тесты на фикстуре.

### P1.5-D — Config/DI/nginx → поведенческие рёбра (+ confidence, provenance)
`rw-db` (строки подключения), `publishes/subscribes` (шина/события), `http-call` (base URL/`SmsMicroserviceProvider`/nginx). Каждое ребро — `Via`+`Confidence`. Порты/секреты `${KEY}` → внешний узел. **DoD**: на таргете `rw-db` и, где видно, `http-call`/`publishes`; неразрешённый target → внешний/`unknown`; тесты «видно/не видно».

### P1.5-E — Сборка системного IR + Map-гейтвей — ✅
`GET /api/system?path=` (Analysis): `SystemGraphMapper` (SystemGraph → аддитивный `GraphDto`), `GetSystemHandler` на `ISystemSource`; `AddManifests` регистрирует источник. Map: `AnalysisClient.GetSystemAsync` + прокси `GET /api/system`. End-to-end проверено вживую (реальный таргет: 83 узла/72 ребра/health=warning). Тесты: маппер + хендлер + шов `AnalysisClient` (стаб транспорта). **Перенесено**: ограниченно-параллельный разбор/капы (P1.5-H), `/api/map` авто system-vs-service + проекция `OnlyService` (P1.5-G, вместе с фронтом). **DoD (сужен)**: системный граф по `/api/system` ✔; отмена (`ct`) до источника ✔; тест шва ✔.

### P1.5-P — Детекция проблем + rollup здоровья (дифференциатор) — срезы 1–5 ✅

**Срез 4 — нарушения направления слоёв ✅.** Свод-НЕЗАВИСИМАЯ арх-проблема (линза `architecture`, реальный Warning): в чистой архитектуре зависимости идут ВНУТРЬ (внешний слой → внутренний), ребро тип-графа из внутреннего во внешний (Domain→Infrastructure, Application→Api…) = нарушение. `LayerRules` (уровни: Domain/Contracts=0, Application=1, Infrastructure/Api=2; tests/tooling/unknown не судим) + `LayerViolationScanner` (per-service через общий `ServiceAnalyzer`). Включается `/api/system?layers=true` (независимо от `drift`; фронт включает линзе нужное). Рефактор: общий `ServiceAnalyzer` (discovery+Classify, только сервисы с compose, изоляция ошибок) — устраняет дублирование обхода между дрейфом и слоями; `DriftScanner` переведён на него. **На реальном таргете**: найдено 1 реальное нарушение — `BonusBalanceSnapshot (Application) → BonusSpendService (Infrastructure)`; остальные сервисы чисты (без флуда — честно). **DoD**: `LayerRules` покрыт таблично; синтетический Domain→Infrastructure → Warning, здоровое направление → пусто, tests/unknown игнор; флаг `layers` переключает линзу (юнит); проброс через Map (`?layers=`). Честность (ревью #23): одноимённые типы в разных слоях неоднозначны → слой не судим (детерминизм + без ложных); счётчик по уникальным парам From→To.

**Срез 5 — объединение проходов ✅.** Сканеры превращены в ЧИСТЫЕ детекторы над `IReadOnlyList<ServiceAnalysis>`: `DriftDetector`/`LayerViolationDetector` (static, `Detect(analyses)`). Хендлер гоняет `ServiceAnalyzer.Analyze` **один раз** при `drift||layers` и питает оба детектора по флагам → нет двойного Roslyn при обеих линзах (замер на таргете: обе вместе ~= одиночному проходу, не ×2). DI упрощён до одного `ServiceAnalyzer`; детекторы чисто-функциональны (тесты конструируют `ServiceAnalysis` напрямую, без стабов транспорта). Обе линзы вместе на таргете: 2 architecture (redis + layer-violation) + 3 convention (дрейф). **Отложено**: сиблинг-правила слоёв (Api↛Infrastructure, Domain↛Contracts); капы/параллелизм разбора при многих сервисах → P1.5-H.


**Срез 3 — две линзы проблем ✅.** Разделены «реальные проблемы» (свод-независимые арх-проблемы) и «дрейф от свода» (соответствие конвенциям) — категория на `ProblemDto` (`architecture` | `convention`), фронт фильтрует по линзе. Мотив: дрейф Фазы 1 = отклонение от ТВОЕГО свода; на ЧУЖОМ таргете это «другие конвенции», не арх-проблема (auth-service = 88 находок конвенц-шума). Смешать их в одну красную карту → утопить north-star. Решение: **дрейф — отдельная линза, opt-in**. `DriftScanner` (Core): по каждому найденному сервису гоняет Фаза-1 анализ (`ITypeSource`+`TypeClassifier`) → отклонения как проблемы `convention`/severity `Info` (не красит health), гранулярность .sln. Включается флагом `/api/system?drift=true` (тяжелее: разбор кода per-service — на таргете 0.67с без / 4.46с с дрейфом). Severity `Info` добавлен в модель+маппер. **На реальном таргете**: линза OFF — 1 арх-проблема (общий redis), health=warning; линза ON — та же арх-проблема + 3 дрейфа (AuthService 88, LoyaltyService 37, KalmykovGroup.Server 347), health не изменился. **DoD**: линза-флаг переключает convention-проблемы (юнит + реальный таргет); дрейф не красит health; арх-линза остаётся чистой. Контракт: `ProblemDto.Category` (аддитивно). Линза доступна фронту через Map (`AnalysisClient.GetSystemAsync(path, drift)` + Map-эндпоинт `?drift=`, adr/0010). `DriftScanner` сканирует только сервисы с compose (у остальных нет узлов → location не осиротеет); сбой разбора сервиса → честная Coverage-проблема `drift-scan-failed`, не тихий пропуск. Формула namespace вынесена в `SystemNamespace.Of` (общий источник для location дрейфа и id узлов). **Отложено**: точная привязка дрейфа к микросервису в моно-репо (сейчас .sln целиком — нужен маппинг compose-сервис→проект, из compose не достаётся); свод-независимый детектор нарушения слоёв как арх-проблема (отдельный срез); `drift=true` гоняет Roslyn per-service последовательно без капов/параллелизма — на крупном моно-репо (15–20 сервисов) это десятки секунд синхронной блокировки → ограниченный параллелизм/таймауты в P1.5-H.

**Срез 2 — событийный поток («висячие события») ✅.** Честная шина + каркас детектора (направление pub/sub из compose не достаётся — adr/0008). Часть 1: `ComposeSystemSource` распознаёт подключение к шине из `ConnectionStrings__*` И config-ключей (`RabbitMQ__Host`, `Kafka__BootstrapServers`, `amqp://…`) → НЕОРИЕНТИРОВАННОЕ ребро `uses-bus` к Topic того же compose (было `publishes` — переименовано в честное `uses-bus`, направление не выдумываем). Часть 2: `DanglingEventDetector` — `publishes` без `subscribes` → Warning `dangling-event`; `subscribes` без `publishes` → Warning `orphaned-subscription`; топик без рёбер → Coverage `isolated-topic`; только `uses-bus` → молчим (направление неизвестно). **На реальном таргете**: rabbitmq перестал быть изолированным — +5 `uses-bus` рёбер (72→77) от сервисов с `RabbitMQ__Host`; dangling/orphaned честно молчат (нет ориентированных рёбер). **DoD**: bus-host env связывает сервис с шиной (юнит + реальный таргет); каркас dangling/orphaned/isolated покрыт синтетикой; на таргете 0 ложных событийных проблем. **Активируется** ориентированными рёбрами `publishes`/`subscribes` из разбора кода (backlog). Контракт: добавлен edge-kind `uses-bus` (аддитивно).

**Срез 1 — общая БД + цикл сервисов ✅.** `SystemProblemDetector` (агрегатор) объединяет `SharedDbDetector` (общая БД/хранилище) + `ServiceCycleDetector` — циклы в `http-call` между Service-узлами через SCC (Тарьян), компонента ≥2 → Critical `service-cycle`, локализована на узел (health узла подсвечивается). Честность (ревью #20): цикл образуют только **уверенно разрешённые** вызовы — цель в том же compose (DNS compose-сети достоверен, conf 0.8); межкомпозное разрешение по совпадению имени неуверенно (0.5) и **не эскалируется в Critical** (иначе две несвязанные системы с одноимёнными сервисами дали бы ложный цикл). Проведён сквозь `ComposeSystemSource.Read`. **DoD (срез 1)**: на таргете честный 0 циклов; синтетический A↔B в одном compose фиксирует Critical (юнит + интеграция); межсистемный A↔B не даёт ложного цикла (регресс-тест); rollup `Health` из P1.5-E работает на новых проблемах.

**Отложено (backlog, отдельные срезы)**: направление pub/sub из кода → активирует `dangling-event`/`orphaned-subscription` (срез 2 — каркас готов); межсистемные циклы отдельным срезом с меньшей серьёзностью (Warning); health красит один узел из цикла, а не все участники → богатый рендер в P1.5-G; рекурсивный Тарьян без итеративного фолбэка → hardening в P1.5-H; подъём проблем Фазы 1 (дрейф/слои/покрытие) на уровень системы — нужны per-service тип-графы в системном IR (интеграция Roslyn); `Fix`-подсказки и пороги по `Confidence` в `ProblemDto`.

### P1.5-F — Член-уровень (endpoints)
`RoslynEndpointSource`: `[Http*]`/`ControllerBase`, minimal-API, `IEndpoint` → `Members` на type-узле + опц. member-узлы на drill. **DoD**: счётчик эндпоинтов совпадает со статикой; рантайм не запускаем; тесты на 3 способа объявления.

### P1.5-G — Фронт: зум + оболочка + проблемы + маршрутизация рёбер
Оболочка (5 механизмов): рендереры `ServiceNode/DatastoreNode/TopicNode/MemberNode/ExternalNode`; системный обзор над `layers`; крошки System→…→Member; drill re-fetch; **панель «Вид»** (тумблеры слоёв/видов рёбер/порога confidence/tests/контур), **панель «Проблемы»** (свёрнута в бейдж → список → клик к узлу), инспектор «почему/как чинить», **сворачивание в узле**, **стиль рёбер по confidence** (толщина/пунктир), **модуль маршрутизации рёбер** (по мотивам archlens). **DoD**: в браузере на таргете виден системный обзор с проблемами; drill в сервис/тип; тумблеры и панели работают; `npm run build/test/lint` зелёные.

**L0-сводка сервисов (бэкенд для карточек макета) ✅.** Макет L0 = карточки .sln-сервисов со строками-слоями и здоровьем каждого слоя, а `/api/system` давал плоские compose-узлы без слоёв. Добавлен `ServiceSummaryBuilder` (агрегирует ТОТ ЖЕ per-service Roslyn-анализ через `ServiceAnalyzer` — без второго прохода): по каждому найденному сервису → `ServiceSummaryDto{Id=namespace, Name, Health, Layers}` с `LayerSummaryDto{Name, Health, Types, Note}`. Health слоя красит **только нарушение направления слоя** (реальная арх-проблема); дрейф и роль-unknown — конвенции/покрытие, показываются в заметке, но health НЕ портят (инвариант линз, adr/0008 — ревью #26 High). Общая логика слоёв (`LayerByName`/`Violations`) вынесена в `LayerGraph` (единый источник с `LayerViolationDetector`). Аддитивно в контракт (`GraphDto.Services?`), приходит при код-линзах (`?drift=`/`?layers=`). **На реальном таргете**: 3 карточки — AuthService/LoyaltyService health=**ok** (дрейф в заметках слоёв), KalmykovGroup.Server health=**warning** только из-за реального нарушения (Application «→ Infrastructure · нарушение слоя»); дрейф Domain/Infrastructure — в заметках, health ok. Зеркало типов во фронте (`ServiceSummary`/`LayerSummary`). **DoD**: сводка на реальном таргете; per-layer health/note; аддитивность (round-trip, omit-when-null); фронт-типы. **Дальше**: L0-UI (карточки) поверх этой сводки.

**L0-UI (System Overview) — срез 1 ✅.** Оболочка по макету на React Flow: крошки System▸Service▸Layer▸Type▸Member; панель «Вид» (линза architecture/convention + легенда рёбер); **узлы = compose-микросервисы + инфра** (единый `SystemNodeView`: здоровье, бейдж проблем, вид по kind); рёбра по видам (rw-db зелёный, http-call оранж-пунктир, шина фиолет-пунктир); инспектор (проблемы выбранной линзы); статус-бар. `systemFlow.ts` — трансформ: узлы как есть (БЕЗ роллапа — граф вызовов между микросервисами сохранён, петли убраны), раскладка dagre TB. Режим в `Root` (Ф1 тип-граф ⇄ Ф1.5 система). **Модель L0 (решение пользователя)**: для монорепо (.sln с N микросервисами) показываем сами микросервисы + граф вызовов, а не 1 карточку-репозиторий; карточка-со-слоями (`ServiceSummary`) уходит в drill/L1. **Проверено вживую**: `product-site-workspace` (3 репо) и `product_site` (76 узлов = 48 микросервисов + инфра, 72 связи, redis ⚠ shared-store), health честный. Тесты `systemFlow.test.ts`; `npm run build/test/lint` зелёные. **Дальше (срез 2)**: раскладка для многих узлов (grid/LR/кластеризация); тумблеры слоёв/рёбер + порог confidence (прореживание hairball); drill L0→L1 (двойной клик → слои через `/api/map` + `ServiceSummary`); edge-violation красный по проблемам.

**Разделение работы**: визуальная оболочка — макет пользователя (Claude Design); реализацию агент строит по нему. За агентом — **не-визуальный слой данных**, к которому оболочка подключается (сделано, новыми файлами, без правки `api.ts`/`App.tsx`): `system.ts` — типы контракта Фазы 1.5 + `fetchSystem(path, {drift, layers})` (через Map, adr/0010; таймаут 120с под код-линзы); `systemModel.ts` — чистая логика: `problemsByLens` (architecture/convention; null→architecture), `worstSeverity`/`severityCounts` (порядок бэкенда), `problemsAt` (node-located) / `problemsUnder` (namespace-роллап дрейфа/слоёв), `nodesByKind` (L0-легенда), `namespaceOf`/`serviceRootPath` (сборка пути для drill L0→L1 через `/api/map`). Тесты (`systemModel.test.ts`), `npm run build/test/lint` зелёные.

### P1.5-H — Робастность мульти-сервиса (сквозной)
Изоляция по сервисам, ограниченный параллелизм, капы, сквозная отмена, флаг деградации в контракте, «укрощение» больших систем. Зеркалит P1-H. **DoD**: падение одного сервиса не рушит карту; abort фронта → Map → Analysis; деградация видима.

## С какой вехи начать и почему

**Начать с P1.5-0 (спайк).** Главный риск, меняющий всю форму: **можно ли из реального воркспейса без единого compose собрать правдивый системный граф И всплыть реальную проблему, не выдумывая связей.** Остальное — кальки существующих паттернов. Проблему (общую БД) и `rw-db` берём первыми, потому что они **надёжнее всего видны** (строка подключения) — минимум эвристики. Прямой аналог P0 (Фаза 1) и P2-0 (Фаза 2).

## Риски и открытые вопросы

1. **Таргет** = `product-site-workspace`: покрывает `service/datastore/rw-db/http-call/события`, **но нет единого compose и BFF-шлюза** (роль шлюза — `nginx-proxmox`). http-топология — из nginx + HTTP-клиентов.
2. **Эндпоинты — статически** (не рантайм), adr/0002.
3. **Резолв `http-call`**: base URL/имя → сервис по имени/порту (compose/nginx); неразрешимый → внешний узел или опустить.
4. **Якоря инфры**: `docker-compose.yml#Ln`.
5. **Пороги проблем**: какой `Confidence` считать достаточным для «нарушения» — калибруем на таргете, консервативно.

## Не входит в Фазу 1.5
Полная реализация окружений (топология/инстанс/профиль) — **модель в контракт сейчас, реализация отдельной вехой позже**. Другие языки; ИИ-правки (Фаза 2); дифф/дрейф между коммитами (Фаза 3); рантайм-скан/генерация OpenAPI (docker+`dotnet run`); собственный BFF-агрегатор.
