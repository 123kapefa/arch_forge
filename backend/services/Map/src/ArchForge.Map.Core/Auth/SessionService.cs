using System.Security.Cryptography;
using ArchForge.SharedKernel.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ArchForge.Map.Core.Auth;

/// <summary>Сессия входа на этой машине (adr/0013): вход по сети один раз, дальше — локально. Токены держит только Map.
/// Автономный срок считается от последнего успешного обмена с сервером входа; перевод часов назад срок не продлевает.</summary>
public sealed class SessionService(ISessionVault vault, IOidcClient oidc, IOptions<LoginSessionOptions> options, AuthOptions auth,
    TimeProvider clock, ILogger<SessionService> log)
{
    private static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(10);
    private const int MaxPending = 20;
    private static readonly TimeSpan SeenSaveEvery = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (string Verifier, string RedirectUri, DateTimeOffset Expires)> _pending = new(StringComparer.Ordinal);
    private StoredSession? _session;
    private bool _loaded;
    private bool _offline;
    private LoginSessionOptions Opt => options.Value;

    // ── вход ──

    /// <summary>Начало входа: запоминает одноразовое состояние и проверочный код (PKCE), отдаёт адрес сервера входа для браузера.</summary>
    public async Task<Uri> BeginLoginAsync(string redirectUri, CancellationToken ct = default)
    {
        var state = Random(32);
        var verifier = Random(32);
        var challenge = Base64Url(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
        await _gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            foreach (var k in _pending.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToList()) _pending.Remove(k);
            if (_pending.Count >= MaxPending) throw new InvalidOperationException("слишком много незавершённых входов; повторите позже");
            _pending[state] = (verifier, redirectUri, now + PendingTtl);
        }
        finally { _gate.Release(); }
        return await oidc.BuildAuthorizeUrlAsync(redirectUri, state, challenge, ct);
    }

    /// <summary>Завершение входа: состояние одноразовое и с ограниченным сроком; чужой или повторный вызов отклоняется.</summary>
    public async Task CompleteLoginAsync(string state, string code, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_pending.Remove(state, out var p) || p.Expires <= clock.GetUtcNow())
                throw new OidcException(OidcFailure.Rejected, "вход не начинался или устарел; начните заново");
            var tokens = await oidc.ExchangeCodeAsync(code, p.RedirectUri, p.Verifier, ct);
            if (string.IsNullOrEmpty(tokens.RefreshToken)) throw new OidcException(OidcFailure.Invalid, "сервер входа не выдал токен обновления");
            var now = clock.GetUtcNow();
            var s = Build(tokens, tokens.RefreshToken, now, now);
            await vault.SaveAsync(s, ct);
            _session = s; _loaded = true; _offline = false;
        }
        finally { _gate.Release(); }
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await vault.ClearAsync(ct); _session = null; _loaded = true; _offline = false; _pending.Clear(); }
        finally { _gate.Release(); }
    }

    // ── состояние ──

    public async Task<SessionStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        var s = await TouchAsync(ct);
        var (state, graceEnd) = Evaluate(s);
        var left = graceEnd is { } g && state is SessionState.Active or SessionState.Offline
            ? Math.Max(0, (int)Math.Ceiling((g - clock.GetUtcNow()).TotalDays)) : (int?)null;
        return new SessionStatusDto(Name(state), state is SessionState.Active or SessionState.Offline ? s!.Subject : null,
            state is SessionState.Active or SessionState.Offline ? graceEnd : null, left,
            state == SessionState.Offline && left is { } d && d <= Opt.WarnDays, vault.Storage);
    }

    /// <summary>Сессия, по которой можно пускать локальные запросы (вошли и срок не вышел, часы не откручены); иначе null.</summary>
    public async Task<StoredSession?> GetUsableAsync(CancellationToken ct = default)
    {
        var s = await TouchAsync(ct);
        return Evaluate(s).State is SessionState.Active or SessionState.Offline ? s : null;
    }

    /// <summary>Фоновое обновление: продлевает токены, пока они близки к истечению или связь была потеряна. Сети нет — работаем по кешу;
    /// сервер отклонил обновление (доступ отозван) — сессию стираем.</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        var s = await TouchAsync(ct);
        if (Evaluate(s).State is not (SessionState.Active or SessionState.Offline)) return;
        var now = clock.GetUtcNow();
        if (!_offline && s!.AccessExpiresAt - now > TimeSpan.FromMinutes(2)) return;

        await _gate.WaitAsync(ct);
        try
        {
            var cur = _session;
            if (cur is null) return;
            try
            {
                var tokens = await oidc.RefreshAsync(cur.RefreshToken, ct);
                var t = clock.GetUtcNow();
                var next = Build(tokens, tokens.RefreshToken ?? cur.RefreshToken, t, Max(t, cur.LastSeenUtc));
                await vault.SaveAsync(next, ct);
                _session = next; _offline = false;
            }
            catch (OidcException e) when (e.Kind == OidcFailure.Rejected)
            {
                log.LogWarning("Сервер входа отклонил обновление сессии — доступ отозван или истёк; сессия стёрта.");
                await vault.ClearAsync(ct); _session = null;
            }
            catch (OidcException e)
            {
                _offline = true;   // сеть/ответ сервера недоступны: остаёмся по кешу в пределах срока
                log.LogInformation("Сервер входа недоступен ({Kind}); работаем по кешу.", e.Kind);
            }
        }
        finally { _gate.Release(); }
    }

    // ── внутреннее ──

    internal (SessionState State, DateTimeOffset? GraceEnd) Evaluate(StoredSession? s)
    {
        if (s is null) return (SessionState.None, null);
        var now = clock.GetUtcNow();
        if (now < s.LastSeenUtc - TimeSpan.FromMinutes(Opt.ClockToleranceMinutes)) return (SessionState.Clock, null);
        var end = s.LastOnlineUtc + TimeSpan.FromDays(Opt.GraceDays);
        if (now >= end) return (SessionState.Expired, end);
        return (_offline ? SessionState.Offline : SessionState.Active, end);
    }

    /// <summary>Загружает сессию (один раз) и продвигает «последнее виденное время», но не при подозрительных часах.</summary>
    private async Task<StoredSession?> TouchAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_loaded)
            {
                try { _session = await vault.LoadAsync(ct); }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Не удалось прочитать сохранённую сессию — считаем, что входа нет."); _session = null; }
                _loaded = true;
            }
            var s = _session;
            if (s is null) return null;
            var now = clock.GetUtcNow();
            if (Evaluate(s).State != SessionState.Clock && now - s.LastSeenUtc >= SeenSaveEvery)
            {
                s = s with { LastSeenUtc = now };
                _session = s;
                try { await vault.SaveAsync(s, ct); } catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Не удалось сохранить метку времени сессии."); }
            }
            return s;
        }
        finally { _gate.Release(); }
    }

    private StoredSession Build(TokenSet tokens, string refresh, DateTimeOffset lastOnline, DateTimeOffset lastSeen)
    {
        JsonWebToken jwt;
        try { jwt = new JsonWebTokenHandler().ReadJsonWebToken(tokens.AccessToken); }
        catch (Exception) { throw new OidcException(OidcFailure.Invalid, "сервер входа вернул непонятный токен"); }

        // Токен получен напрямую от сервера входа по TLS, но свою роль всё равно сверяем: издатель, адресат, субъект.
        if (!string.Equals(jwt.Issuer?.TrimEnd('/'), auth.Authority.TrimEnd('/'), StringComparison.Ordinal))
            throw new OidcException(OidcFailure.Invalid, "токен выдан не тем издателем");
        if (!string.IsNullOrEmpty(auth.Audience) && !jwt.Audiences.Contains(auth.Audience, StringComparer.Ordinal))
            throw new OidcException(OidcFailure.Invalid, "токен адресован не этому приложению");
        if (string.IsNullOrEmpty(jwt.Subject)) throw new OidcException(OidcFailure.Invalid, "в токене нет субъекта");

        string[] Strings(string claim) => [.. jwt.Claims.Where(c => c.Type == claim).Select(c => c.Value).Take(256)];
        var expires = lastOnline.AddSeconds(Math.Clamp(tokens.ExpiresInSeconds, 30, 24 * 3600));
        return new StoredSession(auth.Authority.TrimEnd('/'), jwt.Subject, Strings(AuthClaims.Role), Strings(AuthClaims.Permissions),
            tokens.AccessToken, expires, refresh, lastOnline, lastSeen);
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    private static string Name(SessionState s) => s.ToString().ToLowerInvariant();
    private static string Random(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));
    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
