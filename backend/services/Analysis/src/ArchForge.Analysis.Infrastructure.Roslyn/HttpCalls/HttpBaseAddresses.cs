using ArchForge.Analysis.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpSyntax;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpConfigRefs;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Базовые адреса (срез 2): литеральный/конфигурационный BaseAddress и его разрешение для вызова по ближайшей области.
internal static class HttpBaseAddresses
{
    // Базовый адрес клиента: литерал (Uri) либо ссылка на конфигурацию — прямой ключ («Payments:Url») или свойство класса options
    // (тип + имя свойства; секцию определяем позже, когда собраны все классы options). Значения конфигурации здесь НЕ читаем.
    internal readonly record struct ClientBase(Uri? Uri, string? Key, string? OptionsType, string? Prop);

    internal readonly record struct BaseAddr(ClientBase Base, SyntaxNode Node);

    // Враждебный/сгенерированный файл с тысячами BaseAddress сделал бы разрешение квадратичным (вызовы × адреса × глубина).
    // Сверх лимита относительные пути в файле не разрешаем: столько разных клиентов в одном файле — это уже не «один понятный адрес».
    internal const int MaxBaseAddressesPerFile = 64;

    // Все литеральные присваивания BaseAddress: `x.BaseAddress = new Uri("http://…")` и `BaseAddress = new Uri("…")` в инициализаторе.
    internal static List<BaseAddr>? CollectBaseAddresses(SyntaxNode root)
    {
        var list = new List<BaseAddr>();
        var optionLocals = new Dictionary<SyntaxNode, Dictionary<string, string>>();   // область → переменная → тип options (считаем один раз на область)
        foreach (var asg in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            // `x.BaseAddress = …` или `BaseAddress = …` в инициализаторе объекта; голое присваивание (поле/свойство своего класса) не берём
            var left = asg.Left switch
            {
                MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
                IdentifierNameSyntax i when asg.Parent is InitializerExpressionSyntax => i.Identifier.Text,
                _ => null,
            };
            if (left != "BaseAddress") continue;
            if (BaseOf(asg.Right, optionLocals) is { } cb) list.Add(new BaseAddr(cb, asg));
            if (list.Count > MaxBaseAddressesPerFile) return null;   // слишком много — не гадаем вовсе
        }
        return list;
    }

    // new Uri("http://…") / new("http://…") → абсолютный http(s)-адрес; иначе null (не литерал, другая схема).
    internal static Uri? UriLiteral(ExpressionSyntax e)
    {
        ArgumentListSyntax? args = null;
        switch (e)
        {
            case ObjectCreationExpressionSyntax oc when oc.Type is IdentifierNameSyntax { Identifier.Text: "Uri" }
                                                       || oc.Type is QualifiedNameSyntax { Right.Identifier.Text: "Uri" }:
                args = oc.ArgumentList; break;
            case ImplicitObjectCreationExpressionSyntax io: args = io.ArgumentList; break;
        }
        if (args is null || args.Arguments.Count == 0) return null;
        if (args.Arguments[0].Expression is not LiteralExpressionSyntax lit || !lit.IsKind(SyntaxKind.StringLiteralExpression)) return null;
        return Uri.TryCreate(lit.Token.ValueText, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? uri : null;
    }

    // Правая часть `BaseAddress = …`: литерал Uri или ссылка на конфигурацию внутри `new Uri(<ссылка>)`; иное — null (не знаем).
    internal static ClientBase? BaseOf(ExpressionSyntax rhs, Dictionary<SyntaxNode, Dictionary<string, string>> optionLocals)
    {
        if (UriLiteral(rhs) is { } u) return new ClientBase(u, null, null, null);
        ArgumentListSyntax? args = rhs switch
        {
            ObjectCreationExpressionSyntax oc when oc.Type is IdentifierNameSyntax { Identifier.Text: "Uri" } || oc.Type is QualifiedNameSyntax { Right.Identifier.Text: "Uri" } => oc.ArgumentList,
            ImplicitObjectCreationExpressionSyntax io => io.ArgumentList,
            _ => null,
        };
        if (args is null || args.Arguments.Count == 0) return null;
        return ConfigRef(StripWrappers(args.Arguments[0].Expression), rhs, optionLocals);
    }

    // Базовый адрес для вызова: ближайшая область (член → тип), где он ровно один. Два разных — null (не гадаем).
    // На уровень файла выходим только для кода верхнего уровня (Program.cs без класса): иначе базовый адрес соседнего
    // класса в том же файле чужой клиент не описывает.
    internal static (ClientBase? Base, bool Ambiguous) ResolveBase(SyntaxNode call, IReadOnlyList<BaseAddr> bases)
    {
        if (bases.Count == 0) return (null, false);
        foreach (var scope in call.Ancestors().Where(a => a is MemberDeclarationSyntax or TypeDeclarationSyntax))
        {
            var inScope = bases.Where(b => scope.Span.Contains(b.Node.Span)).Select(b => b.Base).Distinct().ToList();
            if (inScope.Count == 1) return (inScope[0], false);
            if (inScope.Count > 1) return (null, true);        // два разных адреса — неоднозначно, реестр тоже не подставляем
            if (scope is TypeDeclarationSyntax) return (null, false);   // дальше внешнего типа не идём: соседний/вложенный класс — чужой клиент
        }
        // код верхнего уровня: только присваивания вне любого типа (базовый адрес вспомогательного класса сюда не относится)
        var top = bases.Where(b => !b.Node.Ancestors().Any(a => a is TypeDeclarationSyntax)).Select(b => b.Base).Distinct().ToList();
        return top.Count == 1 ? (top[0], false) : (null, top.Count > 1);
    }
}
