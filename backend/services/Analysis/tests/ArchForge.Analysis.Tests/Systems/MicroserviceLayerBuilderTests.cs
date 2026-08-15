using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Tests.Systems;

public class MicroserviceLayerBuilderTests
{
    private static readonly TypeClassifier Classifier = new(ConventionPack.Default());

    private static ServiceAnalysis Svc(string ns, RawType[] raw) =>
        new(new DiscoveredService("Sln", "/" + ns, "compose.yml"), ns, Classifier.Classify(raw));

    [Fact]
    public void Partitions_types_by_microservice_folder()
    {
        RawType[] raw =
        [
            new("CatalogItem", "class", [], "()", "Domain", "/x/src/Services/Catalog/Catalog.Domain/CatalogItem.cs"),
            new("CartLine", "class", [], "()", "Domain", "/x/src/Services/Cart/Cart.Domain/CartLine.cs"),
        ];
        var map = MicroserviceLayerBuilder.Build([Svc("product/backend", raw)]);

        Assert.True(map.ContainsKey("product/backend::catalog"));
        Assert.True(map.ContainsKey("product/backend::cart"));
        Assert.Contains(map["product/backend::catalog"].Layers, l => l.Name == "Domain");
    }

    [Fact]
    public void NodeKey_matches_compose_service_name()
    {
        // узел catalog-api в namespace product/backend → ключ product/backend::catalog (суффикс -api срезан)
        Assert.Equal("product/backend::catalog",
            MicroserviceLayerBuilder.NodeKey("product/backend/catalog-api", "catalog-api"));
    }

    [Fact]
    public void Single_microservice_sln_keyed_by_namespace()
    {
        // нет Services/ → весь .sln = один сервис, ключ по имени namespace (auth-service → auth)
        RawType[] raw = [new("Account", "class", [], "()", "Domain", "/x/auth-service/src/AuthService.Domain/Account.cs")];
        var map = MicroserviceLayerBuilder.Build([Svc("auth-service", raw)]);

        Assert.True(map.ContainsKey("auth-service::auth"));
        Assert.Equal("auth-service::auth", MicroserviceLayerBuilder.NodeKey("auth-service/auth-api", "auth-api"));
    }
}
