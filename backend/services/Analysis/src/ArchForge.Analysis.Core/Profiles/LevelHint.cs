using ArchForge.Contracts;

namespace ArchForge.Analysis.Core.Profiles;

/// <summary>Совет об уровне проекта по сигналам из кода (adr/0012). Только совет с причинами: уровень молча не меняется.
/// Очки прозрачны и перечислены в причинах, чтобы пользователь мог не согласиться.</summary>
public static class LevelHint
{
    /// <param name="services">Сервисов в compose.</param>
    /// <param name="hasBroker">В системе есть брокер сообщений (топики).</param>
    /// <param name="markerPercent">Доля типов, у которых роль определена по маркерному интерфейсу (0..100).</param>
    /// <param name="typeCount">Сколько типов разобрано (при малой выборке доля маркеров не учитывается).</param>
    public static LevelHintDto Suggest(int services, bool hasBroker, int markerPercent, int typeCount)
    {
        var score = 0;
        var reasons = new List<string>();

        if (services >= 4) { score += 3; reasons.Add($"сервисов в compose: {services}"); }
        else if (services >= 2) { score += 2; reasons.Add($"сервисов в compose: {services}"); }
        else reasons.Add(services == 1 ? "один сервис" : "сервисов в compose не найдено");

        if (hasBroker) { score += 1; reasons.Add("есть брокер сообщений"); }

        if (typeCount >= 20)
        {
            if (markerPercent >= 50) { score += 2; reasons.Add($"маркерные интерфейсы у {markerPercent}% типов"); }
            else if (markerPercent >= 20) { score += 1; reasons.Add($"маркерные интерфейсы у {markerPercent}% типов"); }
            else reasons.Add($"маркерные интерфейсы почти не используются ({markerPercent}% типов)");
        }

        var level = score >= 5 ? ProjectLevel.Platform : score >= 2 ? ProjectLevel.Standard : ProjectLevel.Simple;
        return new LevelHintDto(level.ToString(), reasons);
    }
}
