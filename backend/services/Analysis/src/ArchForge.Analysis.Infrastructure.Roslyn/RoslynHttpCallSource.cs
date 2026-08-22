using ArchForge.Analysis.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Исходящие HTTP-вызовы (E3.1 срез 1): HttpClient.GetAsync/PostAsync/… и SendAsync(new HttpRequestMessage(HttpMethod.X, "url")).
// Чисто СИНТАКСИЧЕСКИЙ разбор (как RoslynMinimalApiSource) — берём ТОЛЬКО абсолютный URL-литерал (http[s]://host/…);
// относительные пути и переменные (base-address, именованный HttpClient, Refit) — срезы 2–4.
// Секреты: сохраняем лишь scheme://host[:port]/path — query отбрасываем (secrets-config).
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
            // быстрый отсев: файл без абсолютного http-литерала не сканируем (срез 1 — только абсолютные URL)
            if (!text.Contains("http://", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("https://", StringComparison.OrdinalIgnoreCase)) continue;
            var path = file.Replace('\\', '/');
            var treeRoot = CSharpSyntaxTree.ParseText(text, cancellationToken: ct).GetRoot(ct);
            foreach (var inv in treeRoot.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (inv.Expression is not MemberAccessExpressionSyntax ma) continue;
                var name = ma.Name.Identifier.Text;
                if (ClientVerbs.TryGetValue(name, out var verb))
                    Add(hits, verb, FirstStringLiteral(inv.ArgumentList.Arguments), inv, path);
                else if (name == "SendAsync")
                {
                    var (verb2, url2) = ReadSend(inv.ArgumentList.Arguments);
                    if (verb2 is not null) Add(hits, verb2, url2, inv, path);
                }
            }
        }
        return hits;
    }

    // Первый строковый литерал-аргумент (URL у Get/PostAsync — первый параметр).
    private static string? FirstStringLiteral(SeparatedSyntaxList<ArgumentSyntax> args)
    {
        foreach (var a in args)
            if (a.Expression is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
                return lit.Token.ValueText;
        return null;
    }

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
                else if (url is null && oa.Expression is LiteralExpressionSyntax lit &&
                         lit.IsKind(SyntaxKind.StringLiteralExpression)) url = lit.Token.ValueText;
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

    private static void Add(List<HttpCallHit> hits, string verb, string? url, InvocationExpressionSyntax inv, string path)
    {
        if (url is null) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;               // срез 1 — только абсолютные URL
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;
        var line = inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var clean = $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";               // без query — секреты не тащим
        hits.Add(new HttpCallHit(verb, clean, path, line, $"{verb} {clean}"));
    }
}
