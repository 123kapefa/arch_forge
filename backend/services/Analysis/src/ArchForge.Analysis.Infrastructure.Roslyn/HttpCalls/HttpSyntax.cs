using ArchForge.Analysis.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpSyntax;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpClientCalls;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpWrapperCalls;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpBaseAddresses;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpConfigRefs;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpConfigSections;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpClientRegistry;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpRegisteredClients;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpCallEmitter;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpRefitCalls;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Общие синтаксические помощники распознавания HTTP-вызовов (имена вызовов, строковые литералы, обёртки выражений).
internal static class HttpSyntax
{
    // Правый идентификатор выражения-квалификатора: HttpMethod (using) или …HttpMethod (FQN).
    internal static string? RightName(ExpressionSyntax e) => e switch
    {
        IdentifierNameSyntax i => i.Identifier.Text,
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        _ => null,
    };

    // `x!`, `(x)`, `x ?? "…"` → x
    internal static ExpressionSyntax StripWrappers(ExpressionSyntax e)
    {
        while (true)
            switch (e)
            {
                case PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } p: e = p.Operand; break;
                case ParenthesizedExpressionSyntax par: e = par.Expression; break;
                case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression } b: e = b.Left; break;
                default: return e;
            }
    }

    internal static string? StringLit(ExpressionSyntax? e) =>
        e is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression) ? lit.Token.ValueText : null;

    internal static string? CalleeName(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        IdentifierNameSyntax i => i.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        _ => null,
    };

    // Простые имена типов из AddHttpClient<TI, TImpl>(…); null — не дженерик.
    internal static List<string>? CalleeGenericArgs(InvocationExpressionSyntax inv)
    {
        var name = inv.Expression switch
        {
            MemberAccessExpressionSyntax { Name: GenericNameSyntax g } => g,
            GenericNameSyntax g => g,
            _ => null,
        };
        if (name is null) return null;
        var list = new List<string>();
        foreach (var a in name.TypeArgumentList.Arguments)
            list.Add(a switch
            {
                IdentifierNameSyntax i => i.Identifier.Text,
                QualifiedNameSyntax q => q.Right.Identifier.Text,
                GenericNameSyntax gn => gn.Identifier.Text,
                _ => "",
            });
        list.RemoveAll(string.IsNullOrEmpty);
        return list;
    }

    internal static ExpressionSyntax Unwrap(ExpressionSyntax e) => e is AwaitExpressionSyntax a ? a.Expression : e;

    internal static string UnescapeBraces(string path) => path.Replace("%7B", "{", StringComparison.OrdinalIgnoreCase).Replace("%7D", "}", StringComparison.OrdinalIgnoreCase);
}
