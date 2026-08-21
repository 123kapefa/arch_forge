using System.Text.RegularExpressions;

namespace ArchForge.Analysis.Infrastructure.Manifests;

// Подстановка переменных docker-compose в строковых значениях. Приоритет: значение из .env (vars) →
// значение по умолчанию ${VAR:-default} → отложено ${VAR} (G4).
//   ${VAR}          → vars[VAR] | ${VAR}    (из .env, иначе ОТЛОЖЕНО — придёт при деплое);
//   ${VAR:-default} → vars[VAR] | default   (из .env, иначе значение по умолчанию);
//   ${VAR:?message} → vars[VAR] | ${VAR}    (из .env, иначе отложено).
// Отложенные оставляем как ${VAR}, чтобы вызывающий видел провенанс (внешний узел), а не «пустоту» (adr/0008).
internal static partial class Interpolation
{
    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)(?::-([^}]*)|:\?[^}]*)?\}")]
    private static partial Regex VarRef();

    public static string Resolve(string? value, IReadOnlyDictionary<string, string>? vars = null)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        return VarRef().Replace(value, m =>
            vars is not null && vars.TryGetValue(m.Groups[1].Value, out var v) ? v // из .env — высший приоритет
                : m.Groups[2].Success       // group2 присутствует только у ${VAR:-default}
                    ? m.Groups[2].Value      // → значение по умолчанию
                    : "$" + "{" + m.Groups[1].Value + "}"); // ${VAR} или ${VAR:?...} — отложено
    }

    public static bool IsDeferred(string? value) =>
        !string.IsNullOrEmpty(value) && value.Contains("${", StringComparison.Ordinal);
}
