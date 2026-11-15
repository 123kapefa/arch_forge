using ArchForge.Analysis.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// События в агрегате (методичка «Агрегаты, VO и доменные события»). Чисто синтаксический, консервативный разбор слоя Domain.
// Агрегат — класс/record, который (а) сам где-то поднимает событие (RaiseDomainEvent/AddDomainEvent/RaiseEvent/… или _domainEvents.Add) либо
// (б) наследует *AggregateRoot*. Мутация — публичный не статический метод, который сам присваивает свойству с закрытым сеттером или закрытому полю
// (либо вызывает Add/Remove/Clear на нём). Метод в порядке, если поднимает событие сам или вызывает свой метод, который поднимает.
// Время изменения (UpdatedAt…) проверяем, только если тип сам где-то его ставит (иначе его ставит interceptor — это правило aggregate-time).
// Не судим: partial-типы (метод может лежать в другой части), конструкторы и статические фабрики, закрытые методы, Apply*/When* (event sourcing),
// слои кроме Domain. Потолки — как у соседних источников (враждебный проект).
public sealed class RoslynAggregateEventSource : IAggregateEventSource
{
    private const int MaxSites = 5_000;
    private const int MaxSitesPerFile = 200;
    private const int MaxMethodsPerType = 500;
    private const int MaxNodesPerFile = 300_000;
    private const int MaxNameLength = 200;

    private static readonly HashSet<string> RaiseNames = new(StringComparer.Ordinal) { "RaiseDomainEvent", "RaiseEvent", "Raise", "AddDomainEvent", "AddEvent", "RegisterDomainEvent", "QueueDomainEvent" };
    private static readonly HashSet<string> CollectionMutators = new(StringComparer.Ordinal) { "Add", "AddRange", "Remove", "RemoveAll", "RemoveAt", "Insert", "Clear" };
    private static readonly string[] TimestampNames = ["UpdatedAt", "ModifiedAt", "LastModifiedAt", "LastUpdatedAt", "UpdatedOn", "ModifiedOn"];

    private static string Clean(string s)
    {
        var t = s.Length > MaxNameLength ? s[..MaxNameLength] + "…" : s;
        return new string(t.Where(c => !(char.IsControl(c) || c is >= '​' and <= '‏' or >= '‪' and <= '‮' or >= '⁦' and <= '⁩')).ToArray());
    }

