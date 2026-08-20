namespace ArchForge.Analysis.Infrastructure.Manifests;

// Слияние compose-файлов (E9.1 слой 2): base ← override, по правилам docker compose (подмножество,
// достаточное для того, что читает ComposeParser: image / environment / depends_on / ports / networks /
// healthcheck). Работает на «сыром» YAML-словаре ДО разбора — поэтому override влияет и на deploy, и на
// рёбра (connection strings из environment), одинаково для всех полей, без дублирования логики ридеров.
//
// Правила (docker compose «merge»):
//  • environment/labels → объединяем ПО КЛЮЧУ, нормализовав ОБЕ формы (map | ["K=v"]) к map (как compose);
//  • map + map → объединяем ключи, override выигрывает (recursively) — так мерджатся healthcheck, узлы service;
//  • «мультизначные» списки (ports/expose/networks/depends_on/volumes/…) → КОНКАТЕНАЦИЯ (с дедупом идентичных);
//  • скаляр / список не из мультизначных / несовпадение типов → override заменяет (если задан).
//
// Известные ограничения (backlog, ниже приоритет — реже встречаются в реальных override):
//  • явный `KEY: null` в override НЕ удаляет унаследованный ключ (compose-spec трактует null как «убрать») —
//    здесь остаётся значение base;
//  • дедуп портов не канонизирует short/long-форму («8080:80» vs {target:80,published:8080}) — возможен дубль записи.
internal static class ComposeMerge
{
    // Ключи, для которых compose склеивает последовательности, а не заменяет (docs: multi-value options).
    private static readonly HashSet<string> ConcatKeys = new(StringComparer.Ordinal)
    {
        "ports", "expose", "networks", "depends_on", "volumes",
        "dns", "dns_search", "tmpfs", "external_links", "links", "env_file",
    };

    // Поля «ключ-значение», допускающие ОБЕ формы (map | список "K=v"): мерджим по ключу независимо от формы.
    private static readonly HashSet<string> KeyValueKeys = new(StringComparer.Ordinal) { "environment", "labels" };

    /// <summary>Слить override поверх base. Возвращает новый словарь; исходные не мутирует.</summary>
    public static IDictionary<object, object> Merge(IDictionary<object, object> @base, IDictionary<object, object> ovr) =>
        (IDictionary<object, object>)MergeValue(@base, ovr, key: "");

    private static object MergeValue(object? b, object? o, string key)
    {
        // environment/labels: нормализуем обе стороны к map и мерджим по ключу (override-wins).
        // Иначе list-форма попадала бы в ветку «replace» и теряла ключи base (напр. строки подключения → рёбра).
        if (KeyValueKeys.Contains(key) && (AsKeyValueMap(b) is { } || AsKeyValueMap(o) is { }))
        {
            var res = AsKeyValueMap(b) ?? new Dictionary<object, object>();
            foreach (var kv in AsKeyValueMap(o) ?? []) res[kv.Key] = kv.Value;
            return res;
        }

        if (b is IDictionary<object, object> bm && o is IDictionary<object, object> om)
        {
            var res = new Dictionary<object, object>(bm);
            foreach (var kv in om)
            {
                if (kv.Key is null) continue;
                res[kv.Key] = res.TryGetValue(kv.Key, out var bv)
                    ? MergeValue(bv, kv.Value, kv.Key.ToString() ?? "")
                    : kv.Value!;
            }
            return res;
        }

        // Списки склеиваем только для «мультизначных» ключей; map внутри list-элементов (long-form ports) не трогаем.
        if (b is IList<object> bl && o is IList<object> ol && ConcatKeys.Contains(key))
        {
            var list = new List<object>(bl);
            foreach (var x in ol)
                if (!list.Any(e => YamlEquals(e, x))) list.Add(x); // дедуп идентичных записей
            return list;
        }

        return o ?? b!; // override заменяет (скаляр / replace-список); null override → оставляем base
    }

    // Привести environment/labels к map-форме: {K: v} копируем; ["K=v", …] → {K: v} (split по первому '=').
    // «KEY» без '=' пропускаем — как это делает ComposeParser.ReadEnv (хостовое значение статикой не читается),
    // чтобы merge-путь и обычный путь совпадали. Иначе (не map/не список) → null.
    private static Dictionary<object, object>? AsKeyValueMap(object? v)
    {
        if (v is IDictionary<object, object> m) return new Dictionary<object, object>(m);
        if (v is IList<object> list)
        {
            var res = new Dictionary<object, object>();
            foreach (var item in list)
            {
                if (item?.ToString() is not { Length: > 0 } s) continue;
                var eq = s.IndexOf('=');
                if (eq > 0) res[s[..eq]] = s[(eq + 1)..];   // «KEY=value»; «KEY» без '=' — пропуск (как ReadEnv)
            }
            return res;
        }
        return null;
    }

    // Сравнение YAML-значений для дедупа: скаляры — по строковому виду, простые списки/словари — рекурсивно.
    private static bool YamlEquals(object? a, object? b)
    {
        if (a is IDictionary<object, object> am && b is IDictionary<object, object> bm)
            return am.Count == bm.Count && am.All(kv =>
                bm.TryGetValue(kv.Key, out var bv) && YamlEquals(kv.Value, bv));
        if (a is IList<object> al && b is IList<object> bl)
            return al.Count == bl.Count && al.Zip(bl, YamlEquals).All(x => x);
        return string.Equals(a?.ToString(), b?.ToString(), StringComparison.Ordinal);
    }
}
