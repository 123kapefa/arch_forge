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

// Клиенты из DI (срез 3а): типизированные, именованные и Refit-клиенты → их BaseAddress; сбор регистраций по файлу.
internal static class HttpClientRegistry
{
    // ── клиенты из DI (срез 3а) ──

    // Типизированные (AddHttpClient<T>, AddHttpClient<TI,TImpl>) и именованные (AddHttpClient("имя")) клиенты → их ЛИТЕРАЛЬНЫЙ BaseAddress.
    // Адрес — литерал или ссылка на конфигурацию (срез 3б). Одно имя с разными адресами — неоднозначно.
    internal sealed class ClientRegistry
    {
        public readonly Dictionary<string, HashSet<ClientBase>> Typed = new(StringComparer.Ordinal);
        public readonly Dictionary<string, HashSet<ClientBase>> Named = new(StringComparer.Ordinal);
        public readonly Dictionary<string, HashSet<ClientBase>> Refit = new(StringComparer.Ordinal);   // интерфейс Refit → адрес (срез 4)
        public int Count;
        public static ClientBase? Single(Dictionary<string, HashSet<ClientBase>> map, string key) =>
            map.TryGetValue(key, out var set) && set.Count == 1 ? set.First() : null;
        public static void Add(Dictionary<string, HashSet<ClientBase>> map, string key, ClientBase b)
        {
            if (!map.TryGetValue(key, out var set)) map[key] = set = [];
            set.Add(b);
        }
    }

    // Защита от враждебных/сгенерированных проектов: больше регистраций — реестр не используем (честнее, чем гадать).
    internal const int MaxClientRegistrations = 512;

    internal static void CollectRegistrations(SyntaxNode root, ClientRegistry reg, CancellationToken ct)
    {
        List<BaseAddr>? fileBases = null;
        var optionLocals = new Dictionary<SyntaxNode, Dictionary<string, string>>();   // один кэш на файл (а не новый на каждую регистрацию — квадрат)
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            ct.ThrowIfCancellationRequested();
            if (reg.Count > MaxClientRegistrations) return;
            var callee = CalleeName(inv);
            if (callee is "For" && inv.Expression is MemberAccessExpressionSyntax { Expression: var rsq } && RightName(rsq) == "RestService" && CalleeGenericArgs(inv) is { Count: 1 } rt)
            {
                // RestService.For<T>(адрес | HttpClient { BaseAddress = … } | переменная такого клиента): адрес — первый аргумент
                if (inv.ArgumentList.Arguments.Count == 0) continue;
                var a0 = StripWrappers(inv.ArgumentList.Arguments[0].Expression);
                ClientBase? cbR = StringLit(a0) is { } lit0 && Uri.TryCreate(lit0, UriKind.Absolute, out var ru) && (ru.Scheme == Uri.UriSchemeHttp || ru.Scheme == Uri.UriSchemeHttps)
                    ? new ClientBase(ru, null, null, null)
                    : ConfigRef(a0, inv, optionLocals);
                if (cbR is null)
                {
                    fileBases ??= CollectBaseAddresses(root);
                    // `new HttpClient { BaseAddress = … }` прямо в аргументе или в переменной, объявленной в том же члене
                    var span = a0.Span;
                    if (a0 is IdentifierNameSyntax rid && inv.Ancestors().FirstOrDefault(a => a is MemberDeclarationSyntax) is { } owner
                        && owner.DescendantNodes().OfType<VariableDeclaratorSyntax>().FirstOrDefault(d => d.Identifier.Text == rid.Identifier.Text && d.Initializer is not null) is { } decl)
                        span = decl.Span;
                    var inside = fileBases?.Where(b => span.Contains(b.Node.Span)).Select(b => b.Base).Distinct().ToList();
                    if (inside is { Count: 1 }) cbR = inside[0];
                }
                if (cbR is { } found) { reg.Count++; ClientRegistry.Add(reg.Refit, rt[0], found); }   // считаем только регистрации с адресом: вызовы For без адреса лимит не съедают
                continue;
            }
            if (callee is not ("AddHttpClient" or "AddRefitClient")) continue;
            reg.Count++;
            fileBases ??= CollectBaseAddresses(root);          // один обход файла (а не по лямбде на каждую регистрацию — вложенные давали бы квадрат)
            if (fileBases is null) return;                       // слишком много адресов в файле — не гадаем

            var uris = new HashSet<ClientBase>();
            var node = (SyntaxNode)inv;
            for (var depth = 0; depth < 12; depth++)       // цепочка: AddHttpClient<T>().ConfigureHttpClient(...).AddStandardResilienceHandler()
            {
                if (node is InvocationExpressionSyntax cur && (depth == 0 ? CalleeName(cur) is "AddHttpClient" or "AddRefitClient" : CalleeName(cur) == "ConfigureHttpClient"))
                    foreach (var arg in cur.ArgumentList.Arguments)
                        if (arg.Expression is LambdaExpressionSyntax lam)
                            foreach (var b in fileBases) if (lam.Span.Contains(b.Node.Span)) uris.Add(b.Base);
                if (node.Parent is MemberAccessExpressionSyntax pm && pm.Expression == node && pm.Parent is InvocationExpressionSyntax up) node = up;
                else break;
            }
            if (uris.Count != 1) continue;                 // нет адреса (литерал или ссылка на конфигурацию) или их несколько в одной регистрации
            var uri = uris.First();

            if (callee == "AddRefitClient")
            {
                if (CalleeGenericArgs(inv) is { Count: 1 } refitTypes) ClientRegistry.Add(reg.Refit, refitTypes[0], uri);
                continue;
            }
            // Типизированный клиент: HttpClient внедряется в РЕАЛИЗАЦИЮ — последний аргумент (AddHttpClient<TI, TImpl> → TImpl).
            if (CalleeGenericArgs(inv) is { Count: > 0 } types) ClientRegistry.Add(reg.Typed, types[^1], uri);
            if (inv.ArgumentList.Arguments.Count > 0 && inv.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax lit
                && lit.IsKind(SyntaxKind.StringLiteralExpression))
                ClientRegistry.Add(reg.Named, lit.Token.ValueText, uri);
        }
    }
}