    public AggregateEventFacts Read(string root, CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) return AggregateEventFacts.Empty;
        var sites = new List<EventSite>();
        foreach (var file in RoslynScan.EnumerateCsCapped(root, out _))
        {
            ct.ThrowIfCancellationRequested();
            if (sites.Count >= MaxSites) break;
            if (RoslynScan.Layer(file, root) != "Domain") continue;
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }
            if (!text.Contains("set", StringComparison.Ordinal) && !text.Contains('_')) continue;   // нечему мутировать
            var tree = CSharpSyntaxTree.ParseText(text, cancellationToken: ct).GetRoot(ct);
            var path = file.Replace('\\', '/');
            var nodes = 0;
            var inFile = 0;
            foreach (var td in tree.DescendantNodes(n => n is not (MethodDeclarationSyntax or ConstructorDeclarationSyntax)).OfType<TypeDeclarationSyntax>())
            {
                if (td is not (ClassDeclarationSyntax or RecordDeclarationSyntax { ClassOrStructKeyword.RawKind: not (int)SyntaxKind.StructKeyword })) continue;
                if (td.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword) || m.IsKind(SyntaxKind.StaticKeyword))) continue;
                foreach (var s in Analyze(td, path, ct, ref nodes))
                {
                    if (sites.Count >= MaxSites || ++inFile > MaxSitesPerFile) break;
                    sites.Add(s);
                }
                if (nodes > MaxNodesPerFile) break;
            }
        }
        return new AggregateEventFacts(sites);
    }

    private static List<EventSite> Analyze(TypeDeclarationSyntax td, string path, CancellationToken ct, ref int nodes)
    {
        var result = new List<EventSite>();
        // состояние типа: свойства с закрытым сеттером и закрытые поля; время изменения
        var state = new HashSet<string>(StringComparer.Ordinal);
        string? timestamp = null;
        foreach (var m in td.Members)
        {
            if (m is PropertyDeclarationSyntax p && !p.Modifiers.Any(x => x.IsKind(SyntaxKind.StaticKeyword)) && p.AccessorList is { } al
                && al.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) is { } set
                && set.Modifiers.Any(x => x.IsKind(SyntaxKind.PrivateKeyword) || x.IsKind(SyntaxKind.ProtectedKeyword)))
            {
                state.Add(p.Identifier.Text);
                if (timestamp is null && TimestampNames.Contains(p.Identifier.Text, StringComparer.Ordinal)) timestamp = p.Identifier.Text;
            }
            else if (m is FieldDeclarationSyntax f && !f.Modifiers.Any(x => x.IsKind(SyntaxKind.StaticKeyword) || x.IsKind(SyntaxKind.ConstKeyword) || x.IsKind(SyntaxKind.PublicKeyword)))
                foreach (var v in f.Declaration.Variables) state.Add(v.Identifier.Text);
        }
        var methods = td.Members.OfType<MethodDeclarationSyntax>().Take(MaxMethodsPerType).ToList();
        if (methods.Count == 0 || state.Count == 0) return result;

        // факты по каждому методу (один проход по телу)
        var info = new List<(MethodDeclarationSyntax M, bool Mutates, bool Raises, bool SetsTs, HashSet<string> Calls)>();
        foreach (var m in methods)
        {
            SyntaxNode? body = (SyntaxNode?)m.Body ?? m.ExpressionBody;
            if (body is null) { info.Add((m, false, false, false, [])); continue; }
            var locals = new HashSet<string>(m.ParameterList.Parameters.Select(x => x.Identifier.Text), StringComparer.Ordinal);
            bool mutates = false, raises = false, setsTs = false;
            var calls = new HashSet<string>(StringComparer.Ordinal);
            var all = body.DescendantNodes().ToList();       // локальные имена нужны до разбора присваиваний
            foreach (var n in all)
            {
                if (n is VariableDeclaratorSyntax vd) locals.Add(vd.Identifier.Text);
                else if (n is ParameterSyntax ps) locals.Add(ps.Identifier.Text);
            }
            foreach (var n in all)
            {
                if ((++nodes & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
                switch (n)
                {
                    case AssignmentExpressionSyntax a when StateTarget(a.Left, state, locals) is { } t:
                        if (t == timestamp) setsTs = true; else mutates = true;
                        break;
                    case PostfixUnaryExpressionSyntax { Operand: var po } when StateTarget(po, state, locals) is { } t2 && t2 != timestamp:
                        mutates = true; break;
                    case PrefixUnaryExpressionSyntax { Operand: var pr } when StateTarget(pr, state, locals) is { } t3 && t3 != timestamp:
                        mutates = true; break;
                    case InvocationExpressionSyntax inv:
                        var name = CalleeName(inv, out var recv);
                        if (name is null) break;
                        if (RaiseNames.Contains(name) && (recv is null || recv is ThisExpressionSyntax or BaseExpressionSyntax)) raises = true;
                        else if (name == "Add" && recv is not null && LastIdentifier(recv) is { } rn && rn.Contains("Event", StringComparison.OrdinalIgnoreCase)) raises = true;   // _domainEvents.Add(new …)
                        else if (CollectionMutators.Contains(name) && recv is not null && StateTarget(recv, state, locals) is { }) mutates = true;
                        else if (recv is null or ThisExpressionSyntax) calls.Add(name);
                        break;
                }
            }
            info.Add((m, mutates, raises, setsTs, calls));
        }
        // методы типа, которые сами поднимают событие / ставят время (вызов такого метода считается за него)
        var raisers = info.Where(x => x.Raises).Select(x => x.M.Identifier.Text).ToHashSet(StringComparer.Ordinal);
        var stampers = info.Where(x => x.SetsTs).Select(x => x.M.Identifier.Text).ToHashSet(StringComparer.Ordinal);
        var isAggregate = raisers.Count > 0 || (td.BaseList?.Types.Any(b => BaseName(b.Type)?.Contains("AggregateRoot", StringComparison.Ordinal) == true) ?? false);
        var tsInUse = timestamp is not null && stampers.Count > 0;
        if (!isAggregate && !tsInUse) return result;

        foreach (var (m, mutates, raises, setsTs, calls) in info)
        {
            if (!mutates) continue;
            if (!m.Modifiers.Any(x => x.IsKind(SyntaxKind.PublicKeyword)) || m.Modifiers.Any(x => x.IsKind(SyntaxKind.StaticKeyword) || x.IsKind(SyntaxKind.AbstractKeyword))) continue;
            var n = m.Identifier.Text;
            if (n.StartsWith("Apply", StringComparison.Ordinal) || n.StartsWith("When", StringComparison.Ordinal)) continue;   // event sourcing: состояние меняет обработчик события
            var gap = EventGap.None;
            if (isAggregate && !raises && !calls.Any(raisers.Contains)) gap |= EventGap.NoEvent;
            if (tsInUse && !setsTs && !calls.Any(stampers.Contains)) gap |= EventGap.NoTimestamp;
            if (gap == EventGap.None) continue;
            result.Add(new EventSite(Clean(td.Identifier.Text), Clean(n), gap, timestamp ?? "UpdatedAt", path, m.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1, "Domain"));
        }
        return result;
    }

    // X / this.X / X[i] / X.Y — имя состояния типа, если оно не затенено параметром или локальной переменной
    private static string? StateTarget(ExpressionSyntax e, HashSet<string> state, HashSet<string> locals)
    {
        while (true)
        {
            switch (e)
            {
                case IdentifierNameSyntax { Identifier.Text: var n }: return state.Contains(n) && !locals.Contains(n) ? n : null;
                case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name.Identifier.Text: var n2 }: return state.Contains(n2) ? n2 : null;
                case ElementAccessExpressionSyntax el: e = el.Expression; continue;
                case ParenthesizedExpressionSyntax pe: e = pe.Expression; continue;
                default: return null;
            }
        }
    }

    private static string? CalleeName(InvocationExpressionSyntax inv, out ExpressionSyntax? recv)
    {
        switch (inv.Expression)
        {
            case IdentifierNameSyntax i: recv = null; return i.Identifier.Text;
            case MemberAccessExpressionSyntax m: recv = m.Expression; return m.Name.Identifier.Text;
            default: recv = null; return null;
        }
    }

    private static string? LastIdentifier(ExpressionSyntax e) => e switch
    {
        IdentifierNameSyntax i => i.Identifier.Text,
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        _ => null,
    };

    private static string? BaseName(TypeSyntax t) => t switch
    {
        IdentifierNameSyntax i => i.Identifier.Text,
        QualifiedNameSyntax q => q.Right.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        _ => null,
    };
}
