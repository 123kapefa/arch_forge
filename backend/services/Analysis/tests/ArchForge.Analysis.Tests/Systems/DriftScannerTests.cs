using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Tests.Systems;

public class DriftScannerTests
{
    private sealed class StubDiscovery(IReadOnlyList<DiscoveredService> svcs) : IServiceDiscoverySource
    {
        public IReadOnlyList<DiscoveredService> Discover(string root) => svcs;
    }

    private sealed class StubTypes(Func<string, IReadOnlyList<RawType>> read) : ITypeSource
    {
        public IReadOnlyList<RawType> Read(string path, CancellationToken ct = default) => read(path);
    }

    // «RegisterHandler» без маркера ICommandHandler → эвристическая роль + дрейф (как RoslynClassifierTests).
    private static readonly RawType[] Drifting =
        [new("RegisterHandler", "class", [], "(Dep d)", "Application", "x.cs")];
    // команда с маркером ICommand → роль по маркеру, дрейфа нет.
    private static readonly RawType[] Clean =
        [new("CreateWidgetCommand", "record", ["ICommand"], "(string N)", "Application", "x.cs")];

    // сервис С compose (иначе DriftScanner его отфильтрует — у него нет узлов в графе)
    private static DiscoveredService Svc(string root, string name) =>
        new(name, Path.Combine(root, name), "docker-compose.yml");

    private static DriftScanner Scanner(IReadOnlyList<DiscoveredService> svcs, Func<string, IReadOnlyList<RawType>> read) =>
        new(new ServiceAnalyzer(new StubDiscovery(svcs), new StubTypes(read), new TypeClassifier(ConventionPack.Default())));

    private static string TempRoot() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_drift_" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void Emits_convention_info_problem_per_drifting_service()
    {
        var root = TempRoot();
        try
        {
            var p = Assert.Single(Scanner([Svc(root, "svc-a")], _ => Drifting).Scan(root));
            Assert.Equal(ProblemSeverity.Info, p.Severity);            // мягко — не красный
            Assert.Equal("drift", p.Kind);
            Assert.Equal(ProblemCategory.Convention, p.Category);      // отдельная линза
            Assert.Equal("svc-a", p.Location);                        // namespace = Rel(root, svc.Root)
            Assert.Contains("svc-a", p.Title);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Clean_service_yields_no_drift_problem()
    {
        var root = TempRoot();
        try { Assert.Empty(Scanner([Svc(root, "clean")], _ => Clean).Scan(root)); }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Service_without_compose_is_skipped()
    {
        var root = TempRoot();
        try
        {
            // «голый» .sln без compose → нет узлов в графе → дрейф был бы осиротевшим (ревью #22 M4)
            var svcs = new[] { new DiscoveredService("Lib", Path.Combine(root, "lib"), null) };
            Assert.Empty(Scanner(svcs, _ => Drifting).Scan(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Broken_service_reports_coverage_not_silence()
    {
        var root = TempRoot();
        try
        {
            // «bad» бросает при разборе → не молчим: Coverage-проблема; «good» дрейфит → Info
            var problems = Scanner([Svc(root, "bad"), Svc(root, "good")],
                path => path.EndsWith("bad", StringComparison.Ordinal) ? throw new IOException("boom") : Drifting).Scan(root);

            Assert.Contains(problems, p => p is { Kind: "drift-scan-failed", Severity: ProblemSeverity.Coverage }
                && p.Location == "bad");
            Assert.Contains(problems, p => p is { Kind: "drift", Severity: ProblemSeverity.Info } && p.Location == "good");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Multiple_drifting_services_deterministic_order()
    {
        var root = TempRoot();
        try
        {
            var problems = Scanner([Svc(root, "a"), Svc(root, "b"), Svc(root, "c")], _ => Drifting).Scan(root);
            Assert.Equal(new[] { "a", "b", "c" }, problems.Select(p => p.Location)); // порядок = порядок discovery
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Empty_discovery_yields_empty()
    {
        var root = TempRoot();
        try { Assert.Empty(Scanner([], _ => Drifting).Scan(root)); }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Cancellation_is_observed()
    {
        var root = TempRoot();
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(
                () => Scanner([Svc(root, "a")], _ => Drifting).Scan(root, cts.Token));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Missing_root_yields_empty()
    {
        Assert.Empty(Scanner([Svc("/x", "a")], _ => Drifting).Scan("/no/such/root/archforge-xyz"));
    }
}
