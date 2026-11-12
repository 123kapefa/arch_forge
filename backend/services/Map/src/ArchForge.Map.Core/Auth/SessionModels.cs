namespace ArchForge.Map.Core.Auth;

/// <summary>Настройки сессии (adr/0013). Значения по умолчанию — из решения: автономная работа 14 дней, предупреждение за 3 дня.</summary>
public sealed class LoginSessionOptions
{
    public const string Section = "Auth";

    /// <summary>Сколько дней можно работать без подтверждения в сети с момента последнего успешного обмена токенов.</summary>
    public int GraceDays { get; init; } = 14;

    /// <summary>За сколько дней до конца автономного срока показывать предупреждение.</summary>
    public int WarnDays { get; init; } = 3;

    /// <summary>Допуск перевода часов назад (минуты): меньшие расхождения — обычная подстройка времени.</summary>
    public int ClockToleranceMinutes { get; init; } = 5;

    /// <summary>OAuth-клиент (публичный, с PKCE), зарегистрированный в realm.</summary>
    public string ClientId { get; init; } = "archforge-desktop";

    public string Scope { get; init; } = "openid";

    /// <summary>Адрес Map для возврата из браузера. Только loopback (http://localhost:5200). В Keycloak разрешён redirect URI
    /// <c>{RedirectBaseUrl}/api/auth/callback</c>.</summary>
    public string RedirectBaseUrl { get; init; } = "http://localhost:5200";

    /// <summary>Куда вернуть пользователя после входа (страница приложения). Фиксировано настройкой, не из запроса.</summary>
    public string FrontendUrl { get; init; } = "http://localhost:5173/";

    /// <summary>Папка данных сессии. null → папка данных пользователя.</summary>
    public string? Directory { get; init; }

    /// <summary>Пускать запросы с этой машины без заголовка Authorization как вошедшего пользователя (локальное приложение).
    /// Отключайте, если перед Map стоит прокси на этой же машине или он доступен по сети: тогда только токены.</summary>
    public bool AllowLocalSession { get; init; } = true;
}

/// <summary>Что хранится на машине. Токены наружу (во фронт) не отдаются.</summary>
/// <param name="LastOnlineUtc">Последний успешный обмен с сервером входа (вход или обновление). От него считается автономный срок.</param>
/// <param name="LastSeenUtc">Самое позднее время, которое видело приложение, — для защиты от перевода часов назад.</param>
public sealed record StoredSession(
    string Issuer, string Subject, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions,
    string AccessToken, DateTimeOffset AccessExpiresAt, string RefreshToken,
    DateTimeOffset LastOnlineUtc, DateTimeOffset LastSeenUtc);

public enum SessionState
{
    /// <summary>Не входили.</summary>
    None,
    /// <summary>Вошли, связь с сервером в порядке.</summary>
    Active,
    /// <summary>Вошли, сервер недоступен: работаем по кешу в пределах автономного срока.</summary>
    Offline,
    /// <summary>Автономный срок вышел: нужен новый вход.</summary>
    Expired,
    /// <summary>Часы переведены назад: сессии не доверяем, нужен новый вход.</summary>
    Clock,
    /// <summary>Режим разработки: вход не используется.</summary>
    Dev,
}

/// <summary>Состояние сессии для интерфейса: кто вошёл и до какого срока. Без токенов.</summary>
public sealed record SessionStatusDto(
    string State, string? Subject, DateTimeOffset? GraceEndsAt, int? DaysLeft, bool Warn, string Storage);

public sealed record TokenSet(string AccessToken, string? RefreshToken, int ExpiresInSeconds);

public enum OidcFailure { Network, Rejected, Invalid }

public sealed class OidcException(OidcFailure kind, string message) : Exception(message)
{
    public OidcFailure Kind { get; } = kind;
}

/// <summary>Хранилище сессии на диске (шифрованное). Реализация — в Infrastructure.</summary>
public interface ISessionVault
{
    /// <summary>Способ защиты ключа: dpapi | keychain | file.</summary>
    string Storage { get; }
    Task<StoredSession?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(StoredSession session, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>Обмен с сервером входа (OIDC, код + PKCE).</summary>
public interface IOidcClient
{
    Task<Uri> BuildAuthorizeUrlAsync(string redirectUri, string state, string codeChallenge, CancellationToken ct = default);
    Task<TokenSet> ExchangeCodeAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct = default);
    Task<TokenSet> RefreshAsync(string refreshToken, CancellationToken ct = default);
}
