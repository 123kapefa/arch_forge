import { describe, it, expect, vi, afterEach } from 'vitest';
import {
  type Profile, type RuleDef, type VocabularyDefaults, type ProfileParams,
  levelDefault, effectiveStrictness, withStrictness, withParam, paramValue, isRuleChanged, changedRuleCount,
  resetRule, resetCategory, resetToLevel, withLevel, roleList, isRoleChanged, withRoleList, resetRole,
  namingList, isNamingChanged, withNaming, resetNaming, addItem, removeItem, profilesEqual, filterRules, groupByCategory,
  newCustomRule, withCustomRule, withoutCustomRule, isCustomComplete, customExample, customKind, CUSTOM_TEMPLATES, MAX_CUSTOM_RULES, completeOnly,
  exportManifest, parseImport, sanitizeProfile, impactTotal, ruleImpact, impactDelta, roleCoverage, paramSample, fetchPreview, type Preview, ProfileApiError, createConfig, updateConfig, bindProject, fetchProjectProfile,
} from './profile';

const rule = (id: string, category: string, defaults: Record<string, string | null>, title = id): RuleDef =>
  ({ id, category, title, note: 'заметка', defaults });

const DRIFT = rule('drift', 'convention', { Simple: 'Off', Standard: null, Platform: null }, 'Расхождение со сводом');
const CYCLE = rule('service-cycle', 'architecture', { Simple: null, Standard: null, Platform: 'Error' }, 'Циклы между сервисами');
const DTO = rule('dto-mapping', 'convention', { Simple: 'Off', Standard: 'Info', Platform: 'Warning' }, 'DTO инлайн');
const RULES = [DRIFT, CYCLE, DTO];

const PARAMS: ProfileParams = { serviceCycleMinConfidence: 0.8, dtoMinSites: 3, dtoMinFiles: 3 };
const VOCAB: VocabularyDefaults = {
  roles: {
    Command: { markers: ['ICommand'], fallback: [], suffixes: ['Command'] },
    Handler: { markers: ['ICommandHandler', 'IQueryHandler'], fallback: ['IRequestHandler'], suffixes: ['Handler'] },
  },
  naming: { typeSuffixes: ['-api', '-service'], migrationSuffixes: ['-migrate'], satelliteMarkers: ['migrat', 'seed'] },
  params: PARAMS,
};

describe('строгость правил', () => {
  it('умолчание уровня; null = родная серьёзность детектора', () => {
    expect(levelDefault(DRIFT, 'Simple')).toBe('Off');
    expect(levelDefault(DRIFT, 'Standard')).toBeNull();
    expect(levelDefault(DRIFT, 'Unknown')).toBeNull();
  });

  it('переопределение сильнее умолчания, источник различается', () => {
    expect(effectiveStrictness(DRIFT, { level: 'Simple' })).toEqual({ value: 'Off', overridden: false });
    expect(effectiveStrictness(DRIFT, { level: 'Simple', overrides: { drift: 'Warning' } })).toEqual({ value: 'Warning', overridden: true });
  });

  it('значение, равное умолчанию уровня, не хранится как переопределение', () => {
    const p = withStrictness({ level: 'Platform' }, CYCLE, 'Error');
    expect(p.overrides).toBeUndefined();
    expect(withStrictness({ level: 'Platform' }, CYCLE, 'Info').overrides).toEqual({ 'service-cycle': 'Info' });
  });

  it('смена уровня сохраняет отличия и пересчитывает «переопределено»', () => {
    const p = withLevel(withStrictness({ level: 'Standard' }, DRIFT, 'Warning'), 'Simple');
    expect(p.level).toBe('Simple');
    expect(effectiveStrictness(DRIFT, p).overridden).toBe(true);
  });

  it('правка не мутирует исходный профиль', () => {
    const p: Profile = { level: 'Standard', overrides: { drift: 'Info' } };
    withStrictness(p, DRIFT, 'Error');
    expect(p.overrides).toEqual({ drift: 'Info' });
  });

  it('null снимает переопределение', () => {
    const p = withStrictness({ level: 'Standard', overrides: { drift: 'Info' } }, DRIFT, null);
    expect(p).toEqual({ level: 'Standard' });
  });
});

