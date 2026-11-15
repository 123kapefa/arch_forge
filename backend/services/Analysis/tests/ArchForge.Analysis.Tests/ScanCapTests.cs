using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Infrastructure.Roslyn;

namespace ArchForge.Analysis.Tests;

// Кап числа .cs (#49): граница N / N+1, потоковый обход, отмена, видимость усечения (adr/0008).
public class ScanCapTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "af_cap_" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ } }

    private void Files(int n)
    {
        for (var i = 0; i < n; i++) File.WriteAllText(Path.Combine(_dir, $"C{i:D3}.cs"), $"public class C{i} {{ }}");
    }

    [Theory]
    [InlineData(2, false, 2)]   // ровно N — не усечено
    [InlineData(3, true, 2)]    // N+1 — усечено до N
    [InlineData(0, false, 0)]
    public void Cap_boundary(int files, bool expectCapped, int expectCount)
    {
        Files(files);
        var got = RoslynScan.EnumerateCsCapped(_dir, out var capped, max: 2);
        Assert.Equal(expectCapped, capped);
        Assert.Equal(expectCount, got.Count);
    }

    [Fact]
    public void Capped_result_is_sorted_and_stable()
    {
        Files(6);
        var a = RoslynScan.EnumerateCsCapped(_dir, out _, max: 4);
        var b = RoslynScan.EnumerateCsCapped(_dir, out _, max: 4);
        Assert.Equal(a, b);
        Assert.Equal(a.OrderBy(x => x, StringComparer.Ordinal), a);
    }

    [Fact]
    public void Cancellation_stops_enumeration()
    {
        Files(3);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => RoslynScan.EnumerateCsCapped(_dir, out _, max: 2, ct: cts.Token));
    }

    [Fact]
    public void Floor_type_source_honours_cancellation()
    {
        Files(2);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => new RoslynTypeSource().Read(_dir, cts.Token));
    }

    [Fact]
    public void ScanLimitSource_reports_truncation_only_over_cap()
    {
        Files(3);
        Assert.False(new RoslynScanLimitSource(3).IsTruncated(_dir));
        Assert.True(new RoslynScanLimitSource(2).IsTruncated(_dir));
    }

    private sealed class Limit(bool truncated) : IScanLimitSource
    {
        public int MaxFiles => 7;
        public bool IsTruncated(string root, CancellationToken ct = default) => truncated;
    }

    private sealed class OneSvc : IServiceDiscoverySource
    {
        public IReadOnlyList<DiscoveredService> Discover(string root) =>
            [new DiscoveredService("Svc.A", Path.Combine(root, "a"), "docker-compose.yml")];
    }

    private sealed class NoTypes : ITypeSource
    {
        public IReadOnlyList<RawType> Read(string path, CancellationToken ct = default) => [];
    }

    // Усечённый сервис помечается в ServiceAnalysis → хендлер превратит в Degradations.
    [Theory]
    [InlineData(true, 7)]
    [InlineData(false, null)]
    public void Analyzer_marks_truncated_service(bool truncated, int? expectedCap)
    {
        var a = Assert.Single(new ServiceAnalyzer(new OneSvc(), new NoTypes(), new TypeClassifier(), scanLimits: new Limit(truncated))
            .Analyze(_dir, ConventionPack.Default()));
        Assert.Equal(expectedCap, a.TruncatedAtFiles);
    }
}
