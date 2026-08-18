using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Tests.Systems;

public class DtoMappingDetectorTests
{
    private static ConstructionSite Site(string type, string inType, string file, string layer = "Api", string method = "Handle") =>
        new(type, inType, method, layer, file, 10);

    [Fact]
    public void Dto_built_in_many_places_outside_mappers_is_reported()
    {
        var sites = new[]
        {
            Site("WarehouseDto", "GetWarehousesHandler", "a/GetWarehousesHandler.cs"),
            Site("WarehouseDto", "CreateWarehouseHandler", "b/CreateWarehouseHandler.cs"),
            Site("WarehouseDto", "ListWarehousesHandler", "c/ListWarehousesHandler.cs"),
        };
        var p = Assert.Single(DtoMappingDetector.Detect(sites, "backend", DtoMappingRule.Default));
        Assert.Equal("dto-mapping", p.Kind);
        Assert.Equal(ProblemSeverity.Info, p.Severity);            // мягко — не красит health
        Assert.Equal(ProblemCategory.Convention, p.Category);      // свод-линза
        Assert.Contains("WarehouseDto", p.Title);
        Assert.Equal(3, p.Items!.Count);                           // все места — в фактах
        Assert.All(p.Items!, it => Assert.False(string.IsNullOrEmpty(it.File))); // file для перехода
    }

    [Fact]
    public void Dto_built_only_in_mapper_is_not_reported()
    {
        var sites = Enumerable.Range(0, 5).Select(i => Site("WidgetDto", "WidgetMapper", $"m{i}.cs")).ToArray();
        Assert.Empty(DtoMappingDetector.Detect(sites, "backend", DtoMappingRule.Default)); // централизовано — не смелл
    }

    [Fact]
    public void Below_threshold_is_not_reported()
    {
        var sites = new[] { Site("XDto", "H1", "1.cs"), Site("XDto", "H2", "2.cs") }; // только 2 места
        Assert.Empty(DtoMappingDetector.Detect(sites, "backend", DtoMappingRule.Default));
    }

    [Fact]
    public void Wiring_sites_excluded_from_items()
    {
        var sites = new[]
        {
            Site("YDto", "H1", "a/H1.cs"),
            Site("YDto", "H2", "b/H2.cs"),
            Site("YDto", "H3", "c/H3.cs"),
            new ConstructionSite("YDto", "Program", ".ctor", "Api", "svc/Program.cs", 5), // проводка — не факт
        };
        var p = Assert.Single(DtoMappingDetector.Detect(sites, "backend", DtoMappingRule.Default));
        Assert.Equal(3, p.Items!.Count);
        Assert.DoesNotContain(p.Items!, it => it.Name == "Program");
    }

    [Fact]
    public void Tests_layer_sites_excluded()
    {
        var sites = new[]
        {
            Site("ZDto", "T1", "a/T1.cs", layer: "tests"),
            Site("ZDto", "T2", "b/T2.cs", layer: "tests"),
            Site("ZDto", "T3", "c/T3.cs", layer: "tests"),
        };
        Assert.Empty(DtoMappingDetector.Detect(sites, "backend", DtoMappingRule.Default));
    }

    // правило из свода меняет суффикс DTO и пороги — детектор подчиняется своду, а не хардкоду
    [Fact]
    public void Custom_svod_rule_changes_suffix_and_thresholds()
    {
        var sites = new[]
        {
            Site("WidgetResponse", "H1", "a/H1.cs"),
            Site("WidgetResponse", "H2", "b/H2.cs"),   // 2 места / 2 файла
            Site("IgnoredDto", "H3", "c/H3.cs"),       // *Dto теперь не DTO по правилу
            Site("IgnoredDto", "H4", "d/H4.cs"),
        };
        var rule = new DtoMappingRule(["Response"], ["Mapper"], MinSites: 2, MinFiles: 2);
        var p = Assert.Single(DtoMappingDetector.Detect(sites, "backend", rule));
        Assert.Contains("WidgetResponse", p.Title);
        Assert.Equal(2, p.Items!.Count);
    }
}