describe('пороги', () => {
  it('значение по умолчанию не хранится; иное — хранится', () => {
    expect(withParam({ level: 'Standard' }, 'dtoMinSites', 3, PARAMS).params).toBeUndefined();
    expect(withParam({ level: 'Standard' }, 'dtoMinSites', 5, PARAMS).params).toEqual({ dtoMinSites: 5 });
  });

  it('paramValue берёт профиль, иначе умолчание', () => {
    expect(paramValue({ level: 'Standard' }, 'serviceCycleMinConfidence', PARAMS)).toBe(0.8);
    expect(paramValue({ level: 'Standard', params: { serviceCycleMinConfidence: 0.9 } }, 'serviceCycleMinConfidence', PARAMS)).toBe(0.9);
  });

  it('сдвинутый порог делает правило «изменённым»', () => {
    expect(isRuleChanged(CYCLE, { level: 'Standard' }, PARAMS)).toBe(false);
    expect(isRuleChanged(CYCLE, withParam({ level: 'Standard' }, 'serviceCycleMinConfidence', 0.9, PARAMS), PARAMS)).toBe(true);
    // порог чужого правила не задевает
    expect(isRuleChanged(DRIFT, withParam({ level: 'Standard' }, 'serviceCycleMinConfidence', 0.9, PARAMS), PARAMS)).toBe(false);
  });
});

describe('сброс', () => {
  const edited: Profile = {
    level: 'Standard',
    overrides: { drift: 'Error', 'dto-mapping': 'Off', 'service-cycle': 'Info' },
    params: { dtoMinSites: 7, serviceCycleMinConfidence: 0.95 },
    vocabulary: { Command: { markers: ['IRequest'] } },
  };

  it('правило: убирает и строгость, и свои пороги, остальное не трогает', () => {
    const p = resetRule(edited, DTO);
    expect(p.overrides).toEqual({ drift: 'Error', 'service-cycle': 'Info' });
    expect(p.params).toEqual({ serviceCycleMinConfidence: 0.95 });
  });

  it('категория', () => {
    const p = resetCategory(edited, RULES, 'convention');
    expect(p.overrides).toEqual({ 'service-cycle': 'Info' });
    expect(p.params).toEqual({ serviceCycleMinConfidence: 0.95 });
  });

  it('к уровню — всё, включая словарь', () => {
    expect(resetToLevel(edited)).toEqual({ level: 'Standard' });
  });

  it('счётчик изменённых', () => {
    expect(changedRuleCount(RULES, edited, PARAMS)).toBe(3);
    expect(changedRuleCount(RULES, { level: 'Standard' }, PARAMS)).toBe(0);
  });
});

describe('словарь ролей', () => {
  it('без переопределения — встроенный список', () => {
    expect(roleList({ level: 'Standard' }, VOCAB, 'Command', 'markers')).toEqual(['ICommand']);
    expect(roleList({ level: 'Standard' }, VOCAB, 'Nope', 'markers')).toEqual([]);
  });

  it('правка списка хранится только если отличается от встроенного', () => {
    const p = withRoleList({ level: 'Standard' }, VOCAB, 'Command', 'markers', ['IRequest']);
    expect(p.vocabulary).toEqual({ Command: { markers: ['IRequest'] } });
    expect(roleList(p, VOCAB, 'Command', 'markers')).toEqual(['IRequest']);
    expect(isRoleChanged(p, VOCAB, 'Command')).toBe(true);
    expect(isRoleChanged(p, VOCAB, 'Handler')).toBe(false);
  });

  it('возврат к встроенному значению и пустой список убирают запись', () => {
    const edited = withRoleList({ level: 'Standard' }, VOCAB, 'Command', 'markers', ['IRequest']);
    expect(withRoleList(edited, VOCAB, 'Command', 'markers', ['ICommand'])).toEqual({ level: 'Standard' });
    expect(withRoleList(edited, VOCAB, 'Command', 'markers', [])).toEqual({ level: 'Standard' }); // пусто = «встроенное»
  });

  it('сброс роли не задевает другие', () => {
    let p = withRoleList({ level: 'Standard' }, VOCAB, 'Command', 'markers', ['IRequest']);
    p = withRoleList(p, VOCAB, 'Handler', 'suffixes', ['UseCase']);
    expect(resetRole(p, 'Command').vocabulary).toEqual({ Handler: { suffixes: ['UseCase'] } });
  });
});

