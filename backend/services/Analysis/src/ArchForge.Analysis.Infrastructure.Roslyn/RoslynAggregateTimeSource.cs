using ArchForge.Analysis.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Кто ставит даты и аудит (методичка «Агрегаты, VO и доменные события»). Чисто синтаксический разбор.
// Часы в домене: `DateTime.UtcNow|Now|Today` и `DateTimeOffset.UtcNow|Now` в слое Domain (по сегменту проекта «.Domain»).
// Аудит в interceptor: присваивание CreatedAt/UpdatedAt/ModifiedAt/DeletedAt (или `Property("UpdatedAt").CurrentValue = …`) внутри типа,
// наследующего SaveChangesInterceptor/DbContext, либо в методе SaveChanges/SaveChangesAsync — вне слоя Domain.
// Слои tests, Contracts, Shared не судим. Потолки и чистка имён — как у соседних источников (враждебный проект).
public sealed class RoslynAggregateTimeSource : IAggregateTimeSource
{
    private const int MaxSitesPerKind = 5_000;
    private const int MaxCandidatesPerFile = 200;     // принятых мест на файл
    private const int MaxProbesPerFile = 1_000;          // подъёмов к владельцу на файл, включая отсеянные (DTO-присваивания)
    private const int MaxClimb = 256;                    // глубина подъёма по предкам: враждебная вложенность не удорожает подъём
    private const int MaxNameLength = 200;

    private static readonly HashSet<string> ClockMembers = new(StringComparer.Ordinal) { "UtcNow", "Now", "Today" };
    private static readonly HashSet<string> ClockTypes = new(StringComparer.Ordinal) { "DateTime", "DateTimeOffset" };
    private static readonly HashSet<string> AuditFields = new(StringComparer.Ordinal) { "CreatedAt", "UpdatedAt", "ModifiedAt", "DeletedAt", "CreatedOn", "UpdatedOn", "LastModifiedAt" };

    private static string Clean(string s)
    {
        var t = s.Length > MaxNameLength ? s[..MaxNameLength] + "…" : s;
        return new string(t.Where(c => !(char.IsControl(c) || c is >= '​' and <= '‏' or >= '‪' and <= '‮' or >= '⁦' and <= '⁩')).ToArray());
    }

