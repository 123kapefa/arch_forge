using ArchForge.Analysis.Core.Systems;
using YamlDotNet.RepresentationModel;

namespace ArchForge.Analysis.Infrastructure.Manifests;

// Поиск строки в compose-файле для находки (E9.2: источник `compose.<env>.yml:строка`).
// Обычный разбор (Deserialize) номеров строк не хранит, поэтому для найденной проблемы читаем файл через RepresentationModel.
// Один экземпляр на один Read: каждый файл разбирается ОДИН раз (индекс сервис → записи ports), а не заново на каждое хранилище.
//
// Защита от враждебного YAML (compose — недоверенный ввод):
//  • YAML обходится только до записей ports; запись раскрывается ПЛОСКО (скаляр | map скаляров) — без рекурсии, поэтому циклы
//    алиасов (`x: &r {k: *r}`) и «взрыв» вложенных алиасов не обходятся вовсе;
//  • потолки: размер файла, число записей ports на файл, число находок, для которых ищем место; отмена через CancellationToken.
// Значения env здесь не читаются — только запись порта и ключ сервиса.
internal sealed class ComposeSourceLocator(CancellationToken ct = default)
{
    private const long MaxFileBytes = 2_000_000;
    private const int MaxPortEntriesPerFile = 5_000;
    private const int MaxLookups = 200;

    private sealed record Entry(PortMapping Port, int Line);
    private sealed record ServiceIndex(int Line, IReadOnlyList<Entry> Ports);

    private readonly Dictionary<string, IReadOnlyDictionary<string, ServiceIndex>?> _files = new(StringComparer.Ordinal);
    private int _lookups;

    /// <summary>Место записи порта: ТОЧНОЕ совпадение разобранной записи по всем файлам (в порядке <paramref name="files"/>),
    /// и лишь затем слабое — по контейнерному порту (запись с ${VAR}); иначе строка ключа сервиса в последнем (base) файле.
    /// Порядок «сначала точное по всем файлам» важен: иначе безопасная запись оверлея (`127.0.0.1:5432:5432`) перетянула бы
    /// источник с опубликованной записи base. null — места нет (потолки, битый файл).</summary>
    public (string File, int Line)? Find(IReadOnlyList<string> files, string service, PortMapping port)
    {
        if (++_lookups > MaxLookups) return null;
        foreach (var f in files)
            if (Service(f, service) is { } s && s.Ports.FirstOrDefault(e => e.Port == port) is { } exact) return (f, exact.Line);
        foreach (var f in files)
            if (Service(f, service) is { } s && s.Ports.FirstOrDefault(e => e.Port.Container == port.Container && e.Port.Published) is { } loose) return (f, loose.Line);
        return files.Count > 0 && Service(files[^1], service) is { } b ? (files[^1], b.Line) : null;
    }

    /// <summary>Строка ключа сервиса в первом из файлов, где он определён (оверлей среды раньше base). null — места нет.</summary>
    public (string File, int Line)? ServiceLine(IReadOnlyList<string> files, string service)
    {
        foreach (var f in files)
            if (Service(f, service) is { } s) return (f, s.Line);   // как и в Find: индекс хранит Start.Line как есть
        return null;
    }

    private ServiceIndex? Service(string path, string service)
    {
        if (!_files.TryGetValue(path, out var index)) _files[path] = index = Load(path);
        return index is not null && index.TryGetValue(service, out var s) ? s : null;
    }

    private IReadOnlyDictionary<string, ServiceIndex>? Load(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes) return null;
            var yaml = new YamlStream();
            using (var reader = new StreamReader(path)) yaml.Load(reader);
            if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root) return null;
            if (Child(root, "services") is not YamlMappingNode services) return null;

            var result = new Dictionary<string, ServiceIndex>(StringComparer.Ordinal);
            var total = 0;
            var noVars = new Dictionary<string, string>();
            foreach (var kv in services.Children)
            {
                ct.ThrowIfCancellationRequested();
                if (kv.Key is not YamlScalarNode { Value: { } name } || kv.Value is not YamlMappingNode body) continue;
                var entries = new List<Entry>();
                if (Child(body, "ports") is YamlSequenceNode seq)
                    foreach (var item in seq.Children)
                    {
                        if (++total > MaxPortEntriesPerFile) break;
                        if (ComposeParser.ParsePortItem(Flat(item), noVars) is { } pm) entries.Add(new Entry(pm, (int)item.Start.Line));
                    }
                result[name] = new ServiceIndex((int)kv.Key.Start.Line, entries);
            }
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; /* битый YAML → места нет, находка останется без файла:строки */ }
    }

    private static YamlNode? Child(YamlMappingNode node, string key)
    {
        foreach (var kv in node.Children)
            if (kv.Key is YamlScalarNode k && k.Value == key) return kv.Value;
        return null;
    }

    // Запись ports → простые объекты БЕЗ рекурсии: скаляр → строка; map → словарь только из скалярных значений (вложенное пропускаем).
    private static object? Flat(YamlNode n)
    {
        switch (n)
        {
            case YamlScalarNode s: return s.Value;
            case YamlMappingNode m:
                var d = new Dictionary<object, object>();
                foreach (var kv in m.Children)
                    if (kv.Key is YamlScalarNode { Value: { } k } && kv.Value is YamlScalarNode { Value: { } v }) d[k] = v;
                return d;
            default: return null;
        }
    }
}