describe('имена сервисов', () => {
  it('аналогично: отличие хранится, возврат к встроенному снимает', () => {
    const p = withNaming({ level: 'Standard' }, VOCAB, 'typeSuffixes', ['-api', '-service', '-core']);
    expect(p.params?.naming).toEqual({ typeSuffixes: ['-api', '-service', '-core'] });
    expect(namingList(p, VOCAB, 'typeSuffixes')).toEqual(['-api', '-service', '-core']);
    expect(isNamingChanged(p, VOCAB, 'typeSuffixes')).toBe(true);
    expect(isNamingChanged(p, VOCAB, 'migrationSuffixes')).toBe(false);
    expect(withNaming(p, VOCAB, 'typeSuffixes', ['-api', '-service'])).toEqual({ level: 'Standard' });
    expect(resetNaming(p, 'typeSuffixes')).toEqual({ level: 'Standard' });
  });
});

describe('списки', () => {
  it('addItem: без дублей (без учёта регистра), пустых и слишком длинных', () => {
    expect(addItem(['A'], ' b ')).toEqual(['A', 'b']);
    expect(addItem(['A'], 'a')).toEqual(['A']);
    expect(addItem(['A'], '   ')).toEqual(['A']);
    expect(addItem(['A'], 'x'.repeat(65))).toEqual(['A']);
  });
  it('removeItem', () => {
    expect(removeItem(['a', 'b', 'c'], 1)).toEqual(['a', 'c']);
  });
});

describe('сравнение и фильтры', () => {
  it('профили равны независимо от порядка ключей и пустых секций', () => {
    expect(profilesEqual({ level: 'Standard', overrides: { a: 'Off', b: 'Info' } }, { overrides: { b: 'Info', a: 'Off' }, level: 'Standard' })).toBe(true);
    expect(profilesEqual({ level: 'Standard', overrides: {} }, { level: 'Standard' })).toBe(true);
    expect(profilesEqual({ level: 'Standard' }, { level: 'Simple' })).toBe(false);
  });

  it('поиск по названию и заметке, фильтры «изменённые» и «выключенные»', () => {
    const p: Profile = { level: 'Simple', overrides: { 'service-cycle': 'Info' } };
    expect(filterRules(RULES, p, PARAMS, 'циклы', 'all').map((r) => r.id)).toEqual(['service-cycle']);
    expect(filterRules(RULES, p, PARAMS, '', 'changed').map((r) => r.id)).toEqual(['service-cycle']);
    expect(filterRules(RULES, p, PARAMS, '', 'off').map((r) => r.id)).toEqual(['drift', 'dto-mapping']); // Simple выключает свод-линзы
  });

  it('группы: архитектура раньше конвенций', () => {
    expect(groupByCategory(RULES).map((g) => g.category)).toEqual(['architecture', 'convention']);
    expect(groupByCategory(RULES)[1].rules.map((r) => r.id)).toEqual(['drift', 'dto-mapping']);
  });
});

describe('импорт и экспорт', () => {
  const cfg = { id: 'x', name: 'Для клиента', author: '@k', version: 3, kind: 'own', catalogVersion: '2026.11',
    profile: { level: 'Platform', overrides: { drift: 'Error' } } as Profile };

  it('экспорт без служебных полей сервера, импорт его же возвращает профиль', () => {
    const text = exportManifest(cfg);
    expect(text).not.toContain('"id"');
    expect(text).not.toContain('"kind"');
    const r = parseImport(text);
    expect(r.ok).toBe(true);
    if (r.ok) {
      expect(r.draft.name).toBe('Для клиента');
      expect(r.draft.profile).toEqual(cfg.profile);
      expect(r.draft.id).toBeNull();
      expect(r.draft.version).toBe(0);
    }
  });

  it('принимает «голый» профиль', () => {
    const r = parseImport('{ "level": "Simple", "overrides": { "drift": "Info" } }');
    expect(r.ok && r.draft.profile.level).toBe('Simple');
    expect(r.ok && r.draft.name).toBe('Импортированная конфигурация');
  });

  it('понятные ошибки: не JSON, не объект, нет профиля, слишком большой', () => {
    expect(parseImport('{ oops')).toEqual({ ok: false, error: 'Это не JSON' });
    expect(parseImport('[1]').ok).toBe(false);
    expect(parseImport('{"name":"x"}').ok).toBe(false);
    expect(parseImport('x'.repeat(300 * 1024)).ok).toBe(false);
  });

  it('длинное имя обрезается до 100 символов', () => {
    const r = parseImport(JSON.stringify({ name: 'я'.repeat(300), profile: { level: 'Standard' } }));
    expect(r.ok && r.draft.name.length).toBe(100);
  });
});

