namespace ArchForge.Analysis.Core.Systems;

// Детектор DI-контейнера сервиса (E4.2, architecture): реальные баги, которые IDE не подсвечивает.
//   • di-captive — «пленная зависимость»: Singleton держит Scoped (DbContext фактически становится синглтоном: утечки соединений,
//     гонки, «использование после dispose»), в том числе через цепочку Transient. Только с ВЫСОКОЙ уверенностью — Warning.
//   • di-lifetime-hint — то же, но неуверенное или мягкое (Info, здоровье не красит): Singleton → Transient (Transient живёт как
//     Singleton — обычно безобидно), а также Scoped со средней/низкой уверенностью.
//   • di-missing — зависимость конструктора СВОЕГО типа (объявлен в таргете), которую никто не регистрировал: упадёт при резолве.
//
// ЧЕСТНОСТЬ (adr/0008): разбор синтаксический, поэтому у каждой находки есть уверенность в Note:
//   высокая — прямая цепочка «регистрация → конструктор»; средняя — несколько конструкторов, конфликт регистраций, Transient;
//   низкая — зависимость выведена из тела фабрики (sp => …) либо набор регистраций неполон (сканирование сборок, Scrutor, MediatR…).
// Чужие типы (ILogger, IOptions, типы пакетов) не проверяем — «не зарегистрировано» только для типов, объявленных в самом сервисе.
// Если в сервисе не найдено ни одной регистрации (другой контейнер — Autofac/Lamar, или DI собирается в другом проекте) — молчим.
// Известное ограничение: регистрации из расширений вне таргета (общий пакет) не видны — их типы тоже вне таргета, поэтому молчим.
public static class DiLifetimeDetector
{
    private const string High = "высокая", Medium = "средняя", Low = "низкая";
    private const int MaxDepth = 8;             // глубина цепочки Transient→…; защита от длинных графов
    private const int MaxPathsPerType = 50;     // путей к Scoped под одним Transient (ромбы в графе иначе дают взрыв)
    private const int FindingBudget = 20_000;   // общий предел находок на сервис: после него разбор останавливается
    /// <summary>Предел перечня в одной проблеме (как у CustomRuleDetector): число в названии — полное.</summary>
    public const int MaxItemsPerProblem = 1000;

    public static IReadOnlyList<SystemProblem> Detect(IReadOnlyList<ServiceAnalysis> analyses)
    {
        var problems = new List<SystemProblem>();
        foreach (var a in analyses)
            if (a.Di is { } di) problems.AddRange(DetectService(a.Service.Name, a.NodeId ?? a.Location, di));
        return problems;
    }

    private sealed record Finding(string Name, string Layer, string Note, string File, int Line, string Confidence);
    private sealed record Dep(string Type, bool Required, string Conf);
    private sealed record Hit(IReadOnlyList<string> Tail, string Conf, bool Scoped);

