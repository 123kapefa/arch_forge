using ArchForge.Analysis.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Лишние проверки параметров на null (методичка «Стратегия ошибок и нулей», правило 3): внутри сервиса ненулевой параметр проверяет
// компилятор (Nullable), `ArgumentNullException.ThrowIfNull(p)` на нём — шум; граница API проверяется один раз в валидаторе.
// Три формы: `ArgumentNullException.ThrowIfNull(p)`, `if (p == null / is null) throw new ArgumentNullException(…)`,
// `x = p ?? throw new ArgumentNullException(…)`. Чисто синтаксический разбор.
// НЕ считаем лишней проверку, когда: тип допускает null (`T?`, `Nullable<T>`); у параметра значение по умолчанию null или атрибут
// [AllowNull]/[MaybeNull]; параметр ref/out/in; тип — значимый (int, bool, Guid, DateTime… — null там невозможен, проверка бессмысленна
// по другой причине); тип — generic-параметр метода/типа (может быть ссылочным); проверяется не сам параметр (поле, свойство, `p.Prop`,
// локальная переменная, параметр лямбды; параметры первичного конструктора record — это свойства); в точке проверки nullable не включён
// (директивы `#nullable` учитываются ПО ПОЗИЦИИ, как у компилятора; без них — по проекту: `<Nullable>` в csproj/Directory.Build.props);
// слои tests, Contracts и Shared (публичное API библиотек: вызывающий может быть без nullable, ThrowIfNull там рекомендован самим .NET).
public sealed class RoslynGuardSource : IGuardSource
{
    // Защита от враждебного проекта: потолки на работу и на размер результата (имена из чужого кода не доверенные).
    private const int MaxCandidatesPerFile = 200;
    private const int MaxSites = 10_000;
    private const int MaxNameLength = 200;
    private const long MaxConfigBytes = 2_000_000;

