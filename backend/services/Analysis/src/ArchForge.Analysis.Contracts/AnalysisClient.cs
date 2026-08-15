using System.Net.Http.Json;
using ArchForge.Contracts;

namespace ArchForge.Analysis.Contracts;

/// <summary>
/// Типизированный HTTP-клиент к сервису Analysis — шов «путь → IR».
/// Другие сервисы (Map) регистрируют его через AddHttpClient&lt;AnalysisClient&gt;
/// и не знают деталей транспорта.
/// </summary>
public sealed class AnalysisClient(HttpClient http)
{
    /// <summary>Разобрать проект по пути и получить граф (IR). null — если Analysis не смог.</summary>
    public async Task<GraphDto?> AnalyzeAsync(string path, CancellationToken ct = default)
    {
        string url = $"/api/analysis?path={Uri.EscapeDataString(path)}";
        using HttpResponseMessage response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadFromJsonAsync<GraphDto>(ct);
    }

    /// <summary>Прочитать импорты (библиотеки) проекта по пути. null — если Analysis не смог.</summary>
    public async Task<ImportsDto?> GetImportsAsync(string path, CancellationToken ct = default)
    {
        string url = $"/api/imports?path={Uri.EscapeDataString(path)}";
        using HttpResponseMessage response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadFromJsonAsync<ImportsDto>(ct);
    }

    /// <summary>Собрать СИСТЕМНЫЙ граф (мульти-сервис, Фаза 1.5) по корню. null — если Analysis не смог.
    /// Код-линзы (тяжелее: разбор per-service): <paramref name="drift"/> — «дрейф от свода» (convention);
    /// <paramref name="layers"/> — «нарушение слоёв» (architecture).</summary>
    public async Task<GraphDto?> GetSystemAsync(
        string path, bool drift = false, bool layers = false, CancellationToken ct = default)
    {
        string url = $"/api/system?path={Uri.EscapeDataString(path)}";
        if (drift) url += "&drift=true";
        if (layers) url += "&layers=true";
        using HttpResponseMessage response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadFromJsonAsync<GraphDto>(ct);
    }
}
