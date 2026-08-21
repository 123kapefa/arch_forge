namespace ArchForge.Analysis.Core.Systems;

// Обнаруженный сервис: имя (решения), корень (папка с .slnx/.sln) и путь к его compose (если есть).
public sealed record DiscoveredService(string Name, string Root, string? ComposePath);

// Порт: найти .NET-сервисы в корне системы. Папочная реализация (по .slnx/.sln) — в Infrastructure.Manifests.
// Точнее discovery по имени compose: подхватывает нестандартные (docker-compose-dev.yaml) и
// НЕ считает инфра/мониторинг-compose (prometheus/nginx) сервисами (находка спайка P1.5-0).
public interface IServiceDiscoverySource
{
    IReadOnlyList<DiscoveredService> Discover(string root);
}
