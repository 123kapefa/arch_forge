namespace ArchForge.Analysis.Core.Systems;

/// <summary>Код сервиса читает ключ конфигурации, а в выбранной среде его никто не задаёт (E9.3): ни compose `environment`/`env_file`
/// сервиса этой среды, ни appsettings.json / appsettings.&lt;Среда&gt;.json. Находка `missing-env-key` — «в prod не хватает X».
///
/// Секреты (secrets-config): в находку попадают ТОЛЬКО имена ключей и пометка «секрет»; значений у модели нет вовсе.
///
/// Честность (adr/0008): утверждаем лишь «в compose-среде и appsettings ключа нет». Значения из внешнего окружения (k8s secrets, vault,
/// переменные хоста, user-secrets) статикой не видны — поэтому Warning, а не Critical, и формулировка про источники, которые мы видим.
/// Ключи с нелитеральным именем и свойства IOptions&lt;T&gt; не охвачены (см. IConfigKeySource).</summary>
public static class MissingEnvKeyDetector
{
    public const string Kind = "missing-env-key";
    private const int MaxItems = 50;   // потолок на размер находки: враждебный код не раздувает ответ

    /// <param name="provided">Узел compose → имена env-ключей (`environment` + `env_file`) в выбранной среде. Только имена.</param>
    /// <param name="services">Код сервиса → узлы compose, которые его запускают (НЕ пусто), и ключи кода.</param>
    public static IReadOnlyList<SystemProblem> Detect(
        SystemGraph graph,
        IReadOnlyList<(string ServiceName, IReadOnlyList<string> NodeIds, ServiceConfigKeys Keys)> services)
    {
        var provided = graph.EnvKeys;
        if (provided is null) return [];
        var env = graph.Environments?.FirstOrDefault(e => e.Active)?.Name ?? "default";
        var tokens = AppsettingsTokens(env);
        var problems = new List<SystemProblem>();

        foreach (var (name, nodeIds, keys) in services)
        {
            if (nodeIds.Count == 0 || keys.Used.Count == 0) continue;
            // env_file сервиса не прочитан (нет файла, вне корня, ссылка) — ключи из него неизвестны: «не задан» не утверждаем (adr/0008)
            if (graph.EnvIncomplete is { } inc && nodeIds.Any(inc.Contains)) continue;
            var have = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in nodeIds)
                if (provided.TryGetValue(id, out var ks)) foreach (var k in ks) have.Add(ConfigKeys.Normalize(k));
            foreach (var (tok, set) in keys.DeclaredByEnv)
                if (tok.Length == 0 || tokens.Contains(tok)) foreach (var k in set) have.Add(k);

            var missing = keys.Used
                .GroupBy(u => ConfigKeys.Normalize(u.Key), StringComparer.Ordinal)
                .Where(g => !have.Contains(g.Key))
                .Select(g => g.OrderBy(u => u.File, StringComparer.Ordinal).ThenBy(u => u.Line).First())
                .OrderBy(u => u.Key, StringComparer.Ordinal)
                .ToList();
            if (missing.Count == 0) continue;

            var secrets = missing.Count(u => SecretKeys.IsSecret(u.Key));
            var items = missing.Take(MaxItems)
                .Select(u => new ProblemItem(u.Key, "",   // Layer — слой архитектуры, метку сюда не кладём; «секрет» — в начале Note (отдельного поля в DTO нет)
                    (SecretKeys.IsSecret(u.Key) ? "секрет · " : "") + $"читается в коде, в среде «{env}» не задан", u.File, u.Line))
                .ToList();
            var title = $"сервису «{name}» в среде «{env}» не хватает ключей конфигурации: {missing.Count}" +
                        (secrets > 0 ? $" (из них секретов: {secrets})" : "") + " — не заданы ни в compose, ни в appsettings";
            problems.Add(new SystemProblem(ProblemSeverity.Warning, Kind, title, nodeIds[0], Items: items));
        }
        return problems;
    }

    // Какие appsettings.<токен>.json относятся к среде. Токены — в нижнем регистре.
    private static HashSet<string> AppsettingsTokens(string env)
    {
        var e = env.Trim().ToLowerInvariant();
        return EnvironmentClass.Of(e) switch
        {
            EnvKind.Production => ["production", "prod", e],
            EnvKind.Development => ["development", "dev", e],
            _ => e is "stage" or "staging" ? ["staging", "stage", e] : [e],
        };
    }
}
