using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using System.Text.Json.Serialization;
using ArchForge.SharedKernel.Auth;
using Microsoft.AspNetCore.WebUtilities;

namespace ArchForge.Map.Core.Auth;

/// <summary>OIDC-клиент для входа в браузере (код авторизации + PKCE, публичный клиент без секрета). Адреса берутся из discovery
/// сервера входа; издатель в discovery обязан совпасть с настроенным, адреса — быть https (http — только с AllowInsecureMetadata).</summary>
public sealed class OidcClient(HttpClient http, AuthOptions auth, Microsoft.Extensions.Options.IOptions<LoginSessionOptions> opt, TimeProvider clock, Microsoft.Extensions.Hosting.IHostEnvironment env) : IOidcClient
{
    private const int MaxResponseBytes = 1024 * 1024;
    private sealed record Discovery(string AuthorizationEndpoint, string TokenEndpoint);

    private Discovery? _discovery;
    private DateTimeOffset _discoveredAt;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<Uri> BuildAuthorizeUrlAsync(string redirectUri, string state, string codeChallenge, CancellationToken ct = default)
    {
        var d = await DiscoverAsync(ct);
        var url = QueryHelpers.AddQueryString(d.AuthorizationEndpoint, new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = opt.Value.ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = opt.Value.Scope,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        });
        return new Uri(url);
    }

    public async Task<TokenSet> ExchangeCodeAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct = default) =>
        await TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = opt.Value.ClientId,
            ["code_verifier"] = codeVerifier,
        }, ct);

    public async Task<TokenSet> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
        await TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = opt.Value.ClientId,
        }, ct);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private async Task<TokenSet> TokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        var d = await DiscoverAsync(ct);
        HttpResponseMessage res;
        try { res = await http.PostAsync(d.TokenEndpoint, new FormUrlEncodedContent(form), ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        { throw new OidcException(OidcFailure.Network, "сервер входа недоступен"); }

        using (res)
        {
            // Отказ по существу — только 400 invalid_grant (код/токен обновления отозван или истёк). Прочие 4xx (неверный клиент, настройка)
            // — не причина стирать сессию; 5xx — сервер недоступен.
            if ((int)res.StatusCode == 400 && await IsInvalidGrantAsync(res, ct)) throw new OidcException(OidcFailure.Rejected, "сервер входа отклонил запрос");
            if ((int)res.StatusCode is >= 400 and < 500) throw new OidcException(OidcFailure.Invalid, "сервер входа не принял запрос");
            if (!res.IsSuccessStatusCode) throw new OidcException(OidcFailure.Network, "сервер входа ответил ошибкой");
            TokenResponse? body;
            try { body = await ReadLimitedAsync<TokenResponse>(res.Content, ct); }
            catch (Exception e) when (e is JsonException or NotSupportedException) { throw new OidcException(OidcFailure.Invalid, "непонятный ответ сервера входа"); }
            if (body is null || string.IsNullOrEmpty(body.AccessToken)) throw new OidcException(OidcFailure.Invalid, "в ответе нет токена");
            return new TokenSet(body.AccessToken, body.RefreshToken, body.ExpiresIn);
        }
    }

    /// <summary>Читает JSON не больше <see cref="MaxResponseBytes"/>, даже если сервер не указал длину (chunked).</summary>
    private static async Task<T?> ReadLimitedAsync<T>(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[8192]; int n; long total = 0;
        while ((n = await stream.ReadAsync(buf, ct)) > 0)
        {
            total += n;
            if (total > MaxResponseBytes) throw new OidcException(OidcFailure.Invalid, "ответ сервера входа слишком большой");
            ms.Write(buf, 0, n);
        }
        ms.Position = 0;
        return await JsonSerializer.DeserializeAsync<T>(ms, cancellationToken: ct);
    }

    private static async Task<bool> IsInvalidGrantAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            var err = await ReadLimitedAsync<ErrorBody>(res.Content, ct);
            return string.Equals(err?.Error, "invalid_grant", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or HttpRequestException or OidcException) { return false; }
    }

    private sealed record ErrorBody([property: JsonPropertyName("error")] string? Error);

    private async Task<Discovery> DiscoverAsync(CancellationToken ct)
    {
        if (_discovery is not null && clock.GetUtcNow() - _discoveredAt < TimeSpan.FromHours(1)) return _discovery;
        await _lock.WaitAsync(ct);
        try
        {
            if (_discovery is not null && clock.GetUtcNow() - _discoveredAt < TimeSpan.FromHours(1)) return _discovery;
            var baseUrl = (string.IsNullOrWhiteSpace(auth.InternalAuthority) ? auth.Authority : auth.InternalAuthority!).TrimEnd('/');
            Doc? doc;
            try
            {
                using var res = await http.GetAsync(baseUrl + "/.well-known/openid-configuration", HttpCompletionOption.ResponseHeadersRead, ct);
                if (!res.IsSuccessStatusCode) throw new HttpRequestException("discovery: " + (int)res.StatusCode);
                doc = await ReadLimitedAsync<Doc>(res.Content, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            { throw new OidcException(OidcFailure.Network, "сервер входа недоступен"); }
            catch (Exception e) when (e is JsonException or NotSupportedException)
            { throw new OidcException(OidcFailure.Invalid, "непонятный ответ сервера входа"); }

            if (doc is null || !string.Equals(doc.Issuer?.TrimEnd('/'), auth.Authority.TrimEnd('/'), StringComparison.Ordinal))
                throw new OidcException(OidcFailure.Invalid, "издатель сервера входа не совпадает с настройкой");
            var insecureOk = auth.AllowInsecureMetadata && env.IsDevelopment();   // как и в проверке токена: http только в Development
            foreach (var u in new[] { doc.AuthorizationEndpoint, doc.TokenEndpoint })
                if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !insecureOk))
                    throw new OidcException(OidcFailure.Invalid, "адрес сервера входа должен быть https");
            _discovery = new Discovery(doc.AuthorizationEndpoint!, doc.TokenEndpoint!);
            _discoveredAt = clock.GetUtcNow();
            return _discovery;
        }
        finally { _lock.Release(); }
    }

    private sealed record Doc(
        [property: JsonPropertyName("issuer")] string? Issuer,
        [property: JsonPropertyName("authorization_endpoint")] string? AuthorizationEndpoint,
        [property: JsonPropertyName("token_endpoint")] string? TokenEndpoint);
}
