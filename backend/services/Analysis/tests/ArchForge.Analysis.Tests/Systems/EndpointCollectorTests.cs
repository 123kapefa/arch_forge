using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Domain;

namespace ArchForge.Analysis.Tests.Systems;

// Агрегация HTTP-эндпоинтов сервиса из членов-типов контроллеров (#71).
public class EndpointCollectorTests
{
    private static TypeInfo Type(string name, params MemberInfo[] members) =>
        new(name, "class", "API", "Controller", "marker", [], $"/x/{name}.cs", [], members);

    [Fact]
    public void Collects_only_endpoint_members_with_verb()
    {
        var t = Type("SettingsController",
            new MemberInfo("GetPublic", "endpoint", HttpMethod: "GET", Route: "/api/settings/public", Line: 20),
            new MemberInfo("Helper", "method"),                    // не endpoint — пропускаем
            new MemberInfo("Broken", "endpoint"));                 // endpoint без verb — пропускаем

        var eps = EndpointCollector.FromTypes([t]);

        Assert.Single(eps);
        Assert.Equal("GET", eps[0].Method);
        Assert.Equal("/api/settings/public", eps[0].Route);
        Assert.Equal("controller", eps[0].Source);
        Assert.Equal("SettingsController", eps[0].Type);
        Assert.Equal(20, eps[0].Line);
    }

    [Fact]
    public void Deterministic_order_by_route_then_method()
    {
        var t = Type("C",
            new MemberInfo("B", "endpoint", HttpMethod: "POST", Route: "/b"),
            new MemberInfo("Aget", "endpoint", HttpMethod: "GET", Route: "/a"),
            new MemberInfo("Adel", "endpoint", HttpMethod: "DELETE", Route: "/a"));

        var eps = EndpointCollector.FromTypes([t]);

        Assert.Equal(new[] { "/a", "/a", "/b" }, eps.Select(e => e.Route));
        Assert.Equal(new[] { "DELETE", "GET", "POST" }, eps.Select(e => e.Method)); // /a: DELETE < GET по методу
    }

    [Fact]
    public void No_endpoints_yields_empty()
    {
        var t = Type("Plain", new MemberInfo("Value", "property", "string"));
        Assert.Empty(EndpointCollector.FromTypes([t]));
    }
}
