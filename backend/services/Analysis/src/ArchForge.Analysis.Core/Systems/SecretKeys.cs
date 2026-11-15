using System.Text.RegularExpressions;

namespace ArchForge.Analysis.Core.Systems;

/// <summary>Эвристика «этот ключ конфигурации — секрет» по ИМЕНИ (E9.3). Значения мы не читаем, поэтому судим только по названию.
/// Ключ режется на СЛОВА (по `:` `_` `-` `.` и границам camelCase) и сравнивается со словарём целых слов — подстрока не считается
/// (иначе «Monkey», «Hotkey», «Assault» попадали бы в секреты). Ложное срабатывание безопасно (ключ лишь помечается), промах — нет.</summary>
public static partial class SecretKeys
{
    private static readonly HashSet<string> Words = new(StringComparer.Ordinal)
    {
        "password", "passwd", "pwd", "pass", "secret", "secrets", "token", "tokens", "credential", "credentials",
        "apikey", "connectionstring", "connectionstrings", "certificate", "cert", "salt", "passphrase", "pat", "sas", "dsn",
    };

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|[:_\\-.\\s]+")]
    private static partial Regex Split();

    public static bool IsSecret(string key)
    {
        var words = Split().Split(key.Replace("__", ":", StringComparison.Ordinal)).Where(w => w.Length > 0).Select(w => w.ToLowerInvariant()).ToList();
        if (words.Count == 0) return false;
        if (words[0] is "connectionstrings" || (words.Count > 1 && words[0] == "connection" && words[1] is "string" or "strings")) return true;                 // строка подключения почти всегда с паролем
        if (words.Any(Words.Contains)) return true;
        return words[^1] == "key";                                        // …:Key, ApiKey, PrivateKey, SigningKey, AccessKey
    }
}
