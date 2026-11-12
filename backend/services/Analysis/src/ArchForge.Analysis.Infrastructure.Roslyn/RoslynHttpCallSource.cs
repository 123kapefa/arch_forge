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
// вызов не приписываем (adr/0008: неоднозначное не выдумываем). Именованные/типизированные клиенты и Refit — срезы 3–4.
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
        var hits = new List<HttpCallHit>();
        foreach (var file in RoslynScan.EnumerateCsCapped(root, out _)) // тот же кап файлов, что и в семантике
        {
            ct.ThrowIfCancellationRequested();
            string text;
            try { text = File.ReadAllText(file); } catch { continue; } // недоступный файл — пропускаем
            // быстрый отсев: без http(s)-литерала нет ни абсолютного адреса, ни базового (BaseAddress в том же файле)
            if (!text.Contains("http://", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("https://", StringComparison.OrdinalIgnoreCase)) continue;
            var path = file.Replace('\\', '/');
            var treeRoot = CSharpSyntaxTree.ParseText(text, cancellationToken: ct).GetRoot(ct);
            var bases = CollectBaseAddresses(treeRoot);
            foreach (var inv in treeRoot.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (inv.Expression is not MemberAccessExpressionSyntax ma) continue;
                var name = ma.Name.Identifier.Text;
                if (ClientVerbs.TryGetValue(name, out var verb))
                    Add(hits, verb, FirstUrlText(inv.ArgumentList.Arguments), inv, path, bases);
                else if (name == "SendAsync")
                {
                    var (verb2, url2) = ReadSend(inv.ArgumentList.Arguments);
                    if (verb2 is not null) Add(hits, verb2, url2, inv, path, bases);
                }
            }
        }
        return hits;
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
    private static List<BaseAddr> CollectBaseAddresses(SyntaxNode root)
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
            if (list.Count > MaxBaseAddressesPerFile) return [];
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
    private static Uri? ResolveBase(SyntaxNode call, IReadOnlyList<BaseAddr> bases)
    {
        if (bases.Count == 0) return null;
        foreach (var scope in call.Ancestors().Where(a => a is MemberDeclarationSyntax or TypeDeclarationSyntax))
        {
            var inScope = bases.Where(b => scope.Span.Contains(b.Node.Span)).Select(b => b.Uri).Distinct().ToList();
            if (inScope.Count == 1) return inScope[0];
            if (inScope.Count > 1) return null;
            if (scope is TypeDeclarationSyntax) return null;   // дальше внешнего типа не идём: соседний/вложенный класс — чужой клиент
        }
        // код верхнего уровня: только присваивания вне любого типа (базовый адрес вспомогательного класса сюда не относится)
        var top = bases.Where(b => !b.Node.Ancestors().Any(a => a is TypeDeclarationSyntax)).Select(b => b.Uri).Distinct().ToList();
        return top.Count == 1 ? top[0] : null;
    }

    private static void Add(List<HttpCallHit> hits, string verb, string? url, InvocationExpressionSyntax inv, string path, IReadOnlyList<BaseAddr> bases)
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
            var baseUri = ResolveBase(inv, bases);
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
