using ArchForge.Analysis.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Ось конструирования (usage-граф) через Roslyn, синтаксически (как «пол», adr/0008): два прохода —
// (1) собрать имена всех типов, объявленных в таргете (чтобы не считать new List<>/StringBuilder и прочий
// фреймворк); (2) найти new этих типов и записать место (тип, где, метод, слой, файл:строка).
// Только new: конструирование однозначно. Фабрики/статкод — отдельная ось позже, если понадобится.
public sealed class RoslynConstructionSource : IConstructionSource
{
    public IReadOnlyList<ConstructionSite> Read(string path, CancellationToken ct = default)
    {
        // проход 1: разобрать деревья один раз + собрать имена своих типов
        var trees = new List<(SyntaxNode Root, string File)>();
        var own = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in RoslynScan.EnumerateCs(path))
        {
            ct.ThrowIfCancellationRequested();
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; } // недоступный файл — пропускаем (изоляция ошибок)

            var root = CSharpSyntaxTree.ParseText(text).GetRoot();
            trees.Add((root, file));
            foreach (var decl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                own.Add(decl.Identifier.Text);
        }

        // проход 2: new СВОЕГО типа → сайт
        var sites = new List<ConstructionSite>();
        foreach (var (root, file) in trees)
        {
            ct.ThrowIfCancellationRequested();
            var layer = RoslynScan.Layer(file, path);
            var tree = root.SyntaxTree;
            foreach (var oc in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                var name = RoslynScan.Simple(oc.Type);
                if (!own.Contains(name)) continue;
                var inType = oc.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "";
                var line = tree.GetLineSpan(oc.Span).StartLinePosition.Line + 1;
                sites.Add(new ConstructionSite(name, inType, MethodOf(oc), layer, file, line));
            }
        }
        return sites;
    }

    // Имя ближайшего метода/свойства/локальной функции вокруг узла (контекст сайта); «.ctor» для конструктора.
    private static string MethodOf(SyntaxNode n) =>
        n.Ancestors().Select(a => a switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            ConstructorDeclarationSyntax => ".ctor",
            PropertyDeclarationSyntax p => p.Identifier.Text,
            LocalFunctionStatementSyntax l => l.Identifier.Text,
            _ => (string?)null,
        }).FirstOrDefault(x => x is not null) ?? "";
}
