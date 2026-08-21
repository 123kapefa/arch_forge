using ArchForge.Analysis.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Minimal-API эндпоинты: app.MapGet/MapPost/MapPut/MapDelete/MapPatch("литерал", …). Чисто СИНТАКСИЧЕСКИЙ
// разбор — для литерального маршрута семантика не нужна; маршрут-выражение (переменная/интерполяция) пропускаем.
// Контроллеры сюда не попадают (они идут членами типов через ITypeSource). #71
public sealed class RoslynMinimalApiSource : IEndpointSource
{
    private static readonly IReadOnlyDictionary<string, string> Verbs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["MapGet"] = "GET", ["MapPost"] = "POST", ["MapPut"] = "PUT", ["MapDelete"] = "DELETE", ["MapPatch"] = "PATCH",
    };

    public IReadOnlyList<EndpointHit> Read(string root, CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) return [];
        var hits = new List<EndpointHit>();
        foreach (var file in RoslynScan.EnumerateCsCapped(root, out _)) // тот же кап числа файлов, что и в семантике
        {
            ct.ThrowIfCancellationRequested();
            string text;
            try { text = File.ReadAllText(file); } catch { continue; } // недоступный файл — пропускаем
            if (!text.Contains(".Map", StringComparison.Ordinal)) continue; // быстрый отсев файлов без Map*-вызовов
            var path = file.Replace('\\', '/');
            var treeRoot = CSharpSyntaxTree.ParseText(text, cancellationToken: ct).GetRoot(ct);
            foreach (var inv in treeRoot.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (inv.Expression is not MemberAccessExpressionSyntax ma) continue;
                if (!Verbs.TryGetValue(ma.Name.Identifier.Text, out var verb)) continue;
                var args = inv.ArgumentList.Arguments;
                if (args.Count == 0) continue;
                // берём только литеральный маршрут — переменную/const без семантики не резолвим
                if (args[0].Expression is not LiteralExpressionSyntax lit || !lit.IsKind(SyntaxKind.StringLiteralExpression)) continue;
                var line = inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                hits.Add(new EndpointHit(verb, lit.Token.ValueText, path, line));
            }
        }
        return hits;
    }
}
