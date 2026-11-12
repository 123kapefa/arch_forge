namespace ArchForge.SharedKernel.Auth;

/// <summary>Секция <c>Authentication</c> (adr/0013): откуда брать ключи подписи и что ожидать в токене.
/// Названия совпадают с руководством auth_service, чтобы настройки переносились без переделки.</summary>
public sealed class AuthOptions
{
    public const string Section = "Authentication";

    /// <summary>Адрес realm сервиса входа, например https://id.example.com/realms/archforge.</summary>
    public string Authority { get; init; } = "";

    /// <summary>Тот же realm изнутри сети (контейнер), если Authority снаружи недоступен. Из него берутся ключи.</summary>
    public string? InternalAuthority { get; init; }

    /// <summary>Кому адресован токен (aud).</summary>
    public string Audience { get; init; } = "";

    /// <summary>Только Development: разрешить http-адрес сервера входа. В остальных окружениях всегда https.</summary>
    public bool AllowInsecureMetadata { get; init; }

    /// <summary>Режим разработки (adr/0013, п. 10): локальная личность <c>dev</c> без сети. Включается явно, только в Development
    /// и только для запросов с loopback; вместе с Authority не допускается.</summary>
    public bool DevMode { get; init; }

    /// <summary>Требовать вход для всего API по умолчанию (политика по умолчанию — «нужен вошедший»). Нужно, если Map доступен не только
    /// с этой машины. Без флага (локальное приложение, adr/0013 п. 9) вход не закрывает функции, кроме тех, что требуют его явно.</summary>
    public bool RequireLogin { get; init; }
}

/// <summary>Как приложение проверяет вход (для /api/me и журнала).</summary>
public sealed record AuthMode(bool DevMode);

public static class AuthClaims
{
    public const string Subject = "sub";
    public const string Role = "role";
    public const string Permissions = "permissions";
}

/// <summary>Права локальной личности dev: пользоваться приложением, но не администрировать.</summary>
public static class DevIdentity
{
    public const string Subject = "dev";
    public const string Scheme = "ArchForgeDev";
    public static readonly string[] Permissions = ["archforge.use"];
}
