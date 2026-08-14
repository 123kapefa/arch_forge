using System.Text.RegularExpressions;

namespace ArchForge.Analysis.Infrastructure.Manifests;

// Подстановка переменных docker-compose в строковых значениях:
//   ${VAR:-default} → default        (значение по умолчанию);
//   ${VAR:?message} → ${VAR}         (обязательная, значение неизвестно — ОТЛОЖЕНО, придёт при деплое);
//   ${VAR}          → ${VAR}         (тоже отложено).
// Отложенные оставляем как ${VAR}, чтобы вызывающий видел провенанс (внешний узел), а не «пустоту» (adr/0008).
internal static partial class Interpolation
{
    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)(?::-([^}]*)|:\?[^}]*)?\}")]
    private static partial Regex VarRef();

    public static string Resolve(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        return VarRef().Replace(value, m =>
            m.Groups[2].Success            // group2 присутствует только у ${VAR:-default}
                ? m.Groups[2].Value        // → значение по умолчанию
                : "$" + "{" + m.Groups[1].Value + "}"); // ${VAR} или ${VAR:?...} — отложено
    }

    public static bool IsDeferred(string? value) =>
        !string.IsNullOrEmpty(value) && value.Contains("${", StringComparison.Ordinal);
}
