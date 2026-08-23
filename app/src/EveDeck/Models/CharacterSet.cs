using System.Collections.ObjectModel;
using EveDeck.Utilities;

namespace EveDeck.Models;

public sealed class CharacterSet : ObservableObject
{
    private bool _isActive;
    private string _layoutDisplayName = "";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    private string _name = "Default";
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    // True while this set is loaded into the live Assignments/Hotkeys collections.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public ObservableCollection<SlotAssignment> Assignments { get; set; } = new();
    public ObservableCollection<HotkeyBinding> Hotkeys { get; set; } = new();

    // The LayoutProfile this set flies with, so switching sets also switches where the windows go.
    // A REFERENCE (id) rather than a copy, matching ConfigProfile: two sets can share one layout and
    // editing that layout updates both instead of drifting into stale duplicates. Empty = "leave the
    // layout alone" rather than "no layout" -- a set that only changes who sits where is still valid,
    // and legacy sets saved before this field start out empty. Kept in sync automatically: whatever
    // layout is selected while this set is active is snapshotted back here on the way out.
    public string LayoutProfileId { get; set; } = "";

    // Display-only mirror of LayoutProfileId resolved to the profile's name, for the set buttons'
    // tooltips. Ids mean nothing to a user and XAML can't resolve one without the settings object,
    // so the view-model refreshes this whenever the binding or the profile list changes.
    [System.Text.Json.Serialization.JsonIgnore]
    public string LayoutDisplayName
    {
        get => _layoutDisplayName;
        set => SetProperty(ref _layoutDisplayName, value);
    }

    // Delay (ms) between launching successive clients via LaunchGroupCommand, so Windows/network
    // isn't hammered by launching every EVE Launcher instance at once.
    public int LaunchDelayMs { get; set; } = 3000;
}
