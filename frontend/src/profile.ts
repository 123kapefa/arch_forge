// Профиль проекта и конфигурации анализа (adr/0012): контракт, клиент API Map и чистая логика окна настройки.
// Контракт зеркалит ArchForge.Contracts/Profile.cs (System.Text.Json Web = camelCase). Валидацию делает сервер;
// здесь — только то, что нужно интерфейсу: «что изменено», сброс, правка списков, импорт/экспорт.

import { fetchWithTimeout } from './http';

// ── контракт ──

export type Level = 'Simple' | 'Standard' | 'Platform';
export type Strictness = 'Off' | 'Info' | 'Warning' | 'Error';

export const LEVELS: readonly Level[] = ['Simple', 'Standard', 'Platform'];
export const STRICTNESS: readonly Strictness[] = ['Off', 'Info', 'Warning', 'Error'];
export const DEFAULT_LEVEL: Level = 'Standard';

export const LEVEL_TITLE: Record<Level, string> = { Simple: 'простой', Standard: 'средний', Platform: 'высокий' };
export const STRICTNESS_TITLE: Record<Strictness, string> = { Off: 'выкл', Info: 'инфо', Warning: 'предупр.', Error: 'ошибка' };
export const CATEGORY_TITLE: Record<string, string> = { architecture: 'Архитектура', convention: 'Конвенции свода' };

export interface RoleVocabulary { markers?: string[]; fallback?: string[]; suffixes?: string[] }
export interface ServiceNaming { typeSuffixes?: string[]; migrationSuffixes?: string[]; satelliteMarkers?: string[] }
export interface ProfileParams {
  serviceCycleMinConfidence?: number;
  dtoMinSites?: number;
  dtoMinFiles?: number;
  naming?: ServiceNaming;
}
export type CustomTemplate = 'suffix-base' | 'layer-no-ref' | 'forbid-in-folder' | 'role-in-folder';
/** Своё правило из шаблона (adr/0012): только данные. Нужные поля зависят от шаблона. */
export interface CustomRule {
  id: string;
  template: CustomTemplate;
  title: string;
  strictness: Strictness;
  suffix?: string;
  baseType?: string;
  fromLayer?: string;
  toLayer?: string;
  folder?: string;
  role?: string;
}
export interface Profile {
  level: string;
  overrides?: Record<string, string>;
  params?: ProfileParams;
  vocabulary?: Record<string, RoleVocabulary>;
  customRules?: CustomRule[];
}
export interface ConfigManifest {
  id?: string | null;
  name: string;
  author: string;
  version: number;
  profile: Profile;
  kind?: string | null;          // level | own (назначает сервер)
  catalogVersion?: string | null;
  formatVersion?: number;
  parentId?: string | null;
  updatedAt?: string | null;
}
export interface Binding { configId?: string | null; level?: string | null }
export interface ProfileForPath { binding: Binding; config?: ConfigManifest | null; effective: Profile }
export interface RuleExample { bad: string[]; good: string[] }
export interface RuleDef {
  id: string;
  category: string;
  title: string;
  note: string;
  defaults: Record<string, string | null>;   // уровень → строгость; null = «как решает сам детектор»
  example?: RuleExample | null;
}

// Предпросмотр влияния профиля (ответ POST /api/profile/preview).
export interface RuleImpact { count: number; enabled: boolean }
export interface ImpactSummary { errors: number; warnings: number; infos: number; rules: Record<string, RuleImpact> }
export interface LevelHint { level: string; reasons: string[] }
export interface Preview {
  current: ImpactSummary;
  baseline: ImpactSummary;        // чистый уровень того же профиля, без отличий
  hint: LevelHint;
  markerCounts: Record<string, number>;
  suffixCounts: Record<string, number>;
}
export interface RuleCatalog { version: string; rules: RuleDef[] }
export interface VocabularyDefaults {
  roles: Record<string, RoleVocabulary>;
  naming: ServiceNaming;
  params: ProfileParams;
}

// ── клиент API (через Map) ──

