namespace ArchForge.Analysis.Core.Systems;

// Правило направления слоёв чистой архитектуры (свод-НЕЗАВИСИМО): зависимости идут ВНУТРЬ
// (внешний слой → внутренний). Нарушение = внутренний слой зависит от внешнего.
// Уровни: Domain/Contracts — ядро (0); Application (1); Infrastructure/Api — внешние (2).
// tests/tooling/unknown не судим (уровень неизвестен) — честнее пропустить, чем выдумать нарушение (adr/0008).
public static class LayerRules
{
    private static readonly Dictionary<string, int> Level = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Domain"] = 0, ["Contracts"] = 0, ["Shared"] = 0, // ядро, общий контракт, shared kernel — внутрь можно всем
        ["Application"] = 1,
        ["Infrastructure"] = 2, ["Api"] = 2,  // внешние слои — сюда зависеть изнутри нельзя
    };

    public static int? LevelOf(string layer) => Level.TryGetValue(layer, out var l) ? l : null;

    // Нарушение: слой-источник строго ВНУТРЕННЕЕ слоя-цели (внутренний зависит от внешнего).
    // Равные уровни (Infrastructure↔Api) и неизвестные слои — не нарушение.
    public static bool IsViolation(string fromLayer, string toLayer) =>
        LevelOf(fromLayer) is { } f && LevelOf(toLayer) is { } t && f < t;
}