describe('клиент API', () => {
  afterEach(() => vi.unstubAllGlobals());

  const respond = (status: number, body?: unknown) =>
    vi.fn(async () => new Response(body === undefined ? null : JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }));

  it('отправляет JSON и разбирает ответ', async () => {
    const f = respond(201, { id: 'n', name: 'A', author: 'a', version: 1, profile: { level: 'Standard' } });
    vi.stubGlobal('fetch', f);
    const c = await createConfig({ name: 'A', author: 'a', version: 0, profile: { level: 'Standard' } });
    expect(c.id).toBe('n');
    const [url, init] = f.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/api/profile/configs');
    expect(init.method).toBe('POST');
    expect((init.headers as Record<string, string>)['Content-Type']).toBe('application/json');
  });

  it('ошибки сервера собираются в ProfileApiError со статусом и списком причин', async () => {
    vi.stubGlobal('fetch', respond(409, { error: 'конфликт', errors: ['конфликт', 'обновите'] }));
    const err = await updateConfig('x', { name: 'A', author: 'a', version: 1, profile: { level: 'Standard' } }).catch((e) => e);
    expect(err).toBeInstanceOf(ProfileApiError);
    expect(err.status).toBe(409);
    expect(err.errors).toEqual(['конфликт', 'обновите']);
  });

  it('не-JSON ошибка не ломает разбор', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('<html>', { status: 502 })));
    const err = await fetchProjectProfile('/p').catch((e) => e);
    expect(err).toBeInstanceOf(ProfileApiError);
    expect(err.status).toBe(502);
  });

  it('путь проекта экранируется в адресе', async () => {
    const f = respond(200, { binding: { level: 'Standard' }, effective: { level: 'Standard' } });
    vi.stubGlobal('fetch', f);
    await bindProject('/Users/x y/proj', { level: 'Simple' });
    const [url, init] = f.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/api/profile?path=%2FUsers%2Fx%20y%2Fproj');
    expect(init.method).toBe('PUT');
    expect(JSON.parse(init.body as string)).toEqual({ level: 'Simple' });
  });
});

describe('санитизация импорта (чужой JSON не роняет окно)', () => {
  it('значения не тех типов отбрасываются', () => {
    const p = sanitizeProfile({
      level: 'Standard',
      overrides: { drift: 'Warning', bad: 5, worse: { x: 1 } },
      params: { dtoMinSites: {}, dtoMinFiles: 4, serviceCycleMinConfidence: 'x', naming: { typeSuffixes: [{}], migrationSuffixes: ['-m'], satelliteMarkers: 'seed' } },
      vocabulary: { Command: { markers: 'x', fallback: ['A'], suffixes: [1, 2] }, Handler: 'oops', Other: ['a'] },
      extra: 'лишнее',
    });
    expect(p).toEqual({
      level: 'Standard',
      overrides: { drift: 'Warning' },
      params: { dtoMinFiles: 4, naming: { migrationSuffixes: ['-m'] } },
      vocabulary: { Command: { fallback: ['A'] } },
    });
  });

  it('NaN, бесконечность и массивы вместо объектов отбрасываются', () => {
    const p = sanitizeProfile({ level: 'Simple', params: { dtoMinSites: Number.POSITIVE_INFINITY }, overrides: ['a'], vocabulary: [] });
    expect(p).toEqual({ level: 'Simple' });
  });

  it('без level профиля нет', () => {
    expect(sanitizeProfile({ overrides: {} })).toBeNull();
    expect(sanitizeProfile(null)).toBeNull();
    expect(sanitizeProfile([1])).toBeNull();
    expect(sanitizeProfile({ level: 5 })).toBeNull();
  });

  it('ключи __proto__ и constructor остаются данными и не меняют прототип', () => {
    const raw = JSON.parse('{"level":"Standard","overrides":{"__proto__":"Off","constructor":"Info"},"vocabulary":{"__proto__":{"markers":["x"]}}}');
    const p = sanitizeProfile(raw)!;
    expect(({} as Record<string, unknown>).markers).toBeUndefined();
    expect(Object.getPrototypeOf(p.overrides!)).toBe(Object.prototype);
    expect(Object.keys(p.overrides!)).toContain('constructor');
  });

  it('parseImport отдаёт только очищенный профиль', () => {
    const r = parseImport('{"profile":{"level":"Standard","vocabulary":{"Command":{"markers":"x"}}}}');
    expect(r.ok && r.draft.profile).toEqual({ level: 'Standard' });
  });
});

