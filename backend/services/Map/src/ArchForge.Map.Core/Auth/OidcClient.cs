using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ArchForge.SharedKernel.Auth;
using Microsoft.AspNetCore.WebUtilities;

namespace ArchForge.Map.Core.Auth;

/// <summary>OIDC-клиент для входа в браузере (код авторизации + PKCE, публичный клиент без секрета). Адреса берутся из discovery
/// сервера входа; издатель в discovery обязан совпасть с настроенным, адреса — быть https (http — только с AllowInsecureMetadata).</summary>
public sealed class OidcClient(HttpClient http, AuthOptions auth, Microsoft.Extensions.Options.IOptions<LoginSessionOptions> opt, TimeProvider clock) : IOidcClient
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
            // 400/401 на обновлении — сервер отказал (токен отозван или истёк); 5xx — недоступен, не отказ.
            if ((int)res.StatusCode is 400 or 401 or 403) throw new OidcException(OidcFailure.Rejected, "сервер входа отклонил запрос");
            if (!res.IsSuccessStatusCode) throw new OidcException(OidcFailure.Network, "сервер входа ответил ошибкой");
            if (res.Content.Headers.ContentLength > MaxResponseBytes) throw new OidcException(OidcFailure.Invalid, "ответ сервера входа слишком большой");
            TokenResponse? body;
            try { body = await res.Content.ReadFromJsonAsync<TokenResponse>(ct); }
            catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException) { throw new OidcException(OidcFailure.Invalid, "непонятный ответ сервера входа"); }
            if (body is null || string.IsNullOrEmpty(body.AccessToken)) throw new OidcException(OidcFailure.Invalid, "в ответе нет токена");
            return new TokenSet(body.AccessToken, body.RefreshToken, body.ExpiresIn);
        }
    }

    private async Task<Discovery> DiscoverAsync(CancellationToken ct)
    {
        if (_discovery is not null && clock.GetUtcNow() - _discoveredAt < TimeSpan.FromHours(1)) return _discovery;
        await _lock.WaitAsync(ct);
        try
        {
            if (_discovery is not null && clock.GetUtcNow() - _discoveredAt < TimeSpan.FromHours(1)) return _discovery;
            var baseUrl = (string.IsNullOrWhiteSpace(auth.InternalAuthority) ? auth.Authority : auth.InternalAuthority!).TrimEnd('/');
            Doc? doc;
            try { doc = await http.GetFromJsonAsync<Doc>(baseUrl + "/.well-known/openid-configuration", ct); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            { throw new OidcException(OidcFailure.Network, "сервер входа недоступен"); }
            catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException)
            { throw new OidcException(OidcFailure.Invalid, "непонятный ответ сервера входа"); }

            if (doc is null || !string.Equals(doc.Issuer?.TrimEnd('/'), auth.Authority.TrimEnd('/'), StringComparison.Ordinal))
                throw new OidcException(OidcFailure.Invalid, "издатель сервера входа не совпадает с настройкой");
            var insecureOk = auth.AllowInsecureMetadata;
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
