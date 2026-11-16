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

// Адрес из конфигурации (срез 3б): ссылки IConfiguration / IOptions<T> внутри `new Uri(…)`.
internal static class HttpConfigRefs
{
    // Получатель похож на IConfiguration/IConfigurationSection по точному имени (config, _configuration, cfg, section…).
    internal static bool LooksLikeConfig(ExpressionSyntax? e)
    {
        var name = e switch
        {
            IdentifierNameSyntax i => i.Identifier.Text,
            MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
            _ => null,
        };
        if (name is null) return false;
        name = name.TrimStart('_');
        // точные имена IConfiguration/IConfigurationSection; «appConfigMap», «sectionCache» — не конфигурация
        return name.Equals("config", StringComparison.OrdinalIgnoreCase) || name.Equals("configuration", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cfg", StringComparison.OrdinalIgnoreCase) || name.Equals("section", StringComparison.OrdinalIgnoreCase)
            || name.Equals("configSection", StringComparison.OrdinalIgnoreCase) || name.Equals("configurationSection", StringComparison.OrdinalIgnoreCase);
    }

    // Ссылка на конфигурацию: `config["A:B"]`, `config.GetSection("A")["B"]`, `config.GetValue<string>("A:B")`,
    // `config.GetSection("A:B").Value`, `options.Prop` (options получен из IOptions<T>, в т.ч. `sp.GetRequiredService<IOptions<T>>().Value.Prop`).
    internal static ClientBase? ConfigRef(ExpressionSyntax e, SyntaxNode at, Dictionary<SyntaxNode, Dictionary<string, string>> optionLocals)
    {
        switch (e)
        {
            case ElementAccessExpressionSyntax { ArgumentList.Arguments.Count: 1 } ea when StringLit(ea.ArgumentList.Arguments[0].Expression) is { } k:
                if (ea.Expression is InvocationExpressionSyntax gs && CalleeName(gs) == "GetSection" && gs.ArgumentList.Arguments.Count == 1
                    && StringLit(gs.ArgumentList.Arguments[0].Expression) is { } sec)
                    return new ClientBase(null, $"{sec}:{k}", null, null);
                return LooksLikeConfig(ea.Expression) ? new ClientBase(null, k, null, null) : null;
            case InvocationExpressionSyntax inv when CalleeName(inv) == "GetValue" && inv.ArgumentList.Arguments.Count > 0
                && inv.Expression is MemberAccessExpressionSyntax gm && LooksLikeConfig(gm.Expression)
                && StringLit(inv.ArgumentList.Arguments[0].Expression) is { } gv:
                return new ClientBase(null, gv, null, null);
            case MemberAccessExpressionSyntax { Name.Identifier.Text: "Value", Expression: InvocationExpressionSyntax gs2 }
                when CalleeName(gs2) == "GetSection" && gs2.ArgumentList.Arguments.Count == 1 && StringLit(gs2.ArgumentList.Arguments[0].Expression) is { } sec2:
                return new ClientBase(null, sec2, null, null);
            case MemberAccessExpressionSyntax ma:
            {
                var t = OptionsTypeOf(ma.Expression, at, optionLocals);
                return t is null ? null : new ClientBase(null, null, t, ma.Name.Identifier.Text);
            }
            default: return null;
        }
    }

    // Тип T из `IOptions<T>` в выражении `x` (само выражение или инициализатор локальной переменной x рядом с присваиванием).
    // Строго `<…IOptions<T>…>.Value` (или `.CurrentValue`): `….Value.Inner` — другой уровень вложенности, секция была бы неверной.
    internal static string? OptionsValueType(ExpressionSyntax e) =>
        StripWrappers(e) is MemberAccessExpressionSyntax { Name.Identifier.Text: "Value" or "CurrentValue" } m ? OptionsTypeIn(m.Expression) : null;

    internal static string? OptionsTypeOf(ExpressionSyntax holder, SyntaxNode at, Dictionary<SyntaxNode, Dictionary<string, string>> optionLocals)
    {
        if (OptionsValueType(holder) is { } direct) return direct;
        if (holder is not IdentifierNameSyntax id) return null;
        // ближайшие объемлющие области (лямбда/член), не больше 6: вложенность в сотни лямбд не должна раздувать поиск;
        // переменные области собираются один раз (кэш), а не заново на каждое присваивание
        var scope = at.Ancestors().FirstOrDefault(a => a is LambdaExpressionSyntax or MemberDeclarationSyntax);
        for (var depth = 0; scope is not null && depth < 6; depth++)
        {
            if (!optionLocals.TryGetValue(scope, out var map))
            {
                optionLocals[scope] = map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var d in scope.DescendantNodes().OfType<VariableDeclaratorSyntax>())
                    if (d.Initializer is { } init && OptionsValueType(init.Value) is { } t) map.TryAdd(d.Identifier.Text, t);
            }
            if (map.TryGetValue(id.Identifier.Text, out var found)) return found;
            scope = scope.Ancestors().FirstOrDefault(a => a is LambdaExpressionSyntax or MemberDeclarationSyntax);
        }
        return null;
    }

    internal static string? OptionsTypeIn(SyntaxNode n)
    {
        foreach (var g in n.DescendantNodesAndSelf().OfType<GenericNameSyntax>())
            if (g.Identifier.Text is "IOptions" or "IOptionsSnapshot" or "IOptionsMonitor" && g.TypeArgumentList.Arguments.Count == 1)
                return g.TypeArgumentList.Arguments[0] switch
                {
                    IdentifierNameSyntax i => i.Identifier.Text,
                    QualifiedNameSyntax q => q.Right.Identifier.Text,
                    _ => null,
                };
        return null;
    }
}
