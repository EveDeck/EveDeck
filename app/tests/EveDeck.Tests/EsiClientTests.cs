using System.IO;
using System.Net;
using System.Net.Http;
using EveDeck.Services;
using Xunit;

namespace EveDeck.Tests;

// Covers EsiClient's 401 handling: the forced refresh + retry that recovers a server-invalidated
// token, and the needs-reauth park that stops a dead grant being retried forever. This path shipped
// untested and immediately cost a real outage -- a re-link that stored a good token left every
// character blocked because one of two call sites forgot to lift the park -- so the park is now
// self-clearing and that behaviour is pinned here.
public class EsiClientTests : IDisposable
{
    private const long CharId = 90000042;

    private readonly string _tempDir;

    public EsiClientTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // -- Doubles -------------------------------------------------------------------------------

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _statuses;
        public readonly List<string> SeenAuthTokens = new();

        public StubHandler(params HttpStatusCode[] statuses) => _statuses = new Queue<HttpStatusCode>(statuses);

        public int CallCount => SeenAuthTokens.Count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            SeenAuthTokens.Add(request.Headers.Authorization?.Parameter ?? "");
            var status = _statuses.Count > 0 ? _statuses.Dequeue() : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("{\"ok\":true}"),
            });
        }
    }

    private sealed class FakeRefresher : IEsiTokenRefresher
    {
        private readonly EsiTokenStore _store;
        private readonly bool _throws;
        public int RefreshCount;

        public FakeRefresher(EsiTokenStore store, bool throws = false)
        {
            _store = store;
            _throws = throws;
        }

        public Task<EsiToken> RefreshAsync(EsiToken token, CancellationToken ct)
        {
            RefreshCount++;
            if (_throws) throw new InvalidOperationException("SSO rejected the refresh token.");

            // Mirror the real service: a refresh yields a NEW access token, and the caller persists it.
            var refreshed = Clone(token);
            refreshed.AccessToken = $"access-{RefreshCount}";
            refreshed.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20);
            _store.Put(refreshed);
            return Task.FromResult(refreshed);
        }
    }

    private static EsiToken Clone(EsiToken t) => new()
    {
        CharacterId = t.CharacterId,
        CharacterName = t.CharacterName,
        RefreshToken = t.RefreshToken,
        AccessToken = t.AccessToken,
        ExpiresAt = t.ExpiresAt,
        Scopes = new List<string>(t.Scopes),
    };

    private EsiTokenStore StoreWithLiveToken(string accessToken = "access-original")
    {
        var store = new EsiTokenStore(_tempDir);
        store.Put(new EsiToken
        {
            CharacterId = CharId,
            CharacterName = "Test Pilot",
            RefreshToken = "refresh-secret",
            AccessToken = accessToken,
            // Comfortably unexpired: every refresh in these tests is therefore driven by a 401,
            // never by the clock, which is exactly the path under test.
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20),
            Scopes = new() { "publicData" },
        });
        return store;
    }

    // -- The recovering case -------------------------------------------------------------------

    [Fact]
    public async Task Get_RetriesWithARefreshedToken_WhenEsiReturns401()
    {
        var store = StoreWithLiveToken();
        var refresher = new FakeRefresher(store);
        var handler = new StubHandler(HttpStatusCode.Unauthorized, HttpStatusCode.OK);
        var client = new EsiClient(refresher, store, handler);

        var result = await client.GetAsync<Dictionary<string, bool>>("/x/", CharId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, refresher.RefreshCount);
        Assert.Equal(2, handler.CallCount);
        // The retry must carry the NEW token, not the one ESI just rejected.
        Assert.Equal("access-original", handler.SeenAuthTokens[0]);
        Assert.Equal("access-1", handler.SeenAuthTokens[1]);
        Assert.False(client.NeedsReauth(CharId));
    }

    [Fact]
    public async Task Get_DoesNotRefresh_WhenTheFirstCallSucceeds()
    {
        var store = StoreWithLiveToken();
        var refresher = new FakeRefresher(store);
        var handler = new StubHandler(HttpStatusCode.OK);
        var client = new EsiClient(refresher, store, handler);

        await client.GetAsync<Dictionary<string, bool>>("/x/", CharId, CancellationToken.None);

        Assert.Equal(0, refresher.RefreshCount);
        Assert.Equal(1, handler.CallCount);
    }

    // -- The unrecoverable case ----------------------------------------------------------------

    [Fact]
    public async Task Get_ParksTheCharacter_WhenARefreshedTokenIsAlsoRejected()
    {
        var store = StoreWithLiveToken();
        var refresher = new FakeRefresher(store);
        var handler = new StubHandler(HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized);
        var client = new EsiClient(refresher, store, handler);

        var raised = new List<long>();
        client.ReauthRequired += id => raised.Add(id);

        await Assert.ThrowsAsync<EsiAuthException>(
            () => client.GetAsync<Dictionary<string, bool>>("/x/", CharId, CancellationToken.None));

        Assert.True(client.NeedsReauth(CharId));
        Assert.Equal(new[] { CharId }, raised);
    }

    [Fact]
    public async Task Get_ParksTheCharacter_WhenTheRefreshItselfFails()
    {
        var store = StoreWithLiveToken();
        var refresher = new FakeRefresher(store, throws: true);
        var handler = new StubHandler(HttpStatusCode.Unauthorized);
        var client = new EsiClient(refresher, store, handler);

        await Assert.ThrowsAsync<EsiAuthException>(
            () => client.GetAsync<Dictionary<string, bool>>("/x/", CharId, CancellationToken.None));

        Assert.True(client.NeedsReauth(CharId));
    }

    // This is the whole point of the park: the failure it replaced retried every 30s for four days,
    // 12,776 times in one day. A parked character must cost ZERO further HTTP calls.
    [Fact]
    public async Task Get_MakesNoFurtherRequests_OnceParked()
    {
        var store = StoreWithLiveToken();
        var refresher = new FakeRefresher(store);
        var handler = new StubHandler(HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized);
        var client = new EsiClient(refresher, store, handler);

        await Assert.ThrowsAsync<EsiAuthException>(
            () => client.GetAsync<Dictionary<string, bool>>("/x/", CharId, CancellationToken.None));
        var callsWhenParked = handler.CallCount;

        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<EsiAuthException>(
                () => client.GetAsync<Dictionary<string, bool>>("/x/", CharId, CancellationToken.None));
        }

        Assert.Equal(callsWhenParked, handler.CallCount);
    }

    [Fact]
    public async Task ReauthRequired_FiresOnlyOncePerCharacter()
    {
        var store = StoreWithLiveToken();
        var refresher = new FakeRefresher(store);
        var handler = new StubHandler(HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized);
        var client = new EsiClient(refresher, store, handler);

        var raised = 0;
        client.ReauthRequired += _ => raised++;

        for (var i = 0; i < 4; i++)
        {
            await Assert.ThrowsAsync<EsiAuthException>(
                () => client.GetAsync<Dictionary<string, bool>>("/x/", CharId, CancellationToken.None));
        }

        Assert.Equal(1, raised);
    }

    // -- Getting un-parked ---------------------------------------------------------------------

    // The regression that shipped: ReauthEsiCharacter stored a fresh token but never called
    // ClearReauth, so 16 successful re-auths left every character blocked until restart. The park is
    // now tied to the credential that failed, so ANY newly stored token lifts it -- no call site has
    // to remember.
    [Fact]
    public async Task ParkLiftsItself_WhenAFreshTokenIsStored_EvenWithoutClearReauth()
    {
        var store = StoreWithLiveToken();
        var refresher = new FakeRefresher(store);
        var handler = new StubHandler(HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized);
        var client = new EsiClient(refresher, store, handler);

        await Assert.ThrowsAsync<EsiAuthException>(
            () => client.GetAsync<Dictionary<string, bool>>("/x/", CharId, CancellationToken.None));
        Assert.True(client.NeedsReauth(CharId));

        // Exactly what a re-link does, and nothing more.
        store.Put(new EsiToken
        {
            CharacterId = CharId,
            CharacterName = "Test Pilot",
            RefreshToken = "refresh-new",
            AccessToken = "access-from-relink",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20),
            Scopes = new() { "publicData" },
        });

        Assert.False(client.NeedsReauth(CharId));
    }

    [Fact]
    public async Task ClearReauth_LiftsThePark()
    {
        var store = StoreWithLiveToken();
        var refresher = new FakeRefresher(store);
        var handler = new StubHandler(HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized);
        var client = new EsiClient(refresher, store, handler);

        await Assert.ThrowsAsync<EsiAuthException>(
            () => client.GetAsync<Dictionary<string, bool>>("/x/", CharId, CancellationToken.None));
        Assert.True(client.NeedsReauth(CharId));

        client.ClearReauth(CharId);

        Assert.False(client.NeedsReauth(CharId));
    }

    [Fact]
    public async Task ParkIsPerCharacter()
    {
        var store = StoreWithLiveToken();
        var refresher = new FakeRefresher(store);
        var handler = new StubHandler(HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized);
        var client = new EsiClient(refresher, store, handler);

        await Assert.ThrowsAsync<EsiAuthException>(
            () => client.GetAsync<Dictionary<string, bool>>("/x/", CharId, CancellationToken.None));

        Assert.True(client.NeedsReauth(CharId));
        Assert.False(client.NeedsReauth(CharId + 1));
    }

    // -- The refresh-token rotation rule --------------------------------------------------------

    [Theory]
    [InlineData("rotated", "old", "rotated")]   // SSO rotated it: take the new one
    [InlineData(null, "old", "old")]            // field absent: keep what we have
    [InlineData("", "old", "old")]              // field empty: same
    [InlineData(null, null, "")]                // nothing either way: empty, not null
    public void ResolveRefreshToken_NeverDiscardsAWorkingToken(string? fromResponse, string? existing, string expected)
        => Assert.Equal(expected, EsiAuthService.ResolveRefreshToken(fromResponse, existing));
}
