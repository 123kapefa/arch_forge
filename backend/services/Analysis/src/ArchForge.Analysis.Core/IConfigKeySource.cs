namespace ArchForge.Analysis.Core;

// Порт: ключи конфигурации, которые сервис ЧИТАЕТ из кода (E9.3), и ключи, объявленные в его appsettings*.json.
// Чисто СИНТАКСИЧЕСКИЙ разбор; реализация — в Infrastructure.Roslyn.
// Срез: строковые литералы — `config["A:B"]`, `GetSection("A")["B"]`, `GetValue<T>("A:B")`, `GetConnectionString("n")`.
// Не охвачено (backlog): свойства `IOptions<T>` (какие ключи секции нужны — по типу), нелитеральные ключи.
// СЕКРЕТЫ (secrets-config): отдаются ТОЛЬКО ИМЕНА ключей. Значения не читаются в модель вовсе — ни из кода, ни из appsettings.
public interface IConfigKeySource
{
    ServiceConfigKeys Read(string root, CancellationToken ct = default);
}

/// <param name="Key">Ключ как в коде («Jwt:Secret»).</param>
/// <param name="File">Файл относительно папки над корнем сервиса.</param>
public sealed record ConfigKeyUse(string Key, string File, int Line);

/// <param name="Used">Ключи, читаемые кодом (с первой точкой использования).</param>
/// <param name="DeclaredByEnv">Нормализованные имена ключей, объявленных в appsettings: ключ словаря — токен среды
/// ("" = appsettings.json, "production" = appsettings.Production.json). Только имена.</param>
public sealed record ServiceConfigKeys(
    IReadOnlyList<ConfigKeyUse> Used,
    IReadOnlyDictionary<string, IReadOnlySet<string>> DeclaredByEnv)
{
    public static readonly ServiceConfigKeys Empty = new([], new Dictionary<string, IReadOnlySet<string>>());
}
