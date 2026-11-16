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

// Обёртки базового HTTP-клиента внутри класса зарегистрированного типизированного клиента.
internal static class HttpWrapperCalls
{
    // Методы-обёртки базового HTTP-клиента: имя → метод HTTP. URL — первый аргумент, как у самого HttpClient.
    internal static readonly IReadOnlyDictionary<string, string> WrapperVerbs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GetAsync"] = "GET", ["GetOptionalAsync"] = "GET", ["GetFromJsonAsync"] = "GET", ["GetStringAsync"] = "GET",
        ["PostAsync"] = "POST", ["PostAsJsonAsync"] = "POST", ["PutAsync"] = "PUT", ["PutAsJsonAsync"] = "PUT",
        ["PatchAsync"] = "PATCH", ["DeleteAsync"] = "DELETE",
    };

    // Метод `name`, объявленный в самом классе, должен быть HTTP-обёрткой: его тело передаёт первый параметр (URL) в вызов HttpClient
    // (в том числе внутри лямбды `() => HttpClient.GetAsync(url, ct)`). Не объявлен в классе — унаследован от базового клиента (тело не видно): принимаем.
    internal static bool IsHttpWrapper(TypeDeclarationSyntax td, string name, FileCtx ctx)
    {
        if (ctx.Wrappers.TryGetValue((td, name), out var known)) return known;
        var own = td.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text == name).ToList();
        var ok = own.Count == 0 || own.Any(m =>
        {
            var p = m.ParameterList.Parameters.FirstOrDefault()?.Identifier.Text;
            if (p is null) return false;
            return m.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => i.Expression is MemberAccessExpressionSyntax ma && (ClientVerbs.ContainsKey(ma.Name.Identifier.Text) || ma.Name.Identifier.Text == "SendAsync"))
                .Any(i => i.ArgumentList.DescendantNodes().OfType<IdentifierNameSyntax>().Any(id => id.Identifier.Text == p));
        });
        ctx.Wrappers[(td, name)] = ok;
        return ok;
    }

    // Вызов без получателя (`GetAsync<T>(…)`, `GetAsync(…)`) или с `this.`/`base.`: имя метода-обёртки; иначе null.
    internal static string? WrapperCall(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        IdentifierNameSyntax i => i.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax or BaseExpressionSyntax } m => m.Name.Identifier.Text,
        _ => null,
    };
}
