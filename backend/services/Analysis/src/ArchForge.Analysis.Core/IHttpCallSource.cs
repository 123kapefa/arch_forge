namespace ArchForge.Analysis.Core;

// Порт: ИСХОДЯЩИЕ HTTP-вызовы сервиса (HttpClient.GetAsync/PostAsync/… и SendAsync(new HttpRequestMessage(HttpMethod.X, "url"))).
// Срез 1 (E3.1): АБСОЛЮТНЫЕ URL-литералы (http[s]://host/…). Срез 2: относительные пути, разрешённые по литеральному BaseAddress
// в той же области (метод → тип → файл); именованные/типизированные клиенты с адресом в другом файле и Refit — срезы 3–4.
// Чисто СИНТАКСИЧЕСКИЙ разбор (для литерала семантика не нужна); реализация — в Infrastructure.Roslyn.
// Цель вызова резолвит потребитель (HttpCallGraphBuilder: host → узел системы), провенанс — code.
public interface IHttpCallSource
{
    IReadOnlyList<HttpCallHit> Read(string root, CancellationToken ct = default);
}

// Найденный исходящий вызов: verb + нормализованный абсолютный URL (scheme://host[:port]/path, без query) +
// где (файл/строка) + короткая улика. Query отброшен на этапе сбора — секреты не тащим (secrets-config).
// ViaBase — адрес собран из относительного пути и BaseAddress (достовернее абсолютного литерала не бывает, но привязка к клиенту — вывод).
// ConfigKey — адрес клиента берётся из конфигурации (срез 3б): Url тогда ОТНОСИТЕЛЬНЫЙ путь, а базовый адрес по ключу
// (`Payments:Url`, как в appsettings; в env — `Payments__Url`) находит потребитель по конфигурации сервиса.
public sealed record HttpCallHit(string Method, string Url, string File, int Line, string Evidence, bool ViaBase = false, string? ConfigKey = null);
