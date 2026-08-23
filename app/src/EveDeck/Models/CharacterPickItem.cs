using EveDeck.Services;
using EveDeck.Utilities;

namespace EveDeck.Models;

// One row of the "choose characters for this set" picker: a character from the GLOBAL roster, plus
// whether it is currently in the set being edited.
//
// The roster itself is derived, never persisted (see MainWindowViewModel.BuildCharacterRoster) --
// a character is "known" if it holds an ESI token or already sits in some set. Deriving it means
// linking a character in one set automatically makes it available to every other set with no
// migration, no fourth collection to keep in sync, and no way for the roster to drift out of step
// with the tokens it describes.
public sealed class CharacterPickItem : ObservableObject
{
    private bool _isSelected;

    public long CharacterId { get; init; }

    public string CharacterName { get; init; } = "";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    // False when the roster knows this character only because a set references it, with no ESI grant
    // behind it any more (tokens cleared, DPAPI store reset, or the grant was revoked). Still fully
    // pickable -- a seat works perfectly well without ESI, it just loses the info flyout and health
    // alerts -- so this drives an advisory note rather than disabling the row.
    public bool HasToken { get; init; }

    // Where this character already sits, e.g. "Main, Abyss" -- so picking is an informed choice
    // rather than a guess from a bare name. Empty when it sits in no set at all.
    public string SetSummary { get; init; } = "";

    public string StatusNote => HasToken ? SetSummary : (SetSummary.Length > 0 ? $"{SetSummary} — needs re-link" : "Needs re-link");

    public CharacterPortrait Portrait => PortraitCacheService.Instance.ForId(CharacterId);
}
