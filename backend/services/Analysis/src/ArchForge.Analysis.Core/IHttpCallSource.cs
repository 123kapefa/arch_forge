namespace ArchForge.Analysis.Core;

// Порт: ИСХОДЯЩИЕ HTTP-вызовы сервиса (HttpClient.GetAsync/PostAsync/… и SendAsync(new HttpRequestMessage(HttpMethod.X, "url"))).
// Срез 1 (E3.1): только АБСОЛЮТНЫЕ URL-литералы (http[s]://host/…) — относительные/переменные придут в срезах 2–4.
// Чисто СИНТАКСИЧЕСКИЙ разбор (для литерала семантика не нужна); реализация — в Infrastructure.Roslyn.
// Цель вызова резолвит потребитель (HttpCallGraphBuilder: host → узел системы), провенанс — code.
public interface IHttpCallSource
{
    IReadOnlyList<HttpCallHit> Read(string root, CancellationToken ct = default);
}

// Найденный исходящий вызов: verb + нормализованный абсолютный URL (scheme://host[:port]/path, без query) +
// где (файл/строка) + короткая улика. Query отброшен на этапе сбора — секреты не тащим (secrets-config).
public sealed record HttpCallHit(string Method, string Url, string File, int Line, string Evidence);
