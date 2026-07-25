using EveDeck.Services;
using EveDeck.Utilities;

namespace EveDeck.Models;

public sealed class EsiCharacter : ObservableObject
{
    public long CharacterId { get; set; }
    public string CharacterName { get; set; } = "";

    // True when this is the seat's MAIN character, i.e. it sits at EsiCharacters[0]. Derived from
    // position rather than persisted (SlotAssignment stamps it on every roster change), so the list
    // order stays the single source of truth and a hand-edited settings.json can't disagree with it.
    private bool _isMain;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsMain
    {
        get => _isMain;
        set => SetProperty(ref _isMain, value);
    }

    // Public Fenris Creations image server (no auth). Valid sizes: 32, 64, 128, 256, 512. These raw
    // URLs are kept for any non-UI use; the app renders portraits through PortraitCacheService (see
    // Portrait) so a changed in-game portrait actually refreshes instead of being pinned by WinINET.
    public string PortraitUrl => CharacterId > 0
        ? $"https://images.evetech.net/characters/{CharacterId}/portrait?size=128"
        : "";

    public string PortraitUrlSmall => CharacterId > 0
        ? $"https://images.evetech.net/characters/{CharacterId}/portrait?size=64"
        : "";

    // Shared, cache-backed portrait for this character (auto-refreshing). Bound by the seat roster
    // and setup wizard.
    [System.Text.Json.Serialization.JsonIgnore]
    public CharacterPortrait Portrait => PortraitCacheService.Instance.ForId(CharacterId);
}
