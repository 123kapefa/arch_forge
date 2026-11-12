using System.Security.Cryptography;
using ArchForge.SharedKernel.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ArchForge.Map.Core.Auth;

/// <summary>Сессия входа на этой машине (adr/0013): вход по сети один раз, дальше — локально. Токены держит только Map.
/// Автономный срок считается от последнего успешного обмена с сервером входа; перевод часов назад срок не продлевает.
/// Сетевые вызовы идут вне замка: молчащий сервер входа не должен подвешивать обычные запросы.</summary>
public sealed class SessionService(ISessionVault vault, IOidcClient oidc, IOptions<LoginSessionOptions> options, AuthOptions auth,
    TimeProvider clock, ILogger<SessionService> log)
{
    private static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(10);
    private const int MaxPending = 20;
    private static readonly TimeSpan SeenSaveEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LoadRetryEvery = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (string Verifier, string RedirectUri, string Binder, DateTimeOffset Expires)> _pending = new(StringComparer.Ordinal);
    private StoredSession? _session;
    private bool _loaded;
    private DateTimeOffset _nextLoadAttempt;
    private volatile bool _offline;
    private LoginSessionOptions Opt => options.Value;

    // ── вход ──

    /// <summary>Начало входа: запоминает одноразовое состояние и проверочный код (PKCE), отдаёт адрес сервера входа для браузера.</summary>
    /// <returns>Адрес сервера входа и «привязка к браузеру»: её кладут в cookie браузера, который начал вход, и сверяют на возврате.
    /// Так чужой вызов /login (процесс на машине) не подменит аккаунт: у браузера пользователя этой привязки не будет.</returns>
    public async Task<(Uri Url, string Binder)> BeginLoginAsync(string redirectUri, CancellationToken ct = default)
    {
        var binder = Random(16);
        var state = Random(32);
        var verifier = Random(32);
        var challenge = Base64Url(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
        await _gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            foreach (var k in _pending.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToList()) _pending.Remove(k);
            if (_pending.Count >= MaxPending) throw new InvalidOperationException("слишком много незавершённых входов; повторите позже");
            _pending[state] = (verifier, redirectUri, binder, now + PendingTtl);
        }
        finally { _gate.Release(); }
        return (await oidc.BuildAuthorizeUrlAsync(redirectUri, state, challenge, ct), binder);
    }

    /// <summary>Завершение входа: состояние одноразовое и с ограниченным сроком; чужой или повторный вызов отклоняется.</summary>
    public async Task CompleteLoginAsync(string state, string code, string? binder, CancellationToken ct = default)
    {
        (string Verifier, string RedirectUri, string Binder, DateTimeOffset Expires) p;
        await _gate.WaitAsync(ct);
        try
        {
            if (!_pending.Remove(state, out p) || p.Expires <= clock.GetUtcNow()
                || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(binder ?? ""), System.Text.Encoding.UTF8.GetBytes(p.Binder)))
                throw new OidcException(OidcFailure.Rejected, "вход не начинался, устарел или начат в другом браузере; начните заново");
        }
        finally { _gate.Release(); }

        var tokens = await oidc.ExchangeCodeAsync(code, p.RedirectUri, p.Verifier, ct);   // сеть — вне замка
        if (string.IsNullOrEmpty(tokens.RefreshToken)) throw new OidcException(OidcFailure.Invalid, "сервер входа не выдал токен обновления");
        var now = clock.GetUtcNow();
        var s = Build(tokens, tokens.RefreshToken, now, now);

        await _gate.WaitAsync(ct);
        try
        {
            _session = s; _loaded = true; _offline = false;   // в памяти сразу: код авторизации уже потрачен
            await TrySaveAsync(s, ct);
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
        var live = state is SessionState.Active or SessionState.Offline;
        int? left = live && graceEnd is { } g ? Math.Max(0, (int)Math.Ceiling((g - clock.GetUtcNow()).TotalDays)) : null;
        return new SessionStatusDto(Name(state), live ? s!.Subject : null, live ? graceEnd : null, left,
            state == SessionState.Offline && left is { } d && d <= Opt.WarnDays, vault.Storage);
    }

    /// <summary>Сессия, по которой можно пускать локальные запросы (вошли и срок не вышел, часы не откручены); иначе null.</summary>
    public async Task<StoredSession?> GetUsableAsync(CancellationToken ct = default)
    {
        var s = await TouchAsync(ct);
        return Evaluate(s).State is SessionState.Active or SessionState.Offline ? s : null;
    }

    /// <summary>Фоновое обновление: продлевает токены, пока они близки к истечению или связь была потеряна. Сети нет — работаем по кешу;
    /// сервер отказал по существу (доступ отозван) — сессию стираем; срок вышел — стираем токены, оставляя запись «нужен вход».</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        var s = await TouchAsync(ct);
        var state = Evaluate(s).State;
        if (state == SessionState.Expired && s is { RefreshToken.Length: > 0 }) { await WipeTokensAsync(s, ct); return; }
        if (state is not (SessionState.Active or SessionState.Offline)) return;
        if (!_offline && s!.AccessExpiresAt - clock.GetUtcNow() > TimeSpan.FromMinutes(2)) return;

        var used = s!.RefreshToken;
        TokenSet tokens;
        try { tokens = await oidc.RefreshAsync(used, ct); }   // сеть — вне замка
        catch (OidcException e) when (e.Kind == OidcFailure.Rejected)
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (_session?.RefreshToken == used)   // за время запроса не вышли и не вошли заново
                {
                    log.LogWarning("Сервер входа отклонил обновление сессии — доступ отозван или истёк; сессия стёрта.");
                    await vault.ClearAsync(ct); _session = null;
                }
            }
            finally { _gate.Release(); }
            return;
        }
        catch (OidcException e)
        {
            _offline = true;   // сеть/ответ сервера недоступны: остаёмся по кешу в пределах срока
            log.LogInformation("Сервер входа недоступен ({Kind}); работаем по кешу.", e.Kind);
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var cur = _session;
            if (cur is null || cur.RefreshToken != used) return;   // выход или новый вход во время запроса
            var t = clock.GetUtcNow();
            var newRefresh = tokens.RefreshToken ?? cur.RefreshToken;
            StoredSession next;
            try { next = Build(tokens, newRefresh, t, Max(t, cur.LastSeenUtc)); }
            catch (OidcException e)
            {
                // Токен непонятный, но refresh уже выдан и старый, возможно, погашен: новый не теряем.
                log.LogWarning("Обновление вернуло неподходящий токен ({Message}); срок не продлён.", e.Message);
                next = cur with { RefreshToken = newRefresh };
                _offline = true;
                _session = next; await TrySaveAsync(next, ct);
                return;
            }
            _session = next; _offline = false;   // в памяти сразу: ошибка записи не должна терять свежий refresh-токен
            await TrySaveAsync(next, ct);
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

    /// <summary>Загружает сессию и продвигает «последнее виденное время» (но не при подозрительных часах).
    /// Сбой чтения не залипает: повтор не чаще раза в 30 секунд.</summary>
    private async Task<StoredSession?> TouchAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            if (!_loaded && now >= _nextLoadAttempt)
            {
                try { _session = await vault.LoadAsync(ct); _loaded = true; }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogWarning(e, "Не удалось прочитать сохранённую сессию; повторю позже.");
                    _nextLoadAttempt = now + LoadRetryEvery;
                }
            }
            var s = _session;
            if (s is null) return null;
            if (Evaluate(s).State != SessionState.Clock && now - s.LastSeenUtc >= SeenSaveEvery)
            {
                var seen = s with { LastSeenUtc = now };
                if (await TrySaveAsync(seen, ct)) { _session = seen; s = seen; }   // метка времени растёт, только если записалась
            }
            return s;
        }
        finally { _gate.Release(); }
    }

    private async Task WipeTokensAsync(StoredSession s, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_session is not { RefreshToken.Length: > 0 } cur || cur.RefreshToken != s.RefreshToken) return;
            var wiped = cur with { AccessToken = "", RefreshToken = "" };   // просроченный токен обновления на диске не нужен
            _session = wiped;
            await TrySaveAsync(wiped, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> TrySaveAsync(StoredSession s, CancellationToken ct)
    {
        try { await vault.SaveAsync(s, ct); return true; }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Не удалось сохранить сессию на диск; она остаётся только в памяти до перезапуска.");
            return false;
        }
    }

    private StoredSession Build(TokenSet tokens, string refresh, DateTimeOffset lastOnline, DateTimeOffset lastSeen)
    {
        JsonWebToken jwt;
        try { jwt = new JsonWebTokenHandler().ReadJsonWebToken(tokens.AccessToken); }
        catch (Exception) { throw new OidcException(OidcFailure.Invalid, "сервер входа вернул непонятный токен"); }

        // Токен получен напрямую от сервера входа по TLS (подпись здесь не проверяется), но издателя, адресата и субъект сверяем.
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
