import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Icon } from './Icon';
import {
  type ConfigManifest, type Level, type Profile, type RuleCatalog, type RuleDef, type RuleFilter, type Strictness,
  type VocabularyDefaults, type ProfileParams, type NamingKey, type VocabKey, type ProfileForPath,
  LEVELS, LEVEL_TITLE, STRICTNESS, STRICTNESS_TITLE, CATEGORY_TITLE, DEFAULT_LEVEL, RULE_PARAMS, NAMING_KEYS, NAMING_TITLE,
  ProfileApiError, addItem, removeItem, bindProject, changedRuleCount, createConfig, deleteConfig, effectiveStrictness,
  exportManifest, fetchProjectProfile, fetchRules, fetchVocabulary, filterRules, groupByCategory, isNamingChanged,
  isRoleChanged, isRuleChanged, levelDefault, listConfigs, namingList, paramValue, parseImport, profilesEqual, resetCategory,
  resetNaming, resetRole, resetRule, resetToLevel, roleList, updateConfig, withLevel, withNaming, withParam, withRoleList,
  withStrictness,
} from './profile';

// Окно «Профиль проекта» (adr/0012, срез 3): уровень, конфигурации, строгость правил, словарь ролей, имена сервисов.
// Встроенный уровень не меняется — первая правка создаёт несохранённую копию. Сохранение — версией (проверка конфликта на сервере).

interface Props {
  path: string;                 // корень проекта: к нему привязывается конфигурация
  onClose: () => void;
  onApplied: () => void;        // привязка изменилась → пере-анализировать проект
}

type Tab = 'rules' | 'dict';
const NEW = 'new';              // несохранённая конфигурация (копия, новая, импорт)
const levelOfId = (id?: string | null): Level | null =>
  LEVELS.find((l) => `lvl-${l.toLowerCase()}` === id) ?? null;

function messageOf(e: unknown): string[] {
  if (e instanceof ProfileApiError) return e.errors;
  return [e instanceof Error ? e.message : 'Что-то пошло не так'];
}

