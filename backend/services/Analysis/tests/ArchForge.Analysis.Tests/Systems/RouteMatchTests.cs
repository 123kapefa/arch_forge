using ArchForge.Analysis.Core.Systems;
using ArchForge.Contracts;

namespace ArchForge.Analysis.Tests.Systems;

// Сопоставление маршрута вызова с эндпоинтом цели (E3.1): посегментно, плейсхолдер = wildcard, verb учитывается.
public class RouteMatchTests
{
    private static readonly IReadOnlyList<EndpointDto> Eps =
    [
        new EndpointDto("GET", "/api/items", "controller"),
        new EndpointDto("GET", "/api/items/{id}", "controller"),
        new EndpointDto("POST", "/api/orders", "controller"),
    ];

    [Theory]
    [InlineData("GET", "/api/items", true)]           // точное совпадение
    [InlineData("GET", "/api/items/42", true)]        // плейсхолдер {id}
    [InlineData("POST", "/api/orders", true)]
    [InlineData("GET", "/api/orders", false)]         // verb не совпал (это POST-эндпоинт)
    [InlineData("GET", "/api/items/42/reviews", false)] // лишний сегмент
    [InlineData("GET", "/api/unknown", false)]        // нет такого маршрута
    public void Matches_by_segments_and_verb(string method, string path, bool expected) =>
        Assert.Equal(expected, RouteMatch.Matches(method, path, Eps));

    [Fact]
    public void Placeholder_with_constraint_is_wildcard()  // {id:int} тоже плейсхолдер
    {
        var eps = new[] { new EndpointDto("GET", "/api/lessons/{id:int}", "controller") };
        Assert.True(RouteMatch.Matches("GET", "/api/lessons/7", eps));
    }

    [Fact]
    public void Empty_target_route_never_matches()  // относительный/пустой эндпоинт не сопоставляем (честно false)
    {
        var eps = new[] { new EndpointDto("GET", "", "controller") };
        Assert.False(RouteMatch.Matches("GET", "/api/items", eps));
    }
}
