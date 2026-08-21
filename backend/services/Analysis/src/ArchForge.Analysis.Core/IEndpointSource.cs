namespace ArchForge.Analysis.Core;

// Порт: minimal-API эндпоинты (app.MapGet/MapPost/... с литеральным маршрутом) из исходников сервиса.
// Чисто СИНТАКСИЧЕСКИЙ разбор (семантика для литералов не нужна) — реализация в Infrastructure.Roslyn.
// Контроллеры идут другим путём (члены-типы через ITypeSource). #71
public interface IEndpointSource
{
    IReadOnlyList<EndpointHit> Read(string root, CancellationToken ct = default);
}

// Найденный minimal-API эндпоинт: verb + литеральный маршрут + где (файл/строка) — для привязки к микросервису.
public sealed record EndpointHit(string Method, string Route, string File, int? Line);