export function ProfileView({ path, onClose, onApplied }: Props) {
  const [catalog, setCatalog] = useState<RuleCatalog | null>(null);
  const [vocab, setVocab] = useState<VocabularyDefaults | null>(null);
  const [configs, setConfigs] = useState<ConfigManifest[]>([]);
  const [bound, setBound] = useState<ProfileForPath | null>(null);
  const [loadError, setLoadError] = useState<string[] | null>(null);

  const [sel, setSel] = useState<string>(`lvl-${DEFAULT_LEVEL.toLowerCase()}`);
  const [draft, setDraft] = useState<ConfigManifest | null>(null);
  const [tab, setTab] = useState<Tab>('rules');
  const [selRule, setSelRule] = useState<string | null>(null);
  const [selRole, setSelRole] = useState<string | null>(null);
  const [q, setQ] = useState('');
  const [filter, setFilter] = useState<RuleFilter>('all');
  const [menu, setMenu] = useState(false);
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<string[]>([]);
  const [note, setNote] = useState('');
  const [pending, setPending] = useState<(() => void) | null>(null);   // действие, ждущее подтверждения «отбросить правки»
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [importOpen, setImportOpen] = useState(false);
  const dialogRef = useRef<HTMLDivElement>(null);

  // ── загрузка ──
  const load = useCallback(async (signal?: AbortSignal) => {
    setLoadError(null);
    try {
      const [rules, v, list, forPath] = await Promise.all([
        fetchRules(signal), fetchVocabulary(signal), listConfigs(signal), fetchProjectProfile(path, signal),
      ]);
      setCatalog(rules); setVocab(v); setConfigs(list); setBound(forPath);
      const b = forPath.binding;
      setSel(b.configId ?? `lvl-${(b.level ?? DEFAULT_LEVEL).toLowerCase()}`);
      setDraft(null);
    } catch (e) {
      if (e instanceof DOMException && e.name === 'AbortError') return;
      setLoadError(messageOf(e));
    }
  }, [path]);

  useEffect(() => {
    const ctrl = new AbortController();
    void load(ctrl.signal);
    return () => ctrl.abort();
  }, [load]);

  // ── текущая конфигурация ──
  const persisted = configs.find((c) => c.id === sel) ?? null;
  const working: ConfigManifest | null = draft ?? persisted;
  const readOnly = !draft && persisted?.kind === 'level';
  const dirty = draft !== null && (sel === NEW || !persisted || !profilesEqual(draft.profile, persisted.profile)
    || draft.name !== persisted.name || draft.author !== persisted.author);
  const profile: Profile = working?.profile ?? { level: DEFAULT_LEVEL };
  const defaults: ProfileParams = vocab?.params ?? {};
  const rules = useMemo(() => catalog?.rules ?? [], [catalog]);

  /** Правка профиля. Встроенный уровень не трогаем: первая правка создаёт несохранённую копию. */
  const edit = (fn: (p: Profile) => Profile) => {
    if (!working) return;
    setErrors([]); setNote('');
    if (readOnly) {
      setSel(NEW);
      setDraft({ id: null, name: `Копия: ${working.name}`, author: 'local', version: 0, kind: 'own',
        parentId: working.id ?? null, profile: fn(working.profile) });
    } else setDraft({ ...working, profile: fn(working.profile) });
  };

  /** Правка названия/автора: на встроенном уровне тоже создаёт копию, на своей — обновляет черновик. */
  const patchMeta = (patch: Partial<Pick<ConfigManifest, 'name' | 'author'>>) => {
    if (!working) return;
    setErrors([]); setNote('');
    if (readOnly) edit((p) => p);
    else setDraft({ ...working, ...patch });
  };

  const guard = (action: () => void) => (dirty ? setPending(() => action) : action());
  const selectConfig = (id: string) => guard(() => { setSel(id); setDraft(null); setErrors([]); setNote(''); setConfirmDelete(false); });
  const close = () => guard(onClose);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape' && !importOpen && !pending) close(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  });
  useEffect(() => { dialogRef.current?.focus(); }, []);

  // ── действия ──
  const run = async (fn: () => Promise<void>) => {
    setBusy(true); setErrors([]); setNote('');
    try { await fn(); }
    catch (e) {
      const conflict = e instanceof ProfileApiError && e.status === 409;
      setErrors(conflict ? [...messageOf(e), 'Закройте окно и откройте заново, чтобы увидеть актуальную версию.'] : messageOf(e));
    } finally { setBusy(false); }
  };

  const save = async (): Promise<ConfigManifest | null> => {
    if (!draft) return persisted;
    if (!draft.name.trim()) { setErrors(['Укажите название конфигурации']); return null; }
    let saved: ConfigManifest | null = null;
    await run(async () => {
      saved = sel === NEW || !draft.id ? await createConfig(draft) : await updateConfig(draft.id, draft);
      const s = saved;
      setConfigs((list) => [...list.filter((c) => c.id !== s.id), s]);
      setSel(s.id ?? NEW); setDraft(null);
      setNote(`Сохранено: версия v${s.version}`);
    });
    return saved;
  };

  const apply = async () => {
    const target = dirty ? await save() : persisted;
    if (!target?.id) return;
    await run(async () => {
      const lvl = levelOfId(target.id);
      const res = await bindProject(path, lvl ? { level: lvl } : { configId: target.id });
      setBound(res);
      setNote(`Применено к проекту: ${target.name}`);
      onApplied();
    });
  };

  const remove = async () => {
    if (!persisted?.id || persisted.kind === 'level') return;
    const id = persisted.id;
    await run(async () => {
      await deleteConfig(id);
      setConfigs((list) => list.filter((c) => c.id !== id));
      setSel(`lvl-${DEFAULT_LEVEL.toLowerCase()}`); setDraft(null); setConfirmDelete(false);
      setBound(await fetchProjectProfile(path));    // привязка к удалённой конфигурации снята сервером
      setNote('Конфигурация удалена');
    });
  };

  const copyOf = () => working && guard(() => {
    setSel(NEW);
    setDraft({ id: null, name: `Копия: ${working.name}`, author: 'local', version: 0, kind: 'own', parentId: persisted?.id ?? null, profile: working.profile });
  });
  const startNew = () => guard(() => {
    setSel(NEW); setErrors([]);
    setDraft({ id: null, name: 'Новая конфигурация', author: 'local', version: 0, kind: 'own', parentId: null, profile: { level: profile.level } });
  });

  const exportJson = () => {
    if (!working) return;
    const url = URL.createObjectURL(new Blob([exportManifest(working)], { type: 'application/json' }));
    const a = document.createElement('a');
    a.href = url; a.download = `${working.name.replace(/[^\p{L}\p{N}]+/gu, '-').replace(/^-|-$/g, '') || 'profile'}.archforge-profile.json`;
    a.click();
    URL.revokeObjectURL(url);
  };

  const parentProfile = (): Profile | null => {
    const pid = working?.parentId;
    if (!pid) return null;
    const lvl = levelOfId(pid);
    return lvl ? { level: lvl } : configs.find((c) => c.id === pid)?.profile ?? null;
  };

  const isBound = (c: ConfigManifest) => {
    const b = bound?.binding;
    if (!b || !c.id) return false;
    return b.configId === c.id || (!b.configId && levelOfId(c.id) === b.level);
  };

  // ── отрисовка ──
  if (loadError) {
    return (
      <div className="pv-backdrop" onClick={onClose}>
        <div className="pv pv--small" role="dialog" aria-modal="true" aria-label="Профиль проекта" onClick={(e) => e.stopPropagation()}>
          <div className="pv__head"><div className="pv__title"><Icon name="box" size={16} /> Профиль проекта</div>
            <button className="pv__x" onClick={onClose} title="Закрыть"><Icon name="close" size={16} /></button></div>
          <div className="pv__fail">
            <p>Не удалось загрузить настройки.</p>
            {loadError.map((m, i) => <p key={i} className="pv__err">{m}</p>)}
            <button className="pv__btn" onClick={() => void load()}>Повторить</button>
          </div>
        </div>
      </div>
    );
  }

  const ready = catalog && vocab && working;
  const shownRules = ready ? filterRules(rules, profile, defaults, q, filter) : [];
  const own = configs.filter((c) => c.kind !== 'level');
  const levelCfgs = configs.filter((c) => c.kind === 'level');
  const nChanged = ready ? changedRuleCount(rules, profile, defaults) : 0;
  const activeRule = rules.find((r) => r.id === selRule) ?? rules[0];

  return (
    <div className="pv-backdrop" onClick={close}>
      <div className="pv" ref={dialogRef} tabIndex={-1} role="dialog" aria-modal="true" aria-label="Профиль проекта" onClick={(e) => { e.stopPropagation(); setMenu(false); }}>
        <div className="pv__head">
          <div className="pv__title"><Icon name="box" size={16} /> Профиль проекта <span className="pv__path" title={path}>{path}</span></div>
          <button className="pv__x" onClick={close} title="Закрыть"><Icon name="close" size={16} /></button>
        </div>

        {!ready ? <div className="pv__loading">Загружаю настройки…</div> : (
          <div className="pv__cols">
            <aside className="pv__side">
              <div className="pv__sidehead">Уровни</div>
              {levelCfgs.map((c) => <ConfigButton key={c.id} c={c} on={sel === c.id} bound={isBound(c)} onClick={() => selectConfig(c.id!)} />)}
              <div className="pv__sidehead">Мои конфигурации</div>
              {sel === NEW && draft && <div className="pv__cfg pv__cfg--on"><b><span className="pv__dot pv__dot--new" />{draft.name || 'Без названия'} •</b><small>не сохранена</small></div>}
              {own.length === 0 && sel !== NEW && <div className="pv__muted">Пока нет. Первая правка уровня создаст копию.</div>}
              {own.map((c) => <ConfigButton key={c.id} c={c} on={sel === c.id} bound={isBound(c)} onClick={() => selectConfig(c.id!)} />)}
              <button className="pv__btn" onClick={startNew}>+ Создать на основе «{profile.level}»</button>
              <button className="pv__btn" onClick={() => setImportOpen(true)}>Импорт…</button>
            </aside>

            <main className="pv__mid">
              <div className="pv__top">
                <input className="pv__name" aria-label="Название конфигурации" value={working.name} readOnly={readOnly} maxLength={100}
                  onChange={(e) => patchMeta({ name: e.target.value })} />
                <div className="pv__seg" role="group" aria-label="Базовый уровень">
                  {LEVELS.map((l) => (
                    <button key={l} className={profile.level === l ? 'on' : ''} title={LEVEL_TITLE[l]}
                      onClick={() => readOnly ? selectConfig(`lvl-${l.toLowerCase()}`) : edit((p) => withLevel(p, l))}>
                      <span className={`pv__dot pv__dot--${l}`} />{l}
                    </button>
                  ))}
                </div>
              </div>
              <div className="pv__meta">
                {readOnly ? <span>встроенный уровень «{LEVEL_TITLE[profile.level as Level] ?? profile.level}» · первая правка создаст вашу копию</span> : <>
                  <label>автор <input className="pv__author" value={working.author} maxLength={100} aria-label="Автор"
                    onChange={(e) => patchMeta({ author: e.target.value })} /></label>
                  <span>версия <b>v{Math.max(working.version, 1)}{dirty && sel !== NEW ? ` → v${working.version + 1}` : ''}</b></span>
                  <span>каталог правил <b>{working.catalogVersion ?? catalog.version}</b></span>
                  <span>формат <b>{working.formatVersion ?? 1}</b></span>
                  {working.parentId && <span>на основе <b>{configs.find((c) => c.id === working.parentId)?.name ?? '—'}</b></span>}
                </>}
              </div>

              <div className="pv__tabs" role="tablist">
                <button role="tab" aria-selected={tab === 'rules'} className={tab === 'rules' ? 'on' : ''} onClick={() => setTab('rules')}>
                  Правила{nChanged ? ` · изменено ${nChanged}` : ''}</button>
                <button role="tab" aria-selected={tab === 'dict'} className={tab === 'dict' ? 'on' : ''} onClick={() => setTab('dict')}>Словарь и имена</button>
                <span className="pv__spacer" />
                {!readOnly && (
                  <div className="pv__menu">
                    <button className="pv__btn" onClick={(e) => { e.stopPropagation(); setMenu((m) => !m); }}>Сбросить ▾</button>
                    {menu && (
                      <div className="pv__pop" onClick={(e) => e.stopPropagation()}>
                        <button onClick={() => { edit(resetToLevel); setMenu(false); }}>К уровню {profile.level}<small>убрать все отличия: правила, пороги, словарь</small></button>
                        <button disabled={!parentProfile()} onClick={() => { const p = parentProfile(); if (p) edit(() => p); setMenu(false); }}>
                          К родителю<small>{parentProfile() ? 'вернуть то, что было в исходной конфигурации' : 'у конфигурации нет родителя'}</small></button>
                      </div>
                    )}
                  </div>
                )}
              </div>

              {tab === 'rules' ? (
                <RulesTab rules={shownRules} all={rules} profile={profile} paramDefaults={defaults} q={q} setQ={setQ} filter={filter} setFilter={setFilter}
                  nChanged={nChanged} selected={activeRule?.id ?? null} onSelect={setSelRule}
                  onSet={(r, s) => { setSelRule(r.id); edit((p) => withStrictness(p, r, s)); }}
                  onReset={(r) => edit((p) => resetRule(p, r))}
                  onResetCategory={(cat) => edit((p) => resetCategory(p, rules, cat))} canReset={!readOnly} />
              ) : (
                <DictTab profile={profile} vocab={vocab} selRole={selRole ?? Object.keys(vocab.roles)[0] ?? null} onSelect={setSelRole}
                  onRole={(role, key, list) => { setSelRole(role); edit((p) => withRoleList(p, vocab, role, key, list)); }}
                  onResetRole={(role) => edit((p) => resetRole(p, role))}
                  onNaming={(key, list) => edit((p) => withNaming(p, vocab, key, list))}
                  onResetNaming={(key) => edit((p) => resetNaming(p, key))} canReset={!readOnly} />
              )}
            </main>

            <section className="pv__right">
              {tab === 'rules' && activeRule
                ? <RuleDetails rule={activeRule} profile={profile} vocab={vocab} onParam={(key, v) => edit((p) => withParam(p, key, v, defaults))} />
                : <DictHelp profile={profile} vocab={vocab} role={selRole ?? Object.keys(vocab.roles)[0] ?? null} />}
            </section>
          </div>
        )}

        {(errors.length > 0 || note) && (
          <div className={errors.length ? 'pv__banner pv__banner--err' : 'pv__banner'} role={errors.length ? 'alert' : 'status'}>
            {errors.length ? errors.map((m, i) => <div key={i}>{m}</div>) : note}
          </div>
        )}

        {ready && (
          <div className="pv__foot">
            {!readOnly && <button className="pv__btn pv__btn--pri" disabled={!dirty || busy} onClick={() => void save()}>
              {sel === NEW ? 'Сохранить' : dirty ? `Сохранить v${working.version + 1}` : `Сохранено v${working.version}`}</button>}
            <button className="pv__btn pv__btn--apply" disabled={busy || (!dirty && !!persisted && isBound(persisted))} onClick={() => void apply()}
              title="Проект будет разбираться по этой конфигурации">
              {dirty ? 'Сохранить и применить к проекту' : persisted && isBound(persisted) ? 'Применено к проекту' : 'Применить к проекту'}</button>
            <button className="pv__btn" onClick={copyOf}>Дублировать</button>
            <button className="pv__btn" onClick={exportJson}>Экспорт JSON</button>
            {persisted && persisted.kind !== 'level' && !dirty && (confirmDelete
              ? <span className="pv__confirm">Удалить «{persisted.name}»? <button className="pv__btn pv__btn--danger" disabled={busy} onClick={() => void remove()}>Удалить</button>
                  <button className="pv__btn" onClick={() => setConfirmDelete(false)}>Отмена</button></span>
              : <button className="pv__btn" onClick={() => setConfirmDelete(true)}>Удалить</button>)}
            <span className="pv__spacer" />
            <span className="pv__muted">{bound ? `Сейчас у проекта: ${bound.config?.name ?? bound.binding.level ?? DEFAULT_LEVEL}` : ''}</span>
          </div>
        )}

        {pending && (
          <div className="pv-modal" onClick={(e) => e.stopPropagation()}>
            <div className="pv-modal__box" role="alertdialog" aria-label="Несохранённые правки">
              <h3>Есть несохранённые правки</h3>
              <p>Если продолжить, они пропадут.</p>
              <div className="pv__row">
                <button className="pv__btn" onClick={() => setPending(null)}>Остаться</button>
                <button className="pv__btn pv__btn--danger" onClick={() => { const a = pending; setPending(null); setDraft(null); a(); }}>Отбросить</button>
              </div>
            </div>
          </div>
        )}
        {importOpen && <ImportDialog onClose={() => setImportOpen(false)} onImport={(d) => guard(() => { setImportOpen(false); setSel(NEW); setDraft(d); setErrors([]); setNote('Проверьте и сохраните: полную проверку делает сервер.'); })} />}
      </div>
    </div>
  );
}

