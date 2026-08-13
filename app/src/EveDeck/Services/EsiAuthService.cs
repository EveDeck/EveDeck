using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EveDeck.Services;

public sealed class EsiAuthService
{
    private const string ClientId = "a86176aac0314cc7aa3f94dc2535842f";
    private const string AuthUrl = "https://login.eveonline.com/v2/oauth/authorize";
    private const string TokenUrl = "https://login.eveonline.com/v2/oauth/token";
    private const string VerifyUrl = "https://login.eveonline.com/oauth/verify";
    private const string RedirectUri = "http://localhost:4080/callback/";

    public const string ScopeSkills = "esi-skills.read_skills.v1";
    // Added 2026-07-24 for the skill-queue alerts and the per-seat preview info flyout. All read-only.
    // Adding these to the login scope means existing linked characters must re-consent once to grant
    // them; a character authed before this change simply won't return skillqueue/location/ship/fatigue
    // until re-linked, and the consuming features degrade gracefully (they just show nothing for it).
    public const string ScopeSkillQueue = "esi-skills.read_skillqueue.v1";
    public const string ScopeLocation = "esi-location.read_location.v1";
    public const string ScopeShipType = "esi-location.read_ship_type.v1";
    public const string ScopeFatigue = "esi-characters.read_fatigue.v1";
    // Added 2026-07-24 to resolve the name of a player-owned Upwell structure a character is docked in
    // (the flyout's "Docked" line). Access-gated: only returns a name for structures the character can
    // dock at, which is fine since we only ask about the one they're currently docked in.
    public const string ScopeStructures = "esi-universe.read_structures.v1";
    // Added 2026-07-24 for the flyout's wallet-balance line.
    public const string ScopeWallet = "esi-wallet.read_character_wallet.v1";
    // Added 2026-07-28 for the seat-health "disconnected" alert (ESI online-status vs. the seat's
    // window still being present). Characters linked before this change won't return online status
    // until re-linked; the check degrades gracefully (skips that character) until then.
    public const string ScopeOnline = "esi-location.read_online.v1";

    // Requested on every login.
    private const string Scope = "publicData " + ScopeSkills
        + " " + ScopeSkillQueue + " " + ScopeLocation + " " + ScopeShipType + " " + ScopeFatigue
        + " " + ScopeStructures + " " + ScopeWallet + " " + ScopeOnline;

    private static readonly HttpClient _http = new();

    public async Task<EsiToken> AuthorizeAsync(CancellationToken ct)
    {
        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = ComputeCodeChallenge(codeVerifier);
        var state = Guid.NewGuid().ToString("N");

        var authUri = $"{AuthUrl}?response_type=code" +
            $"&client_id={Uri.EscapeDataString(ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&scope={Uri.EscapeDataString(Scope)}" +
            $"&state={state}" +
            $"&code_challenge={codeChallenge}" +
            $"&code_challenge_method=S256";

        using var listener = new HttpListener();
        listener.Prefixes.Add(RedirectUri);
        listener.Start();

        Process.Start(new ProcessStartInfo(authUri) { UseShellExecute = true });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(5));

        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            listener.Stop();
            throw new TimeoutException("ESI login timed out (5 min). Try again.");
        }

        var query = context.Request.QueryString;
        var code = query["code"];
        var returnedState = query["state"];

        var html = code is not null && returnedState == state
            ? "<html><body style='font-family:sans-serif;background:#0a0d14;color:#e2e8f0;padding:40px'><h2>✓ Login successful — you can close this tab.</h2></body></html>"
            : "<html><body style='font-family:sans-serif;background:#0a0d14;color:#ef4444;padding:40px'><h2>Login failed — check EveDeck.</h2></body></html>";
        var htmlBytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = htmlBytes.Length;
        await context.Response.OutputStream.WriteAsync(htmlBytes, ct);
        context.Response.Close();
        listener.Stop();

        if (code is null) throw new InvalidOperationException("EVE SSO returned no auth code.");
        if (returnedState != state) throw new InvalidOperationException("State mismatch — possible CSRF.");

        // Exchange code for token
        var tokenBody = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = codeVerifier,
        });
        return await ExchangeAsync(tokenBody, ct);
    }

    // Swaps a (possibly rotated) refresh token for a fresh access token. EVE may return a NEW refresh
    // token here — the caller must persist whatever comes back, or the old one eventually stops working.
    public async Task<EsiToken> RefreshAsync(EsiToken token, CancellationToken ct)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = token.RefreshToken,
            ["client_id"] = ClientId,
        });
        return await ExchangeAsync(body, ct, token.RefreshToken);
    }

    // existingRefreshToken: the refresh token this exchange was made WITH, if any. Used only as the
    // fallback when the response carries no replacement -- see the assignment below.
    private static async Task<EsiToken> ExchangeAsync(FormUrlEncodedContent body, CancellationToken ct, string? existingRefreshToken = null)
    {
        var tokenResp = await _http.PostAsync(TokenUrl, body, ct);
        if (!tokenResp.IsSuccessStatusCode)
        {
            var err = await tokenResp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"EVE SSO token exchange failed ({(int)tokenResp.StatusCode}): {err}");
        }

        var tokenDoc = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync(ct));
        var root = tokenDoc.RootElement;
        var accessToken = root.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("No access_token in EVE SSO response.");
        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
        // SSO usually rotates the refresh token, but a response that omits the field means "keep the
        // one you already have". Persisting the empty string instead would silently brick this
        // character -- every later refresh would post an empty refresh_token and be rejected, with
        // nothing short of re-linking able to recover it.
        if (string.IsNullOrEmpty(refreshToken)) refreshToken = existingRefreshToken ?? "";
        var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 1200;

        // Verify → get character identity + the scopes actually granted (which can be fewer than we
        // asked for if the user unticks one on the SSO consent screen).
        var verifyReq = new HttpRequestMessage(HttpMethod.Get, VerifyUrl);
        verifyReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var verifyResp = await _http.SendAsync(verifyReq, ct);
        verifyResp.EnsureSuccessStatusCode();
        var verifyDoc = JsonDocument.Parse(await verifyResp.Content.ReadAsStringAsync(ct));
        var verifyRoot = verifyDoc.RootElement;
        var characterId = verifyRoot.GetProperty("CharacterID").GetInt64();
        var characterName = verifyRoot.GetProperty("CharacterName").GetString()
            ?? throw new InvalidOperationException("No CharacterName in verify response.");
        var scopes = (verifyRoot.TryGetProperty("Scopes", out var sc) ? sc.GetString() ?? "" : "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        return new EsiToken
        {
            CharacterId = characterId,
            CharacterName = characterName,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            Scopes = scopes,
        };
    }

    private static string GenerateCodeVerifier()
        => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string ComputeCodeChallenge(string verifier)
        => Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
