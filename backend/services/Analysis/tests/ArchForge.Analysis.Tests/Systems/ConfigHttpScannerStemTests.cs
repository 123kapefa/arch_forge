using ArchForge.Analysis.Infrastructure.Manifests;

namespace ArchForge.Analysis.Tests.Systems;

// G5 — привязка appsettings к сервису, когда имя папки кода ≠ имя compose-сервиса
// (EducationContentService ↔ education-service). Без стем-матчинга исходящие вызовы education-service терялись,
// и половина реальных циклов не находилась.
public class ConfigHttpScannerStemTests
{
    [Fact]
    public void Attributes_appsettings_by_service_stem_and_finds_cycle()
    {
        var dir = Path.Combine(Path.GetTempPath(), "archforge_g5_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "EducationContentService"));   // имя папки ≠ compose-имя
        Directory.CreateDirectory(Path.Combine(dir, "ProgressService"));
        File.WriteAllText(Path.Combine(dir, "docker-compose.yml"), """
            services:
              education-service:
                image: app-edu
              progress-service:
                image: app-prog
            """);
        File.WriteAllText(Path.Combine(dir, "EducationContentService", "appsettings.Docker.json"),
            """{ "ProgressOptions": { "Url": "http://progress-service:8003" } }""");
        File.WriteAllText(Path.Combine(dir, "ProgressService", "appsettings.Docker.json"),
            """{ "EducationOptions": { "Url": "http://education-service:8001" } }""");

        var g = new ComposeSystemSource().Read(dir);

        var edu = g.Nodes.Single(n => n.Name == "education-service").Id;
        var prog = g.Nodes.Single(n => n.Name == "progress-service").Id;
        // рёбра http-call в обе стороны привязались по стему (education ↔ progress)
        Assert.Contains(g.Edges, e => e.Source == edu && e.Target == prog && e.Kind == "http-call");
        Assert.Contains(g.Edges, e => e.Source == prog && e.Target == edu && e.Kind == "http-call");
        // и как следствие — цикл сервисов
        Assert.Contains(g.Problems, p => p.Kind == "service-cycle");
    }

    [Fact]
    public void Ambiguous_stem_is_not_attributed()
    {
        // стем «report» у ДВУХ сервисов (report-api, report-worker) → неоднозначен → не добавляем;
        // файл под ReportGenerator/ не должен привязаться и создать ложное ребро к catalog.
        var dir = Path.Combine(Path.GetTempPath(), "archforge_g5amb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "ReportGenerator"));
        File.WriteAllText(Path.Combine(dir, "docker-compose.yml"), """
            services:
              report-api:
                image: app-a
              report-worker:
                image: app-b
              catalog-service:
                image: app-c
            """);
        File.WriteAllText(Path.Combine(dir, "ReportGenerator", "appsettings.Docker.json"),
            """{ "CatalogOptions": { "Url": "http://catalog-service:8080" } }""");

        var g = new ComposeSystemSource().Read(dir);

        Assert.DoesNotContain(g.Edges, e => e.Target.EndsWith("/catalog-service", StringComparison.Ordinal)
            && e.Kind == "http-call"); // неоднозначный стем не привязал файл наугад
    }
}