// ── части окна ──

function ConfigButton({ c, on, bound, onClick }: { c: ConfigManifest; on: boolean; bound: boolean; onClick: () => void }) {
  const lvl = levelOfId(c.id);
  return (
    <button className={`pv__cfg${on ? ' pv__cfg--on' : ''}`} onClick={onClick}>
      <b><span className={`pv__dot pv__dot--${lvl ?? c.profile.level}`} />{c.name}{bound ? ' ●' : ''}</b>
      <small>{lvl ? `встроенная · ${LEVEL_TITLE[lvl]}` : `${c.author} · v${c.version}`}{bound ? ' · применена' : ''}</small>
    </button>
  );
}

function StrictnessControl({ rule, profile, onSet }: { rule: RuleDef; profile: Profile; onSet: (s: Strictness | null) => void }) {
  const cur = effectiveStrictness(rule, profile).value;
  return (
    <div className="pv__sev" role="group" aria-label={`Строгость: ${rule.title}`}>
      {STRICTNESS.map((s) => (
        <button key={s} className={`pv__sev-${s}${cur === s ? ' on' : ''}`} aria-pressed={cur === s} onClick={() => onSet(s)}>{STRICTNESS_TITLE[s]}</button>
      ))}
    </div>
  );
}

interface RulesTabProps {
  rules: RuleDef[]; all: RuleDef[]; profile: Profile; paramDefaults: ProfileParams;
  q: string; setQ: (v: string) => void; filter: RuleFilter; setFilter: (f: RuleFilter) => void; nChanged: number;
  selected: string | null; onSelect: (id: string) => void; onSet: (r: RuleDef, s: Strictness | null) => void;
  onReset: (r: RuleDef) => void; onResetCategory: (cat: string) => void; canReset: boolean;
}

