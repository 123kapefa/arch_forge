using ArchForge.Analysis.Core;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Синтаксический источник типов через Roslyn (пол по adr/0008; семантика — апгрейд позже).
// Использует настоящий парсер Roslyn (первичные конструкторы C# 12 разбираются нативно).
public sealed class RoslynTypeSource : ITypeSource
{
    public IReadOnlyList<RawType> Read(string path)
    {
        var result = new List<RawType>();
        foreach (var file in EnumerateCs(path))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }

            var root = CSharpSyntaxTree.ParseText(text).GetRoot();
            var layer = Layer(file);

            foreach (var decl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var name = decl.Identifier.Text;
                if (name.Length == 0 || !(char.IsUpper(name[0]) || name[0] == '_')) continue;
                var bases = decl.BaseList?.Types.Select(b => Simple(b.Type)).ToList() ?? [];
                var ctorParams = decl.ParameterList?.ToString() ?? "";
                result.Add(new RawType(name, decl.Keyword.Text, bases, ctorParams, layer, file));
            }
            foreach (var en in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
                result.Add(new RawType(en.Identifier.Text, "enum", [], "", layer, file));
        }
        return result;
    }

    private static IEnumerable<string> EnumerateCs(string path) =>
        Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                var n = f.Replace('\\', '/');
                return !n.Contains("/obj/") && !n.Contains("/bin/") && !n.EndsWith(".g.cs") && !n.EndsWith(".Designer.cs");
            });

    private static string Simple(TypeSyntax t) => t switch
    {
        GenericNameSyntax g => g.Identifier.Text,
        IdentifierNameSyntax i => i.Identifier.Text,
        QualifiedNameSyntax q => Simple(q.Right),
        AliasQualifiedNameSyntax a => a.Name.Identifier.Text,
        NullableTypeSyntax n => Simple(n.ElementType),
        _ => t.ToString(),
    };

    private static string Layer(string path)
    {
        var p = path.Replace('\\', '/');
        if (p.Contains("Test")) return "tests";
        if (p.Contains(".Domain")) return "Domain";
        if (p.Contains(".Application") || p.Contains(".Core")) return "Application";
        if (p.Contains(".Infrastructure") || p.Contains(".Persistence")) return "Infrastructure";
        if (p.Contains(".Contracts")) return "Contracts";
        if (p.Contains(".Migrator")) return "Tooling";
        if (p.Contains(".Api") || p.Contains(".Web") || p.Contains(".Host")) return "Api";
        return "unknown";
    }
}
