using System.Text.RegularExpressions;
using ArchForge.Analysis.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// DI-контейнер сервиса через Roslyn, синтаксически (adr/0008, «пол»; E4.2): регистрации времён жизни
// (AddSingleton/AddScoped/AddTransient и TryAdd*/Keyed, AddDbContext*, AddHostedService, AddHttpClient<T>, ServiceDescriptor.*)
// + зависимости конструкторов классов таргета. Семантики нет: типы — простые имена без пространств имён и арности.
// Что не разбирается честно, не додумывается: сканирование сборок (Scrutor/MediatR/…) и регистрация по переменной-типу
// поднимают OpaqueReason — детектор тогда понижает уверенность «не зарегистрировано».
public sealed partial class RoslynDiSource : IDiSource
{
    [GeneratedRegex(@"^(?<try>TryAdd)?Add(?<keyed>Keyed)?(?<life>Singleton|Scoped|Transient)$")]
    private static partial Regex AddMethod();

    // Вызовы, регистрирующие типы сканированием/по соглашению: набор регистраций заведомо неполон.
    private static readonly HashSet<string> OpaqueCalls = new(StringComparer.Ordinal)
    {
        "AddMediatR", "RegisterServicesFromAssembly", "RegisterServicesFromAssemblyContaining", "RegisterServicesFromAssemblies",
        "AddValidatorsFromAssembly", "AddValidatorsFromAssemblyContaining", "AddValidatorsFromAssemblies",
        "AddMassTransit", "RegisterAssemblyTypes", "AddFastEndpoints", "AddCarter",
    };

    public DiModel Read(string root, CancellationToken ct = default)
    {
        var regs = new List<DiRegistration>();
        var classes = new List<DiClass>();
        string? opaque = null;

        // порядок файлов детерминирован (EnumerateFiles его не гарантирует) — от него зависит «последняя регистрация побеждает»
        // тот же список и кап, что у отпечатка кеша (ScanCache.ComputeFingerprint): урезанный разбор → набор регистраций неполон
        var files = RoslynScan.EnumerateCsCapped(root, out var capped, ct: ct);
        if (capped) opaque = $"разбор урезан: больше {RoslynScan.MaxFilesPerScan} файлов .cs";
        foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (RoslynScan.Layer(file, root) == "tests") continue;   // регистрации тестовых хостов — не продуктовый контейнер
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }                                      // недоступный файл — пропускаем (изоляция ошибок)

            var tree = CSharpSyntaxTree.ParseText(text);
            var rootNode = tree.GetRoot(ct);
            foreach (var decl in rootNode.DescendantNodes().OfType<TypeDeclarationSyntax>())
                classes.Add(ToClass(decl, file, tree));

