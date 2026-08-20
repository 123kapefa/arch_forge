namespace ArchForge.Analysis.Core.Systems;

// Детектор «хранилище опубликовано наружу» (E9.2): БД/кэш (Datastore) с портом, опубликованным
// на ВСЕ интерфейсы (0.0.0.0 / без host_ip) — доступно с хоста и потенциально из сети, хотя должно
// быть внутренним (только для сервисов своей сети). Красный флаг границ/безопасности.
//
// Честность (adr/0008):
//  • только Datastore — у сервисов/gateway публикация портов нормальна, их не флагаем;
//  • bind на localhost (127.0.0.1 / ::1) НЕ считаем — доступно лишь с самой машины;
//  • Warning, не Critical: без контекста среды (dev/stage/prod) не утверждаем «в prod» —
//    per-env эскалация появится с оверрайдами (E9.1 слой 2);
//  • единый Warning для всех видов хранилища (БД/кэш/blob) — осознанно: без среды тип узла не
//    поднимает серьёзность (в отличие от SharedDbDetector, где primary-БД → Critical). Градация
//    по среде, а не по типу, придёт вместе с оверрайдами.
public static class ExposedPortDetector
{
    public static IReadOnlyList<SystemProblem> Detect(SystemGraph graph)
    {
        var problems = new List<SystemProblem>();
        foreach (var n in graph.Nodes)
        {
            if (n.Kind != SystemNodeKind.Datastore || n.Deploy is null) continue;
            foreach (var p in n.Deploy.Ports)
            {
                if (!p.Published || !IsAllInterfaces(p.HostIp)) continue;
                problems.Add(new SystemProblem(
                    ProblemSeverity.Warning, "exposed-datastore",
                    $"хранилище «{n.Name}» опубликовано наружу (порт {PortText(p)}, все интерфейсы) — " +
                        "БД/кэш должны быть доступны только внутренней сети сервисов",
                    n.Id));
                break; // одна находка на узел, даже если наружу торчит несколько портов
            }
        }
        return problems;
    }

    // «Все интерфейсы» = host_ip не задан (0.0.0.0 по умолчанию) либо явно 0.0.0.0 / ::.
    private static bool IsAllInterfaces(string? hostIp) =>
        string.IsNullOrEmpty(hostIp) || hostIp is "0.0.0.0" or "::";

    private static string PortText(PortMapping p) =>
        p.Host is { } h ? $"{h}→{p.Container}" : p.Container;
}