    private static IEnumerable<SystemProblem> DetectService(string service, string location, DiModel di)
    {
        // обычные регистрации сервиса; keyed — отдельно: резолвятся по ключу, с обычными не смешиваются и конфликтом не считаются
        var byService = di.Registrations.Where(r => !r.Keyed).GroupBy(r => r.Service, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var keyedTypes = di.Registrations.Where(r => r.Keyed).Select(r => r.Service).ToHashSet(StringComparer.Ordinal);

        // при РЕЗОЛВЕ зависимости побеждает последняя «обычная» регистрация; TryAdd действует, только если других нет
        DiRegistration? Effective(string type)
        {
            if (!byService.TryGetValue(type, out var list)) return null;
            return list.LastOrDefault(r => !r.TryAdd) ?? list[0];
        }
        bool Conflict(string type) => byService[type].Where(r => !r.TryAdd).Select(r => r.Lifetime).Distinct().Count() > 1;   // TryAdd ничего не перебивает — конфликтом не считается

        // класс по имени: одноимённые (partial, дубли в разных пространствах) — берём единственный с зависимостями; неоднозначно → null
        var classes = di.Classes.GroupBy(c => c.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        DiClass? ClassOf(string? name)
        {
            if (name is null || !classes.TryGetValue(name, out var l)) return null;
            var withDeps = l.Where(c => c.CtorDeps.Count > 0).ToList();
            return withDeps.Count switch { 1 => withDeps[0], 0 => l[0], _ => null };
        }

        // зависимости регистрации; фабрика — только то, что видно в её теле (обязательными не считаем)
        IReadOnlyList<Dep> DepsOf(DiRegistration r)
        {
            if (r.Factory) return (r.FactoryDeps ?? []).Select(t => new Dep(t, false, Low)).ToList();
            var cls = ClassOf(r.Impl ?? r.Service);
            if (cls is null) return [];
            var conf = cls.AmbiguousCtor ? Medium : High;   // несколько конструкторов: контейнер мог выбрать другой
            return cls.CtorDeps.Select(d => new Dep(d.Type, d.Required, conf)).ToList();
        }
        bool IsOwnResolvable(string type) => classes.TryGetValue(type, out var l) && l.Any(c => c.Kind is "interface" or "class");
        string NameOf(DiRegistration r) => r.Impl ?? r.Service;

        // --- корни обхода: ВСЕ обычные (не TryAdd) регистрации — несколько AddHostedService<W>(), несколько реализаций одного интерфейса;
        //     TryAdd — корень, только если он и есть действующая регистрация сервиса. Keyed — корни тоже (их зависимости реальны).
        var roots = new List<DiRegistration>();
        foreach (var (type, list) in byService)
        {
            roots.AddRange(list.Where(r => !r.TryAdd));
            if (list.All(r => r.TryAdd)) roots.Add(list[0]);
        }
        roots.AddRange(di.Registrations.Where(r => r.Keyed));
        roots = roots.OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Line).ThenBy(r => r.Service, StringComparer.Ordinal).ToList();

        var captive = new List<Finding>();
        var hints = new List<Finding>();
        var missing = new List<Finding>();
        var budget = 0;

        // --- достижимое из Transient: пути до Scoped/Transient глубже. Мемоизация по типу — общая для всех корней.
        var memo = new Dictionary<string, List<Hit>>(StringComparer.Ordinal);
        var inProgress = new HashSet<string>(StringComparer.Ordinal);
        List<Hit> Reach(DiRegistration transient, int depth)
        {
            if (memo.TryGetValue(transient.Service, out var cached)) return cached;
            var hits = new List<Hit>();
            if (depth >= MaxDepth || !inProgress.Add(transient.Service)) return hits;   // цикл/глубина: не запоминаем (результат неполный)
            foreach (var d in DepsOf(transient))
            {
                var e = Effective(d.Type);
                if (e is null || e.Lifetime == DiLifetime.Singleton) continue;
                var c = Weaker(d.Conf, e.TryAdd || Conflict(d.Type) ? Medium : High);
                hits.Add(new Hit([NameOf(e)], c, e.Lifetime == DiLifetime.Scoped));
                if (e.Lifetime == DiLifetime.Transient)
                    foreach (var sub in Reach(e, depth + 1))
                        hits.Add(new Hit([NameOf(e), .. sub.Tail], Weaker(c, sub.Conf), sub.Scoped));
                if (hits.Count >= MaxPathsPerType) break;
            }
            inProgress.Remove(transient.Service);
            memo[transient.Service] = hits;
            return hits;
        }

        foreach (var root in roots)
        {
            if (budget >= FindingBudget) break;
            var rootName = NameOf(root);
            var deps = DepsOf(root);

            // --- captive: от Singleton вниз; через Transient идём дальше (он наследует время жизни владельца)
            if (root.Lifetime == DiLifetime.Singleton)
                foreach (var d in deps)
                {
                    var e = Effective(d.Type);
                    if (e is null || e.Lifetime == DiLifetime.Singleton) continue;
                    var c = Weaker(d.Conf, e.TryAdd || Conflict(d.Type) ? Medium : High);   // регистраций несколько с разным временем жизни — не уверены, какая победит
                    var self = new Hit([NameOf(e)], c, e.Lifetime == DiLifetime.Scoped);
                    var all = new List<Hit> { self };
                    if (e.Lifetime == DiLifetime.Transient)
                        foreach (var sub in Reach(e, 1)) all.Add(new Hit([NameOf(e), .. sub.Tail], Weaker(c, sub.Conf), sub.Scoped));
                    foreach (var h in all)
                    {
                        if (budget++ >= FindingBudget) break;
                        var path = string.Join(" → ", new[] { rootName }.Concat(h.Tail));
                        if (h.Scoped)
                        {
                            var through = h.Tail.Count > 1 ? $" (через Transient: {string.Join(" → ", h.Tail.Take(h.Tail.Count - 1))})" : "";
                            var f = new Finding(path, "Singleton → Scoped", $"Scoped живёт столько же, сколько Singleton{through} · уверенность: {h.Conf}", root.File, root.Line, h.Conf);
                            (h.Conf == High ? captive : hints).Add(f);
                        }
                        else
                        {
                            var conf = Weaker(h.Conf, Medium);   // Transient в Singleton — мягкая находка в любом случае
                            hints.Add(new Finding(path, "Singleton → Transient", $"Transient зафиксирован в Singleton и живёт как он · уверенность: {conf}", root.File, root.Line, conf));
                        }
                    }
                }

            // --- missing: обязательные зависимости своих типов (молчим, если регистраций нет вообще — другой контейнер)
            if (di.Registrations.Count > 0)
                foreach (var d in deps)
                    if (d.Required && !byService.ContainsKey(d.Type) && !keyedTypes.Contains(d.Type) && IsOwnResolvable(d.Type))
                        missing.Add(MissingFinding(rootName, d.Type, d.Conf, root.File, root.Line));
        }

        // контроллеры резолвятся фреймворком без регистрации, но зависимости им нужны так же
        if (di.Registrations.Count > 0)
            foreach (var c in di.Classes.Where(c => c.Resolvable && !byService.ContainsKey(c.Name)))
            {
                if (classes[c.Name].Count(x => x.CtorDeps.Count > 0) > 1) continue;   // одноимённые контроллеры — не гадаем
                foreach (var d in c.CtorDeps.Where(d => d.Required && !byService.ContainsKey(d.Type) && !keyedTypes.Contains(d.Type) && IsOwnResolvable(d.Type)))
                    missing.Add(MissingFinding(c.Name, d.Type, c.AmbiguousCtor ? Medium : High, c.File, c.Line));
            }

        Finding MissingFinding(string owner, string dep, string conf, string file, int line)
        {
            if (di.OpaqueReason is not null) conf = Low;
            var note = di.OpaqueReason is null
                ? $"«{dep}» нигде не зарегистрирован — упадёт при резолве · уверенность: {conf}"
                : $"«{dep}» не найден среди регистраций, но набор неполон ({di.OpaqueReason}) · уверенность: {conf}";
            return new Finding($"{owner} → {dep}", "ctor", note, file, line, conf);
        }

        var result = new List<SystemProblem>();
        Emit(result, "di-captive", ProblemSeverity.Warning, captive, location,
            n => $"«{service}»: {n} {Plural(n)} с неверным временем жизни в DI — Singleton держит Scoped");
        Emit(result, "di-lifetime-hint", ProblemSeverity.Info, hints, location,
            n => $"«{service}»: {n} возможных проблем времён жизни в DI (Singleton → Transient либо неуверенный Scoped)");
        Emit(result, "di-missing", missing.Any(f => f.Confidence == High) ? ProblemSeverity.Warning : ProblemSeverity.Coverage, missing, location,
            n => $"«{service}»: {n} незарегистрированных зависимостей в DI — упадут при резолве");
        return result;
    }

    private static void Emit(List<SystemProblem> result, string kind, ProblemSeverity severity, List<Finding> findings, string location, Func<int, string> title)
    {
        var all = findings.GroupBy(f => (f.Name, f.Layer, f.File, f.Line)).Select(g => g.First())
            .OrderBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
        if (all.Count == 0) return;
        var shown = all.Count > MaxItemsPerProblem ? all.GetRange(0, MaxItemsPerProblem) : all;
        var text = title(all.Count) + (all.Count > shown.Count ? $" (показано {shown.Count} из {all.Count})" : "");
        result.Add(new SystemProblem(severity, kind, text, location, ProblemCategory.Architecture,
            shown.Select(f => new ProblemItem(f.Name, f.Layer, f.Note, f.File, f.Line)).ToList()));
    }

    private static string Plural(int n) => n % 10 == 1 && n % 100 != 11 ? "зависимость" : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? "зависимости" : "зависимостей";

    // слабейшая из двух уверенностей
    private static string Weaker(string a, string b) => Rank(a) <= Rank(b) ? a : b;
    private static int Rank(string c) => c switch { High => 2, Medium => 1, _ => 0 };
}