function RulesTab(p: RulesTabProps) {
  const nOff = p.all.filter((r) => effectiveStrictness(r, p.profile).value === 'Off').length;
  const filters: [RuleFilter, string][] = [['all', `Все ${p.all.length}`], ['changed', `Изменённые ${p.nChanged}`], ['off', `Выключенные ${nOff}`]];
  const groups = groupByCategory(p.rules);
  return (
    <div className="pv__body">
      <div className="pv__tools">
        <input className="pv__search" placeholder="Поиск по правилам" aria-label="Поиск по правилам" value={p.q} onChange={(e) => p.setQ(e.target.value)} />
        <div className="pv__chips">
          {filters.map(([f, label]) => <button key={f} className={p.filter === f ? 'on' : ''} onClick={() => p.setFilter(f)}>{label}</button>)}
        </div>
      </div>
      {groups.length === 0 && <div className="pv__empty">Ничего не найдено. Сбросьте поиск или фильтр.</div>}
      {groups.map((g) => (
        <section key={g.category}>
          <div className="pv__cat"><span>{CATEGORY_TITLE[g.category] ?? g.category}</span><span className="pv__ln" />
            {p.canReset && g.rules.some((r) => isRuleChanged(r, p.profile, p.paramDefaults)) &&
              <button className="pv__link" onClick={() => p.onResetCategory(g.category)}>сбросить категорию</button>}
          </div>
          {g.rules.map((r) => {
            const e = effectiveStrictness(r, p.profile);
            const changed = isRuleChanged(r, p.profile, p.paramDefaults);
            return (
              <div key={r.id} className={`pv__rule${p.selected === r.id ? ' pv__rule--sel' : ''}`} onClick={() => p.onSelect(r.id)}>
                <div className="pv__rule-main">
                  <div className="pv__rule-t">{r.title}</div>
                  <div className="pv__rule-d">
                    <span className={`pv__src${changed ? ' pv__src--cfg' : ''}`}>{changed ? 'ваша конфигурация' : `уровень ${p.profile.level}`}</span>
                    {e.value === 'Off' ? <span>не проверяется на вашем уровне</span>
                      : e.value === null ? <span>как решает само правило</span> : null}
                  </div>
                </div>
                <div className="pv__rule-ctl" onClick={(ev) => ev.stopPropagation()}>
                  <StrictnessControl rule={r} profile={p.profile} onSet={(s) => p.onSet(r, s)} />
                  <button className="pv__rst" title="Сбросить правило к уровню" aria-label={`Сбросить: ${r.title}`} disabled={!changed || !p.canReset} onClick={() => p.onReset(r)}>
                    <Icon name="reset" size={13} /></button>
                </div>
              </div>
            );
          })}
        </section>
      ))}
    </div>
  );
}