describe('предпросмотр влияния', () => {
  const sum = (e: number, w: number, i: number, rules: Record<string, { count: number; enabled: boolean }> = {}) => ({ errors: e, warnings: w, infos: i, rules });
  const PV: Preview = {
    current: sum(1, 2, 3, { drift: { count: 3, enabled: true }, 'dto-mapping': { count: 0, enabled: false } }),
    baseline: sum(4, 2, 3, { drift: { count: 3, enabled: true } }),
    hint: { level: 'Standard', reasons: ['сервисов в compose: 2'] },
    markerCounts: { ICommand: 12, IRequest: 30, IQuery: 0 },
    suffixCounts: { Command: 18, Query: 6 },
  };

  it('итог и разница с чистым уровнем', () => {
    expect(impactTotal(PV.current)).toBe(6);
    expect(impactDelta(PV)).toBe(-3);                               // профиль убрал три находки
  });

  it('находки правила: текущие и базовые; нет данных → null', () => {
    expect(ruleImpact(PV, true, 'drift')).toEqual({ count: 3, enabled: true });
    expect(ruleImpact(PV, true, 'dto-mapping')).toEqual({ count: 0, enabled: false });
    expect(ruleImpact(PV, false, 'dto-mapping')).toBeNull();
    expect(ruleImpact(null, true, 'drift')).toBeNull();
  });

  it('покрытие роли: узнано по маркеру из всех типов с суффиксом; не больше общего', () => {
    expect(roleCoverage(PV, ['ICommand'], ['Command'])).toEqual({ total: 18, marked: 12 });
    expect(roleCoverage(PV, ['IRequest'], ['Command'])).toEqual({ total: 18, marked: 18 });   // IRequest встречается чаще — упираемся в общее
    expect(roleCoverage(PV, ['IQuery'], ['Query'])).toEqual({ total: 6, marked: 0 });
    expect(roleCoverage(PV, ['ICommand'], [])).toBeNull();
    expect(roleCoverage(null, ['ICommand'], ['Command'])).toBeNull();
  });

  it('пример порога: строки попадают под правило по выбранному значению', () => {
    const at = (v: number) => paramSample('service-cycle', 'serviceCycleMinConfidence', v)!.filter((r) => r.hit).length;
    expect(at(0.5)).toBe(4);
    expect(at(0.8)).toBe(2);
    expect(at(0.95)).toBe(1);
    expect(at(1)).toBe(0);
    expect(paramSample('dto-mapping', 'dtoMinSites', 3)!.filter((r) => r.hit).map((r) => r.label)).toEqual(['UserDto', 'OrderDto', 'CourseDto']);
    expect(paramSample('drift', 'dtoMinSites', 3)).toBeNull();
  });

  it('запрос предпросмотра: POST с профилем, путь экранирован, отмена доходит до fetch', async () => {
    const f = vi.fn(async () => new Response(JSON.stringify(PV), { status: 200, headers: { 'Content-Type': 'application/json' } }));
    vi.stubGlobal('fetch', f);
    const ctrl = new AbortController();

    const r = await fetchPreview('/Users/x y/proj', { level: 'Simple' }, ctrl.signal);

    expect(r.hint.level).toBe('Standard');
    const [url, init] = f.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/api/profile/preview?path=%2FUsers%2Fx%20y%2Fproj');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body as string)).toEqual({ level: 'Simple' });
    vi.unstubAllGlobals();
  });
});


