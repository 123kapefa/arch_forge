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
        foreach (var file in RoslynScan.EnumerateCsCapped(root, out _)) // тот же кап файлов, что и в семантике
        {
            ct.ThrowIfCancellationRequested();
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }
            if (IsCandidate(text)) candidates.Add(file);
            var wantsReg = text.Contains("AddHttpClient", StringComparison.Ordinal);
            var wantsSections = text.Contains("const string", StringComparison.Ordinal)
                && (text.Contains("Section", StringComparison.Ordinal) || text.Contains("ConfigKey", StringComparison.Ordinal));
            if (!wantsReg && !wantsSections) continue;
            var tree = CSharpSyntaxTree.ParseText(text, cancellationToken: ct).GetRoot(ct);
            if (wantsReg) CollectRegistrations(tree, registry, ct);
            if (wantsSections) CollectSections(tree, sections);
        }
        if (registry.Count > MaxClientRegistrations) registry = new ClientRegistry { Count = registry.Count };   // слишком много — не используем

        // Проход 2: сами вызовы.
        var hits = new List<HttpCallHit>();
        foreach (var file in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var text = ReadCandidate(file);
            if (text is null || !text.Contains("Async", StringComparison.Ordinal)) continue;   // нет вызовов *Async — нечего искать
            var path = file.Replace('\\', '/');
            var treeRoot = CSharpSyntaxTree.ParseText(text, cancellationToken: ct).GetRoot(ct);
            var bases = CollectBaseAddresses(treeRoot);      // null — слишком много адресов в файле: относительные пути не разрешаем
            var ctx = new FileCtx();   // кэши: переменные из CreateClient по членам, внедрённые HttpClient-члены по типам
            foreach (var inv in treeRoot.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
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
        }
        return hits;
    }

    // Текст файла, если он интересен (есть HTTP-клиент или http(s)-литерал); иначе null. Недоступный файл — тоже null.
    private static string? ReadCandidate(string file)
    {
        string text;
        try { text = File.ReadAllText(file); } catch { return null; }
        return IsCandidate(text) ? text : null;
    }

    private static bool IsCandidate(string text) =>
        text.Contains("HttpClient", StringComparison.Ordinal)
        || text.Contains("http://", StringComparison.OrdinalIgnoreCase)
        || text.Contains("https://", StringComparison.OrdinalIgnoreCase);

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
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            ct.ThrowIfCancellationRequested();
            if (reg.Count > MaxClientRegistrations) return;
            if (CalleeName(inv) != "AddHttpClient") continue;
            reg.Count++;
            fileBases ??= CollectBaseAddresses(root);          // один обход файла (а не по лямбде на каждую регистрацию — вложенные давали бы квадрат)
            if (fileBases is null) return;                       // слишком много адресов в файле — не гадаем

            var uris = new HashSet<ClientBase>();
            var node = (SyntaxNode)inv;
            for (var depth = 0; depth < 12; depth++)       // цепочка: AddHttpClient<T>().ConfigureHttpClient(...).AddStandardResilienceHandler()
            {
                if (node is InvocationExpressionSyntax cur && (depth == 0 ? CalleeName(cur) == "AddHttpClient" : CalleeName(cur) == "ConfigureHttpClient"))
                    foreach (var arg in cur.ArgumentList.Arguments)
                        if (arg.Expression is LambdaExpressionSyntax lam)
                            foreach (var b in fileBases) if (lam.Span.Contains(b.Node.Span)) uris.Add(b.Base);
                if (node.Parent is MemberAccessExpressionSyntax pm && pm.Expression == node && pm.Parent is InvocationExpressionSyntax up) node = up;
                else break;
            }
            if (uris.Count != 1) continue;                 // нет адреса (литерал или ссылка на конфигурацию) или их несколько в одной регистрации
            var uri = uris.First();

            // Типизированный клиент: HttpClient внедряется в РЕАЛИЗАЦИЮ — последний аргумент (AddHttpClient<TI, TImpl> → TImpl).
            if (CalleeGenericArgs(inv) is { Count: > 0 } types) ClientRegistry.Add(reg.Typed, types[^1], uri);
            if (inv.ArgumentList.Arguments.Count > 0 && inv.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax lit
                && lit.IsKind(SyntaxKind.StringLiteralExpression))
                ClientRegistry.Add(reg.Named, lit.Token.ValueText, uri);
        }
    }

    private static string? CalleeName(InvocationExpressionSyntax inv) => inv.Expression switch
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
        IReadOnlyList<BaseAddr>? bases, ClientRegistry registry, FileCtx ctx, IReadOnlyDictionary<string, string> sections)
    {
        url = url?.Trim();
        if (string.IsNullOrEmpty(url) || url.StartsWith('{')) return;                 // пусто или адрес начинается с подстановки — неизвестен
        Uri? uri;
        var viaBase = false;
        if (Uri.TryCreate(url, UriKind.Absolute, out uri) && url.Contains("://", StringComparison.Ordinal))
        {
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;   // ftp://, file:// …
        }
        else
        {
            // относительный путь: только похожий на URL-путь («/x» или «a/b»); голое слово — ключ кэша, имя файла, id — не вызов.
            // Протокол-относительные «//host/x», обратные слэши и «схема:значение» («user:42») относительными не считаем.
            if (url.StartsWith("//", StringComparison.Ordinal) || url.Contains('\\') || !(url.StartsWith('/') || url.Contains('/'))) return;
            if (url.IndexOf(':') is var colon && colon >= 0 && (colon < url.IndexOf('/') || !url.Contains('/'))) return;
            if (bases is null) return;                                                 // в файле слишком много базовых адресов
            var (found, ambiguous) = ResolveBase(inv, bases);
            if (found is null && !ambiguous) found = ResolveRegistered(inv, receiver, registry, ctx);   // конфликт в области реестром не «лечим»
            if (found is not { } cb) return;                                           // нет надёжного базового адреса — не угадываем

            if (cb.Uri is not { } baseUri)
            {
                // адрес клиента — из конфигурации: значения не читаем, отдаём ключ и относительный путь; цель найдёт сборщик графа
                var inferred = false;
                var key = cb.Key;
                if (key is null && cb.OptionsType is { } ot && cb.Prop is { } prop && SectionOf(ot, sections, out inferred) is { } sec) key = $"{sec}:{prop}";
                if (key is null) return;
                var cfgLine = inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var cut = url.IndexOfAny(['?', '#']);
                if (cut >= 0) url = url[..cut];                       // query и fragment (токены) в Url/Evidence не попадают — как у абсолютных адресов
                var note = inferred ? "; секция выведена из имени класса" : "";
                hits.Add(new HttpCallHit(verb, url, path, cfgLine, $"{verb} {url} (адрес из конфигурации {key}{note})", ViaBase: true, ConfigKey: key));
                return;
            }
            if (!Uri.TryCreate(baseUri, url, out uri)) return;
            // после слияния цель обязана остаться на хосте базового адреса (защита от хитрых относительных строк)
            if (!uri.Authority.Equals(baseUri.Authority, StringComparison.OrdinalIgnoreCase) || uri.Scheme != baseUri.Scheme) return;
            viaBase = true;
        }
        var line = inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        // без query (секреты не тащим); подстановки {имя} сохраняем читаемыми, а не %7B…%7D (раскодируем только скобки)
        var clean = $"{uri.Scheme}://{uri.Authority}{UnescapeBraces(uri.AbsolutePath)}";
        hits.Add(new HttpCallHit(verb, clean, path, line, viaBase ? $"{verb} {clean} (по BaseAddress)" : $"{verb} {clean}", viaBase));
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