function RuleDetails({ rule, profile, vocab, onParam }: { rule: RuleDef; profile: Profile; vocab: VocabularyDefaults; onParam: (key: 'serviceCycleMinConfidence' | 'dtoMinSites' | 'dtoMinFiles', v: number) => void }) {
  const cur = effectiveStrictness(rule, profile);
  const def = levelDefault(rule, profile.level);
  return (
    <div>
      <h3 className="pv__h">{rule.title}</h3>
      <p className="pv__sub">{CATEGORY_TITLE[rule.category] ?? rule.category} · в методичке: «{rule.note}»</p>
      <div className="pv__lbl">Строгость по уровням</div>
      <table className="pv__levels"><tbody>
        {LEVELS.map((l) => {
          const v = levelDefault(rule, l);
          return <tr key={l} className={profile.level === l ? 'on' : ''}><td>{l} <span className="pv__muted">{LEVEL_TITLE[l]}</span></td>
            <td>{v ? <span className={`pv__pill pv__pill-${v}`}>{STRICTNESS_TITLE[v]}</span> : <span className="pv__muted">как решает правило</span>}</td></tr>;
        })}
      </tbody></table>
      <div className="pv__verdict">Сейчас: {cur.value ? <span className={`pv__pill pv__pill-${cur.value}`}>{STRICTNESS_TITLE[cur.value]}</span> : <span className="pv__muted">как решает правило</span>}
        {cur.overridden && def !== cur.value && <span className="pv__muted"> · по умолчанию {def ? STRICTNESS_TITLE[def] : 'как решает правило'}</span>}</div>
      {(RULE_PARAMS[rule.id] ?? []).map((s) => {
        const v = paramValue(profile, s.key, vocab.params);
        return (
          <div key={s.key} className="pv__param">
            <div className="pv__param-row"><label htmlFor={`pv-${s.key}`}>{s.label}</label><b className="pv__mono">{v}</b></div>
            <input id={`pv-${s.key}`} type="range" min={s.min} max={s.max} step={s.step} value={v} onChange={(e) => onParam(s.key, Number(e.target.value))} />
            <div className="pv__muted">По умолчанию {vocab.params[s.key]}. {s.hint}</div>
          </div>
        );
      })}
    </div>
  );
}