describe('свои правила', () => {
  const base: Profile = { level: 'Standard' };

  it('новое правило получает свободный id и по умолчанию предупреждает', () => {
    const a = newCustomRule('suffix-base', []);
    const b = newCustomRule('layer-no-ref', [a]);
    expect(a.id).toBe('rule-1');
    expect(b.id).toBe('rule-2');
    expect(a.strictness).toBe('Warning');
  });

  it('добавление, замена и удаление не меняют исходный профиль и чистят пустой список', () => {
    const r = { ...newCustomRule('suffix-base', []), suffix: 'Handler', baseType: 'IHandler' };
    const added = withCustomRule(base, r);
    expect(added.customRules).toHaveLength(1);
    expect(base.customRules).toBeUndefined();
    const replaced = withCustomRule(added, { ...r, strictness: 'Error' });
    expect(replaced.customRules).toHaveLength(1);
    expect(replaced.customRules?.[0].strictness).toBe('Error');
    expect(withoutCustomRule(replaced, r.id).customRules).toBeUndefined();
  });

  it('правил не больше предела', () => {
    let p = base;
    for (let i = 0; i < MAX_CUSTOM_RULES + 5; i++) p = withCustomRule(p, newCustomRule('role-in-folder', p.customRules ?? []));
    expect(p.customRules).toHaveLength(MAX_CUSTOM_RULES);
  });

  it('полнота: нужны все группы полей, для «или» достаточно одного', () => {
    const r = newCustomRule('forbid-in-folder', []);
    expect(isCustomComplete(r)).toBe(false);
    expect(isCustomComplete({ ...r, folder: 'Domain' })).toBe(false);
    expect(isCustomComplete({ ...r, folder: 'Domain', baseType: 'IDto' })).toBe(true);
    expect(isCustomComplete({ ...r, folder: 'Domain', suffix: ' ' })).toBe(false);
    expect(isCustomComplete({ ...r, folder: 'Domain', suffix: 'Dto', title: '  ' })).toBe(false);
  });

  it('пример подставляет значения правила и подсказки, пока поля пусты', () => {
    const r = newCustomRule('suffix-base', []);
    expect(customExample(r).good[0]).toContain('IHandler');
    const ex = customExample({ ...r, suffix: 'Query', baseType: 'IQuery' });
    expect(ex.bad[0]).toContain('CreateQuery');
    expect(ex.good[0]).toContain(': IQuery');
    for (const t of CUSTOM_TEMPLATES) {
      const e = customExample(newCustomRule(t.id, []));
      expect(e.bad.length).toBeGreaterThan(0);
      expect(e.good.length).toBeGreaterThan(0);
    }
  });

  it('равенство профилей видит разницу в своих правилах', () => {
    const r = { ...newCustomRule('suffix-base', []), suffix: 'A', baseType: 'B' };
    expect(profilesEqual(base, withCustomRule(base, r))).toBe(false);
    expect(profilesEqual(withCustomRule(base, r), withCustomRule(base, { ...r }))).toBe(true);
  });

  it('импорт оставляет только допустимые правила и обрезает лишнее', () => {
    const good = { id: 'a', template: 'suffix-base', title: 'T', strictness: 'Error', suffix: 'X', baseType: 'Y', evil: '<script>' };
    const p = sanitizeProfile({
      level: 'Standard',
      customRules: [good, { id: 1 }, null, 'x', { ...good, id: 'b', template: 'magic' }, { ...good, id: 'c', strictness: 'Loud' }, { ...good, id: 'd', suffix: 5 }],
    });
    expect(p?.customRules?.map((r) => r.id)).toEqual(['a', 'd']);
    expect(p?.customRules?.[0]).not.toHaveProperty('evil');
    expect(p?.customRules?.[1].suffix).toBeUndefined();
    expect(sanitizeProfile({ level: 'Standard', customRules: 'nope' })).toEqual({ level: 'Standard' });
    const many = Array.from({ length: MAX_CUSTOM_RULES + 10 }, (_, i) => ({ ...good, id: `r${i}` }));
    expect(sanitizeProfile({ level: 'Standard', customRules: many })?.customRules).toHaveLength(MAX_CUSTOM_RULES);
  });

  it('незаконченные правила не уходят в предпросмотр', () => {
    const full = { ...newCustomRule('suffix-base', []), suffix: 'A', baseType: 'B' };
    const empty = newCustomRule('layer-no-ref', [full]);
    expect(completeOnly({ level: 'Standard', customRules: [full, empty] }).customRules).toEqual([full]);
    expect(completeOnly({ level: 'Standard', customRules: [empty] })).toEqual({ level: 'Standard' });
  });

  it('импорт отбрасывает плохой id и дубли, приводит строгость и обрезает поля', () => {
    const ok = { id: 'a', template: 'suffix-base', title: 'T', strictness: 'error', suffix: '  X  ', baseType: 'Y' };
    const p = sanitizeProfile({ level: 'Standard', customRules: [ok, { ...ok }, { ...ok, id: 'Bad Id' }, { ...ok, id: 'b', suffix: 'z'.repeat(200) }] });
    expect(p?.customRules?.map((r) => r.id)).toEqual(['a', 'b']);
    expect(p?.customRules?.[0].strictness).toBe('Error');
    expect(p?.customRules?.[0].suffix).toBe('X');
    expect(p?.customRules?.[1].suffix).toHaveLength(64);
  });

  it('имя находки для предпросмотра', () => {
    expect(customKind('rule-1')).toBe('custom:rule-1');
  });
});
