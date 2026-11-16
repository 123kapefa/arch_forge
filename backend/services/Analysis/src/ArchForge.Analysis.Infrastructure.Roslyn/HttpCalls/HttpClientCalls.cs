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

// Вызовы HttpClient: имена методов → HTTP-verb, адрес из первого аргумента (литерал/интерполяция), SendAsync(new HttpRequestMessage(HttpMethod.X, "url")).
internal static class HttpClientCalls
{
    // Имя метода HttpClient → HTTP-verb. *Async-хелперы, возвращающие тело (GetStringAsync/…), — тоже GET.
    internal static readonly IReadOnlyDictionary<string, string> ClientVerbs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GetAsync"] = "GET", ["GetStringAsync"] = "GET", ["GetByteArrayAsync"] = "GET", ["GetStreamAsync"] = "GET",
        ["GetFromJsonAsync"] = "GET",
        ["PostAsync"] = "POST", ["PostAsJsonAsync"] = "POST",
        ["PutAsync"] = "PUT", ["PutAsJsonAsync"] = "PUT",
        ["PatchAsync"] = "PATCH", ["PatchAsJsonAsync"] = "PATCH",
        ["DeleteAsync"] = "DELETE",
    };

    // HttpMethod.X → verb (для SendAsync(new HttpRequestMessage(HttpMethod.X, "url"))).
    internal static readonly IReadOnlyDictionary<string, string> HttpMethodMembers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Get"] = "GET", ["Post"] = "POST", ["Put"] = "PUT", ["Delete"] = "DELETE",
        ["Patch"] = "PATCH", ["Head"] = "HEAD", ["Options"] = "OPTIONS",
    };

    // Адрес — ПЕРВЫЙ аргумент (у Get/PostAsync): строковый литерал или интерполяция. Остальные аргументы (тело, токен)
    // адресом не считаем: `PostAsJsonAsync(url, "текст")` не должен превращать тело запроса в маршрут.
    internal static string? FirstUrlText(SeparatedSyntaxList<ArgumentSyntax> args) =>
        args.Count > 0 ? UrlText(args[0].Expression) : null;

    // Текст адреса из литерала или интерполяции: подстановка → {имя}, всё после «?» (query) отбрасывается — секреты не тащим.
    internal static string? UrlText(ExpressionSyntax e)
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
    internal static string HoleName(ExpressionSyntax e) => e switch
    {
        IdentifierNameSyntax i => i.Identifier.Text,
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        _ => "param",
    };

    // SendAsync(new HttpRequestMessage(HttpMethod.X, "url"), …) → (verb, url). Без объекта-сообщения — пропуск.
    internal static (string?, string?) ReadSend(SeparatedSyntaxList<ArgumentSyntax> args)
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
}
