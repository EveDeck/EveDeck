using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace EveDeck.Services;

// Authenticated ESI access for the linked characters. Owns transparent token refresh (via the token
// store) and respects ESI's error-limit budget so a burst of failures can't get the app IP-banned.
public sealed class EsiClient
{
    private const string BaseUrl = "https://esi.evetech.net/latest";

    // ESI requires a descriptive User-Agent so Fenris Creations can contact the app author over misbehaviour.
    private static readonly HttpClient _http = CreateHttp();

    private readonly EsiAuthService _auth;
    private readonly EsiTokenStore _store;

    // Per-character refresh mutex so a tick that fires several requests for one character doesn't
    // kick off two concurrent refreshes and clobber each other's rotated refresh token.
    private readonly Dictionary<long, SemaphoreSlim> _refreshLocks = new();
    private readonly object _locksGate = new();

    // When ESI tells us the error budget is spent, hold all requests until this UTC time.
    private DateTimeOffset _backoffUntil = DateTimeOffset.MinValue;

    // Characters whose ESI grant is gone -- a token we had just refreshed was STILL rejected, which
    // only re-linking can fix. Nothing is requested for them until then. Without this each feature
    // keeps asking on its own timer forever: one live install logged 12,776 rejected calls in a
    // single day (14% of the whole log) while every ESI-backed feature sat silently dead.
    private readonly HashSet<long> _needsReauth = new();
    private readonly object _reauthGate = new();

    // Raised ONCE per character, the moment its ESI access breaks unrecoverably, so the UI can say so
    // out loud instead of leaving the user to discover it in a log file.
    public event Action<long>? ReauthRequired;

    public bool NeedsReauth(long characterId)
    {
        lock (_reauthGate) return _needsReauth.Contains(characterId);
    }

    // Called after the user re-links a character, so its requests start flowing again.
    public void ClearReauth(long characterId)
    {
        lock (_reauthGate) _needsReauth.Remove(characterId);
    }

    private void MarkNeedsReauth(long characterId)
    {
        bool isFirst;
        lock (_reauthGate) isFirst = _needsReauth.Add(characterId);
        if (isFirst) ReauthRequired?.Invoke(characterId);
    }

    public EsiClient(EsiAuthService auth, EsiTokenStore store)
    {
        _auth = auth;
        _store = store;
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EveDeck/PI (github.com/EveDeck/EveDeck)");
        return http;
    }

    // GET an authenticated ESI resource for a character and deserialize it. Returns default(T) on a
    // 404/204 (character has no colonies, empty page, etc.) so callers can treat "nothing" uniformly.
    public async Task<T?> GetAsync<T>(string path, long characterId, CancellationToken ct)
    {
        var resp = await SendAuthedAsync(path, characterId, ct);
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return default;
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(json);
    }

