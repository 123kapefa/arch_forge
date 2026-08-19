using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Tests.Systems;

public class MicroserviceLayerBuilderTests
{
    private static readonly TypeClassifier Classifier = new();

    private static ServiceAnalysis Svc(string ns, RawType[] raw) =>
        new(new DiscoveredService("Sln", "/" + ns, "compose.yml"), ns, Classifier.Classify(raw, ConventionPack.Default()));

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
    public void Aggregates_service_endpoints_from_controller_members()
    {
        RawType[] raw =
        [
            new("SettingsController", "class", [], "()", "API",
                "/x/src/Services/Settings/Settings.API/SettingsController.cs")
            {
                Members = [new RawMember("GetPublic", "endpoint", "() : void", "GET", "/api/settings/public", 20)],
            },
        ];
        var map = MicroserviceLayerBuilder.Build([Svc("product/backend", raw)]);

        var entry = map["product/backend::settings"];
        Assert.Single(entry.Endpoints);
        Assert.Equal("GET", entry.Endpoints[0].Method);
        Assert.Equal("/api/settings/public", entry.Endpoints[0].Route);
        Assert.Equal("controller", entry.Endpoints[0].Source);
    }

    [Fact]
    public void Merges_minimal_api_endpoints_with_controllers()
    {
        RawType[] raw =
        [
            new("GatewayController", "class", [], "()", "API",
                "/x/src/Services/Gateway/Gateway.API/GatewayController.cs")
            {
                Members = [new RawMember("Health", "endpoint", "() : void", "GET", "/gw/health", 10)],
            },
        ];
        var sa = new ServiceAnalysis(new DiscoveredService("Sln", "/gateway", "compose.yml"), "product/backend",
            Classifier.Classify(raw, ConventionPack.Default()))
        {
            MinimalEndpoints = [new EndpointHit("POST", "/api/auth/csrf",
                "/x/src/Services/Gateway/Gateway.API/Program.cs", 20)],
        };

        var eps = MicroserviceLayerBuilder.Build([sa])["product/backend::gateway"].Endpoints;

        Assert.Equal(2, eps.Count);
        Assert.Contains(eps, e => e.Source == "controller" && e.Route == "/gw/health");
        Assert.Contains(eps, e => e.Source == "minimal" && e.Route == "/api/auth/csrf");
    }

    [Fact]
    public void Maps_non_services_code_to_compose_node_by_name() // #75
    {
        // код gateway лежит вне /Services/ (под /src/ApiGateway/Kalmykov.ApiGateway) — привязываем к узлу api-gateway
        RawType[] raw =
        [
            new("GatewayRouteBuilder", "class", [], "()", "Api",
                "/x/backend/src/ApiGateway/Kalmykov.ApiGateway/GatewayRouteBuilder.cs"),
        ];
        var sa = new ServiceAnalysis(new DiscoveredService("Sln", "/x/backend", "compose.yml"), "backend",
            Classifier.Classify(raw, ConventionPack.Default()))
        {
            MinimalEndpoints = [new EndpointHit("GET", "/api/auth/csrf",
                "/x/backend/src/ApiGateway/Kalmykov.ApiGateway/Program.cs", 20)],
        };
        var compose = new (string, string)[] { ("backend/api-gateway", "api-gateway") };

        var map = MicroserviceLayerBuilder.Build([sa], compose);

        var key = MicroserviceLayerBuilder.NodeKey("backend/api-gateway", "api-gateway");
        Assert.True(map.ContainsKey(key), $"нет ключа {key}: [{string.Join(", ", map.Keys)}]");
        Assert.Contains(map[key].Endpoints, e => e.Source == "minimal" && e.Route == "/api/auth/csrf");
        // root = папка проекта, а не корень всего backend — иначе drill грузит весь backend (#78)
        Assert.Equal("/x/backend/src/ApiGateway/Kalmykov.ApiGateway", map[key].Root);
    }

    [Fact]
    public void Non_services_root_covers_multiple_projects_under_area() // #78: общий каталог группы, не один проект
    {
        // gateway из нескольких проектов под одной area-папкой /ApiGateway — root должен охватить оба
        RawType[] raw =
        [
            new("Host", "class", [], "()", "Api", "/x/backend/src/ApiGateway/Gw.Host/Host.cs"),
            new("RouteBuilder", "class", [], "()", "Api", "/x/backend/src/ApiGateway/Gw.Routing/RouteBuilder.cs"),
        ];
        var sa = new ServiceAnalysis(new DiscoveredService("Sln", "/x/backend", "compose.yml"), "backend",
            Classifier.Classify(raw, ConventionPack.Default()));
        var compose = new (string, string)[] { ("backend/api-gateway", "api-gateway") };

        var map = MicroserviceLayerBuilder.Build([sa], compose);

        var key = MicroserviceLayerBuilder.NodeKey("backend/api-gateway", "api-gateway");
        Assert.Equal("/x/backend/src/ApiGateway", map[key].Root); // area, а не один из проектов
    }

