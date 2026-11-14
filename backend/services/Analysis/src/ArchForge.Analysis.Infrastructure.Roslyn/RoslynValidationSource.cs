using ArchForge.Analysis.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Кто владеет правилом формата поля (методичка «Валидация — один раз»). Чисто синтаксический разбор.
// Правило валидатора: в типе, наследующем AbstractValidator<T>, вызов `RuleFor(x => x.A.B)` с цепочкой проверок формата
// (MaximumLength/MinimumLength/Length/Matches/EmailAddress). Цепочка с Must/MustBe/Custom/SetValidator — это делегирование VO, а не
// копия правила: такая цепочка не отдаётся. NotNull/NotEmpty — проверка «поле пришло» на границе API, это зона валидатора.
// Value Object: тип (class/record/struct) со статической фабрикой Of/Create/TryCreate/Parse/From от одного строкового параметра.
// Слои tests, Contracts и Shared не судим. Потолки и чистка имён — как у RoslynGuardSource (враждебный проект).
public sealed class RoslynValidationSource : IValidationSource
{
    private const int MaxSitesPerKind = 5_000;
    private const int MaxCandidatesPerFile = 200;
    private const int MaxChain = 64;
    private const int MaxNameLength = 200;

    private static readonly HashSet<string> FormatChecks = new(StringComparer.Ordinal) { "MaximumLength", "MinimumLength", "Length", "Matches", "EmailAddress" };
    private static readonly HashSet<string> Delegation = new(StringComparer.Ordinal) { "Must", "MustAsync", "MustBe", "Custom", "CustomAsync", "SetValidator" };
    private static readonly HashSet<string> FactoryNames = new(StringComparer.Ordinal) { "Of", "Create", "TryCreate", "Parse", "From" };

    private static string Clean(string s)
    {
        var t = s.Length > MaxNameLength ? s[..MaxNameLength] + "…" : s;
        return new string(t.Where(c => !(char.IsControl(c) || c is >= '​' and <= '‏' or >= '‪' and <= '‮' or >= '⁦' and <= '⁩')).ToArray());
    }

    public ValidationFacts Read(string root, CancellationToken ct = default) =>
        RoslynScan.OnBigStack(() => ReadCore(root, ct));   // парсер Roslyn рекурсивен; StackOverflow не ловится (#167)

