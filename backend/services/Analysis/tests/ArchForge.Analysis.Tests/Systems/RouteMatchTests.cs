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

    [Theory]
    [InlineData("GET", "/api/items/{id}", true)]            // подстановка ↔ плейсхолдер эндпоинта
    [InlineData("GET", "/api/{x}", false)]                  // подстановка не должна «съесть» литеральный сегмент и тем более весь маршрут
    [InlineData("GET", "/api/items/{id}/reviews", false)]
    public void Interpolation_hole_matches_only_endpoint_placeholder(string method, string path, bool expected) =>
        Assert.Equal(expected, RouteMatch.Matches(method, path, Eps));

    [Theory]
    [InlineData("GET", "/api/{version}/items", null)]       // подстановка могла бы быть «v1», но проверить нельзя — «не проверялось», не «не найден»
    [InlineData("GET", "/api/items/{id}", true)]
    [InlineData("GET", "/api/orders/{id}", false)]          // литерал «orders» нигде не подходит даже в мягком режиме
    [InlineData("GET", "/api/items", true)]
    public void Tri_state_match_distinguishes_unknown_from_not_found(string method, string path, bool? expected) =>
        Assert.Equal(expected, RouteMatch.Match(method, path, Eps));
}