    // Имя из чужого кода: обрезать и убрать управляющие/bidi-символы, чтобы не раздувать ответ и не подменять отображение.
    private static string Clean(string s)
    {
        var t = s.Length > MaxNameLength ? s[..MaxNameLength] + "…" : s;
        return t.Any(c => char.IsControl(c) || c is >= '\u200B' and <= '\u200F' or >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069')
            ? new string(t.Where(c => !(char.IsControl(c) || c is >= '\u200B' and <= '\u200F' or >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069')).ToArray())
            : t;
    }

    public IReadOnlyList<NullGuardSite> Read(string root, CancellationToken ct = default) =>
        RoslynScan.OnBigStack(() => ReadCore(root, ct));   // парсер/биндер Roslyn рекурсивны; StackOverflow не ловится (#167)

    private IReadOnlyList<NullGuardSite> ReadCore(string root, CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) return [];
        var sites = new List<NullGuardSite>();
        var nullableByDir = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var file in RoslynScan.EnumerateCsCapped(root, out _))
        {
            ct.ThrowIfCancellationRequested();
            if (sites.Count >= MaxSites) break;
            var layer = RoslynScan.Layer(file, root);
            if (layer is "tests" or "Contracts" or "Shared") continue;   // тесты и публичное API библиотек не судим
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }
            if (!text.Contains("ArgumentNullException", StringComparison.Ordinal)) continue;   // быстрый отсев: без этого имени проверок нет
            var tree = RoslynScan.TryParseRoot(text, file, ct);
            if (tree is null) continue;   // слишком глубокая вложенность — пропуск (#167)
            var path = file.Replace('\\', '/');
            NullableContext? nullable = null;                       // директивы и настройка проекта считаются лениво: только если нашёлся кандидат
            var candidates = 0;
            var perFile = 0;
            foreach (var node in tree.DescendantNodes())
            {
                if ((++candidates & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
                var (param, form) = node switch
                {
                    InvocationExpressionSyntax inv => ThrowIfNullParam(inv),
                    IfStatementSyntax ifs => IfThrowParam(ifs),
                    BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression } bin => CoalesceThrowParam(bin),
                    _ => (null, null),
                };
                if (param is null || form is null) continue;
                if (candidates > 0 && ++perFile > MaxCandidatesPerFile) break;      // потолок работы на файл: подъём к владельцу дорог
                if (OwnerOf(node, param) is not { } owner) continue;
                nullable ??= new NullableContext(tree, file, root, nullableByDir);
                if (!nullable.EnabledAt(node.SpanStart)) continue;
                sites.Add(new NullGuardSite(Clean(owner.Type), Clean(owner.Member), Clean(param), form, path, node.GetLocation().GetLineSpan().StartLinePosition.Line + 1, layer));
            }
        }
        return sites;
    }

    // ── распознавание форм ──

    private static (string?, string?) ThrowIfNullParam(InvocationExpressionSyntax inv)
    {
        if (inv.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ThrowIfNull" } ma || !IsArgumentNullException(ma.Expression)) return (null, null);
        return inv.ArgumentList.Arguments.Count >= 1 && inv.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax id ? (id.Identifier.Text, "ThrowIfNull") : (null, null);
    }

    // if (p == null) throw new ArgumentNullException(…);   if (p is null) { throw new ArgumentNullException(…); }   (без else)
    private static (string?, string?) IfThrowParam(IfStatementSyntax ifs)
    {
        if (ifs.Else is not null) return (null, null);
        var stmt = ifs.Statement is BlockSyntax { Statements.Count: 1 } b ? b.Statements[0] : ifs.Statement;
        if (stmt is not ThrowStatementSyntax { Expression: ObjectCreationExpressionSyntax oc } || !IsArgumentNullException(oc.Type)) return (null, null);
        return NullTestedIdentifier(ifs.Condition) is { } id ? (id, "if-throw") : (null, null);
    }

    private static string? NullTestedIdentifier(ExpressionSyntax cond) => cond switch
    {
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.EqualsExpression, Left: IdentifierNameSyntax l, Right: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } } => l.Identifier.Text,
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.EqualsExpression, Left: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression }, Right: IdentifierNameSyntax r } => r.Identifier.Text,
        IsPatternExpressionSyntax { Expression: IdentifierNameSyntax e, Pattern: ConstantPatternSyntax { Expression: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } } } => e.Identifier.Text,
        _ => null,
    };

    // x = p ?? throw new ArgumentNullException(nameof(p))
    private static (string?, string?) CoalesceThrowParam(BinaryExpressionSyntax bin) =>
        bin.Left is IdentifierNameSyntax l && bin.Right is ThrowExpressionSyntax { Expression: ObjectCreationExpressionSyntax oc } && IsArgumentNullException(oc.Type)
            ? (l.Identifier.Text, "??-throw") : (null, null);

    private static bool IsArgumentNullException(SyntaxNode n) => n switch
    {
        IdentifierNameSyntax i => i.Identifier.Text == "ArgumentNullException",
        QualifiedNameSyntax q => q.Right.Identifier.Text == "ArgumentNullException",
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text == "ArgumentNullException",
        _ => false,
    };

    // ── параметр-владелец ──

    private readonly record struct Owner(string Type, string Member);

    // Ближайший объемлющий метод/конструктор/локальная функция (или тип с первичным конструктором), у которого есть параметр `name`
    // ненулевого, не-generic типа без значения по умолчанию null. Параметр лямбды между проверкой и владельцем перекрывает имя.
    private static Owner? OwnerOf(SyntaxNode at, string name)
    {
        var typeNames = new List<TypeDeclarationSyntax>();
        foreach (var anc in at.Ancestors())
        {
            switch (anc)
            {
                case SimpleLambdaExpressionSyntax sl when sl.Parameter.Identifier.Text == name: return null;
                case ParenthesizedLambdaExpressionSyntax pl when pl.ParameterList.Parameters.Any(p => p.Identifier.Text == name): return null;
                case AnonymousMethodExpressionSyntax am when am.ParameterList?.Parameters.Any(p => p.Identifier.Text == name) == true: return null;

                case BaseMethodDeclarationSyntax m when m is MethodDeclarationSyntax or ConstructorDeclarationSyntax:
                    if (Param(m.ParameterList.Parameters, name) is { } p1)
                        return IsRedundantType(p1, TypeParamNames(m)) && ContainingType(m) is { } t1 ? new Owner(t1.Identifier.Text, MemberName(m)) : null;
                    break;
                case LocalFunctionStatementSyntax lf:
                    if (Param(lf.ParameterList.Parameters, name) is { } p2)
                        return IsRedundantType(p2, TypeParamNames(lf)) && ContainingType(lf) is { } t2 ? new Owner(t2.Identifier.Text, lf.Identifier.Text) : null;
                    break;
                case TypeDeclarationSyntax td:
                    if (td.ParameterList is { } pl2 && Param(pl2.Parameters, name) is { } p3)
                    {
                        if (td is RecordDeclarationSyntax) return null;                                     // у record это свойство, а не параметр
                        if (td.Members.Any(m => m is PropertyDeclarationSyntax pr && pr.Identifier.Text == name
                                             || m is FieldDeclarationSyntax f && f.Declaration.Variables.Any(v => v.Identifier.Text == name))) return null;   // перекрыт членом
                        return IsRedundantType(p3, TypeParamNames(td)) ? new Owner(td.Identifier.Text, td.Identifier.Text) : null;
                    }
                    break;
            }
        }
        return null;
    }

    private static ParameterSyntax? Param(SeparatedSyntaxList<ParameterSyntax> ps, string name) => ps.FirstOrDefault(p => p.Identifier.Text == name);

    // Типы-значения, которые в коде чаще всего пишут без `?`: null у них невозможен, проверка бессмысленна по другой причине.
    private static readonly HashSet<string> ValueTypeNames = new(StringComparer.Ordinal)
    {
        "Guid", "DateTime", "DateTimeOffset", "TimeSpan", "DateOnly", "TimeOnly", "CancellationToken", "Index", "Range", "Half", "Int128", "UInt128",
    };

    private static bool IsRedundantType(ParameterSyntax p, HashSet<string> typeParams)
    {
        if (p.Type is null || p.Type is NullableTypeSyntax) return false;                                   // `T?` — null допустим
        if (p.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword) || m.IsKind(SyntaxKind.OutKeyword) || m.IsKind(SyntaxKind.InKeyword))) return false;
        if (p.Default?.Value is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression }) return false;
        // [AllowNull]/[MaybeNull] (и …Attribute): null допустим вопреки типу
        if (p.AttributeLists.SelectMany(l => l.Attributes).Any(a => AttrName(a) is "AllowNull" or "MaybeNull")) return false;
        switch (p.Type)
        {
            case PredefinedTypeSyntax pt when !pt.Keyword.IsKind(SyntaxKind.StringKeyword) && !pt.Keyword.IsKind(SyntaxKind.ObjectKeyword): return false;   // int, bool, …
            case IdentifierNameSyntax { Identifier.Text: var n } when typeParams.Contains(n) || ValueTypeNames.Contains(n): return false;           // generic / значимый
            case QualifiedNameSyntax { Right.Identifier.Text: var qn } when ValueTypeNames.Contains(qn): return false;
            case GenericNameSyntax { Identifier.Text: "Nullable" }: return false;                           // Nullable<int>
        }
        return true;
    }

    private static string AttrName(AttributeSyntax a)
    {
        var n = a.Name switch { IdentifierNameSyntax i => i.Identifier.Text, QualifiedNameSyntax q => q.Right.Identifier.Text, _ => "" };
        return n.EndsWith("Attribute", StringComparison.Ordinal) ? n[..^"Attribute".Length] : n;
    }

    private static HashSet<string> TypeParamNames(SyntaxNode member)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (SyntaxNode? n = member; n is not null; n = n.Parent)
        {
            var list = n switch { MethodDeclarationSyntax m => m.TypeParameterList, LocalFunctionStatementSyntax l => l.TypeParameterList, TypeDeclarationSyntax t => t.TypeParameterList, _ => null };
            if (list is not null) foreach (var tp in list.Parameters) set.Add(tp.Identifier.Text);
        }
        return set;
    }

    private static TypeDeclarationSyntax? ContainingType(SyntaxNode n) => n.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();

    private static string MemberName(BaseMethodDeclarationSyntax m) => m switch
    {
        MethodDeclarationSyntax md => md.Identifier.Text,
        ConstructorDeclarationSyntax cd => cd.Identifier.Text,
        _ => "",
    };

    // ── nullable включён? ──

    // Контекст nullable в точке кода, как у компилятора: настройка проекта (csproj/Directory.Build.props/.targets вверх от файла до корня
    // разбора и на один уровень выше) задаёт начальное состояние аннотаций, директивы `#nullable` в файле меняют его ПО ПОЗИЦИИ:
    // enable/disable (с `annotations` или без) включают/выключают аннотации, `warnings` их не трогает, restore возвращает настройку проекта.
    private sealed class NullableContext
    {
        private readonly List<(int Pos, string Setting, string? Target)> _directives;
        private readonly bool _projectDefault;

        public NullableContext(SyntaxNode tree, string file, string root, Dictionary<string, bool> cache)
        {
            _directives = tree.DescendantTrivia(descendIntoTrivia: true)
                .Where(t => t.IsKind(SyntaxKind.NullableDirectiveTrivia) && t.GetStructure() is NullableDirectiveTriviaSyntax)
                .Select(t => (NullableDirectiveTriviaSyntax)t.GetStructure()!)
                .Select(d => (d.SpanStart, d.SettingToken.Text, d.TargetToken.IsKind(SyntaxKind.None) ? null : d.TargetToken.Text))
                .OrderBy(d => d.SpanStart).ToList();
            var dir = Path.GetDirectoryName(Path.GetFullPath(file));
            _projectDefault = dir is not null && NullableForDir(dir, Path.GetDirectoryName(Path.GetFullPath(root).TrimEnd('/', '\\')) ?? root, cache);
        }

        public bool EnabledAt(int position)
        {
            var on = _projectDefault;
            foreach (var (pos, setting, target) in _directives)
            {
                if (pos > position) break;
                if (target is "warnings") continue;                         // предупреждения аннотации ссылочных типов не меняют
                if (setting == "enable") on = true;
                else if (setting == "disable") on = false;
                else if (setting == "restore") on = _projectDefault;
            }
            return on;
        }
    }

    private static bool NullableForDir(string dir, string stopAt, Dictionary<string, bool> cache)
    {
        var visited = new List<string>();
        bool? result = null;
        for (var d = dir; d is not null; d = Path.GetDirectoryName(d))
        {
            if (cache.TryGetValue(d, out var known)) { result = known; break; }
            visited.Add(d);
            if (ReadNullable(d) is { } v) { result = v; break; }
            if (string.Equals(d, stopAt, StringComparison.Ordinal)) break;
        }
        var final = result ?? false;
        foreach (var v in visited) cache[v] = final;
        return final;
    }

    // Значение <Nullable> в csproj этой папки, затем в Directory.Build.props/.targets: настоящий XML (комментарии не считаются), элементы с
    // Condition (свой или у группы) пропускаются — значение зависит от условия, которое мы не вычисляем; последнее заданное побеждает.
    // enable/annotations включают аннотации; warnings/disable — нет. null — не задано.
    private static bool? ReadNullable(string dir)
    {
        string[] candidates;
        try { candidates = [.. SafeFiles.Enumerate(dir, "*.csproj", recurse: false), Path.Combine(dir, "Directory.Build.props"), Path.Combine(dir, "Directory.Build.targets")]; }
        catch { return null; }
        foreach (var f in candidates)
        {
            if (!SafeFiles.IsRegularFile(f)) continue;
            try { if (new FileInfo(f).Length > MaxConfigBytes) continue; } catch { continue; }
            System.Xml.Linq.XDocument doc;
            try
            {
                using var reader = System.Xml.XmlReader.Create(f, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
                doc = System.Xml.Linq.XDocument.Load(reader);
            }
            catch { continue; }
            string? last = null;
            foreach (var e in doc.Descendants().Where(e => e.Name.LocalName == "Nullable"))
            {
                if (e.AncestorsAndSelf().Any(a => a.Attribute("Condition") is not null)) continue;
                last = e.Value.Trim();
            }
            if (last is not null) return last.Equals("enable", StringComparison.OrdinalIgnoreCase) || last.Equals("enabled", StringComparison.OrdinalIgnoreCase) || last.Equals("annotations", StringComparison.OrdinalIgnoreCase);
        }
        return null;
    }
}
