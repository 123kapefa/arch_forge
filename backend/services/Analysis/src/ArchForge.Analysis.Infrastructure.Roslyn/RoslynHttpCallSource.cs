using ArchForge.Analysis.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpSyntax;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpClientCalls;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpWrapperCalls;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpBaseAddresses;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpConfigRefs;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpConfigSections;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpClientRegistry;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpRegisteredClients;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpCallEmitter;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpRefitCalls;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Исходящие HTTP-вызовы (E3.1): HttpClient.GetAsync/PostAsync/… и SendAsync(new HttpRequestMessage(HttpMethod.X, "url")).
// Чисто СИНТАКСИЧЕСКИЙ разбор (как RoslynMinimalApiSource).
// Срез 1: абсолютный URL-литерал (http[s]://host/…). Срез 2: относительный путь (литерал или интерполяция) разрешается по
// ЛИТЕРАЛЬНОМУ BaseAddress (`c.BaseAddress = new Uri("http://…")`, инициализатор объекта, лямбда AddHttpClient) из ближайшей
// области: метод → тип (для кода верхнего уровня — файл). Два разных базовых адреса в одной области, нелитеральный адрес или адрес в другом файле —
// вызов не приписываем (adr/0008: неоднозначное не выдумываем).
// Срез 3а: адрес берётся и из реестра DI по ВСЕЙ папке сервиса — типизированные клиенты (AddHttpClient<T>/<TI,TImpl>, цепочка
// ConfigureHttpClient) и именованные (AddHttpClient("имя") ↔ CreateClient("имя")), если адрес — литерал. Адреса из конфигурации
// (new Uri(options.Url)) — срез 3б, Refit — срез 4.
// Секреты: сохраняем лишь scheme://host[:port]/path — query отбрасываем (secrets-config); подстановки интерполяции
// превращаются в {имя} и значения не несут.
public sealed class RoslynHttpCallSource : IHttpCallSource
{
    public IReadOnlyList<HttpCallHit> Read(string root, CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) return [];