function Chips({ list, onChange, changed, label }: { list: string[]; onChange: (l: string[]) => void; changed?: boolean; label: string }) {
  const [v, setV] = useState('');
  const add = () => { const n = addItem(list, v); if (n !== list) onChange(n); setV(''); };
  return (
    <div className="pv__tags">
      {list.map((m, i) => (
        <span key={m} className={`pv__tag${changed ? ' pv__tag--chg' : ''}`}>{m}
          <button title="Убрать" aria-label={`Убрать ${m}`} onClick={() => onChange(removeItem(list, i))}>×</button></span>
      ))}
      <input className="pv__add" placeholder="+ добавить" aria-label={`Добавить: ${label}`} value={v} maxLength={64}
        onChange={(e) => setV(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); add(); } }} onBlur={() => v.trim() && add()} />
    </div>
  );
}

const VOCAB_TITLE: Record<VocabKey, string> = { markers: 'Маркер (уверенно)', fallback: 'Запасной (эвристика)', suffixes: 'Суффикс имени' };

function DictTab(p: {
  profile: Profile; vocab: VocabularyDefaults; selRole: string | null; onSelect: (r: string) => void;
  onRole: (role: string, key: VocabKey, list: string[]) => void; onResetRole: (role: string) => void;
  onNaming: (key: NamingKey, list: string[]) => void; onResetNaming: (key: NamingKey) => void; canReset: boolean;
}) {
  const roles = Object.keys(p.vocab.roles).sort((a, b) => {
    const ma = (p.vocab.roles[a].markers?.length ?? 0) > 0, mb = (p.vocab.roles[b].markers?.length ?? 0) > 0;
    return ma === mb ? a.localeCompare(b) : ma ? -1 : 1;     // роли с маркерами — выше технических
  });
  return (
    <div className="pv__body">
      <div className="pv__note">Здесь вы говорите, как называются вещи в вашем коде: правила смотрят на роли, а не на имена. Пустой список возвращает встроенное значение. Оранжевая рамка — отличие от встроенного.</div>
      {roles.map((role) => {
        const ch = isRoleChanged(p.profile, p.vocab, role);
        return (
          <div key={role} className={`pv__role${p.selRole === role ? ' pv__role--sel' : ''}`} onClick={() => p.onSelect(role)}>
            <div className="pv__role-h"><b>{role}</b>{ch && <span className="pv__src pv__src--cfg">изменено</span>}<span className="pv__spacer" />
              <button className="pv__rst" title="Сбросить роль" aria-label={`Сбросить роль ${role}`} disabled={!ch || !p.canReset} onClick={(e) => { e.stopPropagation(); p.onResetRole(role); }}><Icon name="reset" size={13} /></button></div>
            {(['markers', 'fallback', 'suffixes'] as VocabKey[]).map((k) => (
              <div key={k} className="pv__fld"><div className="pv__k">{VOCAB_TITLE[k]}</div>
                <Chips label={`${role}, ${VOCAB_TITLE[k]}`} list={roleList(p.profile, p.vocab, role, k)} changed={!!p.profile.vocabulary?.[role]?.[k]?.length}
                  onChange={(l) => p.onRole(role, k, l)} /></div>
            ))}
          </div>
        );
      })}
      <div className="pv__cat"><span>Имена сервисов в compose</span><span className="pv__ln" /></div>
      {NAMING_KEYS.map((k) => {
        const ch = isNamingChanged(p.profile, p.vocab, k);
        return (
          <div key={k} className="pv__role">
            <div className="pv__role-h"><b>{NAMING_TITLE[k].title}</b>{ch && <span className="pv__src pv__src--cfg">изменено</span>}<span className="pv__spacer" />
              <button className="pv__rst" title="Сбросить" aria-label={`Сбросить: ${NAMING_TITLE[k].title}`} disabled={!ch || !p.canReset} onClick={() => p.onResetNaming(k)}><Icon name="reset" size={13} /></button></div>
            <div className="pv__muted">{NAMING_TITLE[k].hint}</div>
            <Chips label={NAMING_TITLE[k].title} list={namingList(p.profile, p.vocab, k)} changed={ch} onChange={(l) => p.onNaming(k, l)} />
          </div>
        );
      })}
    </div>
  );
}

