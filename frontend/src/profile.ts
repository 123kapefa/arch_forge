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
export interface Profile {
  level: string;
  overrides?: Record<string, string>;
  params?: ProfileParams;
  vocabulary?: Record<string, RoleVocabulary>;
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
export interface RuleDef {
  id: string;
  category: string;
  title: string;
  note: string;
  defaults: Record<string, string | null>;   // уровень → строгость; null = «как решает сам детектор»
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

async function request<T>(url: string, init?: RequestInit, signal?: AbortSignal): Promise<T> {
  const res = await fetchWithTimeout(url, TIMEOUT_MS, signal, init);
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

export type ImportResult = { ok: true; draft: ConfigManifest } | { ok: false; error: string };

/** Разбирает вставленный/загруженный JSON в черновик НОВОЙ конфигурации (полную проверку делает сервер при сохранении).
 *  Принимает файл обмена ({name, profile}) и «голый» профиль ({level, overrides…}). */
export function parseImport(text: string, author = 'local'): ImportResult {
  if (new Blob([text]).size > MAX_IMPORT_BYTES) return { ok: false, error: 'Файл слишком большой (не больше 256 КБ)' };
  let raw: unknown;
  try { raw = JSON.parse(text); } catch { return { ok: false, error: 'Это не JSON' }; }
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return { ok: false, error: 'Ожидается JSON-объект' };
  const o = raw as Record<string, unknown>;
  const profile = (o.profile ?? (typeof o.level === 'string' ? o : null)) as Profile | null;
  if (!profile || typeof profile !== 'object' || typeof profile.level !== 'string')
    return { ok: false, error: 'Не найден профиль: нужно поле profile.level или level' };
  const name = typeof o.name === 'string' && o.name.trim() ? o.name.trim() : 'Импортированная конфигурация';
  return { ok: true, draft: { id: null, name: name.slice(0, 100), author, version: 0, profile: clone(profile), kind: 'own' } };
}
