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

    private static DriftScanner Scanner(IReadOnlyList<DiscoveredService> svcs, Func<string, IReadOnlyList<RawType>> read) =>
        new(new StubDiscovery(svcs), new StubTypes(read), new TypeClassifier(ConventionPack.Default()));

    private static string TempRoot() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_drift_" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void Emits_convention_info_problem_per_drifting_service()
    {
        var root = TempRoot();
        var svcs = new[] { new DiscoveredService("Svc.A", Path.Combine(root, "svc-a"), null) };
        try
        {
            var p = Assert.Single(Scanner(svcs, _ => Drifting).Scan(root));
            Assert.Equal(ProblemSeverity.Info, p.Severity);            // мягко — не красный
            Assert.Equal("drift", p.Kind);
            Assert.Equal(ProblemCategory.Convention, p.Category);      // отдельная линза
            Assert.Equal("svc-a", p.Location);                        // namespace = Rel(root, svc.Root)
            Assert.Contains("Svc.A", p.Title);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Clean_service_yields_no_drift_problem()
    {
        var root = TempRoot();
        var svcs = new[] { new DiscoveredService("Svc.Clean", Path.Combine(root, "clean"), null) };
        try { Assert.Empty(Scanner(svcs, _ => Clean).Scan(root)); }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Broken_service_is_skipped_not_thrown()
    {
        var root = TempRoot();
        var svcs = new[]
        {
            new DiscoveredService("Bad", Path.Combine(root, "bad"), null),
            new DiscoveredService("Good", Path.Combine(root, "good"), null),
        };
        try
        {
            // «bad» бросает при разборе → изоляция ошибок (adr/0008); «good» дрейфит → одна проблема
            var p = Assert.Single(Scanner(svcs,
                path => path.EndsWith("bad", StringComparison.Ordinal) ? throw new IOException("boom") : Drifting).Scan(root));
            Assert.Equal("good", p.Location);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Missing_root_yields_empty()
    {
        var svcs = new[] { new DiscoveredService("X", "/x", null) };
        Assert.Empty(Scanner(svcs, _ => Drifting).Scan("/no/such/root/archforge-xyz"));
    }
}
