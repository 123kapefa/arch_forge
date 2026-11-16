using ArchForge.Analysis.Core;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Читает из кеша разбора типов тот самый флаг, который выставил RoslynSemanticTypeSource (пол вместо семантики).
// Вызывать после чтения типов сервиса; нет живой записи — «не знаем» → false, ложной пометки не будет.
public sealed class CacheSemanticStatusSource(ScanCache cache) : ISemanticStatusSource
{
    public bool IsSyntaxOnly(string root) => cache.IsDegraded("types", root);
}
