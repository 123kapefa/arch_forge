using ArchForge.Analysis.Domain;

namespace ArchForge.Analysis.Core.Systems;

// Сквозной shared kernel (типы под src/Shared) не принадлежит ни одному compose-сервису, поэтому в L0 его нет
// и он не дриллится. Строим для него отдельный L0-узел «Shared kernel» с корнем-папкой src/Shared и сводкой
// слоя Shared — дальше обычный drill (root → /api/map → L1/L2) показывает его типы с ролями.
public static class SharedKernelBuilder
{
    /// <summary>Узел + сводка слоёв для shared kernel (первый разобранный сервис с типами слоя Shared). null — shared нет.</summary>
    public static (SystemNode Node, MicroserviceLayerBuilder.Entry Entry)? Build(IReadOnlyList<ServiceAnalysis> analyses)
    {
        foreach (var (_, _, result) in analyses)
        {
            if (result is null) continue;
            var shared = result.Types.Where(t => string.Equals(t.Layer, "Shared", StringComparison.Ordinal)).ToList();
            if (shared.Count == 0) continue;

            var root = shared.Select(t => SharedRootOf(t.File)).FirstOrDefault(r => r is not null);
            if (root is null) continue;

            var names = shared.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            var edges = result.Edges.Where(e => names.Contains(e.From) && names.Contains(e.To)).ToList();
            var layers = ServiceSummaryBuilder.Layers(shared, edges);
            if (layers.Count == 0) continue;

            // отдельный вид «shared» → своя расцветка/оформление на L0; drill по layers как у сервиса
            var node = new SystemNode("shared/kernel", "Shared kernel", SystemNodeKind.Shared);
            var entry = new MicroserviceLayerBuilder.Entry(ServiceSummaryBuilder.WorstHealth(layers), root, layers);
            return (node, entry);
        }
        return null;
    }

    // Корень shared-папки из пути файла: «.../src/Shared/Kalmykov.Shared.X/...» → «.../src/Shared».
    private static string? SharedRootOf(string file)
    {
        var p = file.Replace('\\', '/');
        var i = p.IndexOf("/Shared/", StringComparison.OrdinalIgnoreCase);
        return i < 0 ? null : p[..(i + "/Shared".Length)];
    }
}
