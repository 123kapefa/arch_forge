using ArchForge.Analysis.Core;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Усечён ли разбор по капу числа .cs. Обход потоковый и встаёт на файле №max+1; вердикт по файлам кешируется (DepthCache).
public sealed class RoslynScanLimitSource(int maxFiles = RoslynScan.MaxFilesPerScan) : IScanLimitSource
{
    public int MaxFiles => maxFiles;

    public bool IsTruncated(string root, CancellationToken ct = default)
    {
        RoslynScan.EnumerateCsCapped(root, out var capped, maxFiles, ct);
        return capped;
    }
}
