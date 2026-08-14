using ArchForge.Analysis.Core;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Синтаксический источник типов через Roslyn — «пол» (adr/0008): работает даже на несобираемом коде.
// Семантику даёт RoslynSemanticTypeSource поверх; сюда падаем, когда семантика недоступна.
public sealed class RoslynTypeSource : ITypeSource
{
    public IReadOnlyList<RawType> Read(string path)
    {
        var result = new List<RawType>();
        foreach (var file in RoslynScan.EnumerateCs(path))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }

            var root = CSharpSyntaxTree.ParseText(text).GetRoot();
            var layer = RoslynScan.Layer(file);

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
