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
        string path, bool drift = false, bool layers = false, string? env = null, bool calls = false, bool refresh = false, CancellationToken ct = default)
    {
        string url = $"/api/system?path={Uri.EscapeDataString(path)}";
        if (drift) url += "&drift=true";
        if (layers) url += "&layers=true";
        if (!string.IsNullOrWhiteSpace(env)) url += $"&env={Uri.EscapeDataString(env)}";
        if (calls) url += "&calls=true"; // E3.1 call-graph (opt-in, требует Roslyn)
        if (refresh) url += "&refresh=true"; // «Пере-анализировать»: сбросить кеш разбора кода
        using HttpResponseMessage response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadFromJsonAsync<GraphDto>(ct);
    }

    /// <summary>То же, что <see cref="GetSystemAsync"/>, но с профилем проекта (adr/0012): POST с профилем в теле.
    /// null — если Analysis не смог или не принял профиль.</summary>
    public async Task<GraphDto?> GetSystemWithProfileAsync(
        string path, ProfileDto profile, bool drift = false, bool layers = false, string? env = null, bool calls = false,
        bool refresh = false, CancellationToken ct = default)
    {
        string url = $"/api/system?path={Uri.EscapeDataString(path)}";
        if (drift) url += "&drift=true";
        if (layers) url += "&layers=true";
        if (!string.IsNullOrWhiteSpace(env)) url += $"&env={Uri.EscapeDataString(env)}";
        if (calls) url += "&calls=true";
        if (refresh) url += "&refresh=true";
        try
        {
            using HttpResponseMessage response = await http.PostAsJsonAsync(url, profile, ct);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadFromJsonAsync<GraphDto>(ct);
        }
        catch (Exception e) when (IsTransportFailure(e, ct)) { return null; } // Analysis недоступен — как «не смог», не 500
    }

    /// <summary>Предпросмотр влияния профиля на проект (adr/0012). null — если Analysis не смог или не принял профиль.
    /// Долгий при первом разборе проекта (потом идёт из кеша), поэтому принимает отмену.</summary>
    public async Task<PreviewDto?> PreviewImpactAsync(string path, ProfileDto profile, CancellationToken ct = default)
    {
        try
        {
            using HttpResponseMessage response = await http.PostAsJsonAsync($"/api/system/preview?path={Uri.EscapeDataString(path)}", profile, ct);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadFromJsonAsync<PreviewDto>(ct);
        }
        catch (Exception e) when (IsTransportFailure(e, ct)) { return null; }
    }

    /// <summary>Каталог правил анализатора (adr/0012). null — если Analysis не смог.</summary>
    public async Task<RuleCatalogDto?> GetRulesAsync(CancellationToken ct = default)
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync("/api/rules", ct);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadFromJsonAsync<RuleCatalogDto>(ct);
        }
        catch (Exception e) when (IsTransportFailure(e, ct)) { return null; }
    }

    /// <summary>Встроенный словарь ролей, имена сервисов и пороги (adr/0012). null — если Analysis не смог.</summary>
    public async Task<VocabularyDefaultsDto?> GetVocabularyAsync(CancellationToken ct = default)
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync("/api/vocabulary", ct);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadFromJsonAsync<VocabularyDefaultsDto>(ct);
        }
        catch (Exception e) when (IsTransportFailure(e, ct)) { return null; }
    }

    // Отказ соединения, таймаут или битый ответ — сбой Analysis; отмена самим вызывающим сюда не относится.
    private static bool IsTransportFailure(Exception e, CancellationToken ct) =>
        !ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException;
}
