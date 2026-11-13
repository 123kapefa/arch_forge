using ArchForge.Analysis.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
    // Имя метода HttpClient → HTTP-verb. *Async-хелперы, возвращающие тело (GetStringAsync/…), — тоже GET.
    private static readonly IReadOnlyDictionary<string, string> ClientVerbs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GetAsync"] = "GET", ["GetStringAsync"] = "GET", ["GetByteArrayAsync"] = "GET", ["GetStreamAsync"] = "GET",
        ["GetFromJsonAsync"] = "GET",
        ["PostAsync"] = "POST", ["PostAsJsonAsync"] = "POST",
        ["PutAsync"] = "PUT", ["PutAsJsonAsync"] = "PUT",
        ["PatchAsync"] = "PATCH", ["PatchAsJsonAsync"] = "PATCH",
        ["DeleteAsync"] = "DELETE",
    };

    // HttpMethod.X → verb (для SendAsync(new HttpRequestMessage(HttpMethod.X, "url"))).
    private static readonly IReadOnlyDictionary<string, string> HttpMethodMembers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Get"] = "GET", ["Post"] = "POST", ["Put"] = "PUT", ["Delete"] = "DELETE",
        ["Patch"] = "PATCH", ["Head"] = "HEAD", ["Options"] = "OPTIONS",
    };

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

    // Методы-обёртки базового HTTP-клиента: имя → метод HTTP. URL — первый аргумент, как у самого HttpClient.
    private static readonly IReadOnlyDictionary<string, string> WrapperVerbs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GetAsync"] = "GET", ["GetOptionalAsync"] = "GET", ["GetFromJsonAsync"] = "GET", ["GetStringAsync"] = "GET",
        ["PostAsync"] = "POST", ["PostAsJsonAsync"] = "POST", ["PutAsync"] = "PUT", ["PutAsJsonAsync"] = "PUT",
        ["PatchAsync"] = "PATCH", ["DeleteAsync"] = "DELETE",
    };

    // Метод `name`, объявленный в самом классе, должен быть HTTP-обёрткой: его тело передаёт первый параметр (URL) в вызов HttpClient
    // (в том числе внутри лямбды `() => HttpClient.GetAsync(url, ct)`). Не объявлен в классе — унаследован от базового клиента (тело не видно): принимаем.
    private static bool IsHttpWrapper(TypeDeclarationSyntax td, string name, FileCtx ctx)
    {
        if (ctx.Wrappers.TryGetValue((td, name), out var known)) return known;
        var own = td.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text == name).ToList();
        var ok = own.Count == 0 || own.Any(m =>
        {
            var p = m.ParameterList.Parameters.FirstOrDefault()?.Identifier.Text;
            if (p is null) return false;
            return m.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => i.Expression is MemberAccessExpressionSyntax ma && (ClientVerbs.ContainsKey(ma.Name.Identifier.Text) || ma.Name.Identifier.Text == "SendAsync"))
                .Any(i => i.ArgumentList.DescendantNodes().OfType<IdentifierNameSyntax>().Any(id => id.Identifier.Text == p));
        });
        ctx.Wrappers[(td, name)] = ok;
        return ok;
    }

    // Вызов без получателя (`GetAsync<T>(…)`, `GetAsync(…)`) или с `this.`/`base.`: имя метода-обёртки; иначе null.
    private static string? WrapperCall(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        IdentifierNameSyntax i => i.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax or BaseExpressionSyntax } m => m.Name.Identifier.Text,
        _ => null,
    };

    // Адрес — ПЕРВЫЙ аргумент (у Get/PostAsync): строковый литерал или интерполяция. Остальные аргументы (тело, токен)
    // адресом не считаем: `PostAsJsonAsync(url, "текст")` не должен превращать тело запроса в маршрут.
    private static string? FirstUrlText(SeparatedSyntaxList<ArgumentSyntax> args) =>
        args.Count > 0 ? UrlText(args[0].Expression) : null;

    // Текст адреса из литерала или интерполяции: подстановка → {имя}, всё после «?» (query) отбрасывается — секреты не тащим.
    private static string? UrlText(ExpressionSyntax e)
    {
        switch (e)
        {
            case LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression):
                return lit.Token.ValueText;
            case InterpolatedStringExpressionSyntax interp:
                var sb = new System.Text.StringBuilder();
                foreach (var part in interp.Contents)
                {
                    if (part is InterpolatedStringTextSyntax txt)
                    {
                        var v = txt.TextToken.ValueText;
                        var q = v.IndexOf('?');
                        if (q >= 0) { sb.Append(v, 0, q); return sb.ToString(); }
                        sb.Append(v);
                    }
                    else if (part is InterpolationSyntax ip) sb.Append('{').Append(HoleName(ip.Expression)).Append('}');
                }
                return sb.ToString();
            default:
                return null;
        }
    }

    // Имя для подстановки: простой идентификатор или последний член (order.Id → Id); иначе безымянная.
    private static string HoleName(ExpressionSyntax e) => e switch
    {
        IdentifierNameSyntax i => i.Identifier.Text,
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        _ => "param",
    };

    // SendAsync(new HttpRequestMessage(HttpMethod.X, "url"), …) → (verb, url). Без объекта-сообщения — пропуск.
    private static (string?, string?) ReadSend(SeparatedSyntaxList<ArgumentSyntax> args)
    {
        foreach (var a in args)
        {
            if (a.Expression is not ObjectCreationExpressionSyntax oc) continue;
            var typeName = oc.Type switch
            {
                IdentifierNameSyntax idn => idn.Identifier.Text,
                QualifiedNameSyntax qn => qn.Right.Identifier.Text,
                _ => null,
            };
            if (typeName != "HttpRequestMessage") continue;
            string? verb = null, url = null;
            foreach (var oa in oc.ArgumentList?.Arguments ?? default)
            {
                // HttpMethod.X — квалификатор либо IdentifierName (using), либо MemberAccess (FQN System.Net.Http.HttpMethod)
                if (verb is null && oa.Expression is MemberAccessExpressionSyntax mm &&
                    RightName(mm.Expression) == "HttpMethod" &&
                    HttpMethodMembers.TryGetValue(mm.Name.Identifier.Text, out var v)) verb = v;
                else if (url is null) url = UrlText(oa.Expression);
            }
            return (verb, url);
        }
        return (null, null);
    }

    // Правый идентификатор выражения-квалификатора: HttpMethod (using) или …HttpMethod (FQN).
    private static string? RightName(ExpressionSyntax e) => e switch
    {
        IdentifierNameSyntax i => i.Identifier.Text,
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        _ => null,
    };

    // ── базовые адреса (срез 2) ──

    // Базовый адрес клиента: литерал (Uri) либо ссылка на конфигурацию — прямой ключ («Payments:Url») или свойство класса options
    // (тип + имя свойства; секцию определяем позже, когда собраны все классы options). Значения конфигурации здесь НЕ читаем.
    private readonly record struct ClientBase(Uri? Uri, string? Key, string? OptionsType, string? Prop);

    private readonly record struct BaseAddr(ClientBase Base, SyntaxNode Node);

    // Враждебный/сгенерированный файл с тысячами BaseAddress сделал бы разрешение квадратичным (вызовы × адреса × глубина).
    // Сверх лимита относительные пути в файле не разрешаем: столько разных клиентов в одном файле — это уже не «один понятный адрес».
    private const int MaxBaseAddressesPerFile = 64;

    // Все литеральные присваивания BaseAddress: `x.BaseAddress = new Uri("http://…")` и `BaseAddress = new Uri("…")` в инициализаторе.
    private static List<BaseAddr>? CollectBaseAddresses(SyntaxNode root)
    {
        var list = new List<BaseAddr>();
        var optionLocals = new Dictionary<SyntaxNode, Dictionary<string, string>>();   // область → переменная → тип options (считаем один раз на область)
        foreach (var asg in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            // `x.BaseAddress = …` или `BaseAddress = …` в инициализаторе объекта; голое присваивание (поле/свойство своего класса) не берём
            var left = asg.Left switch
            {
                MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
                IdentifierNameSyntax i when asg.Parent is InitializerExpressionSyntax => i.Identifier.Text,
                _ => null,
            };
            if (left != "BaseAddress") continue;
            if (BaseOf(asg.Right, optionLocals) is { } cb) list.Add(new BaseAddr(cb, asg));
            if (list.Count > MaxBaseAddressesPerFile) return null;   // слишком много — не гадаем вовсе
        }
        return list;
    }

    // new Uri("http://…") / new("http://…") → абсолютный http(s)-адрес; иначе null (не литерал, другая схема).
    private static Uri? UriLiteral(ExpressionSyntax e)
    {
        ArgumentListSyntax? args = null;
        switch (e)
        {
            case ObjectCreationExpressionSyntax oc when oc.Type is IdentifierNameSyntax { Identifier.Text: "Uri" }
                                                       || oc.Type is QualifiedNameSyntax { Right.Identifier.Text: "Uri" }:
                args = oc.ArgumentList; break;
            case ImplicitObjectCreationExpressionSyntax io: args = io.ArgumentList; break;
        }
        if (args is null || args.Arguments.Count == 0) return null;
        if (args.Arguments[0].Expression is not LiteralExpressionSyntax lit || !lit.IsKind(SyntaxKind.StringLiteralExpression)) return null;
        return Uri.TryCreate(lit.Token.ValueText, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? uri : null;
    }

    // Правая часть `BaseAddress = …`: литерал Uri или ссылка на конфигурацию внутри `new Uri(<ссылка>)`; иное — null (не знаем).
    private static ClientBase? BaseOf(ExpressionSyntax rhs, Dictionary<SyntaxNode, Dictionary<string, string>> optionLocals)
    {
        if (UriLiteral(rhs) is { } u) return new ClientBase(u, null, null, null);
        ArgumentListSyntax? args = rhs switch
        {
            ObjectCreationExpressionSyntax oc when oc.Type is IdentifierNameSyntax { Identifier.Text: "Uri" } || oc.Type is QualifiedNameSyntax { Right.Identifier.Text: "Uri" } => oc.ArgumentList,
            ImplicitObjectCreationExpressionSyntax io => io.ArgumentList,
            _ => null,
        };
        if (args is null || args.Arguments.Count == 0) return null;
        return ConfigRef(StripWrappers(args.Arguments[0].Expression), rhs, optionLocals);
    }

    // `x!`, `(x)`, `x ?? "…"` → x
    private static ExpressionSyntax StripWrappers(ExpressionSyntax e)
    {
        while (true)
            switch (e)
            {
                case PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } p: e = p.Operand; break;
                case ParenthesizedExpressionSyntax par: e = par.Expression; break;
                case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression } b: e = b.Left; break;
                default: return e;
            }
    }

    private static string? StringLit(ExpressionSyntax? e) =>
        e is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression) ? lit.Token.ValueText : null;

    // Получатель похож на IConfiguration/IConfigurationSection по точному имени (config, _configuration, cfg, section…).
    private static bool LooksLikeConfig(ExpressionSyntax? e)
    {
        var name = e switch
        {
            IdentifierNameSyntax i => i.Identifier.Text,
            MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
            _ => null,
        };
        if (name is null) return false;
        name = name.TrimStart('_');
        // точные имена IConfiguration/IConfigurationSection; «appConfigMap», «sectionCache» — не конфигурация
        return name.Equals("config", StringComparison.OrdinalIgnoreCase) || name.Equals("configuration", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cfg", StringComparison.OrdinalIgnoreCase) || name.Equals("section", StringComparison.OrdinalIgnoreCase)
            || name.Equals("configSection", StringComparison.OrdinalIgnoreCase) || name.Equals("configurationSection", StringComparison.OrdinalIgnoreCase);
    }

    // Ссылка на конфигурацию: `config["A:B"]`, `config.GetSection("A")["B"]`, `config.GetValue<string>("A:B")`,
    // `config.GetSection("A:B").Value`, `options.Prop` (options получен из IOptions<T>, в т.ч. `sp.GetRequiredService<IOptions<T>>().Value.Prop`).
    private static ClientBase? ConfigRef(ExpressionSyntax e, SyntaxNode at, Dictionary<SyntaxNode, Dictionary<string, string>> optionLocals)
    {
        switch (e)
        {
            case ElementAccessExpressionSyntax { ArgumentList.Arguments.Count: 1 } ea when StringLit(ea.ArgumentList.Arguments[0].Expression) is { } k:
                if (ea.Expression is InvocationExpressionSyntax gs && CalleeName(gs) == "GetSection" && gs.ArgumentList.Arguments.Count == 1
                    && StringLit(gs.ArgumentList.Arguments[0].Expression) is { } sec)
                    return new ClientBase(null, $"{sec}:{k}", null, null);
                return LooksLikeConfig(ea.Expression) ? new ClientBase(null, k, null, null) : null;
            case InvocationExpressionSyntax inv when CalleeName(inv) == "GetValue" && inv.ArgumentList.Arguments.Count > 0
                && inv.Expression is MemberAccessExpressionSyntax gm && LooksLikeConfig(gm.Expression)
                && StringLit(inv.ArgumentList.Arguments[0].Expression) is { } gv:
                return new ClientBase(null, gv, null, null);
            case MemberAccessExpressionSyntax { Name.Identifier.Text: "Value", Expression: InvocationExpressionSyntax gs2 }
                when CalleeName(gs2) == "GetSection" && gs2.ArgumentList.Arguments.Count == 1 && StringLit(gs2.ArgumentList.Arguments[0].Expression) is { } sec2:
                return new ClientBase(null, sec2, null, null);
            case MemberAccessExpressionSyntax ma:
            {
                var t = OptionsTypeOf(ma.Expression, at, optionLocals);
                return t is null ? null : new ClientBase(null, null, t, ma.Name.Identifier.Text);
            }
            default: return null;
        }
    }

    // Тип T из `IOptions<T>` в выражении `x` (само выражение или инициализатор локальной переменной x рядом с присваиванием).
    // Строго `<…IOptions<T>…>.Value` (или `.CurrentValue`): `….Value.Inner` — другой уровень вложенности, секция была бы неверной.
    private static string? OptionsValueType(ExpressionSyntax e) =>
        StripWrappers(e) is MemberAccessExpressionSyntax { Name.Identifier.Text: "Value" or "CurrentValue" } m ? OptionsTypeIn(m.Expression) : null;

    private static string? OptionsTypeOf(ExpressionSyntax holder, SyntaxNode at, Dictionary<SyntaxNode, Dictionary<string, string>> optionLocals)
    {
        if (OptionsValueType(holder) is { } direct) return direct;
        if (holder is not IdentifierNameSyntax id) return null;
        // ближайшие объемлющие области (лямбда/член), не больше 6: вложенность в сотни лямбд не должна раздувать поиск;
        // переменные области собираются один раз (кэш), а не заново на каждое присваивание
        var scope = at.Ancestors().FirstOrDefault(a => a is LambdaExpressionSyntax or MemberDeclarationSyntax);
        for (var depth = 0; scope is not null && depth < 6; depth++)
        {
            if (!optionLocals.TryGetValue(scope, out var map))
            {
                optionLocals[scope] = map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var d in scope.DescendantNodes().OfType<VariableDeclaratorSyntax>())
                    if (d.Initializer is { } init && OptionsValueType(init.Value) is { } t) map.TryAdd(d.Identifier.Text, t);
            }
            if (map.TryGetValue(id.Identifier.Text, out var found)) return found;
            scope = scope.Ancestors().FirstOrDefault(a => a is LambdaExpressionSyntax or MemberDeclarationSyntax);
        }
        return null;
    }

    private static string? OptionsTypeIn(SyntaxNode n)
    {
        foreach (var g in n.DescendantNodesAndSelf().OfType<GenericNameSyntax>())
            if (g.Identifier.Text is "IOptions" or "IOptionsSnapshot" or "IOptionsMonitor" && g.TypeArgumentList.Arguments.Count == 1)
                return g.TypeArgumentList.Arguments[0] switch
                {
                    IdentifierNameSyntax i => i.Identifier.Text,
                    QualifiedNameSyntax q => q.Right.Identifier.Text,
                    _ => null,
                };
        return null;
    }

    // Базовый адрес для вызова: ближайшая область (член → тип), где он ровно один. Два разных — null (не гадаем).
    // На уровень файла выходим только для кода верхнего уровня (Program.cs без класса): иначе базовый адрес соседнего
    // класса в том же файле чужой клиент не описывает.
    private static (ClientBase? Base, bool Ambiguous) ResolveBase(SyntaxNode call, IReadOnlyList<BaseAddr> bases)
    {
        if (bases.Count == 0) return (null, false);
        foreach (var scope in call.Ancestors().Where(a => a is MemberDeclarationSyntax or TypeDeclarationSyntax))
        {
            var inScope = bases.Where(b => scope.Span.Contains(b.Node.Span)).Select(b => b.Base).Distinct().ToList();
            if (inScope.Count == 1) return (inScope[0], false);
            if (inScope.Count > 1) return (null, true);        // два разных адреса — неоднозначно, реестр тоже не подставляем
            if (scope is TypeDeclarationSyntax) return (null, false);   // дальше внешнего типа не идём: соседний/вложенный класс — чужой клиент
        }
        // код верхнего уровня: только присваивания вне любого типа (базовый адрес вспомогательного класса сюда не относится)
        var top = bases.Where(b => !b.Node.Ancestors().Any(a => a is TypeDeclarationSyntax)).Select(b => b.Base).Distinct().ToList();
        return top.Count == 1 ? (top[0], false) : (null, top.Count > 1);
    }

    // ── клиенты из DI (срез 3а) ──

    // Типизированные (AddHttpClient<T>, AddHttpClient<TI,TImpl>) и именованные (AddHttpClient("имя")) клиенты → их ЛИТЕРАЛЬНЫЙ BaseAddress.
    // Адрес — литерал или ссылка на конфигурацию (срез 3б). Одно имя с разными адресами — неоднозначно.
    private sealed class ClientRegistry
    {
        public readonly Dictionary<string, HashSet<ClientBase>> Typed = new(StringComparer.Ordinal);
        public readonly Dictionary<string, HashSet<ClientBase>> Named = new(StringComparer.Ordinal);
        public readonly Dictionary<string, HashSet<ClientBase>> Refit = new(StringComparer.Ordinal);   // интерфейс Refit → адрес (срез 4)
        public int Count;
        public static ClientBase? Single(Dictionary<string, HashSet<ClientBase>> map, string key) =>
            map.TryGetValue(key, out var set) && set.Count == 1 ? set.First() : null;
        public static void Add(Dictionary<string, HashSet<ClientBase>> map, string key, ClientBase b)
        {
            if (!map.TryGetValue(key, out var set)) map[key] = set = [];
            set.Add(b);
        }
    }

    // Защита от враждебных/сгенерированных проектов: больше регистраций — реестр не используем (честнее, чем гадать).
    private const int MaxClientRegistrations = 512;

    private static void CollectRegistrations(SyntaxNode root, ClientRegistry reg, CancellationToken ct)
    {
        List<BaseAddr>? fileBases = null;
        var optionLocals = new Dictionary<SyntaxNode, Dictionary<string, string>>();   // один кэш на файл (а не новый на каждую регистрацию — квадрат)
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            ct.ThrowIfCancellationRequested();
            if (reg.Count > MaxClientRegistrations) return;
            var callee = CalleeName(inv);
            if (callee is "For" && inv.Expression is MemberAccessExpressionSyntax { Expression: var rsq } && RightName(rsq) == "RestService" && CalleeGenericArgs(inv) is { Count: 1 } rt)
            {
                // RestService.For<T>(адрес | HttpClient { BaseAddress = … } | переменная такого клиента): адрес — первый аргумент
                if (inv.ArgumentList.Arguments.Count == 0) continue;
                var a0 = StripWrappers(inv.ArgumentList.Arguments[0].Expression);
                ClientBase? cbR = StringLit(a0) is { } lit0 && Uri.TryCreate(lit0, UriKind.Absolute, out var ru) && (ru.Scheme == Uri.UriSchemeHttp || ru.Scheme == Uri.UriSchemeHttps)
                    ? new ClientBase(ru, null, null, null)
                    : ConfigRef(a0, inv, optionLocals);
                if (cbR is null)
                {
                    fileBases ??= CollectBaseAddresses(root);
                    // `new HttpClient { BaseAddress = … }` прямо в аргументе или в переменной, объявленной в том же члене
                    var span = a0.Span;
                    if (a0 is IdentifierNameSyntax rid && inv.Ancestors().FirstOrDefault(a => a is MemberDeclarationSyntax) is { } owner
                        && owner.DescendantNodes().OfType<VariableDeclaratorSyntax>().FirstOrDefault(d => d.Identifier.Text == rid.Identifier.Text && d.Initializer is not null) is { } decl)
                        span = decl.Span;
                    var inside = fileBases?.Where(b => span.Contains(b.Node.Span)).Select(b => b.Base).Distinct().ToList();
                    if (inside is { Count: 1 }) cbR = inside[0];
                }
                if (cbR is { } found) { reg.Count++; ClientRegistry.Add(reg.Refit, rt[0], found); }   // считаем только регистрации с адресом: вызовы For без адреса лимит не съедают
                continue;
            }
            if (callee is not ("AddHttpClient" or "AddRefitClient")) continue;
            reg.Count++;
            fileBases ??= CollectBaseAddresses(root);          // один обход файла (а не по лямбде на каждую регистрацию — вложенные давали бы квадрат)
            if (fileBases is null) return;                       // слишком много адресов в файле — не гадаем

            var uris = new HashSet<ClientBase>();
            var node = (SyntaxNode)inv;
            for (var depth = 0; depth < 12; depth++)       // цепочка: AddHttpClient<T>().ConfigureHttpClient(...).AddStandardResilienceHandler()
            {
                if (node is InvocationExpressionSyntax cur && (depth == 0 ? CalleeName(cur) is "AddHttpClient" or "AddRefitClient" : CalleeName(cur) == "ConfigureHttpClient"))
                    foreach (var arg in cur.ArgumentList.Arguments)
                        if (arg.Expression is LambdaExpressionSyntax lam)
                            foreach (var b in fileBases) if (lam.Span.Contains(b.Node.Span)) uris.Add(b.Base);
                if (node.Parent is MemberAccessExpressionSyntax pm && pm.Expression == node && pm.Parent is InvocationExpressionSyntax up) node = up;
                else break;
            }
            if (uris.Count != 1) continue;                 // нет адреса (литерал или ссылка на конфигурацию) или их несколько в одной регистрации
            var uri = uris.First();

            if (callee == "AddRefitClient")
            {
                if (CalleeGenericArgs(inv) is { Count: 1 } refitTypes) ClientRegistry.Add(reg.Refit, refitTypes[0], uri);
                continue;
            }
            // Типизированный клиент: HttpClient внедряется в РЕАЛИЗАЦИЮ — последний аргумент (AddHttpClient<TI, TImpl> → TImpl).
            if (CalleeGenericArgs(inv) is { Count: > 0 } types) ClientRegistry.Add(reg.Typed, types[^1], uri);
            if (inv.ArgumentList.Arguments.Count > 0 && inv.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax lit
                && lit.IsKind(SyntaxKind.StringLiteralExpression))
                ClientRegistry.Add(reg.Named, lit.Token.ValueText, uri);
        }
    }

    internal static string? CalleeName(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        IdentifierNameSyntax i => i.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        _ => null,
    };

    // Простые имена типов из AddHttpClient<TI, TImpl>(…); null — не дженерик.
    private static List<string>? CalleeGenericArgs(InvocationExpressionSyntax inv)
    {
        var name = inv.Expression switch
        {
            MemberAccessExpressionSyntax { Name: GenericNameSyntax g } => g,
            GenericNameSyntax g => g,
            _ => null,
        };
        if (name is null) return null;
        var list = new List<string>();
        foreach (var a in name.TypeArgumentList.Arguments)
            list.Add(a switch
            {
                IdentifierNameSyntax i => i.Identifier.Text,
                QualifiedNameSyntax q => q.Right.Identifier.Text,
                GenericNameSyntax gn => gn.Identifier.Text,
                _ => "",
            });
        list.RemoveAll(string.IsNullOrEmpty);
        return list;
    }

    // Адрес для вызова из реестра: (1) вызов лежит в классе типизированного клиента; (2) получатель — именованный клиент
    // (`f.CreateClient("имя").GetAsync(…)` или переменная, полученная из CreateClient("имя") в том же члене).
    private sealed class FileCtx
    {
        public readonly Dictionary<SyntaxNode, Dictionary<string, string>> Locals = [];     // член → переменная → имя клиента (по CreateClient)
        public readonly Dictionary<SyntaxNode, HashSet<string>> InjectedHttp = [];           // тип → имена членов/параметров типа HttpClient без `new`
        public readonly Dictionary<(SyntaxNode, string), bool> Wrappers = [];                // (тип, имя метода) → это HTTP-обёртка
    }

    private static ClientBase? ResolveRegistered(InvocationExpressionSyntax inv, ExpressionSyntax receiver, ClientRegistry reg, FileCtx ctx)
    {
        // 1. Явный именованный клиент важнее класса, в котором вызов лежит: `CreateClient("x")` внутри класса T — это клиент «x», не T.
        string? clientName = null;
        if (receiver is InvocationExpressionSyntax ci) clientName = CreateClientName(ci);
        else if (receiver is IdentifierNameSyntax id)
        {
            var scope = inv.Ancestors().FirstOrDefault(a => a is MemberDeclarationSyntax) ?? inv.SyntaxTree.GetRoot();
            if (!ctx.Locals.TryGetValue(scope, out var map)) ctx.Locals[scope] = map = CollectCreateClientLocals(scope);
            if (map.TryGetValue(id.Identifier.Text, out var n)) clientName = n;     // даже если имя не зарегистрировано: это не клиент класса
        }
        if (clientName is not null) return ClientRegistry.Single(reg.Named, clientName);
        if (receiver is InvocationExpressionSyntax) return null;   // результат другого вызова (не CreateClient) — какой это клиент, не знаем

        // 2. Класс типизированного клиента: ближайший объемлющий тип (вложенный чужой класс адреса не наследует);
        //    вызов по полю/параметру/свойству (`_http.GetAsync`), а не по клиенту, полученному из CreateClient выше.
        if (reg.Typed.Count > 0 && inv.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() is { } td
            && ReceiverName(receiver) is { } rn && IsInjectedHttpClient(td, rn, ctx))
            return ClientRegistry.Single(reg.Typed, td.Identifier.Text);
        return null;
    }

    // `_http` / `http` / `this._http` → имя члена; иное выражение — null.
    private static string? ReceiverName(ExpressionSyntax e) => e switch
    {
        IdentifierNameSyntax i => i.Identifier.Text,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: var n } => n.Identifier.Text,
        _ => null,
    };

    // Имя — это поле/свойство/параметр конструктора (в том числе первичного) типа HttpClient, не созданный через `new`
    // в самом классе: именно такой клиент внедряет DI. `new HttpClient()` внутри класса и чужие клиенты адреса не получают.
    private static bool IsInjectedHttpClient(TypeDeclarationSyntax td, string name, FileCtx ctx)
    {
        if (!ctx.InjectedHttp.TryGetValue(td, out var names))
        {
            names = new HashSet<string>(StringComparer.Ordinal);
            static bool IsHttp(TypeSyntax? t) => t switch
            {
                IdentifierNameSyntax i => i.Identifier.Text == "HttpClient",
                QualifiedNameSyntax q => q.Right.Identifier.Text == "HttpClient",
                NullableTypeSyntax n => IsHttp(n.ElementType),
                _ => false,
            };
            foreach (var prm in td.ParameterList?.Parameters ?? default) if (IsHttp(prm.Type)) names.Add(prm.Identifier.Text);
            foreach (var m in td.Members)
                switch (m)
                {
                    case FieldDeclarationSyntax f when IsHttp(f.Declaration.Type):
                        foreach (var v in f.Declaration.Variables)
                            if (v.Initializer?.Value is not (ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax)) names.Add(v.Identifier.Text);
                        break;
                    case PropertyDeclarationSyntax pr when IsHttp(pr.Type) && pr.Initializer?.Value is not (ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax):
                        names.Add(pr.Identifier.Text);
                        break;
                    case ConstructorDeclarationSyntax c:
                        foreach (var prm in c.ParameterList.Parameters) if (IsHttp(prm.Type)) names.Add(prm.Identifier.Text);
                        break;
                }
            ctx.InjectedHttp[td] = names;
        }
        return names.Contains(name);
    }

    // `x.CreateClient("имя")` → имя
    private static string? CreateClientName(InvocationExpressionSyntax ci) =>
        CalleeName(ci) == "CreateClient" && ci.ArgumentList.Arguments.Count > 0
        && ci.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression)
            ? lit.Token.ValueText : null;

    // Переменные члена, полученные из CreateClient("имя"): `var c = f.CreateClient("x")` и `c = f.CreateClient("x")`.
    // Переменная с двумя разными именами в одном члене — не гадаем (ключ убирается).
    private static Dictionary<string, string> CollectCreateClientLocals(SyntaxNode member)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var conflicted = new HashSet<string>(StringComparer.Ordinal);
        void Put(string v, string n) { if (map.TryGetValue(v, out var old) && old != n) conflicted.Add(v); else map[v] = n; }
        foreach (var node in member.DescendantNodes())
        {
            if (node is VariableDeclaratorSyntax { Initializer.Value: var init } d && Unwrap(init) is InvocationExpressionSyntax i1 && CreateClientName(i1) is { } n1)
                Put(d.Identifier.Text, n1);
            else if (node is AssignmentExpressionSyntax { Left: IdentifierNameSyntax l, Right: var r } && Unwrap(r) is InvocationExpressionSyntax i2 && CreateClientName(i2) is { } n2)
                Put(l.Identifier.Text, n2);
        }
        foreach (var c in conflicted) map.Remove(c);
        return map;
    }

    private static ExpressionSyntax Unwrap(ExpressionSyntax e) => e is AwaitExpressionSyntax a ? a.Expression : e;

    private static void Add(List<HttpCallHit> hits, string verb, string? url, InvocationExpressionSyntax inv, ExpressionSyntax receiver, string path,
        IReadOnlyList<BaseAddr>? bases, ClientRegistry registry, FileCtx ctx, IReadOnlyDictionary<string, string> sections, bool wrapper = false)
    {
        url = url?.Trim();
        if (string.IsNullOrEmpty(url) || url.StartsWith('{')) return;                 // пусто или адрес начинается с подстановки — неизвестен
        if (StartsWithScheme(url) && Uri.TryCreate(url, UriKind.Absolute, out var abs))
        {
            if (abs.Scheme != Uri.UriSchemeHttp && abs.Scheme != Uri.UriSchemeHttps) return;   // ftp://, file:// …
            var line = inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            // без query (секреты не тащим); подстановки {имя} сохраняем читаемыми, а не %7B…%7D (раскодируем только скобки)
            var clean = $"{abs.Scheme}://{abs.Authority}{UnescapeBraces(abs.AbsolutePath)}";
            hits.Add(new HttpCallHit(verb, clean, path, line, $"{verb} {clean}"));
            return;
        }
        // относительный путь: только похожий на URL-путь («/x» или «a/b»); голое слово — ключ кэша, имя файла, id — не вызов.
        if (!LooksLikePath(url) || bases is null) return;                          // в файле слишком много базовых адресов — тоже не гадаем
        var (found, ambiguous) = ResolveBase(inv, bases);
        if (found is null && !ambiguous)
            found = wrapper
                ? (inv.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() is { } td ? ClientRegistry.Single(registry.Typed, td.Identifier.Text) : null)   // обёртка: адрес клиента — по классу
                : ResolveRegistered(inv, receiver, registry, ctx);   // конфликт в области реестром не «лечим»
        if (found is { } cb) EmitFromBase(hits, verb, url, inv, path, cb, sections, how: null);    // нет надёжного базового адреса — не угадываем
    }

    // «схема://…» в НАЧАЛЕ строки (http://, ftp://). «://» внутри query («/redirect?to=http://x») адресом не делает; на Unix путь «/x»
    // Uri.TryCreate сам считает абсолютным file-адресом, поэтому по одному TryCreate абсолютный адрес не отличить.
    private static bool StartsWithScheme(string url)
    {
        var i = url.IndexOf("://", StringComparison.Ordinal);
        if (i <= 0) return false;
        for (var k = 0; k < i; k++) if (!(char.IsAsciiLetterOrDigit(url[k]) || url[k] is '+' or '-' or '.')) return false;
        return char.IsAsciiLetter(url[0]);
    }

    // Протокол-относительные «//host/x», обратные слэши и «схема:значение» («user:42») относительными не считаем.
    // Голое слово без «/» — не путь (ключ кэша, имя файла, id).
    private static bool LooksLikePath(string url) => IsSafeRelative(url) && (url.StartsWith('/') || url.Contains('/'));

    private static bool IsSafeRelative(string url)
    {
        if (url.Any(char.IsControl)) return false;   // переводы строк, NUL — в путь вызова не берём (и в Evidence)
        var cut = url.IndexOfAny(['?', '#']);
        if (cut >= 0) url = url[..cut];          // «://» или «:» в query («/redirect?to=http://x») путь небезопасным не делает
        if (url.StartsWith("//", StringComparison.Ordinal) || url.Contains('\\') || url.Contains("://", StringComparison.Ordinal)) return false;
        var colon = url.IndexOf(':');
        return colon < 0 || (url.Contains('/') && url.IndexOf('/') < colon);
    }

    // Относительный путь + базовый адрес клиента → вызов: литерал даёт абсолютный адрес, ключ конфигурации — вызов с ключом
    // (значение подставит сборщик графа). `how` — пометка источника в Evidence (по умолчанию «по BaseAddress»).
    private static void EmitFromBase(List<HttpCallHit> hits, string verb, string url, SyntaxNode lineAt, string path, ClientBase cb,
        IReadOnlyDictionary<string, string> sections, string? how, bool append = false)
    {
        var line = lineAt.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        if (cb.Uri is not { } baseUri)
        {
            // адрес клиента — из конфигурации: значения не читаем, отдаём ключ и относительный путь; цель найдёт сборщик графа
            var inferred = false;
            var key = cb.Key;
            if (key is null && cb.OptionsType is { } ot && cb.Prop is { } prop && SectionOf(ot, sections, out inferred) is { } sec) key = $"{sec}:{prop}";
            if (key is null) return;
            var cut = url.IndexOfAny(['?', '#']);
            if (cut >= 0) url = url[..cut];                       // query и fragment (токены) в Url/Evidence не попадают — как у абсолютных адресов
            var note = (how is null ? "" : $"{how}; ") + $"адрес из конфигурации {key}" + (inferred ? "; секция выведена из имени класса" : "");
            hits.Add(new HttpCallHit(verb, url, path, line, $"{verb} {url} ({note})", ViaBase: true, ConfigKey: key, AppendToBasePath: append));
            return;
        }
        var target = append
            ? $"{baseUri.GetLeftPart(UriPartial.Authority)}{baseUri.AbsolutePath.TrimEnd('/')}/{url.TrimStart('/')}"   // Refit: путь дописывается к пути базы
            : null;
        Uri? uri = null;
        if (!(target is null ? Uri.TryCreate(baseUri, url, out uri) : Uri.TryCreate(target, UriKind.Absolute, out uri))) return;
        // после слияния цель обязана остаться на хосте базового адреса (защита от хитрых относительных строк)
        if (!uri.Authority.Equals(baseUri.Authority, StringComparison.OrdinalIgnoreCase) || uri.Scheme != baseUri.Scheme) return;
        var clean = $"{uri.Scheme}://{uri.Authority}{UnescapeBraces(uri.AbsolutePath)}";
        hits.Add(new HttpCallHit(verb, clean, path, line, $"{verb} {clean} ({how ?? "по BaseAddress"})", ViaBase: true, AppendToBasePath: append));
    }

    // ── Refit (срез 4) ──

    private static readonly IReadOnlyDictionary<string, string> RefitVerbs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Get"] = "GET", ["Post"] = "POST", ["Put"] = "PUT", ["Delete"] = "DELETE", ["Patch"] = "PATCH", ["Head"] = "HEAD", ["Options"] = "OPTIONS",
    };

    // Методы Refit-интерфейса, зарегистрированного с адресом: каждый `[Get("/путь")]` — вызов (декларация, а не место использования).
    // Интерфейс без регистрации/адреса и с разными адресами у одного имени не берём.
    private static void CollectRefitCalls(SyntaxNode root, string path, ClientRegistry reg, IReadOnlyDictionary<string, string> sections, List<(string Name, string Ns, HttpCallHit Hit)> refitHits)
    {
        foreach (var itf in root.DescendantNodes().OfType<InterfaceDeclarationSyntax>())
        {
            if (itf.Parent is TypeDeclarationSyntax) continue;                      // вложенный интерфейс — отдельная область, по простому имени не отличить
            var name = itf.Identifier.Text;
            var ns = string.Join('.', itf.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).Reverse());
            if (ClientRegistry.Single(reg.Refit, name) is not { } cb) continue;
            foreach (var m in itf.Members.OfType<MethodDeclarationSyntax>())
                foreach (var attr in m.AttributeLists.SelectMany(l => l.Attributes))
                {
                    var an = attr.Name switch { IdentifierNameSyntax i => i.Identifier.Text, QualifiedNameSyntax q => q.Right.Identifier.Text, _ => "" };
                    if (an.EndsWith("Attribute", StringComparison.Ordinal)) an = an[..^"Attribute".Length];
                    if (!RefitVerbs.TryGetValue(an, out var verb)) continue;
                    var rel = StringLit(attr.ArgumentList?.Arguments.FirstOrDefault()?.Expression)?.Trim();
                    if (string.IsNullOrEmpty(rel) || rel.StartsWith('{')) continue;
                    var cut = rel.IndexOfAny(['?', '#']);
                    if (cut == 0) continue;
                    if (cut > 0) rel = rel[..cut];
                    if (!IsSafeRelative(rel)) continue;                                                          // путь здесь заведомо путь: голое слово допустимо
                    var one = new List<HttpCallHit>();
                    EmitFromBase(one, verb, rel, attr, path, cb, sections, how: $"Refit {name}.{m.Identifier.Text}", append: true);
                    foreach (var h in one) refitHits.Add((name, ns, h));
                }
        }
    }

    // Секция конфигурации класса options: константа SectionName/Section/… в классе; иначе имя типа без суффикса Options/Settings/Config.
    // Две разные константы у одноимённых классов («») — неоднозначно, не гадаем.
    private static string? SectionOf(string optionsType, IReadOnlyDictionary<string, string> sections, out bool inferredFromName)
    {
        inferredFromName = false;
        if (sections.TryGetValue(optionsType, out var s)) return s.Length == 0 ? null : s;
        inferredFromName = true;       // секцию реально задаёт Configure<T>(GetSection("…")), а имя класса — только догадка: помечаем в Evidence
        foreach (var suffix in new[] { "Options", "Settings", "Config", "Configuration" })
            if (optionsType.Length > suffix.Length && optionsType.EndsWith(suffix, StringComparison.Ordinal)) return optionsType[..^suffix.Length];
        return optionsType;
    }

    private static readonly HashSet<string> SectionConstNames = new(StringComparer.Ordinal)
        { "SectionName", "Section", "ConfigSection", "ConfigurationSection", "SettingsSection", "ConfigKey" };

    private static void AddRegistered(Dictionary<string, string> reg, string type, string section)
    {
        if (reg.Count > 4000) return;
        reg[type] = reg.TryGetValue(type, out var old) && old != section ? "" : section;   // два разных раздела под один класс — неоднозначно
    }

    // Регистрации options: `Configure<T>(config.GetSection(X))`, `AddOptions<T>().Bind(config.GetSection(X))`, `AddOptions<T>().BindConfiguration(X)`;
    // X — строка, `nameof(Type)` или `Owner.Const` (константу разрешает вызывающий). Это реальная секция, в отличие от догадки по имени класса.
    private static void CollectOptionRegistrations(SyntaxNode root, Dictionary<string, string> reg, List<(string Type, string Owner)> pendingConst)
    {
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            string? type = null;
            ExpressionSyntax? sectionArg = null;
            var name = CalleeName(inv);
            if (name == "Configure" && CalleeGenericArgs(inv) is { Count: 1 } g && inv.ArgumentList.Arguments.Count == 1
                && inv.ArgumentList.Arguments[0].Expression is InvocationExpressionSyntax gs && CalleeName(gs) == "GetSection" && gs.ArgumentList.Arguments.Count == 1)
            { type = g[0]; sectionArg = gs.ArgumentList.Arguments[0].Expression; }
            else if (name is "Bind" or "BindConfiguration" && inv.Expression is MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax opt }
                && CalleeName(opt) == "AddOptions" && CalleeGenericArgs(opt) is { Count: 1 } og && inv.ArgumentList.Arguments.Count >= 1)
            {
                type = og[0];
                var a0 = inv.ArgumentList.Arguments[0].Expression;
                sectionArg = name == "Bind" ? (a0 is InvocationExpressionSyntax bgs && CalleeName(bgs) == "GetSection" && bgs.ArgumentList.Arguments.Count == 1 ? bgs.ArgumentList.Arguments[0].Expression : null) : a0;
            }
            if (type is null || sectionArg is null) continue;

            if (StringLit(sectionArg) is { } lit) AddRegistered(reg, type, lit);
            else if (sectionArg is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "nameof" } } nof
                && nof.ArgumentList.Arguments.Count == 1)
            {
                var target = nof.ArgumentList.Arguments[0].Expression;
                var n = target switch { IdentifierNameSyntax i => i.Identifier.Text, MemberAccessExpressionSyntax m => m.Name.Identifier.Text, _ => null };
                if (n is not null) AddRegistered(reg, type, n);
            }
            else if (sectionArg is MemberAccessExpressionSyntax { Expression: var q } && RightName(q) is { } owner) pendingConst.Add((type, owner));
        }
    }

    // Классы с const string SectionName = "…": тип → секция (для options-паттерна). Лимит записей — защита от мусора.
    private static void CollectSections(SyntaxNode root, Dictionary<string, string> sections)
    {
        foreach (var td in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (sections.Count > 4000) return;
            foreach (var f in td.Members.OfType<FieldDeclarationSyntax>())
            {
                if (!f.Modifiers.Any(SyntaxKind.ConstKeyword)) continue;
                foreach (var v in f.Declaration.Variables)
                {
                    if (!SectionConstNames.Contains(v.Identifier.Text)) continue;
                    var name = td.Identifier.Text;
                    // константа секции есть, но не строковый литерал (nameof(X), выражение) — прочитать нельзя: НЕ подменяем догадкой по имени
                    var lit = StringLit(v.Initializer?.Value);
                    sections[name] = lit is null || (sections.TryGetValue(name, out var old) && old != lit) ? "" : lit;
                }
            }
        }
    }

    internal static string UnescapeBraces(string path) => path.Replace("%7B", "{", StringComparison.OrdinalIgnoreCase).Replace("%7D", "}", StringComparison.OrdinalIgnoreCase);
}
