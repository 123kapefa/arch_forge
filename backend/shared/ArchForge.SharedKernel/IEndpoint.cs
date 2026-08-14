using Microsoft.AspNetCore.Routing;

namespace ArchForge.SharedKernel;

// Само-регистрируемый эндпоинт (по своду): срез сам объявляет свой маршрут рядом с хендлером.
public interface IEndpoint
{
    void MapEndpoint(IEndpointRouteBuilder app);
}
