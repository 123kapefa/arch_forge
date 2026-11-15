using ArchForge.Analysis.Core;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Синтаксический источник типов через Roslyn — «пол» (adr/0008): работает даже на несобираемом коде.
// Семантику даёт RoslynSemanticTypeSource поверх; сюда падаем, когда семантика недоступна.
public sealed class RoslynTypeSource(ILogger<RoslynTypeSource>? logger = null) : ITypeSource
{
    private readonly ILogger _log = logger ?? NullLogger<RoslynTypeSource>.Instance;

    public IReadOnlyList<RawType> Read(string path, CancellationToken ct = default)
    {
        var result = new List<RawType>();
        var files = RoslynScan.EnumerateCsCapped(path, out var capped, ct: ct); // тот же кап, что и в семантике (backstop)
        if (capped)
            _log.LogWarning("Сервис крупнее капа: > {Max} .cs — разбираем первые {Max} (робастность): {Path}",
                RoslynScan.MaxFilesPerScan, RoslynScan.MaxFilesPerScan, path);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested(); // abort клиента → перестаём парсить, а не досчитываем впустую
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }

            var root = CSharpSyntaxTree.ParseText(text).GetRoot();
            var layer = RoslynScan.Layer(file, path);

            foreach (var decl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var name = decl.Identifier.Text;
                if (!RoslynScan.IsTypeName(name)) continue;
                var ctorParams = decl.ParameterList?.ToString() ?? "";
                result.Add(new RawType(name, decl.Keyword.Text, RoslynScan.SyntacticBases(decl), ctorParams, layer, file));
            }
            foreach (var en in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
                result.Add(new RawType(en.Identifier.Text, "enum", [], "", layer, file));
        }
        return result;
    }
}
