import { Component, type ReactNode, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Icon } from './Icon';
import {
  type ConfigManifest, type Level, type Profile, type RuleCatalog, type RuleDef, type RuleFilter, type Strictness,
  type VocabularyDefaults, type ProfileParams, type Preview, type NamingKey, type VocabKey, type ProfileForPath,
  LEVELS, LEVEL_TITLE, STRICTNESS, STRICTNESS_TITLE, CATEGORY_TITLE, DEFAULT_LEVEL, RULE_PARAMS, NAMING_KEYS, NAMING_TITLE,
  ProfileApiError, addItem, removeItem, bindProject, changedRuleCount, createConfig, deleteConfig, effectiveStrictness,
  exportManifest, fetchProjectProfile, fetchRules, fetchVocabulary, filterRules, groupByCategory, isNamingChanged,
  isRoleChanged, isRuleChanged, levelDefault, listConfigs, namingList, paramValue, parseImport, profilesEqual, resetCategory,
  resetNaming, resetRole, resetRule, resetToLevel, roleList, updateConfig, withLevel, withNaming, withParam, withRoleList,
  withStrictness, fetchPreview, impactDelta, impactTotal, ruleImpact, roleCoverage, paramSample,
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

/** Роли в порядке показа: с маркерами выше технических. Первая из них открывается по умолчанию. */
function sortedRoles(vocab: VocabularyDefaults): string[] {
  const has = (r: string) => (vocab.roles[r].markers?.length ?? 0) > 0;
  return Object.keys(vocab.roles).sort((a, b) => has(a) === has(b) ? a.localeCompare(b) : has(a) ? -1 : 1);
}

function messageOf(e: unknown): string[] {
  if (e instanceof ProfileApiError) return e.errors;
  return [e instanceof Error ? e.message : 'Что-то пошло не так'];
}

/** Граница ошибок: сбой отрисовки окна настройки не должен ронять всю карту — показываем сообщение и даём закрыть. */
class ProfileBoundary extends Component<{ onClose: () => void; children: ReactNode }, { failed: boolean }> {
  state = { failed: false };
  static getDerivedStateFromError() { return { failed: true }; }
  render() {
    if (!this.state.failed) return this.props.children;
    return (
      <div className="pv-backdrop" onClick={this.props.onClose}>
        <div className="pv pv--small" role="alertdialog" aria-label="Ошибка окна профиля" onClick={(e) => e.stopPropagation()}>
          <div className="pv__fail">
            <p>В окне профиля произошла ошибка. Карта и сохранённые данные не затронуты.</p>
            <button className="pv__btn" onClick={this.props.onClose}>Закрыть</button>
          </div>
        </div>
      </div>
    );
  }
}

export function ProfileView(props: Props) {
  return <ProfileBoundary onClose={props.onClose}><ProfileViewInner {...props} /></ProfileBoundary>;
}

function ProfileViewInner({ path, onClose, onApplied }: Props) {
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
  const [conflict, setConflict] = useState(false);   // 409: конфигурация изменилась в другом месте
  const [note, setNote] = useState('');
  const [pending, setPending] = useState<(() => void) | null>(null);   // действие, ждущее подтверждения «отбросить правки»
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [importOpen, setImportOpen] = useState(false);
  const [preview, setPreview] = useState<Preview | null>(null);      // влияние профиля на проект (последний удачный расчёт)
  const [previewBusy, setPreviewBusy] = useState(false);
  const [previewErr, setPreviewErr] = useState<string | null>(null);
  const [previewTick, setPreviewTick] = useState(0);                    // «Повторить» после ошибки
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

  // Предпросмотр влияния: пересчёт после паузы в правках. Предыдущий запрос рвём (Analysis прекращает работу).
  // Код проекта читается один раз, дальше Analysis отдаёт его из кеша — правка стоит только детекторов.
  const profileKey = JSON.stringify(profile);
  useEffect(() => {
    if (!catalog) return;
    const ctrl = new AbortController();
    setPreviewBusy(true); setPreviewErr(null);
    const timer = setTimeout(async () => {
      try {
        const pv = await fetchPreview(path, JSON.parse(profileKey) as Profile, ctrl.signal);
        if (!ctrl.signal.aborted) { setPreview(pv); setPreviewErr(null); }
      } catch (e) {
        if (!ctrl.signal.aborted) setPreviewErr(messageOf(e)[0]);
      } finally {
        if (!ctrl.signal.aborted) setPreviewBusy(false);
      }
    }, 450);
    return () => { clearTimeout(timer); ctrl.abort(); };
  }, [path, profileKey, catalog, previewTick]);

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
    const onKey = (e: KeyboardEvent) => { if (e.key !== 'Escape') return;
      if (pending) setPending(null); else if (importOpen) setImportOpen(false); else if (menu) setMenu(false); else close(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  });
  useEffect(() => { dialogRef.current?.focus(); }, []);

  // ── действия ──
  const run = async (fn: () => Promise<void>) => {
    setBusy(true); setErrors([]); setNote(''); setConflict(false);
    try { await fn(); }
    catch (e) {
      setConflict(e instanceof ProfileApiError && e.status === 409);
      setErrors(messageOf(e));
    } finally { setBusy(false); }
  };

  const save = async (): Promise<ConfigManifest | null> => {
    if (!draft) return persisted;
    if (!draft.name.trim()) { setErrors(['Укажите название конфигурации']); return null; }
    let saved: ConfigManifest | null = null;
    await run(async () => {
      saved = sel === NEW || !draft.id ? await createConfig(draft) : await updateConfig(draft.id, draft);
      const s = saved;
      const wasBound = isBound(s);                  // привязана к проекту → карта должна пересчитаться по новой версии
      setConfigs((list) => list.some((c) => c.id === s.id) ? list.map((c) => c.id === s.id ? s : c) : [...list, s]);   // на месте, без прыжков
      setSel(s.id ?? NEW); setDraft(null);
      setNote(wasBound ? `Сохранено: версия v${s.version}. Проект пересчитывается` : `Сохранено: версия v${s.version}`);
      if (wasBound) onApplied();
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
      const wasBound = isBound(persisted);
      setBound(await fetchProjectProfile(path));    // привязка к удалённой конфигурации снята сервером
      setNote(wasBound ? 'Конфигурация удалена. Проект пересчитывается по уровню по умолчанию' : 'Конфигурация удалена');
      if (wasBound) onApplied();
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
    document.body.appendChild(a);                   // в части браузеров ссылка вне DOM не скачивает
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
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
      <div className="pv-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
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
    <div className="pv-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget) close(); }}>
      <div className="pv" ref={dialogRef} tabIndex={-1} role="dialog" aria-modal="true" aria-label="Профиль проекта" onClick={(e) => { e.stopPropagation(); setMenu(false); }}>
        <div className="pv__head">
          <div className="pv__title"><Icon name="box" size={16} /> Профиль проекта <span className="pv__path" title={path}>{path}</span></div>
          <button className="pv__x" onClick={close} title="Закрыть"><Icon name="close" size={16} /></button>
        </div>

        {!ready ? <div className="pv__loading">Загружаю настройки…</div> : (
          <div className="pv__cols" inert={busy} aria-busy={busy}>
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

              <ImpactPanel preview={preview} busy={previewBusy} error={previewErr} level={profile.level} onRetry={() => setPreviewTick((t) => t + 1)} />
              {preview && preview.hint.level !== profile.level && (
                <div className="pv__hint">
                  <span>По коду проекта похоже на <b>{preview.hint.level}</b>: {preview.hint.reasons.join('; ')}. Это совет, выбор за вами.</span>
                  <button className="pv__btn" onClick={() => readOnly ? selectConfig(`lvl-${preview.hint.level.toLowerCase()}`) : edit((p) => withLevel(p, preview.hint.level))}>
                    Выбрать {preview.hint.level}</button>
                </div>
              )}
              {preview && preview.hint.level === profile.level && (
                <div className="pv__hint pv__hint--ok">По коду проекта уровень <b>{profile.level}</b> подходит: {preview.hint.reasons.join('; ')}.</div>
              )}

              <div className="pv__tabs" role="tablist">
                <button role="tab" aria-selected={tab === 'rules'} className={tab === 'rules' ? 'on' : ''} onClick={() => setTab('rules')}>
                  Правила{nChanged ? ` · изменено ${nChanged}` : ''}</button>
                <button role="tab" aria-selected={tab === 'dict'} className={tab === 'dict' ? 'on' : ''} onClick={() => setTab('dict')}>Словарь и имена</button>
                <span className="pv__spacer" />
                {!readOnly && (
                  <div className="pv__menu">
                    <button className="pv__btn" aria-haspopup="menu" aria-expanded={menu} onClick={(e) => { e.stopPropagation(); setMenu((m) => !m); }}>Сбросить ▾</button>
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
                  nChanged={nChanged} preview={preview} selected={activeRule?.id ?? null} onSelect={setSelRule}
                  onSet={(r, s) => { setSelRule(r.id); edit((p) => withStrictness(p, r, s)); }}
                  onReset={(r) => edit((p) => resetRule(p, r))}
                  onResetCategory={(cat) => edit((p) => resetCategory(p, rules, cat))} canReset={!readOnly} />
              ) : (
                <DictTab profile={profile} vocab={vocab} preview={preview} selRole={selRole ?? sortedRoles(vocab)[0] ?? null} onSelect={setSelRole}
                  onRole={(role, key, list) => { setSelRole(role); edit((p) => withRoleList(p, vocab, role, key, list)); }}
                  onResetRole={(role) => edit((p) => resetRole(p, role))}
                  onNaming={(key, list) => edit((p) => withNaming(p, vocab, key, list))}
                  onResetNaming={(key) => edit((p) => resetNaming(p, key))} canReset={!readOnly} />
              )}
            </main>

            <section className="pv__right">
              {tab === 'rules' && activeRule
                ? <RuleDetails rule={activeRule} profile={profile} vocab={vocab} onParam={(key, v) => edit((p) => withParam(p, key, v, defaults))} />
                : <DictHelp profile={profile} vocab={vocab} role={selRole ?? sortedRoles(vocab)[0] ?? null} preview={preview} />}
            </section>
          </div>
        )}

        {(errors.length > 0 || note) && (
          <div className={errors.length ? 'pv__banner pv__banner--err' : 'pv__banner'} role={errors.length ? 'alert' : 'status'}>
            {errors.length ? errors.map((m, i) => <div key={i}>{m}</div>) : note}
            {conflict && <button className="pv__btn" onClick={() => { setConflict(false); void load(); }}>Загрузить актуальную версию (правки пропадут)</button>}
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
                <button className="pv__btn" autoFocus onClick={() => setPending(null)}>Остаться</button>
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
  q: string; setQ: (v: string) => void; filter: RuleFilter; setFilter: (f: RuleFilter) => void; nChanged: number; preview: Preview | null;
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
              <div key={r.id} className={`pv__rule${p.selected === r.id ? ' pv__rule--sel' : ''}`} role="button" tabIndex={0} aria-pressed={p.selected === r.id}
                onClick={() => p.onSelect(r.id)} onKeyDown={(e) => { if (e.target === e.currentTarget && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); p.onSelect(r.id); } }}>
                <div className="pv__rule-main">
                  <div className="pv__rule-t">{r.title}</div>
                  <div className="pv__rule-d">
                    <span className={`pv__src${changed ? ' pv__src--cfg' : ''}`}>{changed ? 'ваша конфигурация' : `уровень ${p.profile.level}`}</span>
                    {e.value === 'Off' ? <span>не проверяется на вашем уровне</span>
                      : e.value === null ? <span>как решает само правило</span> : null}
                    <RuleFindings preview={p.preview} ruleId={r.id} />
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

function RuleFindings({ preview, ruleId }: { preview: Preview | null; ruleId: string }) {
  const now = ruleImpact(preview, true, ruleId);
  const was = ruleImpact(preview, false, ruleId);
  if (!now || !now.enabled) return null;
  const changed = was && was.count !== now.count;
  return <span className="pv__found" title="Сколько находок даёт правило на этом проекте">найдено: <b>{now.count}</b>{changed ? <span className="pv__muted"> (на уровне без правок: {was.count})</span> : null}</span>;
}

function CodeBlock({ lines, kind }: { lines: string[]; kind: 'bad' | 'good' }) {
  return <pre className={`pv__code pv__code--${kind}`}>{lines.map((l, i) => <span key={i} className="pv__code-l">{l}{'\n'}</span>)}</pre>;
}

/** Влияние профиля на проект одной полосой: находки по строгости и разница с уровнем без правок. Всегда на виду. */
function ImpactPanel({ preview, busy, error, onRetry, level }: { preview: Preview | null; busy: boolean; error: string | null; onRetry: () => void; level: string }) {
  const off = preview ? Object.values(preview.current.rules).filter((r) => !r.enabled).length : 0;
  const d = preview ? impactDelta(preview) : 0;
  return (
    <div className="pv__impact" aria-busy={busy} aria-label="Влияние на проект">
      <span className="pv__impact-t">Влияние на проект</span>
      {preview && (
        <span className={`pv__impact-b${busy ? ' pv__dim' : ''}`}>
          <b className="pv__big">{impactTotal(preview.current)}</b> <span className="pv__muted">находок</span>
          <span className="pv__muted" title={`На уровне ${level} без правок: ${impactTotal(preview.baseline)}`}>
            {d === 0 ? ` · как на уровне ${level}` : ` · ${d > 0 ? '+' : '−'}${Math.abs(d)} к уровню ${level} (было ${impactTotal(preview.baseline)})`}</span>
          <span className="pv__pill pv__pill-Error">ошибок {preview.current.errors}</span>
          <span className="pv__pill pv__pill-Warning">предупр. {preview.current.warnings}</span>
          <span className="pv__pill pv__pill-Info">инфо {preview.current.infos}</span>
          {off > 0 && <span className="pv__pill pv__pill-Off">выкл. правил {off}</span>}
        </span>
      )}
      {busy && <span className="pv__muted">{preview ? 'пересчитываю…' : 'читаю код проекта — первый раз это занимает время, дальше быстро…'}</span>}
      {error && <span className="pv__err" role="alert">{error} <button className="pv__link" onClick={onRetry}>Повторить</button></span>}
      {!preview && !busy && !error && <span className="pv__muted">нет данных</span>}
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
      {rule.example && (
        <>
          <div className="pv__lbl">Плохо</div><CodeBlock lines={rule.example.bad} kind="bad" />
          <div className="pv__lbl">Хорошо</div><CodeBlock lines={rule.example.good} kind="good" />
          <div className="pv__muted">{cur.value === 'Off' ? 'Правило выключено: такой код не даст находок.' : cur.value ? `Такой код даст находку: ${STRICTNESS_TITLE[cur.value]}.` : 'Серьёзность находки определяет само правило.'}</div>
        </>
      )}
      {(RULE_PARAMS[rule.id] ?? []).map((s) => {
        const v = paramValue(profile, s.key, vocab.params);
        const sample = paramSample(rule.id, s.key, v);
        return (
          <div key={s.key} className="pv__param">
            <div className="pv__param-row"><label htmlFor={`pv-${s.key}`}>{s.label}</label><b className="pv__mono">{v}</b></div>
            <input id={`pv-${s.key}`} type="range" min={s.min} max={s.max} step={s.step} value={v} onChange={(e) => onParam(s.key, Number(e.target.value))} />
            <div className="pv__muted">По умолчанию {vocab.params[s.key]}. {s.hint}</div>
            {sample && (
              <div className="pv__sample" aria-label="Пример: что попадёт под правило">
                {sample.map((r) => <div key={r.label} className={`pv__sample-row${r.hit ? ' pv__sample-row--hit' : ''}`}><span>{r.label}</span><span className="pv__mono">{r.value}</span><span>{r.hit ? 'находка' : 'пропуск'}</span></div>)}
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}

function Chips({ list, onChange, changed, label, counts }: { list: string[]; onChange: (l: string[]) => void; changed?: boolean; label: string; counts?: Record<string, number> | null }) {
  const [v, setV] = useState('');
  const add = () => { const n = addItem(list, v); if (n !== list) onChange(n); setV(''); };
  return (
    <div className="pv__tags">
      {list.map((m, i) => (
        <span key={`${m}-${i}`} className={`pv__tag${changed ? ' pv__tag--chg' : ''}${counts && !counts[m] ? ' pv__tag--zero' : ''}`}
          title={counts ? (counts[m] ? `Типов с ${m} в проекте: ${counts[m]}` : `В проекте нет типов с ${m}`) : undefined}>{m}{counts ? <i className="pv__tag-n">{counts[m] ?? 0}</i> : null}
          <button title="Убрать" aria-label={`Убрать ${m}`} onClick={() => onChange(removeItem(list, i))}>×</button></span>
      ))}
      <input className="pv__add" placeholder="+ добавить" aria-label={`Добавить: ${label}`} value={v} maxLength={64}
        onChange={(e) => setV(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); add(); } }} onBlur={() => v.trim() && add()} />
    </div>
  );
}

const VOCAB_TITLE: Record<VocabKey, string> = { markers: 'Маркер (уверенно)', fallback: 'Запасной (эвристика)', suffixes: 'Суффикс имени' };

function DictTab(p: {
  profile: Profile; vocab: VocabularyDefaults; preview: Preview | null; selRole: string | null; onSelect: (r: string) => void;
  onRole: (role: string, key: VocabKey, list: string[]) => void; onResetRole: (role: string) => void;
  onNaming: (key: NamingKey, list: string[]) => void; onResetNaming: (key: NamingKey) => void; canReset: boolean;
}) {
  const roles = sortedRoles(p.vocab);
  return (
    <div className="pv__body">
      <div className="pv__note">Здесь вы говорите, как называются вещи в вашем коде: правила смотрят на роли, а не на имена. Пустой список возвращает встроенное значение. Жёлтый фон — отличие от встроенного. Число у маркера — сколько типов проекта его используют; оранжевая рамка — в проекте такого маркера нет.</div>
      {roles.map((role) => {
        const ch = isRoleChanged(p.profile, p.vocab, role);
        const cov = roleCoverage(p.preview, roleList(p.profile, p.vocab, role, 'markers'), roleList(p.profile, p.vocab, role, 'suffixes'));
        return (
          <div key={role} className={`pv__role${p.selRole === role ? ' pv__role--sel' : ''}`} onClick={() => p.onSelect(role)} onFocus={() => p.onSelect(role)}>
            <div className="pv__role-h"><b>{role}</b>{ch && <span className="pv__src pv__src--cfg">изменено</span>}
              {cov && cov.total > 0 && <span className={`pv__muted${cov.marked < cov.total ? ' pv__muted--warn' : ''}`} title="Типов с суффиксами роли и сколько из них реализуют её маркер">узнано по маркеру: {cov.marked} из {cov.total}</span>}<span className="pv__spacer" />
              <button className="pv__rst" title="Сбросить роль" aria-label={`Сбросить роль ${role}`} disabled={!ch || !p.canReset} onClick={(e) => { e.stopPropagation(); p.onResetRole(role); }}><Icon name="reset" size={13} /></button></div>
            {(['markers', 'fallback', 'suffixes'] as VocabKey[]).map((k) => (
              <div key={k} className="pv__fld"><div className="pv__k">{VOCAB_TITLE[k]}</div>
                <Chips label={`${role}, ${VOCAB_TITLE[k]}`} list={roleList(p.profile, p.vocab, role, k)} changed={!!p.profile.vocabulary?.[role]?.[k]?.length}
                  counts={k === 'suffixes' ? null : p.preview?.markerCounts} onChange={(l) => p.onRole(role, k, l)} /></div>
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

function DictHelp({ profile, vocab, role, preview }: { profile: Profile; vocab: VocabularyDefaults; role: string | null; preview: Preview | null }) {
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
      <div className="pv__muted">Встроенный свод → этот профиль → файл <code>archforge.conventions.json</code> в проекте (он сильнее всех).{preview ? ' Числа у маркеров и «узнано по маркеру» посчитаны по коду проекта.' : ''}</div>
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
        <textarea rows={7} autoFocus aria-label="JSON конфигурации" value={text} onChange={(e) => { setText(e.target.value); setErr(''); }} placeholder='{ "name": "…", "profile": { "level": "Standard" } }' />
        <input type="file" accept=".json,application/json" aria-label="Файл конфигурации"
          onChange={async (e) => { const f = e.target.files?.[0]; if (f) { if (f.size > 256 * 1024) setErr('Файл слишком большой (не больше 256 КБ)'); else submit(await f.text()); } }} />
        {err && <div className="pv__err" role="alert">{err}</div>}
        <div className="pv__row"><button className="pv__btn" onClick={onClose}>Отмена</button>
          <button className="pv__btn pv__btn--pri" disabled={!text.trim()} onClick={() => submit(text)}>Импортировать</button></div>
      </div>
    </div>
  );
}