    private ValidationFacts ReadCore(string root, CancellationToken ct)
    {
        if (!Directory.Exists(root)) return ValidationFacts.Empty;
        var rules = new List<ValidatorRuleSite>();
        var vos = new List<ValueObjectSite>();
        foreach (var file in RoslynScan.EnumerateCsCapped(root, out _))
        {
            ct.ThrowIfCancellationRequested();
            if (rules.Count >= MaxSitesPerKind && vos.Count >= MaxSitesPerKind) break;
            var layer = RoslynScan.Layer(file, root);
            if (layer is "tests" or "Contracts" or "Shared") continue;
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }
            var hasRule = text.Contains("RuleFor", StringComparison.Ordinal);
            var hasFactory = FactoryNames.Any(n => text.Contains(n + "(", StringComparison.Ordinal));
            if (!hasRule && !hasFactory) continue;                                  // быстрый отсев
            var tree = RoslynScan.TryParseRoot(text, file, ct);
            if (tree is null) continue;   // слишком глубокая вложенность — пропуск (#167)
            var path = file.Replace('\\', '/');
            var seen = 0;
            var candidates = 0;
            foreach (var node in tree.DescendantNodes())
            {
                if ((++seen & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
                if (hasRule && rules.Count < MaxSitesPerKind && node is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "RuleFor" } } inv)
                {
                    if (++candidates > MaxCandidatesPerFile) continue;
                    if (RuleOf(inv) is { } rule && ValidatorOf(inv) is { } validator)
                        rules.Add(new ValidatorRuleSite(Clean(validator), Clean(rule.Member), rule.Checks, path, Line(inv), layer));
                }
                else if (hasFactory && vos.Count < MaxSitesPerKind && node is TypeDeclarationSyntax td && IsValueObject(td))
                    vos.Add(new ValueObjectSite(Clean(td.Identifier.Text), path, Line(td), layer));
            }
        }
        return new ValidationFacts(rules, vos);
    }

    private static int Line(SyntaxNode n) => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    // RuleFor(x => x.A.B).NotEmpty().MaximumLength(50)… → (B, [MaximumLength]); null, если нет формата или цепочка делегирует VO.
    private static (string Member, IReadOnlyList<string> Checks)? RuleOf(InvocationExpressionSyntax ruleFor)
    {
        if (ruleFor.ArgumentList.Arguments.Count != 1) return null;
        var member = ruleFor.ArgumentList.Arguments[0].Expression switch
        {
            SimpleLambdaExpressionSyntax { Body: var b1 } => MemberPath(b1),
            ParenthesizedLambdaExpressionSyntax { Body: var b2 } => MemberPath(b2),
            _ => null,
        };
        if (member is null) return null;
        var checks = new List<string>();
        SyntaxNode cur = ruleFor;
        for (var i = 0; i < MaxChain && cur.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax next } ma3 && ma3.Expression == cur; i++)
        {
            var name = ma3.Name.Identifier.Text;
            // делегирование VO, а не копия правила: SetValidator/MustBe, а также Must/Custom, в аргументах которых вызывается фабрика VO
            // (Must(Inn.Of)). Обычные бизнес-проверки (Must(IsUnique), MustAsync(NotExist)) делегированием не считаются.
            if (name is "SetValidator" or "MustBe" || Delegation.Contains(name) && next.ArgumentList.ToString().Contains(".Of", StringComparison.Ordinal)
                || Delegation.Contains(name) && FactoryNames.Any(f => next.ArgumentList.ToString().Contains("." + f, StringComparison.Ordinal))) return null;
            if (FormatChecks.Contains(name) && !checks.Contains(name)) checks.Add(name);
            cur = next;
        }
        return checks.Count == 0 ? null : (member, checks);
    }

    // x.Title → Title; x.Customer.Name → Customer.Name (два последних сегмента: Customer.Name и Product.Name — разные поля); x.Title! → Title
    private static string? MemberPath(CSharpSyntaxNode body)
    {
        if (body is PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } bang) body = bang.Operand;
        if (body is not MemberAccessExpressionSyntax ma) return null;
        return ma.Expression is MemberAccessExpressionSyntax outer ? outer.Name.Identifier.Text + "." + ma.Name.Identifier.Text : ma.Name.Identifier.Text;
    }

    private static string? ValidatorOf(SyntaxNode at)
    {
        var td = at.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (td?.BaseList is null) return null;
        return td.BaseList.Types.Any(b => b.Type is GenericNameSyntax { Identifier.Text: "AbstractValidator" }
                                       || b.Type is QualifiedNameSyntax { Right: GenericNameSyntax { Identifier.Text: "AbstractValidator" } })
            ? td.Identifier.Text : null;
    }

    // Value Object: не static/abstract тип с публичной статической фабрикой от строки, которая возвращает САМ тип (T, T?, Result<T,…>).
    // Сущности с Create(string name) и хелперы с Parse под это не подходят по возвращаемому типу не всегда — это эвристика.
    private static bool IsValueObject(TypeDeclarationSyntax td) =>
        !td.Modifiers.Any(x => x.IsKind(SyntaxKind.StaticKeyword) || x.IsKind(SyntaxKind.AbstractKeyword))
        && td.Members.OfType<MethodDeclarationSyntax>().Any(m =>
            FactoryNames.Contains(m.Identifier.Text) && m.Modifiers.Any(x => x.IsKind(SyntaxKind.StaticKeyword))
            && m.ParameterList.Parameters.Count >= 1 && IsString(m.ParameterList.Parameters[0].Type)
            && m.ParameterList.Parameters.Skip(1).All(p => p.Default is not null)
            && ReturnsSelf(m.ReturnType, td.Identifier.Text));

    private static bool IsString(TypeSyntax? t) =>
        t is NullableTypeSyntax n ? IsString(n.ElementType) : t is PredefinedTypeSyntax pt && pt.Keyword.IsKind(SyntaxKind.StringKeyword);

    private static bool ReturnsSelf(TypeSyntax t, string self) => t switch
    {
        IdentifierNameSyntax i => i.Identifier.Text == self,
        NullableTypeSyntax n => ReturnsSelf(n.ElementType, self),
        GenericNameSyntax g => g.TypeArgumentList.Arguments.Count > 0 && ReturnsSelf(g.TypeArgumentList.Arguments[0], self),   // Result<T, Error>
        QualifiedNameSyntax q => ReturnsSelf(q.Right, self),
        _ => false,
    };
}
