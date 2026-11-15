namespace ArchForge.Analysis.Core.Systems;

// Детектор «хранилище опубликовано наружу» (E9.2): БД/кэш (Datastore) с портом, опубликованным
// на ВСЕ интерфейсы (0.0.0.0 / без host_ip) — доступно с хоста и потенциально из сети, хотя должно
// быть внутренним (только для сервисов своей сети). Красный флаг границ/безопасности.
//
// Честность (adr/0008):
//  • только Datastore — у сервисов/gateway публикация портов нормальна, их не флагаем;
//  • bind на localhost (127.0.0.1 / ::1) НЕ считаем — доступно лишь с самой машины;
//  • серьёзность зависит от среды (E9.2 ч.2): prod → Critical; dev/local/test → не находка
//    (опубликованная БД на машине разработчика ожидаема); stage, «default» (base + override) и
//    незнакомые имена → Warning — «в prod» не утверждаем без подтверждения именем среды;
//  • единый уровень для всех видов хранилища (БД/кэш/blob) — тип узла серьёзность не меняет;
//  • источник (файл:строка записи ports в compose выбранной среды) отдаёт `locate` и кладётся в Items.
/// <summary>Где в исходнике задан порт: путь (относительный) и строка (1-based). null — место неизвестно.</summary>
public delegate (string File, int Line)? PortSourceLookup(string nodeId, PortMapping port);

public static class ExposedPortDetector
{
    public static IReadOnlyList<SystemProblem> Detect(SystemGraph graph, string? env = null, PortSourceLookup? locate = null)
    {
        var kind = EnvironmentClass.Of(env ?? graph.Environments?.FirstOrDefault(e => e.Active)?.Name);
        if (kind == EnvKind.Development) return [];
        var severity = kind == EnvKind.Production ? ProblemSeverity.Critical : ProblemSeverity.Warning;
        var envLabel = env ?? graph.Environments?.FirstOrDefault(e => e.Active)?.Name;
        var inEnv = kind == EnvKind.Production && !string.IsNullOrEmpty(envLabel) ? $" в среде «{envLabel}»" : "";
        var problems = new List<SystemProblem>();
        foreach (var n in graph.Nodes)
        {
            if (n.Kind != SystemNodeKind.Datastore || n.Deploy is null) continue;
            foreach (var p in n.Deploy.Ports)
            {
                if (!p.Published || !IsAllInterfaces(p.HostIp)) continue;
                var src = locate?.Invoke(n.Id, p);
                var items = src is { } l
                    ? new[] { new ProblemItem($"порт {PortText(p)}", "", "опубликован на все интерфейсы", l.File, l.Line) }
                    : null;
                problems.Add(new SystemProblem(
                    severity, "exposed-datastore",
                    $"хранилище «{n.Name}» опубликовано наружу{inEnv} (порт {PortText(p)}, все интерфейсы) — " +
                        "БД/кэш должны быть доступны только внутренней сети сервисов",
                    n.Id, Items: items));
                break; // одна находка на узел, даже если наружу торчит несколько портов
            }
        }
        return problems;
    }

    // «Все интерфейсы» = host_ip не задан (0.0.0.0 по умолчанию) либо явно 0.0.0.0 / ::.
    private static bool IsAllInterfaces(string? hostIp) =>
        string.IsNullOrEmpty(hostIp) || hostIp is "0.0.0.0" or "::";

    // В заголовок идёт только ЧИСЛОВОЙ порт (или диапазон): значение из ${VAR}/.env после подстановки может оказаться чем угодно
    // (в т.ч. секретом) — нечисловое не показываем (secrets-config).
    private static string PortText(PortMapping p)
    {
        var c = Safe(p.Container);
        return p.Host is { } h ? $"{Safe(h)}→{c}" : c;
    }

    private static string Safe(string v) => IsPortLike(v) ? v : "?";

    private static bool IsPortLike(string v) =>
        v.Length is > 0 and <= 11 && v.All(ch => char.IsAsciiDigit(ch) || ch == '-') && char.IsAsciiDigit(v[0]) && char.IsAsciiDigit(v[^1]);
}
