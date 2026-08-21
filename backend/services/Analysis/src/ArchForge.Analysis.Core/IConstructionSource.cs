namespace ArchForge.Analysis.Core;

// Порт оси конструирования: где в таргете создаются (new) экземпляры СВОИХ (объявленных в таргете) типов.
// Реализация — Roslyn в Infrastructure. Отдельно от ITypeSource: другая гранулярность (место в теле метода).
public interface IConstructionSource
{
    IReadOnlyList<ConstructionSite> Read(string path, CancellationToken ct = default);
}
