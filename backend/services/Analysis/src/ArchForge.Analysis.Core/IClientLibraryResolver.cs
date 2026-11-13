namespace ArchForge.Analysis.Core;

// Порт: клиентские библиотеки (*.Contracts с AddHttpClient/Refit), которые сервис ПОДКЛЮЧАЕТ (ProjectReference на проект вне своей
// папки) и РЕГИСТРИРУЕТ (вызывает их метод регистрации). Вызовы такой библиотеки — это вызовы потребителя: сервис-владелец библиотеки
// их не делает (для него это был бы вызов самому себе). Реализация — в Infrastructure.Roslyn (разбор .csproj и синтаксиса).
public interface IClientLibraryResolver
{
    // systemRoot — корень анализа: библиотека обязана лежать внутри него (враждебный csproj не должен уводить чтение за пределы проекта).
    IReadOnlyList<ClientLibrary> Resolve(string consumerRoot, string systemRoot, CancellationToken ct = default);
}

/// <summary>Библиотека: имя проекта (для пометки в Evidence) и его папка (корень для <see cref="IHttpCallSource"/>).</summary>
public sealed record ClientLibrary(string Name, string Dir);