        // Проход 1: файлы, где вообще есть HTTP-клиент или http(s)-литерал. Деревья НЕ храним (память не растёт с числом файлов):
        // из каждого берём только регистрации клиентов DI, а список кандидатов разбираем заново во втором проходе.
        var candidates = new List<string>();
        var registry = new ClientRegistry();
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);   // класс options → секция конфигурации (срез 3б)
        var regSections = new Dictionary<string, string>(StringComparer.Ordinal);   // то же, но из РЕГИСТРАЦИИ Configure<T>/Bind (сильнее константы и догадки)
        var pendingConst = new List<(string Type, string Owner)>();                  // Configure<T>(GetSection(Owner.SectionName)) — константу разрешим после обхода
        foreach (var file in RoslynScan.EnumerateCsCapped(root, out _)) // тот же кап файлов, что и в семантике
        {
            ct.ThrowIfCancellationRequested();
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }
            if (IsCandidate(text)) candidates.Add(file);
            var wantsReg = text.Contains("AddHttpClient", StringComparison.Ordinal) || text.Contains("AddRefitClient", StringComparison.Ordinal)
                || text.Contains("RestService", StringComparison.Ordinal);
            var wantsSections = text.Contains("const string", StringComparison.Ordinal)
                && (text.Contains("Section", StringComparison.Ordinal) || text.Contains("ConfigKey", StringComparison.Ordinal));
            var wantsOptionRegs = text.Contains("Configure<", StringComparison.Ordinal) || text.Contains("AddOptions<", StringComparison.Ordinal)
                || text.Contains("BindConfiguration", StringComparison.Ordinal);
            if (!wantsReg && !wantsSections && !wantsOptionRegs) continue;
            var tree = CSharpSyntaxTree.ParseText(text, cancellationToken: ct).GetRoot(ct);
            if (wantsReg) CollectRegistrations(tree, registry, ct);
            if (wantsSections) CollectSections(tree, sections);
            if (wantsOptionRegs) CollectOptionRegistrations(tree, regSections, pendingConst);
        }
        // `Configure<T>(GetSection(Owner.SectionName))`: константа Owner — из собранных секций; регистрация перекрывает догадку и константу класса
        foreach (var (type, owner) in pendingConst)
            if (sections.TryGetValue(owner, out var constSection) && constSection.Length > 0) AddRegistered(regSections, type, constSection);
            // константу прочитать нельзя — регистрацию не учитываем (прежняя константа класса/догадка по имени остаются как были)
        foreach (var (type, section) in regSections) sections[type] = section;
        if (registry.Count > MaxClientRegistrations) registry = new ClientRegistry { Count = registry.Count };   // слишком много — не используем

        // Проход 2: сами вызовы.
        var hits = new List<HttpCallHit>();
        var refitHits = new List<(string Name, string Ns, HttpCallHit Hit)>();
        foreach (var file in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var text = ReadCandidate(file);
            if (text is null || (!text.Contains("Async", StringComparison.Ordinal) && registry.Refit.Count == 0)) continue;   // нет вызовов *Async и Refit-регистраций — нечего искать
            var path = file.Replace('\\', '/');
            var treeRoot = CSharpSyntaxTree.ParseText(text, cancellationToken: ct).GetRoot(ct);
            var bases = CollectBaseAddresses(treeRoot);      // null — слишком много адресов в файле: относительные пути не разрешаем
            var ctx = new FileCtx();   // кэши: переменные из CreateClient по членам, внедрённые HttpClient-члены по типам
            foreach (var inv in treeRoot.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                // Обёртки базового клиента (`GetAsync<T>(url)` / `this.PostAsync(url, body)` / `base.DeleteAsync(url)`) без получателя-HttpClient:
                // вызов метода самого класса. Считаем вызовом ТОЛЬКО внутри класса зарегистрированного типизированного клиента — там адрес известен.
                if (WrapperCall(inv) is { } wrap && WrapperVerbs.TryGetValue(wrap, out var wverb))
                {
                    if (registry.Typed.Count == 0) continue;
                    if (inv.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() is not { } wtd || !registry.Typed.ContainsKey(wtd.Identifier.Text)) continue;
                    // собственный метод класса с этим именем должен быть HTTP-обёрткой (передаёт URL в HttpClient); иначе это не вызов (приватный GetAsync(string key))
                    if (!IsHttpWrapper(wtd, wrap, ctx)) continue;
                    Add(hits, wverb, FirstUrlText(inv.ArgumentList.Arguments), inv, inv.Expression, path, bases, registry, ctx, sections, wrapper: true);
                    continue;
                }
                if (inv.Expression is IdentifierNameSyntax or GenericNameSyntax) continue;   // прочие вызовы без получателя — не HTTP
                if (inv.Expression is not MemberAccessExpressionSyntax ma) continue;
                var name = ma.Name.Identifier.Text;
                if (ClientVerbs.TryGetValue(name, out var verb))
                    Add(hits, verb, FirstUrlText(inv.ArgumentList.Arguments), inv, ma.Expression, path, bases, registry, ctx, sections);
                else if (name == "SendAsync")
                {
                    var (verb2, url2) = ReadSend(inv.ArgumentList.Arguments);
                    if (verb2 is not null) Add(hits, verb2, url2, inv, ma.Expression, path, bases, registry, ctx, sections);
                }
            }
            if (registry.Refit.Count > 0) CollectRefitCalls(treeRoot, path, registry, sections, refitHits);
        }
        // Refit: интерфейс ищется по простому имени, поэтому одноимённые интерфейсы из разных пространств имён неразличимы — их вызовы
        // не приписываем (adr/0008), берём только интерфейсы, чьё имя объявлено в одном пространстве имён.
        var nsByName = refitHits.GroupBy(h => h.Name).ToDictionary(g => g.Key, g => g.Select(h => h.Ns).Distinct(StringComparer.Ordinal).Count());
        hits.AddRange(refitHits.Where(h => nsByName[h.Name] == 1).Select(h => h.Hit));
        return hits;
    }

    // Текст файла, если он интересен (есть HTTP-клиент или http(s)-литерал); иначе null. Недоступный файл — тоже null.
    private static string? ReadCandidate(string file)
    {
        string text;
        try { text = File.ReadAllText(file); } catch { return null; }
        return IsCandidate(text) ? text : null;
    }

    // `[Get(` / `[Post(` … — признак интерфейса Refit без упоминания «Refit» в файле (global using).
    private static bool HasRefitVerbAttribute(string text) =>
        text.Contains("[Get(", StringComparison.Ordinal) || text.Contains("[Post(", StringComparison.Ordinal) || text.Contains("[Put(", StringComparison.Ordinal)
        || text.Contains("[Delete(", StringComparison.Ordinal) || text.Contains("[Patch(", StringComparison.Ordinal);

    private static bool IsCandidate(string text) =>
        text.Contains("HttpClient", StringComparison.Ordinal)
        || text.Contains("Refit", StringComparison.Ordinal)
        || HasRefitVerbAttribute(text)                       // интерфейс Refit может не упоминать Refit при global using
        || text.Contains("http://", StringComparison.OrdinalIgnoreCase)
        || text.Contains("https://", StringComparison.OrdinalIgnoreCase);

    internal static string? CalleeName(InvocationExpressionSyntax inv) => HttpSyntax.CalleeName(inv);
}