    // GET an X-Pages paginated collection, concatenating every page. Used for /assets, which can span
    // dozens of pages for an asset-heavy character.
    public async Task<List<T>> GetPagedAsync<T>(string path, long characterId, CancellationToken ct)
    {
        var results = new List<T>();
        var sep = path.Contains('?') ? '&' : '?';

        var first = await SendAuthedAsync($"{path}{sep}page=1", characterId, ct);
        if (first.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return results;
        first.EnsureSuccessStatusCode();
        AppendPage(results, await first.Content.ReadAsStringAsync(ct));

        var pages = 1;
        if (first.Headers.TryGetValues("X-Pages", out var xp) && int.TryParse(xp.FirstOrDefault(), out var n))
            pages = Math.Max(1, n);

        for (var p = 2; p <= pages; p++)
        {
            var resp = await SendAuthedAsync($"{path}{sep}page={p}", characterId, ct);
            if (!resp.IsSuccessStatusCode) break;
            AppendPage(results, await resp.Content.ReadAsStringAsync(ct));
        }
        return results;

        static void AppendPage(List<T> acc, string json)
        {
            var page = JsonSerializer.Deserialize<List<T>>(json);
            if (page is not null) acc.AddRange(page);
        }
    }

    private async Task<HttpResponseMessage> SendAuthedAsync(string path, long characterId, CancellationToken ct)
    {
        var wait = _backoffUntil - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);

        if (NeedsReauth(characterId))
            throw new EsiAuthException($"ESI access for character {characterId} needs re-authorisation -- re-link the character.");

        var (resp, usedAccessToken) = await SendOnceAsync(path, characterId, null, ct);
        if (resp.StatusCode != HttpStatusCode.Unauthorized) return resp;

        // A 401 on a token we believed valid: ESI can invalidate an access token ahead of its stated
        // expiry (grant revoked, scopes changed, SSO-side invalidation), and IsExpired cannot see any
        // of that. Force one refresh and retry before concluding anything -- that alone recovers the
        // ordinary case with no user action at all.
        resp.Dispose();
        try
        {
            (resp, _) = await SendOnceAsync(path, characterId, usedAccessToken, ct);
        }
        catch (EsiAuthException)
        {
            // The refresh itself was rejected, so the grant is genuinely gone.
            MarkNeedsReauth(characterId);
            throw;
        }

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            resp.Dispose();
            MarkNeedsReauth(characterId);
            throw new EsiAuthException(
                $"ESI rejected a freshly refreshed token for character {characterId} -- the grant was revoked; re-link the character.");
        }
        return resp;
    }

    // One authenticated attempt. Returns the access token it actually used, so a 401 can ask for a
    // refresh of *that specific* token rather than blindly refreshing whatever is current.
    private async Task<(HttpResponseMessage Response, string UsedAccessToken)> SendOnceAsync(
        string path, long characterId, string? invalidAccessToken, CancellationToken ct)
    {
        var token = await GetValidTokenAsync(characterId, ct, invalidAccessToken);
        var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var resp = await _http.SendAsync(req, ct);
        ObserveErrorLimit(resp);
        return (resp, token.AccessToken);
    }

    // Returns a token guaranteed unexpired, refreshing (and re-persisting the rotated refresh token)
    // if needed. Throws EsiAuthException if the character isn't linked or the refresh is rejected.
    //
    // invalidAccessToken: an access token ESI just rejected with a 401. Supplying it forces a refresh
    // even though the token still looks unexpired locally -- but only while the store still holds
    // that same token, so several callers that each got a 401 trigger ONE refresh between them
    // rather than one apiece (which would rotate the refresh token repeatedly for no reason).
    public async Task<EsiToken> GetValidTokenAsync(long characterId, CancellationToken ct, string? invalidAccessToken = null)
    {
        var token = _store.Get(characterId)
            ?? throw new EsiAuthException($"Character {characterId} is not linked to ESI.");
        if (!token.IsExpired && invalidAccessToken is null) return token;

        var gate = LockFor(characterId);
        await gate.WaitAsync(ct);
        try
        {
            // Re-read inside the lock: another caller may have refreshed while we waited.
            token = _store.Get(characterId) ?? token;
            if (!token.IsExpired && token.AccessToken != invalidAccessToken) return token;

            try
            {
                var refreshed = await _auth.RefreshAsync(token, ct);
                _store.Put(refreshed);
                return refreshed;
            }
            catch (Exception ex)
            {
                throw new EsiAuthException(
                    $"ESI session for {token.CharacterName} expired and could not be refreshed — re-link the character.", ex);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private void ObserveErrorLimit(HttpResponseMessage resp)
    {
        // ESI publishes a rolling error budget; when it hits 0 (or we get a 420) we must stop until the
        // window resets or Fenris Creations escalates to an IP ban.
        var remain = HeaderInt(resp, "X-ESI-Error-Limit-Remain");
        var reset = HeaderInt(resp, "X-ESI-Error-Limit-Reset");
        if ((int)resp.StatusCode == 420 || remain is <= 0)
        {
            var seconds = reset ?? 60;
            _backoffUntil = DateTimeOffset.UtcNow.AddSeconds(seconds);
        }

        static int? HeaderInt(HttpResponseMessage r, string name)
            => r.Headers.TryGetValues(name, out var v) && int.TryParse(v.FirstOrDefault(), out var i) ? i : null;
    }

    private SemaphoreSlim LockFor(long characterId)
    {
        lock (_locksGate)
        {
            if (!_refreshLocks.TryGetValue(characterId, out var s))
                _refreshLocks[characterId] = s = new SemaphoreSlim(1, 1);
            return s;
        }
    }
}

public sealed class EsiAuthException : Exception
{
    public EsiAuthException(string message, Exception? inner = null) : base(message, inner) { }
}
