using System.Collections.Concurrent;
using EveDeck.Models;

namespace EveDeck.Services;

// Typed, short-TTL-cached access to the live per-character ESI facts behind the preview info flyout:
// location, active ship, jump fatigue, and skill points.
//
// Every getter caches its result per (characterId, endpoint) for a short window (default 60s) so that
// repeatedly opening a seat's info flyout doesn't refetch on every click, while the data stays fresh
// enough to be useful.
//
// This is a thin fetch+cache layer only. Name resolution (skill/ship type id -> name, system id ->
// name) is deliberately left to the existing EsiTypeCache, which the consuming features already use;
// keeping display concerns out of here means one job per class. Failures surface as null (EsiClient
// already maps 404/204 to default and honours ESI's error-limit backoff), and consumers degrade to
// "show nothing for this line" rather than throwing.
public sealed class CharacterInfoService
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);

    private readonly EsiClient _client;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<(long CharacterId, string Kind), CacheEntry> _cache = new();

    public CharacterInfoService(EsiClient client, TimeSpan? ttl = null)
    {
        _client = client;
        _ttl = ttl ?? DefaultTtl;
    }

    public Task<List<EsiSkillQueueEntry>?> GetSkillQueueAsync(long characterId, bool forceRefresh, CancellationToken ct)
        => CachedAsync<List<EsiSkillQueueEntry>>($"/characters/{characterId}/skillqueue/", characterId, "skillqueue", forceRefresh, ct);

    public Task<EsiCharacterLocation?> GetLocationAsync(long characterId, bool forceRefresh, CancellationToken ct)
        => CachedAsync<EsiCharacterLocation>($"/characters/{characterId}/location/", characterId, "location", forceRefresh, ct);

    public Task<EsiCharacterShip?> GetShipAsync(long characterId, bool forceRefresh, CancellationToken ct)
        => CachedAsync<EsiCharacterShip>($"/characters/{characterId}/ship/", characterId, "ship", forceRefresh, ct);

    public Task<EsiCharacterFatigue?> GetFatigueAsync(long characterId, bool forceRefresh, CancellationToken ct)
        => CachedAsync<EsiCharacterFatigue>($"/characters/{characterId}/fatigue/", characterId, "fatigue", forceRefresh, ct);

    // Total SP + unallocated SP for the flyout's SP line (esi-skills.read_skills.v1).
    public Task<EsiCharacterSkillsResponse?> GetSkillsAsync(long characterId, bool forceRefresh, CancellationToken ct)
        => CachedAsync<EsiCharacterSkillsResponse>($"/characters/{characterId}/skills/", characterId, "skills", forceRefresh, ct);

    // Whether the character's game session is actually connected (esi-location.read_online.v1), for
    // the seat-health "disconnected" alert -- distinct from the seat's window merely still existing.
    public Task<EsiCharacterOnline?> GetOnlineAsync(long characterId, bool forceRefresh, CancellationToken ct)
        => CachedAsync<EsiCharacterOnline>($"/characters/{characterId}/online/", characterId, "online", forceRefresh, ct);

    // Character wallet balance (esi-wallet.read_character_wallet.v1). Swallows failures (e.g. a character
    // linked before the wallet scope was granted -> 403) so the flyout just omits the line instead of
    // losing every line after it. Not cached -- a single cheap call, and the balance changes constantly.
    public async Task<double?> GetWalletAsync(long characterId, CancellationToken ct)
    {
        try { return await _client.GetAsync<double?>($"/characters/{characterId}/wallet/", characterId, ct); }
        catch { return null; }
    }

    // Name of a player-owned Upwell structure the character is docked in (esi-universe.read_structures.v1).
    // Needs an authed call because the endpoint is access-gated to characters that can dock there. Names
    // are immutable, so cache permanently by structure id; "" on a 403 (no access) / any failure, and the
    // caller falls back to a generic "Docked at structure" label.
    private readonly ConcurrentDictionary<long, string> _structureNames = new();

    public async Task<string> GetStructureNameAsync(long structureId, long characterId, CancellationToken ct)
    {
        if (structureId <= 0) return "";
        if (_structureNames.TryGetValue(structureId, out var cached)) return cached;
        try
        {
            var info = await _client.GetAsync<EsiStructureInfo>($"/universe/structures/{structureId}/", characterId, ct);
            var name = info?.Name ?? "";
            if (!string.IsNullOrEmpty(name)) _structureNames[structureId] = name;
            return name;
        }
        catch
        {
            return "";
        }
    }

    // Drops all cached facts for one character -- call when a seat's occupant changes or a character is
    // unlinked so the flyout can't show a previous occupant's stale data.
    public void Invalidate(long characterId)
    {
        foreach (var key in _cache.Keys)
            if (key.CharacterId == characterId) _cache.TryRemove(key, out _);
    }

    private async Task<T?> CachedAsync<T>(string path, long characterId, string kind, bool forceRefresh, CancellationToken ct)
        where T : class
    {
        var key = (characterId, kind);
        if (!forceRefresh && _cache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.FetchedAt < _ttl)
            return (T?)hit.Value;

        var value = await _client.GetAsync<T>(path, characterId, ct);
        _cache[key] = new CacheEntry(DateTimeOffset.UtcNow, value);
        return value;
    }

    private readonly record struct CacheEntry(DateTimeOffset FetchedAt, object? Value);
}
