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

/// <summary>Права и роли arch_forge — единый источник (adr/0013). В <c>auth_service</c> их объявляет
/// <c>tools/keycloak-stand/declare-app-rights.py</c> (держать в согласии с этим списком).</summary>
public static class ArchForgeRights
{
    /// <summary>Пользоваться приложением: открывать проекты, смотреть карту, настраивать профили.</summary>
    public const string Use = "archforge.use";

    /// <summary>Код приложения в auth_service (клиент-носитель ролей, адресат токенов — <c>Authentication:Audience</c>).</summary>
    public const string App = "archforge";

    /// <summary>Роль по умолчанию у каждого пользователя проекта и права, которые она даёт.</summary>
    public const string DefaultRole = "user";

    public static readonly string[] DefaultRolePermissions = [Use];
}

/// <summary>Права локальной личности dev: пользоваться приложением, но не администрировать.</summary>
public static class DevIdentity
{
    public const string Subject = "dev";
    public const string Scheme = "ArchForgeDev";
    public static readonly string[] Permissions = [ArchForgeRights.Use];
}