export class ProfileApiError extends Error {
  status: number;
  errors: string[];
  constructor(status: number, errors: string[]) {
    super(errors[0] ?? `Ошибка Map (${status})`);
    this.status = status;
    this.errors = errors;
  }
}

const TIMEOUT_MS = 20_000;
const PREVIEW_TIMEOUT_MS = 120_000;   // первый предпросмотр читает весь код проекта; дальше идёт из кеша

async function request<T>(url: string, init?: RequestInit, signal?: AbortSignal, timeoutMs = TIMEOUT_MS): Promise<T> {
  const res = await fetchWithTimeout(url, timeoutMs, signal, init);
  if (!res.ok) {
    let errors: string[] = [];
    try {
      const body = (await res.json()) as { error?: string; errors?: string[] };
      errors = body.errors?.length ? body.errors : body.error ? [body.error] : [];
    } catch { /* тело не JSON */ }
    throw new ProfileApiError(res.status, errors.length ? errors : [`Ошибка Map (${res.status})`]);
  }
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

const JSON_HEADERS = { 'Content-Type': 'application/json' };
const body = (v: unknown): RequestInit => ({ headers: JSON_HEADERS, body: JSON.stringify(v) });

export const fetchRules = (signal?: AbortSignal) => request<RuleCatalog>('/api/rules', undefined, signal);
export const fetchVocabulary = (signal?: AbortSignal) => request<VocabularyDefaults>('/api/vocabulary', undefined, signal);
export const listConfigs = (signal?: AbortSignal) => request<ConfigManifest[]>('/api/profile/configs', undefined, signal);
export const createConfig = (c: ConfigManifest) =>
  request<ConfigManifest>('/api/profile/configs', { method: 'POST', ...body(c) });
export const updateConfig = (id: string, c: ConfigManifest) =>
  request<ConfigManifest>(`/api/profile/configs/${encodeURIComponent(id)}`, { method: 'PUT', ...body(c) });
export const deleteConfig = (id: string) =>
  request<void>(`/api/profile/configs/${encodeURIComponent(id)}`, { method: 'DELETE' });
export const duplicateConfig = (id: string, author?: string) =>
  request<ConfigManifest>(`/api/profile/configs/${encodeURIComponent(id)}/duplicate${author ? `?author=${encodeURIComponent(author)}` : ''}`, { method: 'POST' });
export const fetchPreview = (path: string, profile: Profile, signal?: AbortSignal) =>
  request<Preview>(`/api/profile/preview?path=${encodeURIComponent(path)}`, { method: 'POST', ...body(profile) }, signal, PREVIEW_TIMEOUT_MS);
export const fetchProjectProfile = (path: string, signal?: AbortSignal) =>
  request<ProfileForPath>(`/api/profile?path=${encodeURIComponent(path)}`, undefined, signal);
export const bindProject = (path: string, binding: Binding) =>
  request<ProfileForPath>(`/api/profile?path=${encodeURIComponent(path)}`, { method: 'PUT', ...body(binding) });

// ── строгость правил ──

const isStrictness = (s: string | null | undefined): s is Strictness => !!s && (STRICTNESS as readonly string[]).includes(s);

/** Строгость правила на уровне профиля по умолчанию; null — «как решает сам детектор». */
export function levelDefault(rule: RuleDef, level: string): Strictness | null {
  const v = rule.defaults[level];
  return isStrictness(v) ? v : null;
}

/** Текущая строгость: переопределение → умолчание уровня → null (родная серьёзность). */
export function effectiveStrictness(rule: RuleDef, p: Profile): { value: Strictness | null; overridden: boolean } {
  const o = p.overrides?.[rule.id];
  if (isStrictness(o)) return { value: o, overridden: true };
  return { value: levelDefault(rule, p.level), overridden: false };
}

/** Параметры правил, которые можно двигать в окне (порог → ключ профиля). */
export interface ParamSpec {
  key: 'serviceCycleMinConfidence' | 'dtoMinSites' | 'dtoMinFiles';
  label: string;
  min: number;
  max: number;
  step: number;
  hint: string;
}
export const RULE_PARAMS: Record<string, ParamSpec[]> = {
  'service-cycle': [
    { key: 'serviceCycleMinConfidence', label: 'Порог уверенности связи', min: 0.5, max: 1, step: 0.05,
      hint: 'Цикл образуют только вызовы не слабее порога. Ниже — больше догадок.' },
  ],
  'dto-mapping': [
    { key: 'dtoMinSites', label: 'Мест сборки DTO — не меньше', min: 1, max: 20, step: 1,
      hint: 'DTO собирается через new вне маппера в стольких местах и больше — замечание.' },
    { key: 'dtoMinFiles', label: 'Файлов — не меньше', min: 1, max: 20, step: 1,
      hint: 'и в стольких файлах.' },
  ],
};

function clone<T>(v: T): T { return JSON.parse(JSON.stringify(v)) as T; }
function prune(p: Profile): Profile {
  if (p.overrides && !Object.keys(p.overrides).length) delete p.overrides;
  if (p.params) {
    if (p.params.naming && !Object.keys(p.params.naming).length) delete p.params.naming;
    if (!Object.keys(p.params).length) delete p.params;
  }
  if (p.vocabulary && !Object.keys(p.vocabulary).length) delete p.vocabulary;
  if (p.customRules && !p.customRules.length) delete p.customRules;
  return p;
}

/** Ставит строгость правила. Значение, совпадающее с умолчанием уровня (или null), снимает переопределение. */
export function withStrictness(p: Profile, rule: RuleDef, value: Strictness | null): Profile {
  const next = clone(p);
  const def = levelDefault(rule, next.level);
  if (value === null || value === def) { if (next.overrides) delete next.overrides[rule.id]; }
  else (next.overrides ??= {})[rule.id] = value;
  return prune(next);
}

export function withParam(p: Profile, key: ParamSpec['key'], value: number, defaults: ProfileParams): Profile {
  const next = clone(p);
  if (value === defaults[key]) { if (next.params) delete next.params[key]; }
  else (next.params ??= {})[key] = value;
  return prune(next);
}

export function paramValue(p: Profile, key: ParamSpec['key'], defaults: ProfileParams): number {
  return p.params?.[key] ?? defaults[key] ?? 0;
}

/** Правило отличается от умолчания уровня: переопределена строгость или сдвинут порог. */
export function isRuleChanged(rule: RuleDef, p: Profile, defaults: ProfileParams): boolean {
  if (effectiveStrictness(rule, p).overridden) return true;
  return (RULE_PARAMS[rule.id] ?? []).some((s) => p.params?.[s.key] !== undefined && p.params[s.key] !== defaults[s.key]);
}

export function changedRuleCount(rules: RuleDef[], p: Profile, defaults: ProfileParams): number {
  return rules.filter((r) => isRuleChanged(r, p, defaults)).length;
}

export function resetRule(p: Profile, rule: RuleDef): Profile {
  const next = clone(p);
  if (next.overrides) delete next.overrides[rule.id];
  for (const s of RULE_PARAMS[rule.id] ?? []) if (next.params) delete next.params[s.key];
  return prune(next);
}

export function resetCategory(p: Profile, rules: RuleDef[], category: string): Profile {
  return rules.filter((r) => r.category === category).reduce((acc, r) => resetRule(acc, r), p);
}

/** Убирает все отличия (правила, пороги, словарь): остаётся чистый уровень. */
export function resetToLevel(p: Profile): Profile { return { level: p.level }; }

/** Меняет базовый уровень, сохраняя отличия. */
export function withLevel(p: Profile, level: string): Profile { return prune({ ...clone(p), level }); }

// ── словарь ролей и имена сервисов ──

export type VocabKey = 'markers' | 'fallback' | 'suffixes';
export const VOCAB_KEYS: readonly VocabKey[] = ['markers', 'fallback', 'suffixes'];

const same = (a?: string[], b?: string[]) => JSON.stringify(a ?? []) === JSON.stringify(b ?? []);

/** Список роли: переопределение (непустое) иначе встроенный. Пустой список на сервере = «оставить встроенное». */
export function roleList(p: Profile, defaults: VocabularyDefaults, role: string, key: VocabKey): string[] {
  const o = p.vocabulary?.[role]?.[key];
  return o && o.length ? o : (defaults.roles[role]?.[key] ?? []);
}

export function isRoleChanged(p: Profile, defaults: VocabularyDefaults, role: string): boolean {
  const v = p.vocabulary?.[role];
  return !!v && VOCAB_KEYS.some((k) => v[k] && v[k].length && !same(v[k], defaults.roles[role]?.[k]));
}

export function withRoleList(p: Profile, defaults: VocabularyDefaults, role: string, key: VocabKey, list: string[]): Profile {
  const next = clone(p);
  const v = (next.vocabulary ??= {});
  const entry = (v[role] ??= {});
  if (!list.length || same(list, defaults.roles[role]?.[key])) delete entry[key]; // пусто/как встроенное → снимаем
  else entry[key] = list;
  if (!Object.keys(entry).length) delete v[role];
  return prune(next);
}

export function resetRole(p: Profile, role: string): Profile {
  const next = clone(p);
  if (next.vocabulary) delete next.vocabulary[role];
  return prune(next);
}

export type NamingKey = 'typeSuffixes' | 'migrationSuffixes' | 'satelliteMarkers';
export const NAMING_KEYS: readonly NamingKey[] = ['typeSuffixes', 'migrationSuffixes', 'satelliteMarkers'];
export const NAMING_TITLE: Record<NamingKey, { title: string; hint: string }> = {
  typeSuffixes: { title: 'Суффиксы типа сервиса', hint: '«-api», «-service»…: снимаются, когда «auth-api» и «auth-migrate» сводятся к одному приложению.' },
  migrationSuffixes: { title: 'Суффиксы миграторов', hint: 'Снимаются раньше суффикса типа.' },
  satelliteMarkers: { title: 'Маркеры сателлитов', hint: 'Подстроки имён деплоев, которые не несут код сервиса (миграторы, сидеры, крон).' },
};

export function namingList(p: Profile, defaults: VocabularyDefaults, key: NamingKey): string[] {
  const o = p.params?.naming?.[key];
  return o && o.length ? o : (defaults.naming[key] ?? []);
}

export function isNamingChanged(p: Profile, defaults: VocabularyDefaults, key: NamingKey): boolean {
  const o = p.params?.naming?.[key];
  return !!o && o.length > 0 && !same(o, defaults.naming[key]);
}

export function withNaming(p: Profile, defaults: VocabularyDefaults, key: NamingKey, list: string[]): Profile {
  const next = clone(p);
  const naming = ((next.params ??= {}).naming ??= {});
  if (!list.length || same(list, defaults.naming[key])) delete naming[key];
  else naming[key] = list;
  return prune(next);
}

export function resetNaming(p: Profile, key: NamingKey): Profile {
  const next = clone(p);
  if (next.params?.naming) delete next.params.naming[key];
  return prune(next);
}

/** Добавляет запись в список без дублей; пустые и слишком длинные отбрасывает. */
export function addItem(list: string[], raw: string, maxLen = 64): string[] {
  const v = raw.trim();
  if (!v || v.length > maxLen || list.some((x) => x.toLowerCase() === v.toLowerCase())) return list;
  return [...list, v];
}
export const removeItem = (list: string[], i: number): string[] => list.filter((_, idx) => idx !== i);

// ── сравнение, фильтры ──

/** Профили равны по содержанию (порядок ключей не важен). */
export function profilesEqual(a: Profile, b: Profile): boolean {
  const norm = (v: unknown): unknown =>
    Array.isArray(v) ? v.map(norm)
    : v && typeof v === 'object' ? Object.fromEntries(Object.entries(v as Record<string, unknown>).sort(([x], [y]) => x.localeCompare(y)).map(([k, x]) => [k, norm(x)]))
    : v;
  return JSON.stringify(norm(prune(clone(a)))) === JSON.stringify(norm(prune(clone(b))));
}

export type RuleFilter = 'all' | 'changed' | 'off';

export function filterRules(rules: RuleDef[], p: Profile, defaults: ProfileParams, q: string, filter: RuleFilter): RuleDef[] {
  const needle = q.trim().toLowerCase();
  return rules.filter((r) => {
    if (needle && !`${r.title} ${r.note} ${r.id}`.toLowerCase().includes(needle)) return false;
    if (filter === 'changed') return isRuleChanged(r, p, defaults);
    if (filter === 'off') return effectiveStrictness(r, p).value === 'Off';
    return true;
  });
}

export function groupByCategory(rules: RuleDef[]): { category: string; rules: RuleDef[] }[] {
  const order = ['architecture', 'convention'];
  const cats = [...new Set(rules.map((r) => r.category))].sort((a, b) => {
    const ia = order.indexOf(a), ib = order.indexOf(b);
    return (ia < 0 ? 99 : ia) - (ib < 0 ? 99 : ib) || a.localeCompare(b);
  });
  return cats.map((category) => ({ category, rules: rules.filter((r) => r.category === category) }));
}

// ── импорт и экспорт (JSON) ──

export const MAX_IMPORT_BYTES = 256 * 1024;

/** Файл обмена: манифест без служебных полей сервера (id, kind, updatedAt). */
export function exportManifest(c: ConfigManifest): string {
  return JSON.stringify({
    format: c.formatVersion ?? 1,
    name: c.name,
    author: c.author,
    version: c.version,
    catalogVersion: c.catalogVersion ?? null,
    profile: c.profile,
  }, null, 2);
}

const isStrArray = (v: unknown): v is string[] => Array.isArray(v) && v.every((x) => typeof x === 'string');
const strList = (v: unknown): string[] | undefined => (isStrArray(v) ? v : undefined);

/** Оставляет в профиле только допустимую форму: чужой JSON не должен нести значения не тех типов (иначе окно упадёт
 *  при отрисовке). Лишние поля и ключи не того вида отбрасываются; содержательную проверку делает сервер при сохранении. */
export function sanitizeProfile(raw: unknown): Profile | null {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return null;
  const r = raw as Record<string, unknown>;
  if (typeof r.level !== 'string') return null;
  const out: Profile = { level: r.level };

  if (r.overrides && typeof r.overrides === 'object' && !Array.isArray(r.overrides)) {
    const ov: Record<string, string> = {};
    for (const [k, v] of Object.entries(r.overrides)) if (typeof v === 'string') ov[k] = v;
    if (Object.keys(ov).length) out.overrides = ov;
  }

  if (r.params && typeof r.params === 'object' && !Array.isArray(r.params)) {
    const p = r.params as Record<string, unknown>;
    const params: ProfileParams = {};
    for (const k of ['serviceCycleMinConfidence', 'dtoMinSites', 'dtoMinFiles'] as const)
      if (typeof p[k] === 'number' && Number.isFinite(p[k])) params[k] = p[k];
    if (p.naming && typeof p.naming === 'object' && !Array.isArray(p.naming)) {
      const n = p.naming as Record<string, unknown>;
      const naming: ServiceNaming = {};
      for (const k of NAMING_KEYS) { const l = strList(n[k]); if (l) naming[k] = l; }
      if (Object.keys(naming).length) params.naming = naming;
    }
    if (Object.keys(params).length) out.params = params;
  }

  if (r.vocabulary && typeof r.vocabulary === 'object' && !Array.isArray(r.vocabulary)) {
    const voc: Record<string, RoleVocabulary> = {};
    for (const [role, v] of Object.entries(r.vocabulary)) {
      if (!v || typeof v !== 'object' || Array.isArray(v)) continue;
      const e = v as Record<string, unknown>;
      const entry: RoleVocabulary = {};
      for (const k of VOCAB_KEYS) { const l = strList(e[k]); if (l) entry[k] = l; }
      if (Object.keys(entry).length) voc[role] = entry;
    }
    if (Object.keys(voc).length) out.vocabulary = voc;
  }

  if (Array.isArray(r.customRules)) {
    const seen = new Set<string>();
    const rules = r.customRules.map(sanitizeCustomRule)
      .filter((x): x is CustomRule => x !== null && !seen.has(x.id) && !!seen.add(x.id)).slice(0, MAX_CUSTOM_RULES);
    if (rules.length) out.customRules = rules;
  }
  return out;
}

export type ImportResult = { ok: true; draft: ConfigManifest } | { ok: false; error: string };

/** Разбирает вставленный/загруженный JSON в черновик НОВОЙ конфигурации (полную проверку делает сервер при сохранении).
 *  Принимает файл обмена ({name, profile}) и «голый» профиль ({level, overrides…}). */
export function parseImport(text: string, author = 'local'): ImportResult {
  if (new Blob([text]).size > MAX_IMPORT_BYTES) return { ok: false, error: 'Файл слишком большой (не больше 256 КБ)' };
  let raw: unknown;
  try { raw = JSON.parse(text); } catch { return { ok: false, error: 'Это не JSON' }; }
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return { ok: false, error: 'Ожидается JSON-объект' };
  const o = raw as Record<string, unknown>;
  const profile = sanitizeProfile(o.profile ?? (typeof o.level === 'string' ? o : null));
  if (!profile) return { ok: false, error: 'Не найден профиль: нужно поле profile.level или level' };
  const name = typeof o.name === 'string' && o.name.trim() ? o.name.trim() : 'Импортированная конфигурация';
  return { ok: true, draft: { id: null, name: name.slice(0, 100), author, version: 0, profile, kind: 'own' } };
}

// ── предпросмотр: чистая логика ──

export const impactTotal = (s: ImpactSummary): number => s.errors + s.warnings + s.infos;

/** Сколько находок даёт правило и проверяется ли оно. Нет данных (предпросмотр не пришёл) → null. */
export function ruleImpact(p: Preview | null, current: boolean, ruleId: string): RuleImpact | null {
  return (current ? p?.current : p?.baseline)?.rules[ruleId] ?? null;
}

/** Разница находок с чистым уровнем: положительная — профиль добавляет находки. */
export function impactDelta(p: Preview): number { return impactTotal(p.current) - impactTotal(p.baseline); }

/** Роль: сколько типов с её суффиксами в проекте и сколько из них узнано по маркерам роли. Нет данных → null. */
export function roleCoverage(p: Preview | null, markers: string[], suffixes: string[]): { total: number; marked: number } | null {
  if (!p || !suffixes.length) return null;
  const total = suffixes.reduce((n, s) => n + (p.suffixCounts[s] ?? 0), 0);
  const marked = Math.min(total, markers.reduce((n, m) => n + (p.markerCounts[m] ?? 0), 0));
  return { total, marked };
}

/** Живой пример порога: на выдуманных данных видно, какие строки попадают под правило при выбранном значении. */
export interface SampleRow { label: string; value: string; hit: boolean }
export function paramSample(ruleId: string, key: ParamSpec['key'], value: number): SampleRow[] | null {
  if (ruleId === 'service-cycle' && key === 'serviceCycleMinConfidence') {
    return [
      { label: 'orders → billing → orders', c: 0.95 }, { label: 'courses → users → courses', c: 0.85 },
      { label: 'notify → orders → notify', c: 0.7 }, { label: 'search → courses → search', c: 0.55 },
    ].map((r) => ({ label: r.label, value: r.c.toFixed(2), hit: r.c >= value - 1e-9 }));
  }
  if (ruleId === 'dto-mapping' && key === 'dtoMinSites') {
    return [{ label: 'UserDto', n: 7 }, { label: 'OrderDto', n: 4 }, { label: 'CourseDto', n: 3 }, { label: 'LessonDto', n: 2 }]
      .map((r) => ({ label: r.label, value: `${r.n} мест`, hit: r.n >= value }));
  }
  if (ruleId === 'dto-mapping' && key === 'dtoMinFiles') {
    return [{ label: 'UserDto', n: 5 }, { label: 'OrderDto', n: 3 }, { label: 'CourseDto', n: 1 }]
      .map((r) => ({ label: r.label, value: `${r.n} файл.`, hit: r.n >= value }));
  }
  return null;
}


// ── свои правила (шаблоны без кода) ──

export const MAX_CUSTOM_RULES = 32;
export const CUSTOM_FIELDS = ['suffix', 'baseType', 'fromLayer', 'toLayer', 'folder', 'role'] as const;
export type CustomField = typeof CUSTOM_FIELDS[number];

/** Часть предложения шаблона: обычный текст или поле ввода. */
export type SentencePart = string | { field: CustomField; placeholder: string; list?: 'layers' | 'roles' };

export interface TemplateSpec {
  id: CustomTemplate;
  title: string;          // короткое название для выбора
  hint: string;           // когда это нужно
  sentence: SentencePart[];
  required: CustomField[][];   // каждая группа: достаточно ОДНОГО заполненного поля из группы
}

export const LAYER_NAMES = ['Domain', 'Application', 'Infrastructure', 'Api', 'Contracts', 'Shared'] as const;

export const CUSTOM_TEMPLATES: readonly TemplateSpec[] = [
  { id: 'suffix-base', title: 'Суффикс → базовый тип', hint: 'Все типы с определённым окончанием имени обязаны наследовать нужный тип или интерфейс.',
    sentence: ['Тип, имя которого оканчивается на', { field: 'suffix', placeholder: 'Handler' }, 'должен наследовать', { field: 'baseType', placeholder: 'IHandler' }],
    required: [['suffix'], ['baseType']] },
  { id: 'layer-no-ref', title: 'Слой не ссылается на слой', hint: 'Запрет зависимости одного слоя от другого, помимо встроенной проверки направления слоёв.',
    sentence: ['Слой', { field: 'fromLayer', placeholder: 'Domain', list: 'layers' }, 'не должен ссылаться на слой', { field: 'toLayer', placeholder: 'Infrastructure', list: 'layers' }],
    required: [['fromLayer'], ['toLayer']] },
  { id: 'forbid-in-folder', title: 'Запретить тип в папке', hint: 'Типы с таким окончанием имени и базой не должны лежать в указанной папке. Заполните одно поле или оба (тогда нужны оба условия сразу).',
    sentence: ['Тип с окончанием', { field: 'suffix', placeholder: 'Dto' }, 'и базой', { field: 'baseType', placeholder: 'IDto' }, '(хватит одного из двух) не должен лежать в папке', { field: 'folder', placeholder: 'Domain' }],
    required: [['suffix', 'baseType'], ['folder']] },
  { id: 'role-in-folder', title: 'Роль живёт в папке', hint: 'Типы определённой роли свода обязаны лежать в указанной папке.',
    sentence: ['Тип роли', { field: 'role', placeholder: 'Command', list: 'roles' }, 'должен лежать в папке', { field: 'folder', placeholder: 'Features' }],
    required: [['role'], ['folder']] },
];

export const templateSpec = (t: string): TemplateSpec | undefined => CUSTOM_TEMPLATES.find((x) => x.id === t);
const isTemplate = (t: unknown): t is CustomTemplate => CUSTOM_TEMPLATES.some((x) => x.id === t);
const filled = (v: string | undefined): boolean => !!v && v.trim().length > 0;

/** Все ли обязательные поля заполнены (иначе правило не сохранится: сервер вернёт ошибку). */
export function isCustomComplete(r: CustomRule): boolean {
  const spec = templateSpec(r.template);
  return !!spec && r.title.trim().length > 0 && spec.required.every((g) => g.some((f) => filled(r[f])));
}

/** Свободный идентификатор вида «rule-3». */
export function nextCustomId(rules: CustomRule[]): string {
  const used = new Set(rules.map((r) => r.id));
  for (let i = 1; ; i++) if (!used.has(`rule-${i}`)) return `rule-${i}`;
}

const TEMPLATE_TITLE: Record<CustomTemplate, string> = {
  'suffix-base': 'Суффикс → базовый тип', 'layer-no-ref': 'Слой не ссылается на слой',
  'forbid-in-folder': 'Запрет типа в папке', 'role-in-folder': 'Роль живёт в папке',
};

export function newCustomRule(template: CustomTemplate, existing: CustomRule[]): CustomRule {
  return { id: nextCustomId(existing), template, title: TEMPLATE_TITLE[template], strictness: 'Warning' };
}

export function withCustomRule(p: Profile, rule: CustomRule): Profile {
  const next = clone(p);
  const list = next.customRules ?? [];
  const i = list.findIndex((r) => r.id === rule.id);
  if (i >= 0) list[i] = rule; else if (list.length < MAX_CUSTOM_RULES) list.push(rule);
  next.customRules = list;
  return prune(next);
}

export function withoutCustomRule(p: Profile, id: string): Profile {
  const next = clone(p);
  next.customRules = (next.customRules ?? []).filter((r) => r.id !== id);
  return prune(next);
}

/** Имя находки в проблемах и в предпросмотре влияния. */
export const FIELD_TITLE: Record<CustomField, string> = {
  suffix: 'окончание имени', baseType: 'базовый тип', fromLayer: 'слой-источник', toLayer: 'слой-цель', folder: 'папка', role: 'роль',
};

/** Профиль без незаконченных правил: для предпросмотра (сервер отклонил бы весь профиль из-за одного пустого поля). */
export function completeOnly(p: Profile): Profile {
  if (!p.customRules) return p;
  return prune({ ...p, customRules: p.customRules.filter(isCustomComplete) });
}

export const customKind = (id: string): string => `custom:${id}`;

/** Пример «плохо / хорошо», собранный из значений правила (пока поля пусты — подсказки шаблона). */
export function customExample(r: CustomRule): RuleExample {
  const v = (x: string | undefined, d: string) => (filled(x) ? x!.trim() : d);
  switch (r.template) {
    case 'suffix-base': {
      const s = v(r.suffix, 'Handler'), b = v(r.baseType, 'IHandler');
      return { bad: [`public class Create${s}            // нет ${b}`], good: [`public class Create${s} : ${b}`] };
    }
    case 'layer-no-ref': {
      const f = v(r.fromLayer, 'Domain'), t = v(r.toLayer, 'Infrastructure');
      return { bad: [`// слой ${f}`, `using MyApp.${t};     // зависит от ${t}`], good: [`// слой ${f}`, `// зависит только от внутренних слоёв, без ${t}`] };
    }
    case 'forbid-in-folder': {
      const name = filled(r.suffix) ? `Order${r.suffix!.trim()}` : `Order${v(r.baseType, 'Dto')}`;
      const f = v(r.folder, 'Domain');
      return { bad: [`${f}/${name}.cs        // тип в запрещённой папке`], good: [`Contracts/${name}.cs   // тип вне папки ${f}`] };
    }
    case 'role-in-folder': {
      const role = v(r.role, 'Command'), f = v(r.folder, 'Features');
      return { bad: [`Other/Create${role}.cs       // роль ${role} вне папки ${f}`], good: [`${f}/Create${role}.cs`] };
    }
  }
}

/** Оставляет только допустимую форму чужого правила (JSON из импорта); недопустимое отбрасывает. */
export function sanitizeCustomRule(raw: unknown): CustomRule | null {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return null;
  const r = raw as Record<string, unknown>;
  if (typeof r.id !== 'string' || typeof r.title !== 'string' || !isTemplate(r.template)) return null;
  const strictness = STRICTNESS.find((x) => typeof r.strictness === 'string' && x.toLowerCase() === r.strictness.toLowerCase());
  if (!strictness) return null;
  if (!/^[a-z0-9-]{1,40}$/.test(r.id)) return null;
  const out: CustomRule = { id: r.id, template: r.template, title: r.title.slice(0, 100), strictness };
  for (const f of CUSTOM_FIELDS) if (typeof r[f] === 'string' && (r[f] as string).trim()) out[f] = (r[f] as string).trim().slice(0, 64);
  return out;
}
