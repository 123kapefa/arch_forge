using ArchForge.Analysis.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpSyntax;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpClientRegistry;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpCallEmitter;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Refit (срез 4): каждый `[Get("/путь")]` интерфейса, зарегистрированного с адресом, — вызов.
internal static class HttpRefitCalls
{
    // ── Refit (срез 4) ──

    internal static readonly IReadOnlyDictionary<string, string> RefitVerbs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Get"] = "GET", ["Post"] = "POST", ["Put"] = "PUT", ["Delete"] = "DELETE", ["Patch"] = "PATCH", ["Head"] = "HEAD", ["Options"] = "OPTIONS",
    };

    // Методы Refit-интерфейса, зарегистрированного с адресом: каждый `[Get("/путь")]` — вызов (декларация, а не место использования).
    // Интерфейс без регистрации/адреса и с разными адресами у одного имени не берём.
    internal static void CollectRefitCalls(SyntaxNode root, string path, ClientRegistry reg, IReadOnlyDictionary<string, string> sections, List<(string Name, string Ns, HttpCallHit Hit)> refitHits)
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
}
