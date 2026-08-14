using ArchForge.Analysis.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

/// <summary>
/// Семантический источник типов: строит <see cref="CSharpCompilation"/> из исходников таргета плюс
/// ссылки на сборки (рантайм + собранный bin/ таргета) и резолвит наследование/интерфейсы
/// <b>транзитивно</b> — так тип, наследующий, напр., <c>IdentityDbContext</c>, «видит» и <c>DbContext</c>.
/// Ссылки берём из готового bin/ — это обходит строгий design-time build (валился на auth_service).
/// Оппортунистичен (adr/0008): при любой проблеме или пустом результате — синтаксический пол.
/// </summary>
public sealed class RoslynSemanticTypeSource(RoslynTypeSource floor) : ITypeSource
{
    public IReadOnlyList<RawType> Read(string path)
    {
        try
        {
            var semantic = ReadSemantic(path);
            return semantic is { Count: > 0 } ? semantic : floor.Read(path);
        }
        catch
        {
            return floor.Read(path); // семантика — best-effort; при сбое честно падаем на пол
        }
    }

    private static IReadOnlyList<RawType>? ReadSemantic(string path)
    {
        var trees = new List<SyntaxTree>();
        foreach (var file in RoslynScan.EnumerateCs(path))
        {
            try { trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file)); }
            catch { /* нечитаемый файл — пропускаем */ }
        }
        if (trees.Count == 0) return null;

        var references = GatherReferences(path);
        if (references.Count == 0) return null; // без ссылок семантики нет — пусть будет пол

        var compilation = CSharpCompilation.Create(
            "ArchForgeAnalysis",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var result = new List<RawType>();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();
            var layer = RoslynScan.Layer(tree.FilePath);

            foreach (var decl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var name = decl.Identifier.Text;
                if (!RoslynScan.IsTypeName(name)) continue;

                var syntactic = RoslynScan.SyntacticBases(decl);
                var bases = model.GetDeclaredSymbol(decl) is INamedTypeSymbol symbol
                    ? Merge(SemanticBases(symbol), syntactic) // семантика ⊇ синтаксис — не теряем базы
                    : syntactic;

                var ctorParams = decl.ParameterList?.ToString() ?? "";
                result.Add(new RawType(name, decl.Keyword.Text, bases, ctorParams, layer, tree.FilePath));
            }
            foreach (var en in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
                result.Add(new RawType(en.Identifier.Text, "enum", [], "", layer, tree.FilePath));
        }
        return result;
    }

    /// <summary>Базовые типы по цепочке (кроме object) + все интерфейсы транзитивно, простыми именами.</summary>
    private static IReadOnlyList<string> SemanticBases(INamedTypeSymbol symbol)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var b = symbol.BaseType; b is not null && b.SpecialType != SpecialType.System_Object; b = b.BaseType)
            if (b.TypeKind != TypeKind.Error && seen.Add(b.Name)) names.Add(b.Name);

        foreach (var i in symbol.AllInterfaces)
            if (i.TypeKind != TypeKind.Error && seen.Add(i.Name)) names.Add(i.Name);

        return names;
    }

    private static IReadOnlyList<string> Merge(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<string>();
        foreach (var n in a.Concat(b))
            if (seen.Add(n)) merged.Add(n);
        return merged;
    }

    /// <summary>Ссылки: сборки текущего рантайма (System/ASP.NET) + NuGet-DLL из bin/ таргета (кроме своих проектов).</summary>
    private static IReadOnlyList<MetadataReference> GatherReferences(string targetPath)
    {
        var references = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var runtime = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dll in runtime)
            if (seen.Add(Path.GetFileName(dll)))
                TryAdd(references, dll);

        // Свои проекты таргета берём из исходников, а не из bin/ (иначе дубль-определения символов).
        var own = Directory.EnumerateFiles(targetPath, "*.csproj", SearchOption.AllDirectories)
            .Select(p => Path.GetFileNameWithoutExtension(p)!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var dll in Directory.EnumerateFiles(targetPath, "*.dll", SearchOption.AllDirectories))
        {
            if (!dll.Replace('\\', '/').Contains("/bin/")) continue;
            if (own.Contains(Path.GetFileNameWithoutExtension(dll))) continue;
            if (seen.Add(Path.GetFileName(dll))) TryAdd(references, dll);
        }
        return references;
    }

    private static void TryAdd(List<MetadataReference> references, string path)
    {
        try { references.Add(MetadataReference.CreateFromFile(path)); }
        catch { /* битый/недоступный dll — пропускаем */ }
    }
}
