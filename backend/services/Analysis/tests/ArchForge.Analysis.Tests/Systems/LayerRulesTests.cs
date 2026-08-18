using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Tests.Systems;

public class LayerRulesTests
{
    [Theory]
    // внутренний → внешний = нарушение (зависимости чистой архитектуры идут внутрь)
    [InlineData("Domain", "Infrastructure", true)]
    [InlineData("Domain", "Application", true)]
    [InlineData("Domain", "Api", true)]
    [InlineData("Application", "Infrastructure", true)]
    [InlineData("Application", "Api", true)]
    [InlineData("Contracts", "Infrastructure", true)]
    // внешний → внутренний = здоровое направление
    [InlineData("Api", "Application", false)]
    [InlineData("Api", "Domain", false)]
    [InlineData("Infrastructure", "Domain", false)]
    [InlineData("Application", "Domain", false)]
    [InlineData("Api", "Contracts", false)]
    // shared kernel = ядро (0): на него можно зависеть всем; сам он наружу — нарушение
    [InlineData("Api", "Shared", false)]
    [InlineData("Application", "Shared", false)]
    [InlineData("Domain", "Shared", false)]
    [InlineData("Shared", "Application", true)]
    [InlineData("Shared", "Infrastructure", true)]
    // равные уровни — не нарушение (Infrastructure↔Api — сиблинги)
    [InlineData("Infrastructure", "Api", false)]
    [InlineData("Domain", "Contracts", false)]
    [InlineData("Shared", "Domain", false)]
    // неизвестные/несудимые слои — не нарушение (честнее пропустить, чем выдумать)
    [InlineData("tests", "Infrastructure", false)]
    [InlineData("Domain", "tests", false)]
    [InlineData("unknown", "Domain", false)]
    [InlineData("Tooling", "Api", false)]
    public void IsViolation(string from, string to, bool expected) =>
        Assert.Equal(expected, LayerRules.IsViolation(from, to));
}
