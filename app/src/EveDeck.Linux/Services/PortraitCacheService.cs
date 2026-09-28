using System.Collections.Concurrent;
using EveDeck.Models;

namespace EveDeck.Services;

// Linux counterpart of the Windows PortraitCacheService. Same members the shared models call, one
// shared CharacterPortrait per id so bindings update together. Downloading and the on-disk cache are
// not ported yet, so every portrait stays imageless (HasImage == false) for now.
public sealed class PortraitCacheService
{
    public static PortraitCacheService Instance { get; } = new();

    private readonly ConcurrentDictionary<long, CharacterPortrait> _byId = new();
    private readonly ConcurrentDictionary<string, long> _idByName = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public CharacterPortrait ForId(long id) => _byId.GetOrAdd(id, i => new CharacterPortrait(i));

    public CharacterPortrait? ForName(string name) =>
        _idByName.TryGetValue(name, out var id) ? ForId(id) : null;

    public void RefreshAll() => Changed?.Invoke();

    public void Warm(IEnumerable<long> ids)
    {
        foreach (var id in ids) ForId(id);
    }
}
