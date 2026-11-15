namespace ArchForge.Analysis.Core;

// Порт: упёрся ли разбор сервиса в кап числа файлов. Усечённый разбор не должен выглядеть полным (adr/0008) →
// ServiceAnalyzer помечает сервис, хендлер добавляет заметку в Degradations («карта неполная»). Реализация — Roslyn.
public interface IScanLimitSource
{
    /// <summary>true — в сервисе больше файлов, чем разбирается; карта по нему неполная.</summary>
    bool IsTruncated(string root, CancellationToken ct = default);

    /// <summary>Сколько файлов разбирается максимум (для текста заметки).</summary>
    int MaxFiles { get; }
}
