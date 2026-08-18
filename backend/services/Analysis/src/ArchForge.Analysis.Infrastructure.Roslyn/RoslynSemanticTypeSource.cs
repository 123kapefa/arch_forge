using ArchForge.Analysis.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
public sealed class RoslynSemanticTypeSource(RoslynTypeSource floor, ILogger<RoslynSemanticTypeSource>? logger = null) : ITypeSource
{
    // Порог ссылок вынесен в тестируемую единицу: 0 → семантики нет; слишком много → Compilation
    // была бы медленной. И там, и там — честный синтаксический пол (adr/0008).
    internal const int MaxReferences = 4000;
    internal static bool ReferencesUsable(int count) => count is > 0 and <= MaxReferences;

    private readonly ILogger _log = logger ?? NullLogger<RoslynSemanticTypeSource>.Instance;

    public IReadOnlyList<RawType> Read(string path, CancellationToken ct = default)
    {
        try
        {
            var semantic = ReadSemantic(path, ct);
            if (semantic is { Count: > 0 }) return semantic;
            _log.LogDebug("Семантика пуста — синтаксический пол: {Path}", path);
            return floor.Read(path, ct);
        }
        catch (OperationCanceledException) { throw; } // отмена — не «сбой семантики», пробрасываем
        catch (Exception ex)
        {
            // Деградация видима в логах, а не молча (adr/0008: семантика — best-effort).
            _log.LogWarning(ex, "Семантика упала — синтаксический пол: {Path}", path);
            return floor.Read(path, ct);
        }
    }

    private IReadOnlyList<RawType>? ReadSemantic(string path, CancellationToken ct)
    {
        var trees = new List<SyntaxTree>();
        foreach (var file in RoslynScan.EnumerateCs(path))
        {
            ct.ThrowIfCancellationRequested(); // abort клиента → перестаём парсить
            try { trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file)); }
            catch { /* нечитаемый файл — пропускаем */ }
        }
        if (trees.Count == 0) return null;

        var references = GatherReferences(path);
        if (!ReferencesUsable(references.Count))
        {
            _log.LogInformation("Ссылок {Count} (>{Max} или 0) — синтаксический пол: {Path}", references.Count, MaxReferences, path);
            return null;
        }

        var compilation = CSharpCompilation.Create(
            "ArchForgeAnalysis",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var own = compilation.Assembly;
        var result = new List<RawType>();
        foreach (var tree in trees)
        {
            ct.ThrowIfCancellationRequested(); // семантика по дереву дорогая — даём отмене шанс между файлами
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
                result.Add(new RawType(name, decl.Keyword.Text, bases, decl.ParameterList?.ToString() ?? "", layer, tree.FilePath)
                    { Refs = refs, Members = ExtractMembers(symbol) });
            }
            foreach (var en in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
            {
                var values = en.Members.Select(mm => new RawMember(mm.Identifier.Text, "field",
                    Line: mm.GetLocation().GetLineSpan().StartLinePosition.Line + 1)).ToList();
                result.Add(new RawType(en.Identifier.Text, "enum", [], "", layer, tree.FilePath) { Members = values });
            }
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

    // Короткое имя типа (с дженериками, без неймспейса): «Task<Product>», «Guid».
    private static readonly SymbolDisplayFormat ShortType = SymbolDisplayFormat.MinimallyQualifiedFormat;
    // Синтезированные компилятором члены record — не показываем как «настоящие».
    private static readonly HashSet<string> RecordSynth = new(StringComparer.Ordinal)
        { "Equals", "GetHashCode", "ToString", "Deconstruct", "PrintMembers" };

    /// <summary>Члены типа для L3: свойства, поля, методы; у контроллеров экшены → endpoint с verb/route.</summary>
    private static IReadOnlyList<RawMember> ExtractMembers(INamedTypeSymbol symbol)
    {
        var isController = InheritsController(symbol) || symbol.Name.EndsWith("Controller", StringComparison.Ordinal);
        var members = new List<RawMember>();
        foreach (var m in symbol.GetMembers())
        {
            if (m.DeclaredAccessibility == Accessibility.Private) continue;
            switch (m)
            {
                case IPropertySymbol { IsIndexer: false } p when p.Name != "EqualityContract":
                    members.Add(new RawMember(p.Name, "property", p.Type.ToDisplayString(ShortType), Line: LineOf(p)));
                    break;
                case IMethodSymbol { MethodKind: MethodKind.Ordinary, IsImplicitlyDeclared: false } method when !RecordSynth.Contains(method.Name):
                    var (http, route) = isController ? HttpOf(method) : (null, null);
                    members.Add(new RawMember(method.Name, http is not null ? "endpoint" : "method",
                        MethodSig(method), http, route, LineOf(method)));
                    break;
                case IFieldSymbol { IsImplicitlyDeclared: false, IsConst: false } field:
                    members.Add(new RawMember(field.Name, "field", field.Type.ToDisplayString(ShortType), Line: LineOf(field)));
                    break;
            }
        }
        return members;
    }

    private static string MethodSig(IMethodSymbol m)
    {
        var ps = string.Join(", ", m.Parameters.Select(p => p.Type.ToDisplayString(ShortType)));
        var ret = m.ReturnsVoid ? "void" : m.ReturnType.ToDisplayString(ShortType);
        return $"({ps}) : {ret}";
    }

    // Verb+route экшена по атрибутам ([HttpGet("...")], [Route("...")]).
    private static (string? Http, string? Route) HttpOf(IMethodSymbol m)
    {
        string? http = null, route = null;
        foreach (var a in m.GetAttributes())
        {
            var verb = (a.AttributeClass?.Name) switch
            {
                "HttpGetAttribute" => "GET", "HttpPostAttribute" => "POST", "HttpPutAttribute" => "PUT",
                "HttpDeleteAttribute" => "DELETE", "HttpPatchAttribute" => "PATCH", _ => null,
            };
            if (verb is not null)
            {
                http = verb;
                if (a.ConstructorArguments.FirstOrDefault().Value is string s) route = s;
            }
            else if (a.AttributeClass?.Name == "RouteAttribute" && a.ConstructorArguments.FirstOrDefault().Value is string rs)
            {
                route ??= rs;
            }
        }
        return (http, route);
    }

    private static bool InheritsController(INamedTypeSymbol symbol)
    {
        for (var b = symbol.BaseType; b is not null; b = b.BaseType)
            if (b.Name is "ControllerBase" or "Controller") return true;
        return false;
    }

    private static int? LineOf(ISymbol s)
    {
        var loc = s.Locations.FirstOrDefault(l => l.IsInSource);
        return loc is null ? null : loc.GetLineSpan().StartLinePosition.Line + 1;
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
