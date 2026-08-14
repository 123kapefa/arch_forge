using ArchForge.Analysis.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

/// <summary>
/// Семантический источник типов: строит <see cref="CSharpCompilation"/> из исходников таргета плюс
/// ссылки на сборки (рантайм + собранный bin/ таргета) и извлекает:
///  • базы/интерфейсы ТРАНЗИТИВНО (тип через <c>IdentityDbContext</c> «видит» и <c>DbContext</c>);
///  • исходящие ссылки на СВОИ типы (<see cref="Ref"/>): поля/свойства → holds, параметры ctor/методов →
///    uses, брошенные свои исключения → raises, аргумент дженерик-хендлера → handles.
/// Ссылки из готового bin/ обходят строгий design-time build (валился на auth_service, adr/0008).
/// Оппортунистичен: при любой проблеме или пустом результате — синтаксический пол.
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
        // Нет ссылок → семантики нет; слишком много → Compilation будет медленной. И там, и там —
        // честный синтаксический пол (adr/0008: семантику берём оппортунистически).
        if (references.Count is 0 or > 4000) return null;

        var compilation = CSharpCompilation.Create(
            "ArchForgeAnalysis",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var own = compilation.Assembly;
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
                if (model.GetDeclaredSymbol(decl) is not INamedTypeSymbol symbol)
                {
                    result.Add(new RawType(name, decl.Keyword.Text, syntactic, decl.ParameterList?.ToString() ?? "", layer, tree.FilePath));
                    continue;
                }

                var bases = Merge(SemanticBases(symbol), syntactic); // семантика ⊇ синтаксис
                var refs = ResolveRefs(symbol, decl, model, own);
                result.Add(new RawType(name, decl.Keyword.Text, bases, decl.ParameterList?.ToString() ?? "", layer, tree.FilePath) { Refs = refs });
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

    // Приоритет вида на пару типов (выше — тот и остаётся): handles > holds > raises > uses.
    private static readonly Dictionary<string, int> KindRank = new(StringComparer.Ordinal)
    {
        ["uses"] = 1, ["raises"] = 2, ["holds"] = 3, ["handles"] = 4,
    };

    /// <summary>Исходящие ссылки типа на СВОИ (из этой же компиляции) типы, по видам рёбер.</summary>
    private static IReadOnlyList<Ref> ResolveRefs(INamedTypeSymbol symbol, TypeDeclarationSyntax decl, SemanticModel model, IAssemblySymbol own)
    {
        var best = new Dictionary<string, string>(StringComparer.Ordinal); // target → kind

        void Consider(ITypeSymbol? type, string kind)
        {
            var acc = new HashSet<string>(StringComparer.Ordinal);
            CollectOwn(type, own, acc);
            foreach (var target in acc)
            {
                if (target == symbol.Name) continue;
                if (!best.TryGetValue(target, out var cur) || KindRank[kind] > KindRank[cur]) best[target] = kind;
            }
        }

        foreach (var member in symbol.GetMembers())
        {
            switch (member)
            {
                case IFieldSymbol { IsStatic: false, IsImplicitlyDeclared: false } f: Consider(f.Type, "holds"); break;
                case IPropertySymbol { IsStatic: false } p: Consider(p.Type, "holds"); break;
                case IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.Ordinary } m:
                    foreach (var param in m.Parameters) Consider(param.Type, "uses");
                    break;
            }
        }

        // handles: аргумент(ы) дженерик-интерфейса-хендлера (ICommandHandler<T>, IRequestHandler<T,R>, …)
        foreach (var iface in symbol.AllInterfaces)
            if (iface.IsGenericType && iface.Name.Contains("Handler", StringComparison.Ordinal))
                foreach (var arg in iface.TypeArguments) Consider(arg, "handles");

        // raises: свои типы, бросаемые через throw
        foreach (var node in decl.DescendantNodes())
        {
            ExpressionSyntax? thrown = node switch
            {
                ThrowStatementSyntax ts => ts.Expression,
                ThrowExpressionSyntax te => te.Expression,
                _ => null,
            };
            if (thrown is not null) Consider(model.GetTypeInfo(thrown).Type, "raises");
        }

        return best.Select(kv => new Ref(kv.Key, kv.Value)).ToList();
    }

    /// <summary>Собирает простые имена СВОИХ именованных типов внутри type (включая аргументы дженериков/элементы массивов).</summary>
    private static void CollectOwn(ITypeSymbol? type, IAssemblySymbol own, ISet<string> acc)
    {
        switch (type)
        {
            case IArrayTypeSymbol arr:
                CollectOwn(arr.ElementType, own, acc);
                break;
            case INamedTypeSymbol nt:
                if (nt.TypeKind != TypeKind.Error && SymbolEqualityComparer.Default.Equals(nt.ContainingAssembly, own))
                    acc.Add(nt.Name);
                foreach (var arg in nt.TypeArguments) CollectOwn(arg, own, acc);
                break;
        }
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
        var ownProjects = Directory.EnumerateFiles(targetPath, "*.csproj", SearchOption.AllDirectories)
            .Select(p => Path.GetFileNameWithoutExtension(p)!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var dll in Directory.EnumerateFiles(targetPath, "*.dll", SearchOption.AllDirectories))
        {
            if (!dll.Replace('\\', '/').Contains("/bin/")) continue;
            if (ownProjects.Contains(Path.GetFileNameWithoutExtension(dll))) continue;
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