function DictHelp({ profile, vocab, role }: { profile: Profile; vocab: VocabularyDefaults; role: string | null }) {
  if (!role) return <div className="pv__muted">Выберите роль.</div>;
  const markers = roleList(profile, vocab, role, 'markers');
  const suffixes = roleList(profile, vocab, role, 'suffixes');
  return (
    <div>
      <h3 className="pv__h">Роль: {role}</h3>
      <p className="pv__sub">Как свод узнаёт тип этой роли и что от него ожидает.</p>
      <div className="pv__lbl">Что проверяется</div>
      {markers.length && suffixes.length
        ? <ul className="pv__list">{suffixes.map((s) => <li key={s}>тип с суффиксом <code>*{s}</code> должен реализовывать <code>{markers.join(' / ')}</code>; иначе — расхождение со сводом</li>)}</ul>
        : <div className="pv__muted">Для этой роли нет пары «суффикс → маркер»: она узнаётся по маркеру или запасному признаку, ожидание не проверяется.</div>}
      <div className="pv__lbl">Откуда значения</div>
      <div className="pv__muted">Встроенный свод → этот профиль → файл <code>archforge.conventions.json</code> в проекте (он сильнее всех). Число найденных в проекте типов появится позже.</div>
    </div>
  );
}

function ImportDialog({ onClose, onImport }: { onClose: () => void; onImport: (c: ConfigManifest) => void }) {
  const [text, setText] = useState('');
  const [err, setErr] = useState('');
  const submit = (raw: string) => { const r = parseImport(raw); if (r.ok) onImport(r.draft); else setErr(r.error); };
  return (
    <div className="pv-modal" onClick={(e) => { e.stopPropagation(); onClose(); }}>
      <div className="pv-modal__box" role="dialog" aria-label="Импорт конфигурации" onClick={(e) => e.stopPropagation()}>
        <h3>Импорт конфигурации</h3>
        <p className="pv__muted">Вставьте JSON или выберите файл. Появится несохранённая конфигурация — проверьте и сохраните.</p>
        <textarea rows={7} aria-label="JSON конфигурации" value={text} onChange={(e) => { setText(e.target.value); setErr(''); }} placeholder='{ "name": "…", "profile": { "level": "Standard" } }' />
        <input type="file" accept=".json,application/json" aria-label="Файл конфигурации"
          onChange={async (e) => { const f = e.target.files?.[0]; if (f) { if (f.size > 256 * 1024) setErr('Файл слишком большой (не больше 256 КБ)'); else submit(await f.text()); } }} />
        {err && <div className="pv__err" role="alert">{err}</div>}
        <div className="pv__row"><button className="pv__btn" onClick={onClose}>Отмена</button>
          <button className="pv__btn pv__btn--pri" disabled={!text.trim()} onClick={() => submit(text)}>Импортировать</button></div>
      </div>
    </div>
  );
}