    [Fact]
    public void Single_group_multi_project_root_is_common_prefix() // #78: не сужаем root до папки первого файла
    {
        // одно-сервисный .sln, типы в разных проектах в ОДНОЙ группе (нет compose-матча) — root = общий корень,
        // а не папка первого проекта (репро блокера ревью: раньше NonServicesRootOf брал первый файл).
        RawType[] raw =
        [
            new("Account", "class", [], "()", "Domain", "/x/auth-service/src/Auth.Domain/Account.cs"),
            new("AuthController", "class", [], "()", "Api", "/x/auth-service/src/Auth.API/AuthController.cs"),
        ];
        var map = MicroserviceLayerBuilder.Build([Svc("auth-service", raw)]);

        Assert.Equal("/x/auth-service/src", map["auth-service::auth"].Root); // общий /src, не .../Auth.Domain
    }

    [Fact]
    public void Single_service_all_projects_map_to_primary_node() // #81
    {
        // одно-сервисный .sln (нет /src/Services/), location "." (скан в корне); узлы auth-api + auth-migrate.
        // Все проекты (.API/.Domain/.Infrastructure) садятся на ГЛАВНЫЙ узел (auth-api собрал name-матч),
        // а не дробятся/теряются. Сателлит auth-migrate кода не получает.
        RawType[] raw =
        [
            new("AuthController", "class", [], "()", "Api", "/x/auth/src/Auth.API/AuthController.cs"),
            new("Account", "class", [], "()", "Domain", "/x/auth/src/Auth.Domain/Account.cs"),
            new("Repo", "class", [], "()", "Infrastructure", "/x/auth/src/Auth.Infrastructure/Repo.cs"),
        ];
        var sa = new ServiceAnalysis(new DiscoveredService("Sln", "/x/auth", "compose.yml"), ".",
            Classifier.Classify(raw, ConventionPack.Default()));
        var compose = new (string, string)[] { ("./auth-api", "auth-api"), ("./auth-migrate", "auth-migrate") };

        var map = MicroserviceLayerBuilder.Build([sa], compose);

        var apiKey = MicroserviceLayerBuilder.NodeKey("./auth-api", "auth-api");
        Assert.Contains(map[apiKey].Layers, l => l.Name == "API");
        Assert.Contains(map[apiKey].Layers, l => l.Name == "Domain");
        Assert.Contains(map[apiKey].Layers, l => l.Name == "Infrastructure");
        Assert.False(map.ContainsKey(MicroserviceLayerBuilder.NodeKey("./auth-migrate", "auth-migrate"))); // сателлит без кода
    }

    [Fact]
    public void Single_service_satellite_project_does_not_hijack_primary() // #81
    {
        // сателлит migrator совпадает с проектом AuthService.Migrator, НО код должен сесть на auth-service (главный:
        // имя ≈ .sln «AuthService»), а не на migrator/mailpit. Иначе весь сервис уезжал бы на джобу-мигратор.
        RawType[] raw =
        [
            new("Ctrl", "class", [], "()", "Api", "/x/auth/src/AuthService.Api/Ctrl.cs"),
            new("Account", "class", [], "()", "Domain", "/x/auth/src/AuthService.Domain/Account.cs"),
            new("Migr", "class", [], "()", "Tooling", "/x/auth/src/AuthService.Migrator/Migr.cs"),
        ];
        var sa = new ServiceAnalysis(new DiscoveredService("AuthService", "/x/auth", "compose.yaml"), ".",
            Classifier.Classify(raw, ConventionPack.Default()));
        var compose = new (string, string)[] { ("./mailpit", "mailpit"), ("./migrator", "migrator"), ("./auth-service", "auth-service") };

        var map = MicroserviceLayerBuilder.Build([sa], compose);

        var authKey = MicroserviceLayerBuilder.NodeKey("./auth-service", "auth-service");
        Assert.Contains(map[authKey].Layers, l => l.Name == "API");
        Assert.Contains(map[authKey].Layers, l => l.Name == "Domain");
        Assert.False(map.ContainsKey(MicroserviceLayerBuilder.NodeKey("./migrator", "migrator"))); // сателлит без кода
        Assert.False(map.ContainsKey(MicroserviceLayerBuilder.NodeKey("./mailpit", "mailpit")));
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
