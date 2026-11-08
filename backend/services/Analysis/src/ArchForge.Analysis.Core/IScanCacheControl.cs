namespace ArchForge.Analysis.Core;

// Порт управления кешем разбора кода (реализация — ScanCache в Infrastructure.Roslyn). Нужен, чтобы «Пере-анализировать»
// мог принудительно сбросить кеш под корнем проекта, а ядро не зависело от инфраструктуры.
public interface IScanCacheControl
{
    /// <summary>Сбрасывает записи кеша для всех путей внутри <paramref name="root"/> (включая сам корень).</summary>
    void InvalidateUnder(string root);
}