    public AggregateTimeFacts Read(string root, CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) return AggregateTimeFacts.Empty;
        var clock = new List<TimeSite>();
        var audit = new List<TimeSite>();
        foreach (var file in RoslynScan.EnumerateCsCapped(root, out _))
        {
            ct.ThrowIfCancellationRequested();
            if (clock.Count >= MaxSitesPerKind && audit.Count >= MaxSitesPerKind) break;
            var layer = RoslynScan.Layer(file, root);
            if (layer is "tests" or "Contracts" or "Shared") continue;
            var isDomain = layer == "Domain";
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }
            // быстрый отсев: часы нужны только в Domain, аудит — только вне его и только рядом с SaveChanges/DbContext
            var wantClock = isDomain && clock.Count < MaxSitesPerKind && (text.Contains("Now", StringComparison.Ordinal) || text.Contains("Today", StringComparison.Ordinal));
            var wantAudit = !isDomain && audit.Count < MaxSitesPerKind && (text.Contains("SaveChanges", StringComparison.Ordinal) || text.Contains("DbContext", StringComparison.Ordinal));
            if (!wantClock && !wantAudit) continue;
            var tree = CSharpSyntaxTree.ParseText(text, cancellationToken: ct).GetRoot(ct);
            var path = file.Replace('\\', '/');
            var seen = 0;
            var candidates = 0;
            var probes = 0;
            foreach (var node in tree.DescendantNodes())
            {
                if ((++seen & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
                if (wantClock && clock.Count < MaxSitesPerKind
                    && node is MemberAccessExpressionSyntax { Expression: var recv, Name.Identifier.Text: var m } && ClockMembers.Contains(m) && ClockTypeName(recv) is { } ct0)
                {
                    if (++candidates > MaxCandidatesPerFile) continue;
                    var (t, mem) = Owner(node);
                    clock.Add(new TimeSite(Clean(t), Clean(mem), ct0 + "." + m, path, Line(node), layer));
                }
                else if (wantAudit && audit.Count < MaxSitesPerKind && node is AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression } asg
                    && AuditFieldOf(asg.Left) is { } field)
                {
                    if (asg.Parent is InitializerExpressionSyntax) continue;          // new X { CreatedAt = … } (seed, DTO) — не аудит
                    if (++probes > MaxProbesPerFile) continue;                    // потолок работы: считаем и отсеянные подъёмы
                    if (!InAuditContext(asg)) continue;
                    if (++candidates > MaxCandidatesPerFile) continue;            // потолок принятых мест: DTO-присваивания его не съедают
                    var (t, mem) = Owner(node);
                    audit.Add(new TimeSite(Clean(t), Clean(mem), Clean(field), path, Line(node), layer));
                }
            }
        }
        return new AggregateTimeFacts(clock, audit);
    }

    private static int Line(SyntaxNode n) => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    // DateTime / System.DateTime / DateTimeOffset
    private static string? ClockTypeName(ExpressionSyntax e) => e switch
    {
        IdentifierNameSyntax { Identifier.Text: var n } when ClockTypes.Contains(n) => n,
        MemberAccessExpressionSyntax { Name.Identifier.Text: var n2 } when ClockTypes.Contains(n2) => n2,
        _ => null,
    };

    // x.UpdatedAt = … / UpdatedAt = … / entry.Property("UpdatedAt").CurrentValue = …
    private static string? AuditFieldOf(ExpressionSyntax left) => left switch
    {
        IdentifierNameSyntax { Identifier.Text: var n } when AuditFields.Contains(n) => n,
        MemberAccessExpressionSyntax { Name.Identifier.Text: "CurrentValue", Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Property" }, ArgumentList.Arguments: [{ Expression: LiteralExpressionSyntax lit }] } }
            when AuditFields.Contains(lit.Token.ValueText) => lit.Token.ValueText,
        MemberAccessExpressionSyntax { Name.Identifier.Text: var n2 } when AuditFields.Contains(n2) => n2,
        _ => null,
    };

    // Присваивание в interceptor/DbContext или в SaveChanges*: обычные DTO-маппинги (CreatedAt = …) не считаются
    private static bool InAuditContext(SyntaxNode at)
    {
        foreach (var anc in at.Ancestors().Take(MaxClimb))
        {
            if (anc is MethodDeclarationSyntax { Identifier.Text: "SaveChanges" or "SaveChangesAsync" or "SavingChanges" or "SavingChangesAsync" }) return true;
            if (anc is TypeDeclarationSyntax td)
                return td.BaseList is { } bl && bl.Types.Any(b => BaseName(b.Type) is { } n && (n.EndsWith("SaveChangesInterceptor", StringComparison.Ordinal) || n.EndsWith("DbContext", StringComparison.Ordinal)));
        }
        return false;
    }

    private static string? BaseName(TypeSyntax t) => t switch
    {
        IdentifierNameSyntax i => i.Identifier.Text,
        QualifiedNameSyntax q => q.Right.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        _ => null,
    };

    private static (string Type, string Member) Owner(SyntaxNode at)
    {
        string? member = null;
        foreach (var anc in at.Ancestors().Take(MaxClimb))
        {
            switch (anc)
            {
                case MethodDeclarationSyntax m: member ??= m.Identifier.Text; break;
                case ConstructorDeclarationSyntax c: member ??= c.Identifier.Text; break;
                case PropertyDeclarationSyntax p: member ??= p.Identifier.Text; break;
                case TypeDeclarationSyntax td: return (td.Identifier.Text, member ?? td.Identifier.Text);
            }
        }
        return ("?", member ?? "?");
    }
}
