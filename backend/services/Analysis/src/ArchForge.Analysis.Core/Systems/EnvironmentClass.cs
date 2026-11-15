namespace ArchForge.Analysis.Core.Systems;

/// <summary>Класс среды по имени (E9.2): от него зависит серьёзность находок «наружу» — то, что ожидаемо в dev,
/// в prod критично. Имя среды — это суффикс файла docker-compose.&lt;env&gt;.yml; незнакомое имя = Other (не гадаем).</summary>
public enum EnvKind { Other, Development, Production }

public static class EnvironmentClass
{
    public static EnvKind Of(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "prod" or "production" or "prd" or "live" => EnvKind.Production,
        "dev" or "development" or "local" or "test" or "testing" or "ci" => EnvKind.Development,
        _ => EnvKind.Other,
    };
}
