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
        foreach (var file in RoslynScan.EnumerateCsCapped(root, out _)) // тот же кап файлов, что и в семантике
        {
            ct.ThrowIfCancellationRequested();
            var text = ReadCandidate(file);
            if (text is null) continue;
            candidates.Add(file);
            if (text.Contains("AddHttpClient", StringComparison.Ordinal))
                CollectRegistrations(CSharpSyntaxTree.ParseText(text, cancellationToken: ct).GetRoot(ct), registry, ct);
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
                    Add(hits, verb, FirstUrlText(inv.ArgumentList.Arguments), inv, ma.Expression, path, bases, registry, ctx);
                else if (name == "SendAsync")
                {
                    var (verb2, url2) = ReadSend(inv.ArgumentList.Arguments);
                    if (verb2 is not null) Add(hits, verb2, url2, inv, ma.Expression, path, bases, registry, ctx);
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
        return text.Contains("HttpClient", StringComparison.Ordinal)
            || text.Contains("http://", StringComparison.OrdinalIgnoreCase)
            || text.Contains("https://", StringComparison.OrdinalIgnoreCase) ? text : null;
    }

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

    private readonly record struct BaseAddr(Uri Uri, SyntaxNode Node);

    // Враждебный/сгенерированный файл с тысячами BaseAddress сделал бы разрешение квадратичным (вызовы × адреса × глубина).
    // Сверх лимита относительные пути в файле не разрешаем: столько разных клиентов в одном файле — это уже не «один понятный адрес».
    private const int MaxBaseAddressesPerFile = 64;

    // Все литеральные присваивания BaseAddress: `x.BaseAddress = new Uri("http://…")` и `BaseAddress = new Uri("…")` в инициализаторе.
    private static List<BaseAddr>? CollectBaseAddresses(SyntaxNode root)
    {
        var list = new List<BaseAddr>();
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
            var uri = UriLiteral(asg.Right);
            if (uri is not null) list.Add(new BaseAddr(uri, asg));
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

    // Базовый адрес для вызова: ближайшая область (член → тип), где он ровно один. Два разных — null (не гадаем).
    // На уровень файла выходим только для кода верхнего уровня (Program.cs без класса): иначе базовый адрес соседнего
    // класса в том же файле чужой клиент не описывает.
    private static (Uri? Uri, bool Ambiguous) ResolveBase(SyntaxNode call, IReadOnlyList<BaseAddr> bases)
    {
        if (bases.Count == 0) return (null, false);
        foreach (var scope in call.Ancestors().Where(a => a is MemberDeclarationSyntax or TypeDeclarationSyntax))
        {
            var inScope = bases.Where(b => scope.Span.Contains(b.Node.Span)).Select(b => b.Uri).Distinct().ToList();
            if (inScope.Count == 1) return (inScope[0], false);
            if (inScope.Count > 1) return (null, true);        // два разных адреса — неоднозначно, реестр тоже не подставляем
            if (scope is TypeDeclarationSyntax) return (null, false);   // дальше внешнего типа не идём: соседний/вложенный класс — чужой клиент
        }
        // код верхнего уровня: только присваивания вне любого типа (базовый адрес вспомогательного класса сюда не относится)
        var top = bases.Where(b => !b.Node.Ancestors().Any(a => a is TypeDeclarationSyntax)).Select(b => b.Uri).Distinct().ToList();
        return top.Count == 1 ? (top[0], false) : (null, top.Count > 1);
    }

    // ── клиенты из DI (срез 3а) ──

    // Типизированные (AddHttpClient<T>, AddHttpClient<TI,TImpl>) и именованные (AddHttpClient("имя")) клиенты → их ЛИТЕРАЛЬНЫЙ BaseAddress.
    // Адрес из конфигурации (new Uri(options.Url)) здесь не понимаем — это срез 3б. Одно имя с разными адресами — неоднозначно.
    private sealed class ClientRegistry
    {
        public readonly Dictionary<string, HashSet<Uri>> Typed = new(StringComparer.Ordinal);
        public readonly Dictionary<string, HashSet<Uri>> Named = new(StringComparer.Ordinal);
        public int Count;
        public static Uri? Single(Dictionary<string, HashSet<Uri>> map, string key) =>
            map.TryGetValue(key, out var set) && set.Count == 1 ? set.First() : null;
        public static void Add(Dictionary<string, HashSet<Uri>> map, string key, Uri uri)
        {
            if (!map.TryGetValue(key, out var set)) map[key] = set = [];
            set.Add(uri);
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

            var uris = new HashSet<Uri>();
            var node = (SyntaxNode)inv;
            for (var depth = 0; depth < 12; depth++)       // цепочка: AddHttpClient<T>().ConfigureHttpClient(...).AddStandardResilienceHandler()
            {
                if (node is InvocationExpressionSyntax cur && (depth == 0 ? CalleeName(cur) == "AddHttpClient" : CalleeName(cur) == "ConfigureHttpClient"))
                    foreach (var arg in cur.ArgumentList.Arguments)
                        if (arg.Expression is LambdaExpressionSyntax lam)
                            foreach (var b in fileBases) if (lam.Span.Contains(b.Node.Span)) uris.Add(b.Uri);
                if (node.Parent is MemberAccessExpressionSyntax pm && pm.Expression == node && pm.Parent is InvocationExpressionSyntax up) node = up;
                else break;
            }
            if (uris.Count != 1) continue;                 // нет литерального адреса или их несколько в одной регистрации
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

    private static Uri? ResolveRegistered(InvocationExpressionSyntax inv, ExpressionSyntax receiver, ClientRegistry reg, FileCtx ctx)
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
        IReadOnlyList<BaseAddr>? bases, ClientRegistry registry, FileCtx ctx)
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
            var (baseUri, ambiguous) = ResolveBase(inv, bases);
            if (baseUri is null && !ambiguous) baseUri = ResolveRegistered(inv, receiver, registry, ctx);   // конфликт в области реестром не «лечим»
            if (baseUri is null || !Uri.TryCreate(baseUri, url, out uri)) return;      // нет надёжного базового адреса — не угадываем
            // после слияния цель обязана остаться на хосте базового адреса (защита от хитрых относительных строк)
            if (!uri.Authority.Equals(baseUri.Authority, StringComparison.OrdinalIgnoreCase) || uri.Scheme != baseUri.Scheme) return;
            viaBase = true;
        }
        var line = inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        // без query (секреты не тащим); подстановки {имя} сохраняем читаемыми, а не %7B…%7D (раскодируем только скобки)
        var clean = $"{uri.Scheme}://{uri.Authority}{UnescapeBraces(uri.AbsolutePath)}";
        hits.Add(new HttpCallHit(verb, clean, path, line, viaBase ? $"{verb} {clean} (по BaseAddress)" : $"{verb} {clean}", viaBase));
    }

    internal static string UnescapeBraces(string path) => path.Replace("%7B", "{", StringComparison.OrdinalIgnoreCase).Replace("%7D", "}", StringComparison.OrdinalIgnoreCase);
}
