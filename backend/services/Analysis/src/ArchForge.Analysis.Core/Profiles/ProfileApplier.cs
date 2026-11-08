using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Core.Profiles;

/// <summary>Применяет профиль к найденным проблемам: выключенные правила убирает, остальным ставит заданную строгость.
/// Чистая функция. Проблемы вне каталога (покрытие, «не удалось разобрать») не трогает — это честные отметки, не правила.</summary>
public static class ProfileApplier
{
    public static IReadOnlyList<SystemProblem> Apply(IReadOnlyList<SystemProblem> problems, ProjectProfile profile)
    {
        var result = new List<SystemProblem>(problems.Count);
        foreach (var p in problems)
        {
            switch (profile.Effective(p.Kind))
            {
                case null: result.Add(p); break;                      // родная серьёзность детектора
                case Strictness.Off: break;                           // правило выключено на этом уровне
                case { } s: result.Add(p with { Severity = ToSeverity(s) }); break;
            }
        }
        return result;
    }

    /// <summary>Какие правила выключены профилем — чтобы интерфейс мог сказать «не проверяется на вашем уровне», а не молчать.</summary>
    public static IReadOnlyList<string> DisabledRules(ProjectProfile profile) =>
        [.. RuleCatalog.All.Where(r => profile.Effective(r.Id) == Strictness.Off).Select(r => r.Id)];

    private static ProblemSeverity ToSeverity(Strictness s) => s switch
    {
        Strictness.Error => ProblemSeverity.Critical,
        Strictness.Warning => ProblemSeverity.Warning,
        _ => ProblemSeverity.Info,
    };
}