            foreach (var oc in rootNode.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                ct.ThrowIfCancellationRequested();
                if (RoslynScan.Simple(oc.Type) == "ServiceDescriptor")
                    opaque ??= $"регистрация через new ServiceDescriptor(…), {Path.GetFileName(file)}:{tree.GetLineSpan(oc.Span).StartLinePosition.Line + 1}";
            }
            foreach (var inv in rootNode.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                ct.ThrowIfCancellationRequested();   // файл ≤2 МБ может дать сотни тысяч узлов — отмена не ждёт конца файла
                var name = CalleeName(inv, out var generic);
                if (name is null) continue;
                var line = tree.GetLineSpan(inv.Span).StartLinePosition.Line + 1;

                if (OpaqueCalls.Contains(name))
                    opaque ??= $"сканирование сборок ({name}), {Path.GetFileName(file)}:{line}";
                else if (name == "Scan" && HasCall(inv.ArgumentList, "AddClasses"))
                    opaque ??= $"сканирование сборок (Scrutor.Scan), {Path.GetFileName(file)}:{line}";

                var reg = ParseRegistration(inv, name, generic, file, line, out var dynamicReg);
                if (dynamicReg) opaque ??= $"регистрация по типу из переменной ({name}), {Path.GetFileName(file)}:{line}";
                if (reg is not null) regs.AddRange(reg);
            }
        }
        return new DiModel(regs, classes, opaque);
    }

    // Имя вызываемого метода и его явные дженерик-аргументы (AddScoped<IX, X>() → "AddScoped", [IX, X]).
    private static string? CalleeName(InvocationExpressionSyntax inv, out IReadOnlyList<string> generic)
    {
        generic = [];
        SimpleNameSyntax? n = inv.Expression switch
        {
            MemberAccessExpressionSyntax m => m.Name,
            MemberBindingExpressionSyntax b => b.Name,
            IdentifierNameSyntax i => i,
            GenericNameSyntax g => g,
            _ => null,
        };
        if (n is null) return null;
        if (n is GenericNameSyntax gn) generic = gn.TypeArgumentList.Arguments.Select(RoslynScan.Simple).ToList();
        return n.Identifier.Text;
    }

    private static IReadOnlyList<DiRegistration>? ParseRegistration(InvocationExpressionSyntax inv, string name,
        IReadOnlyList<string> generic, string file, int line, out bool dynamicReg)
    {
        dynamicReg = false;
        var args = inv.ArgumentList.Arguments;

        // ServiceDescriptor.Singleton<I,T>() — дескриптор для Add/TryAddEnumerable
        if (name is "Singleton" or "Scoped" or "Transient"
            && inv.Expression is MemberAccessExpressionSyntax { Expression: var recv } && RoslynScan.LastIdentifier(recv) == "ServiceDescriptor")
        {
            if (generic.Count > 0) return [Make(generic, Life(name), file, line, factory: args.Count > 0 && !IsTypeof(args[0].Expression), tryAdd: false, null)];
            dynamicReg = true;   // ServiceDescriptor.Singleton(typeof(I), …) — разбирать не берёмся
            return null;
        }

        // EF Core: контекст по умолчанию Scoped (если не задан ServiceLifetime.X в аргументах); фабрика контекстов — Singleton
        if (name is "AddDbContext" or "AddDbContextPool")
        {
            if (generic.Count == 0) return null;
            var life = name == "AddDbContextPool" ? DiLifetime.Scoped : ExplicitLifetime(args) ?? DiLifetime.Scoped;
            return [new DiRegistration(generic[0], generic.Count > 1 ? generic[1] : generic[0], life, file, line)];
        }
        if (name == "AddHostedService" && generic.Count > 0)
            return [new DiRegistration("IHostedService", generic[^1], DiLifetime.Singleton, file, line)];
        // типизированный клиент: Transient (AddHttpClient<T>, AddHttpClient<I,T>); именованный (без дженериков) — не регистрирует тип
        if (name is "AddHttpClient" or "AddRefitClient" or "AddGrpcClient")
        {
            if (generic.Count > 0) return [new DiRegistration(generic[0], generic[^1], DiLifetime.Transient, file, line)];
            dynamicReg = name != "AddHttpClient";   // AddHttpClient("name") — именованный клиент, типа не регистрирует; AddRefitClient(typeof(…)) — неизвестная форма
            return null;
        }

        var m = AddMethod().Match(name);
        if (!m.Success) return null;
        var lifetime = Life(m.Groups["life"].Value);
        var tryAdd = m.Groups["try"].Success;
        var keyed = m.Groups["keyed"].Success;
        // ключ keyed-регистрации: AddKeyedX<I,T>("key") — первый аргумент; AddKeyedX(typeof(I), "key", typeof(T)) — второй
        var exprs = args.Select(a => a.Expression).ToList();
        if (keyed && exprs.Count > 0) exprs.RemoveAt(generic.Count > 0 ? 0 : Math.Min(1, exprs.Count - 1));
        var rest = exprs;

        if (generic.Count > 0)
        {
            // AddScoped<I, T>() / AddScoped<T>() — тип создаёт контейнер; аргумент-лямбда/экземпляр = фабрика
            var factory = rest.Count > 0;
            var deps = factory ? FactoryDeps(rest[0]) : null;
            return [Make(generic, lifetime, file, line, factory, tryAdd, deps, keyed)];
        }

        // без дженериков: (typeof(I), typeof(T)) | (typeof(T)) | (typeof(I), sp => …) — иначе форма неизвестна → набор неполон
        if (rest.Count >= 1 && rest[0] is TypeOfExpressionSyntax t0)
        {
            var service = RoslynScan.Simple(t0.Type);
            if (rest.Count == 1) return [new DiRegistration(service, service, lifetime, file, line, TryAdd: tryAdd, Keyed: keyed)];
            if (rest[1] is TypeOfExpressionSyntax t1)
                return [new DiRegistration(service, RoslynScan.Simple(t1.Type), lifetime, file, line, TryAdd: tryAdd, Keyed: keyed)];
            if (rest[1] is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax)
                return [new DiRegistration(service, null, lifetime, file, line, Factory: true, TryAdd: tryAdd, FactoryDeps: FactoryDeps(rest[1]), Keyed: keyed)];
        }
        dynamicReg = true;   // AddSingleton(new Foo()), AddScoped(type), лямбда без типа и прочее — молча не пропускаем
        return null;
    }

    private static DiRegistration Make(IReadOnlyList<string> generic, DiLifetime life, string file, int line, bool factory, bool tryAdd, IReadOnlyList<string>? deps, bool keyed = false) =>
        new(generic[0], generic.Count > 1 ? generic[1] : generic[0], life, file, line, factory, tryAdd, deps, keyed);

    private static bool IsTypeof(ExpressionSyntax e) => e is TypeOfExpressionSyntax;

    private static DiLifetime Life(string s) => s switch
    {
        "Singleton" => DiLifetime.Singleton,
        "Scoped" => DiLifetime.Scoped,
        _ => DiLifetime.Transient,
    };

    // AddDbContext<T>(…, ServiceLifetime.Singleton): первое упоминание — contextLifetime (optionsLifetime идёт после него)
    private static DiLifetime? ExplicitLifetime(SeparatedSyntaxList<ArgumentSyntax> args)
    {
        foreach (var a in args)
            if (a.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "ServiceLifetime" }, Name.Identifier.Text: var n }
                && n is "Singleton" or "Scoped" or "Transient")
                return Life(n);
        return null;
    }

    // Зависимости, которые фабрика берёт у провайдера: sp.GetService<T>() / GetRequiredService<T>().
    // Вложенные регистрации (AddX внутри лямбды) не обходим: у них своя фабрика, и она разберётся в своём проходе.
    private static IReadOnlyList<string>? FactoryDeps(ExpressionSyntax factory)
    {
        if (factory is not (LambdaExpressionSyntax or AnonymousMethodExpressionSyntax)) return null;
        var found = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<SyntaxNode>();
        stack.Push(factory);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is InvocationExpressionSyntax inv)
            {
                var n = CalleeName(inv, out var g);
                if (n is not null && (AddMethod().IsMatch(n) || IsKnownRegistration(n))) continue;
                if (g.Count == 1 && n is "GetService" or "GetRequiredService" or "GetRequiredKeyedService" or "GetKeyedService")
                    found.Add(g[0]);
            }
            foreach (var c in node.ChildNodes()) stack.Push(c);
        }
        return found.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private static bool IsKnownRegistration(string n) =>
        n is "AddDbContext" or "AddDbContextPool" or "AddHostedService" or "AddHttpClient" or "AddRefitClient" or "AddGrpcClient";

    // Есть ли в поддереве вызов с таким именем — без сериализации узла в строку (она квадратична на длинных цепочках).
    private static bool HasCall(SyntaxNode node, string callee)
    {
        var stack = new Stack<SyntaxNode>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur is InvocationExpressionSyntax inv && CalleeName(inv, out _) == callee) return true;
            foreach (var c in cur.ChildNodes()) stack.Push(c);
        }
        return false;
    }

    private static DiClass ToClass(TypeDeclarationSyntax decl, string file, SyntaxTree tree)
    {
        var line = tree.GetLineSpan(decl.Identifier.Span).StartLinePosition.Line + 1;
        var mods = decl.Modifiers.Select(m => m.Text).ToHashSet();
        var kind = decl is InterfaceDeclarationSyntax ? "interface"
            : decl is ClassDeclarationSyntax && !mods.Contains("abstract") && !mods.Contains("static") ? "class"
            : "other";

        // основной конструктор: C# 12 primary ctor, иначе [ActivatorUtilitiesConstructor], иначе самый «жадный» не-private
        // (как выбирает контейнер). Несколько конструкторов с параметрами без атрибута — выбор неоднозначен.
        IReadOnlyList<ParameterSyntax> best = decl.ParameterList?.Parameters.ToList() ?? [];
        var eligible = decl.ParameterList is { Parameters.Count: > 0 } ? 1 : 0;   // сколько конструкторов контейнер мог бы выбрать
        var marked = false;
        foreach (var c in decl.Members.OfType<ConstructorDeclarationSyntax>())
        {
            if (c.Modifiers.Any(m => m.Text is "static" or "private")) continue;
            var isMarked = c.AttributeLists.SelectMany(l => l.Attributes).Any(at => RoslynScan.Simple(at.Name) is "ActivatorUtilitiesConstructor" or "ActivatorUtilitiesConstructorAttribute");
            eligible++;
            if (isMarked) { best = c.ParameterList.Parameters.ToList(); marked = true; }
            else if (!marked && c.ParameterList.Parameters.Count > best.Count) best = c.ParameterList.Parameters.ToList();
        }
        var ambiguous = !marked && eligible > 1;

        var deps = new List<DiDep>();
        foreach (var p in best)
            if (p.Type is not null && Dep(p) is { } d) deps.Add(d);

        var baseName = RoslynScan.SyntacticBases(decl);
        var resolvable = kind == "class" && (decl.Identifier.Text.EndsWith("Controller", StringComparison.Ordinal)
            || baseName.Any(b => b is "ControllerBase" or "Controller"));
        return new DiClass(decl.Identifier.Text, kind, deps, file, line, resolvable, ambiguous);
    }

    // Параметр → зависимость; null — не зависимость контейнера (примитивы, Func/Lazy, массивы значений).
    private static DiDep? Dep(ParameterSyntax p)
    {
        var type = p.Type!;
        var optional = p.Default is not null;
        if (type is NullableTypeSyntax nt) { type = nt.ElementType; optional = true; }
        if (type is PredefinedTypeSyntax) return null;
        if (type is ArrayTypeSyntax at) { type = at.ElementType; optional = true; }
        if (type is GenericNameSyntax { Identifier.Text: var g } gn && gn.TypeArgumentList.Arguments.Count == 1)
        {
            if (g is "Func" or "Lazy" or "Task" or "ValueTask") return null;
            if (g is "IEnumerable" or "IReadOnlyList" or "IReadOnlyCollection" or "IList" or "ICollection" or "List")
            {
                type = gn.TypeArgumentList.Arguments[0];
                optional = true;
            }
        }
        var name = RoslynScan.Simple(type);
        return name.Length > 0 && char.IsUpper(name[0]) ? new DiDep(name, !optional) : null;
    }
}
