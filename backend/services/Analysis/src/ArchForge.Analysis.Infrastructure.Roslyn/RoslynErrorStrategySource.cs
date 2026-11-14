using ArchForge.Analysis.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Чем сервис сообщает об ожидаемых ошибках (методичка «Стратегия ошибок и нулей», правило 1). Чисто синтаксический разбор.
// Result-возврат: метод с возвращаемым типом `Result` / `Result<…>` (в том числе внутри Task<>/ValueTask<>).
// Бросок ожидаемой ошибки: `throw new <X>Exception(…)` (оператор или выражение), где X содержит NotFound/Conflict/Validation/Forbidden/
// Unauthorized/AlreadyExists/BadRequest/Duplicate. НЕ считаем смесью «мосты» по методичке (вернуть значение нельзя): конструкторы,
// аксессоры свойств, операторы; типы *Behavior/*Middleware/*Handler для исключений/*Converter/*Filter/*Interceptor; слой tests.
public sealed class RoslynErrorStrategySource : IErrorStrategySource
{
    // потолки против враждебного проекта (как у RoslynGuardSource)
    private const int MaxSitesPerKind = 5_000;
    private const int MaxNameLength = 200;
    private const int MaxCandidatesPerFile = 200;     // подъём к владельцу дорог (глубина вложенности): считаем КАНДИДАТОВ, а не добавленные места

    private static readonly string[] ExpectedWords = ["NotFound", "Conflict", "Validation", "Forbidden", "Unauthorized", "AlreadyExists", "BadRequest", "Duplicate"];
    private static readonly string[] BridgeTypeSuffixes = ["Behavior", "Middleware", "ExceptionHandler", "Converter", "Filter", "Interceptor"];

    private static string Clean(string s)
    {
        var t = s.Length > MaxNameLength ? s[..MaxNameLength] + "…" : s;
        return new string(t.Where(c => !(char.IsControl(c) || c is >= '​' and <= '‏' or >= '‪' and <= '‮' or >= '⁦' and <= '⁩')).ToArray());
    }

    public ErrorStrategyFacts Read(string root, CancellationToken ct = default) =>
        RoslynScan.OnBigStack(() => ReadCore(root, ct));   // парсер/биндер Roslyn рекурсивны; StackOverflow не ловится (#167)

