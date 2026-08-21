using ArchForge.Analysis.Infrastructure.Roslyn;

namespace ArchForge.Analysis.Tests;

// Склейка маршрута контроллера (класс-[Route] + метод) — чистая строковая логика (#71).
public class RoslynRouteTests
{
    [Theory]
    [InlineData("api/settings", "public", "/api/settings/public")] // обычная склейка
    [InlineData("api/settings", null, "/api/settings")]            // метод без своего route → только класс
    [InlineData(null, "csrf", "/csrf")]                            // нет класс-route → только метод
    [InlineData("api/x", "/abs/y", "/abs/y")]                      // абсолютный метод-маршрут отменяет префикс класса
    [InlineData("api/x", "~/root", "/root")]                       // '~/' — тоже абсолютный
    [InlineData("/api/", "public/", "/api/public")]                // лишние слэши обрезаются
    public void Combines_class_and_method_route(string? cls, string? method, string expected)
        => Assert.Equal(expected, RoslynSemanticTypeSource.CombineRoute(cls, method));

    [Fact]
    public void Both_empty_yields_null()
        => Assert.Null(RoslynSemanticTypeSource.CombineRoute(null, null));

    [Theory]
    [InlineData("/api/[controller]/list", "ProductsController", "List", "/api/Products/list")] // [controller] без суффикса
    [InlineData("/api/[controller]/[action]", "OrdersController", "Create", "/api/Orders/Create")]
    [InlineData("/api/[CONTROLLER]", "CartController", "Get", "/api/Cart")]                     // токен регистронезависим
    [InlineData("/api/products", "ProductsController", "List", "/api/products")]                // нет токенов — без изменений
    public void Expands_route_tokens(string route, string controller, string action, string expected)
        => Assert.Equal(expected, RoslynSemanticTypeSource.ExpandTokens(route, controller, action));
}
