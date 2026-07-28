using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;

namespace EveDeck.Services;

// Recent kill activity for a solar system from zKillboard, for the preview info flyout's "danger"
// line (added 2026-07-24). Counts the killmails zKill reports in the system over the last hour and the
// last day. Results are cached per system for a few minutes so opening a flyout repeatedly doesn't
// hammer zKill (which asks callers to be gentle and send a contactable User-Agent).
//
// "Kills" here is ships destroyed IN the system (every zKill entry for a system is one ship dying
// there) -- at system scope there is no attacker/victim "kill vs loss" split, so this is total wrecks.
public sealed class ZkillSystemActivityService
{
    private static readonly HttpClient _http = CreateHttp();
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    // -1 in a slot means "couldn't fetch" (zKill error/timeout); the caller shows "?" for it.
    private readonly ConcurrentDictionary<int, (DateTimeOffset At, int Kills1h, int Kills24h)> _cache = new();

    public async Task<(int Kills1h, int Kills24h)> GetActivityAsync(int systemId, CancellationToken ct)
    {
        if (systemId <= 0) return (-1, -1);
        if (_cache.TryGetValue(systemId, out var hit) && DateTimeOffset.UtcNow - hit.At < Ttl)
            return (hit.Kills1h, hit.Kills24h);

        var kills1h = await CountAsync(systemId, 3600, ct);
        var kills24h = await CountAsync(systemId, 86400, ct);
        _cache[systemId] = (DateTimeOffset.UtcNow, kills1h, kills24h);
        return (kills1h, kills24h);
    }

    // Killmails zKill reports for the system within the past `pastSeconds`. zKill returns a JSON array
    // of compact {killmail_id, zkb} entries, so the count is just its length. -1 on any failure.
    private static async Task<int> CountAsync(int systemId, int pastSeconds, CancellationToken ct)
    {
        try
        {
            var url = $"https://zkillboard.com/api/systemID/{systemId}/pastSeconds/{pastSeconds}/";
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        }
        catch
        {
            return -1;
        }
    }

    // Whether zKillboard has recorded this character as a LOSS victim within the past `pastSeconds`
    // (max 604800 = 7 days, zKill's own cap) -- used by the seat-health "podded" alert to distinguish
    // an actual kill from a deliberate bare-pod flight or an in-station ship swap. zKill's own
    // pastSeconds filter does the recency check server-side (the compact {killmail_id, zkb} entries
    // it returns carry no timestamp to parse client-side), so "any entries at all" already means
    // "within the window" -- no separate timestamp comparison needed. false on any failure (network,
    // zKill error) rather than throwing; the caller falls back to an unconfirmed-but-still-useful alert.
    public static async Task<bool> HasRecentLossAsync(long characterId, int pastSeconds, CancellationToken ct)
    {
        try
        {
            var url = $"https://zkillboard.com/api/losses/characterID/{characterId}/pastSeconds/{pastSeconds}/";
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
            return doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0;
        }
        catch
        {
            return false;
        }
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // zKillboard asks for a descriptive, contactable User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EveDeck (github.com/objectless/EveDeck)");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }
}
