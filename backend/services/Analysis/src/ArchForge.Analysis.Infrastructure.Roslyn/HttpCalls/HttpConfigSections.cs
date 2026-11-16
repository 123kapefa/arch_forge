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

// Секции конфигурации классов options: константы SectionName, регистрации Configure<T>/Bind, вывод секции для ключа.
internal static class HttpConfigSections
{
    // Секция конфигурации класса options: константа SectionName/Section/… в классе; иначе имя типа без суффикса Options/Settings/Config.
    // Две разные константы у одноимённых классов («») — неоднозначно, не гадаем.
    internal static string? SectionOf(string optionsType, IReadOnlyDictionary<string, string> sections, out bool inferredFromName)
    {
        inferredFromName = false;
        if (sections.TryGetValue(optionsType, out var s)) return s.Length == 0 ? null : s;
        inferredFromName = true;       // секцию реально задаёт Configure<T>(GetSection("…")), а имя класса — только догадка: помечаем в Evidence
        foreach (var suffix in new[] { "Options", "Settings", "Config", "Configuration" })
            if (optionsType.Length > suffix.Length && optionsType.EndsWith(suffix, StringComparison.Ordinal)) return optionsType[..^suffix.Length];
        return optionsType;
    }

    internal static readonly HashSet<string> SectionConstNames = new(StringComparer.Ordinal)
        { "SectionName", "Section", "ConfigSection", "ConfigurationSection", "SettingsSection", "ConfigKey" };

    internal static void AddRegistered(Dictionary<string, string> reg, string type, string section)
    {
        if (reg.Count > 4000) return;
        reg[type] = reg.TryGetValue(type, out var old) && old != section ? "" : section;   // два разных раздела под один класс — неоднозначно
    }

    // Регистрации options: `Configure<T>(config.GetSection(X))`, `AddOptions<T>().Bind(config.GetSection(X))`, `AddOptions<T>().BindConfiguration(X)`;
    // X — строка, `nameof(Type)` или `Owner.Const` (константу разрешает вызывающий). Это реальная секция, в отличие от догадки по имени класса.
    internal static void CollectOptionRegistrations(SyntaxNode root, Dictionary<string, string> reg, List<(string Type, string Owner)> pendingConst)
    {
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            string? type = null;
            ExpressionSyntax? sectionArg = null;
            var name = CalleeName(inv);
            if (name == "Configure" && CalleeGenericArgs(inv) is { Count: 1 } g && inv.ArgumentList.Arguments.Count == 1
                && inv.ArgumentList.Arguments[0].Expression is InvocationExpressionSyntax gs && CalleeName(gs) == "GetSection" && gs.ArgumentList.Arguments.Count == 1)
            { type = g[0]; sectionArg = gs.ArgumentList.Arguments[0].Expression; }
            else if (name is "Bind" or "BindConfiguration" && inv.Expression is MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax opt }
                && CalleeName(opt) == "AddOptions" && CalleeGenericArgs(opt) is { Count: 1 } og && inv.ArgumentList.Arguments.Count >= 1)
            {
                type = og[0];
                var a0 = inv.ArgumentList.Arguments[0].Expression;
                sectionArg = name == "Bind" ? (a0 is InvocationExpressionSyntax bgs && CalleeName(bgs) == "GetSection" && bgs.ArgumentList.Arguments.Count == 1 ? bgs.ArgumentList.Arguments[0].Expression : null) : a0;
            }
            if (type is null || sectionArg is null) continue;

            if (StringLit(sectionArg) is { } lit) AddRegistered(reg, type, lit);
            else if (sectionArg is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "nameof" } } nof
                && nof.ArgumentList.Arguments.Count == 1)
            {
                var target = nof.ArgumentList.Arguments[0].Expression;
                var n = target switch { IdentifierNameSyntax i => i.Identifier.Text, MemberAccessExpressionSyntax m => m.Name.Identifier.Text, _ => null };
                if (n is not null) AddRegistered(reg, type, n);
            }
            else if (sectionArg is MemberAccessExpressionSyntax { Expression: var q } && RightName(q) is { } owner) pendingConst.Add((type, owner));
        }
    }

    // Классы с const string SectionName = "…": тип → секция (для options-паттерна). Лимит записей — защита от мусора.
    internal static void CollectSections(SyntaxNode root, Dictionary<string, string> sections)
    {
        foreach (var td in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (sections.Count > 4000) return;
            foreach (var f in td.Members.OfType<FieldDeclarationSyntax>())
            {
                if (!f.Modifiers.Any(SyntaxKind.ConstKeyword)) continue;
                foreach (var v in f.Declaration.Variables)
                {
                    if (!SectionConstNames.Contains(v.Identifier.Text)) continue;
                    var name = td.Identifier.Text;
                    // константа секции есть, но не строковый литерал (nameof(X), выражение) — прочитать нельзя: НЕ подменяем догадкой по имени
                    var lit = StringLit(v.Initializer?.Value);
                    sections[name] = lit is null || (sections.TryGetValue(name, out var old) && old != lit) ? "" : lit;
                }
            }
        }
    }
}
