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

// Разрешение адреса вызова по реестру DI: именованный клиент (CreateClient) или внедрённый HttpClient типизированного клиента.
internal static class HttpRegisteredClients
{
    // Адрес для вызова из реестра: (1) вызов лежит в классе типизированного клиента; (2) получатель — именованный клиент
    // (`f.CreateClient("имя").GetAsync(…)` или переменная, полученная из CreateClient("имя") в том же члене).
    internal sealed class FileCtx
    {
        public readonly Dictionary<SyntaxNode, Dictionary<string, string>> Locals = [];     // член → переменная → имя клиента (по CreateClient)
        public readonly Dictionary<SyntaxNode, HashSet<string>> InjectedHttp = [];           // тип → имена членов/параметров типа HttpClient без `new`
        public readonly Dictionary<(SyntaxNode, string), bool> Wrappers = [];                // (тип, имя метода) → это HTTP-обёртка
    }

    internal static ClientBase? ResolveRegistered(InvocationExpressionSyntax inv, ExpressionSyntax receiver, ClientRegistry reg, FileCtx ctx)
    {
        // 1. Явный именованный клиент важнее класса, в котором вызов лежит: `CreateClient("x")` внутри класса T — это клиент «x», не T.
        string? clientName = null;
        if (receiver is InvocationExpressionSyntax ci) clientName = CreateClientName(ci);
        else if (receiver is IdentifierNameSyntax id)
        {
            var scope = inv.Ancestors().FirstOrDefault(a => a is MemberDeclarationSyntax) ?? inv.SyntaxTree.GetRoot();
            if (!ctx.Locals.TryGetValue(scope, out var map)) ctx.Locals[scope] = map = CollectCreateClientLocals(scope);
            if (map.TryGetValue(id.Identifier.Text, out var n)) clientName = n;     // даже если имя не зарегистрировано: это не клиент класса
        }
        if (clientName is not null) return ClientRegistry.Single(reg.Named, clientName);
        if (receiver is InvocationExpressionSyntax) return null;   // результат другого вызова (не CreateClient) — какой это клиент, не знаем

        // 2. Класс типизированного клиента: ближайший объемлющий тип (вложенный чужой класс адреса не наследует);
        //    вызов по полю/параметру/свойству (`_http.GetAsync`), а не по клиенту, полученному из CreateClient выше.
        if (reg.Typed.Count > 0 && inv.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() is { } td
            && ReceiverName(receiver) is { } rn && IsInjectedHttpClient(td, rn, ctx))
            return ClientRegistry.Single(reg.Typed, td.Identifier.Text);
        return null;
    }

    // `_http` / `http` / `this._http` → имя члена; иное выражение — null.
    internal static string? ReceiverName(ExpressionSyntax e) => e switch
    {
        IdentifierNameSyntax i => i.Identifier.Text,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: var n } => n.Identifier.Text,
        _ => null,
    };

    // Имя — это поле/свойство/параметр конструктора (в том числе первичного) типа HttpClient, не созданный через `new`
    // в самом классе: именно такой клиент внедряет DI. `new HttpClient()` внутри класса и чужие клиенты адреса не получают.
    internal static bool IsInjectedHttpClient(TypeDeclarationSyntax td, string name, FileCtx ctx)
    {
        if (!ctx.InjectedHttp.TryGetValue(td, out var names))
        {
            names = new HashSet<string>(StringComparer.Ordinal);
            static bool IsHttp(TypeSyntax? t) => t switch
            {
                IdentifierNameSyntax i => i.Identifier.Text == "HttpClient",
                QualifiedNameSyntax q => q.Right.Identifier.Text == "HttpClient",
                NullableTypeSyntax n => IsHttp(n.ElementType),
                _ => false,
            };
            foreach (var prm in td.ParameterList?.Parameters ?? default) if (IsHttp(prm.Type)) names.Add(prm.Identifier.Text);
            foreach (var m in td.Members)
                switch (m)
                {
                    case FieldDeclarationSyntax f when IsHttp(f.Declaration.Type):
                        foreach (var v in f.Declaration.Variables)
                            if (v.Initializer?.Value is not (ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax)) names.Add(v.Identifier.Text);
                        break;
                    case PropertyDeclarationSyntax pr when IsHttp(pr.Type) && pr.Initializer?.Value is not (ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax):
                        names.Add(pr.Identifier.Text);
                        break;
                    case ConstructorDeclarationSyntax c:
                        foreach (var prm in c.ParameterList.Parameters) if (IsHttp(prm.Type)) names.Add(prm.Identifier.Text);
                        break;
                }
            ctx.InjectedHttp[td] = names;
        }
        return names.Contains(name);
    }

    // `x.CreateClient("имя")` → имя
    internal static string? CreateClientName(InvocationExpressionSyntax ci) =>
        CalleeName(ci) == "CreateClient" && ci.ArgumentList.Arguments.Count > 0
        && ci.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression)
            ? lit.Token.ValueText : null;

    // Переменные члена, полученные из CreateClient("имя"): `var c = f.CreateClient("x")` и `c = f.CreateClient("x")`.
    // Переменная с двумя разными именами в одном члене — не гадаем (ключ убирается).
    internal static Dictionary<string, string> CollectCreateClientLocals(SyntaxNode member)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var conflicted = new HashSet<string>(StringComparer.Ordinal);
        void Put(string v, string n) { if (map.TryGetValue(v, out var old) && old != n) conflicted.Add(v); else map[v] = n; }
        foreach (var node in member.DescendantNodes())
        {
            if (node is VariableDeclaratorSyntax { Initializer.Value: var init } d && Unwrap(init) is InvocationExpressionSyntax i1 && CreateClientName(i1) is { } n1)
                Put(d.Identifier.Text, n1);
            else if (node is AssignmentExpressionSyntax { Left: IdentifierNameSyntax l, Right: var r } && Unwrap(r) is InvocationExpressionSyntax i2 && CreateClientName(i2) is { } n2)
                Put(l.Identifier.Text, n2);
        }
        foreach (var c in conflicted) map.Remove(c);
        return map;
    }
}