    private ErrorStrategyFacts ReadCore(string root, CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) return ErrorStrategyFacts.Empty;
        var results = new List<ErrorSite>();
        var throws = new List<ErrorSite>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);                  // один метод — одна запись (сравниваем методы с методами)
        foreach (var file in RoslynScan.EnumerateCsCapped(root, out _))
        {
            ct.ThrowIfCancellationRequested();
            if (results.Count >= MaxSitesPerKind && throws.Count >= MaxSitesPerKind) break;
            var layer = RoslynScan.Layer(file, root);
            if (layer is "tests" or "Contracts" or "Shared") continue;   // тесты и публичное API библиотек не судим
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }
            var hasResult = text.Contains("Result", StringComparison.Ordinal);
            var hasThrow = text.Contains("Exception", StringComparison.Ordinal);
            if (!hasResult && !hasThrow) continue;                                  // быстрый отсев
            var tree = RoslynScan.TryParseRoot(text, file, ct);
            if (tree is null) continue;   // слишком глубокая вложенность — пропуск (#167)
            var path = file.Replace('\\', '/');
            var seen = 0;
            var candidates = 0;
            foreach (var node in tree.DescendantNodes())
            {
                if ((++seen & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
                if (node is MethodDeclarationSyntax md && hasResult && results.Count < MaxSitesPerKind)
                {
                    // без тела (интерфейс, abstract) и фабрики собственного типа Result — не «возврат Result» в смысле стратегии
                    if (md.Body is null && md.ExpressionBody is null) continue;
                    if (ResultName(md.ReturnType) is { } rn && ContainingType(md) is { } t && !IsResultType(t) && seenKeys.Add("R|" + path + "|" + t.Identifier.Text + "." + md.Identifier.Text))
                        results.Add(new ErrorSite(Clean(t.Identifier.Text), Clean(md.Identifier.Text), Clean(rn), path, Line(md), layer));
                }
                else if (hasThrow && throws.Count < MaxSitesPerKind)
                {
                    var created = node switch
                    {
                        ThrowStatementSyntax { Expression: ObjectCreationExpressionSyntax oc } => oc,
                        ThrowExpressionSyntax { Expression: ObjectCreationExpressionSyntax oc2 } => oc2,
                        _ => null,
                    };
                    if (created is null || ExceptionName(created.Type) is not { } en || !IsExpected(en)) continue;
                    if (++candidates > MaxCandidatesPerFile) continue;
                    if (OwnerOf(node) is not { } owner) continue;                       // мост или нет понятного владельца
                    if (!seenKeys.Add("T|" + path + "|" + owner.Type + "." + owner.Member)) continue;
                    throws.Add(new ErrorSite(Clean(owner.Type), Clean(owner.Member), Clean(en), path, Line(node), layer));
                }
            }
        }
        return new ErrorStrategyFacts(results, throws);
    }

    private static int Line(SyntaxNode n) => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    // Result / Result<…> / Task<Result<…>> / ValueTask<Result>
    private static string? ResultName(TypeSyntax t)
    {
        switch (t)
        {
            case IdentifierNameSyntax { Identifier.Text: "Result" }: return "Result";
            case GenericNameSyntax { Identifier.Text: "Result" }: return "Result<…>";
            case GenericNameSyntax { Identifier.Text: "Task" or "ValueTask", TypeArgumentList.Arguments.Count: 1 } g: return ResultName(g.TypeArgumentList.Arguments[0]);
            case QualifiedNameSyntax q: return ResultName(q.Right);
            default: return null;
        }
    }

    private static string? ExceptionName(TypeSyntax t) => t switch
    {
        IdentifierNameSyntax i => i.Identifier.Text,
        QualifiedNameSyntax q => q.Right.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        _ => null,
    };

    private static bool IsExpected(string name) =>
        name.EndsWith("Exception", StringComparison.Ordinal) && ExpectedWords.Any(w => name.Contains(w, StringComparison.Ordinal));

    private static bool IsResultType(TypeDeclarationSyntax t) => t.Identifier.Text is "Result" or "Error" or "Errors";

    private static TypeDeclarationSyntax? ContainingType(SyntaxNode n) => n.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();

    // Владелец броска, если это НЕ мост. null — пропустить.
    private static (string Type, string Member)? OwnerOf(SyntaxNode at)
    {
        string? member = null, type = null;
        foreach (var anc in at.Ancestors())
        {
            switch (anc)
            {
                case ConstructorDeclarationSyntax or AccessorDeclarationSyntax or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax or DestructorDeclarationSyntax
                    or PropertyDeclarationSyntax or IndexerDeclarationSyntax:
                    return null;                                                    // вернуть значение нельзя (в т.ч. свойство с телом-выражением)
                case MethodDeclarationSyntax m: member = m.Identifier.Text; break;  // внешний метод важнее вложенной локальной функции/лямбды
                case TypeDeclarationSyntax td:
                    if (IsBridgeType(td.Identifier.Text)) return null;              // мост и во вложенном типе
                    type ??= td.Identifier.Text;
                    break;
            }
        }
        return type is null ? null : (type, member ?? type);
    }

    // Pipeline/middleware/конвертер/фильтр/IExceptionHandler — «мост» методички. Обычный хендлер команды (…Handler) мостом НЕ считается.
    private static bool IsBridgeType(string typeName) =>
        BridgeTypeSuffixes.Any(s => typeName.EndsWith(s, StringComparison.Ordinal));
}
