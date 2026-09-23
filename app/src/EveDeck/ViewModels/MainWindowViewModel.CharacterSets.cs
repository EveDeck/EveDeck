using System.Windows;
using MessageBox = System.Windows.MessageBox;
using EveDeck.Models;

namespace EveDeck.ViewModels;

public sealed partial class MainWindowViewModel
{
    public Models.CharacterSet? ActiveCharacterSet
        => _settings.CharacterSets.FirstOrDefault(s => s.Id == _settings.ActiveCharacterSetId);

    public string ActiveCharacterSetId => _settings.ActiveCharacterSetId;

    // Called from hotkey dispatch (1-based index into CharacterSets).
    internal void SwitchCharacterSet(int oneBasedIndex)
    {
        if (oneBasedIndex < 1 || oneBasedIndex > _settings.CharacterSets.Count) return;
        SwitchToCharacterSet(_settings.CharacterSets[oneBasedIndex - 1].Id);
    }

    // applyLayout=false is for callers that are going to place windows themselves afterwards --
    // ConfigProfile.Apply picks its OWN layout after switching the set. Without the opt-out its
    // apply would collide with this one and lose: ApplyActiveProfile is async and drops re-entrant
    // calls via _applyInProgress, so the set's layout would silently win over the config profile's.
    private void SwitchToCharacterSet(string targetId, bool applyLayout = true)
    {
        if (targetId == _settings.ActiveCharacterSetId) return;
        var target = _settings.CharacterSets.FirstOrDefault(s => s.Id == targetId);
        if (target is null) return;

        // Snapshot the live collections back into the current set before leaving it.
        SnapshotLiveToActiveSet();

        // Update IsActive flag on all sets (used by UI toggle buttons).
        foreach (var s in _settings.CharacterSets) s.IsActive = false;
        target.IsActive = true;
        _settings.ActiveCharacterSetId = targetId;

        // Repopulate live collections from the target set.
        Assignments.Clear();
        foreach (var a in target.Assignments) Assignments.Add(a);
        Hotkeys.Clear();
        foreach (var h in target.Hotkeys) Hotkeys.Add(h);

        // Sets carry their layout too, so switching one moves the windows as well as the seats.
        // Degrades the same way ConfigProfile.Apply does: an empty or dangling reference means
        // "keep the current layout" rather than throwing or blanking the selection.
        var switchedLayout = false;
        if (!string.IsNullOrWhiteSpace(target.LayoutProfileId))
        {
            var layout = _settings.Profiles.FirstOrDefault(p => p.Id == target.LayoutProfileId);
            if (layout is null)
                Log.Warn($"Character set '{target.Name}' references a layout profile that no longer exists; keeping the current one.");
            else if (!ReferenceEquals(layout, SelectedProfile))
            {
                SelectedProfile = layout;
                switchedLayout = true;
            }
        }

        HotkeysChanged?.Invoke(this, EventArgs.Empty);
        SyncMasterSlot();
        UpdatePositionCodes();
        RaiseIdentityDependents();
        RefreshCharacterSetLayoutNames();
        OnPropertyChanged(nameof(ActiveCharacterSet));
        OnPropertyChanged(nameof(ActiveCharacterSetId));
        OnPropertyChanged(nameof(ActiveSetLayout));
        Save();
        Log.Info($"Switched to character set '{target.Name}'.");

        // Placed last so the seats/hotkeys above are already live when the windows start moving.
        if (switchedLayout && applyLayout)
        {
            Log.Info($"Applying layout '{SelectedProfile?.Name}' bound to character set '{target.Name}'.");
            ApplyActiveProfile();
        }
    }

    private void SnapshotLiveToActiveSet()
    {
        var active = ActiveCharacterSet;
        if (active is null) return;
        active.Assignments.Clear();
        foreach (var a in Assignments) active.Assignments.Add(a);
        active.Hotkeys.Clear();
        foreach (var h in Hotkeys) active.Hotkeys.Add(h);
        // Auto-track the layout the same way seats and hotkeys are tracked: whatever profile was
        // selected while this set was active IS this set's layout. Keeps legacy sets (empty binding)
        // from needing a one-time manual pick before the feature does anything.
        if (SelectedProfile is not null) active.LayoutProfileId = SelectedProfile.Id;
    }

    // The active set's bound layout, surfaced as a picker in the Character Sets panel so the rule
    // ("this set flies this layout") is visible rather than something the user has to infer from a
    // switch that silently rearranges their screen. Setting it re-points the live selection too --
    // for the ACTIVE set those are by definition the same thing under auto-tracking.
    public LayoutProfile? ActiveSetLayout
    {
        get
        {
            var active = ActiveCharacterSet;
            if (active is null || string.IsNullOrWhiteSpace(active.LayoutProfileId)) return SelectedProfile;
            return _settings.Profiles.FirstOrDefault(p => p.Id == active.LayoutProfileId) ?? SelectedProfile;
        }
        set
        {
            if (value is null) return;
            var active = ActiveCharacterSet;
            if (active is not null) active.LayoutProfileId = value.Id;
            if (!ReferenceEquals(value, SelectedProfile)) SelectedProfile = value;
            RefreshCharacterSetLayoutNames();
            OnPropertyChanged();
            Save();
        }
    }

    // Resolve each set's layout id to a name for its button tooltip. Cheap enough to just redo the
    // whole list whenever anything moves rather than tracking which single set changed.
    internal void RefreshCharacterSetLayoutNames()
    {
        foreach (var set in _settings.CharacterSets)
        {
            var id = set.Id == _settings.ActiveCharacterSetId && SelectedProfile is not null
                ? SelectedProfile.Id
                : set.LayoutProfileId;
            set.LayoutDisplayName = string.IsNullOrWhiteSpace(id)
                ? "Keeps the current layout"
                : _settings.Profiles.FirstOrDefault(p => p.Id == id)?.Name ?? "Missing layout";
        }
    }

    private void AddCharacterSet()
    {
        SnapshotLiveToActiveSet();

        var newSet = new Models.CharacterSet
        {
            Name = $"Set {_settings.CharacterSets.Count + 1}",
            // Seeded from the current layout, matching the seat/hotkey cloning below: "add a set"
            // means "another set like this one", not "a set that rearranges my screen".
            LayoutProfileId = SelectedProfile?.Id ?? ""
        };
        // Clone seat structure (same slot numbers/labels) but clear window assignments.
        foreach (var a in Assignments)
        {
            var seat = new SlotAssignment
            {
                SlotNumber = a.SlotNumber,
                Label = a.Label,
                IsMaster = a.IsMaster,
                FrameColor = a.FrameColor,
                LabelFontFamily = a.LabelFontFamily,
                LabelFontSize = a.LabelFontSize,
                LabelColor = a.LabelColor,
                NeverMinimize = a.NeverMinimize
            };
            // Carry the ESI links across. Tokens were always global (EsiTokenStore keys them by
            // character id), so copying the linkage costs nothing and spares the user re-running the
            // browser login for characters the app is already authorised for. Deselect the ones this
            // set does not need with "Choose Characters".
            foreach (var character in a.EsiCharacters)
                seat.EsiCharacters.Add(new EsiCharacter
                {
                    CharacterId = character.CharacterId,
                    CharacterName = character.CharacterName
                });
            foreach (var window in a.AssignedWindows)
                seat.AssignedWindows.Add(new SlotWindowEntry { Title = window.Title });
            newSet.Assignments.Add(seat);
        }
        // Clone hotkey bindings fully -- gesture, enabled state and character target included -- so a
        // new set starts as a working copy of the current one rather than a set of dead keys. (This
        // used to clone them unbound, which is why sets created by older builds can have every
        // "Switch to character" action enabled and bound but pointing at nobody.)
        foreach (var h in Hotkeys)
            newSet.Hotkeys.Add(new HotkeyBinding
            {
                ActionId = h.ActionId,
                DisplayName = h.DisplayName,
                Modifiers = h.Modifiers,
                VirtualKey = h.VirtualKey,
                GestureText = h.GestureText,
                Enabled = h.Enabled,
                TargetCharacter = h.TargetCharacter
            });

        _settings.CharacterSets.Add(newSet);
        DeleteCharacterSetCommand.RaiseCanExecuteChanged();
        SwitchToCharacterSet(newSet.Id);
        Log.Info($"Added character set '{newSet.Name}'.");
    }

    private void DeleteCharacterSet(Models.CharacterSet set)
    {
        if (_settings.CharacterSets.Count <= 1) return;

        var result = MessageBox.Show(
            $"Delete character set '{set.Name}'?",
            "Delete Character Set", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var wasActive = set.Id == _settings.ActiveCharacterSetId;
        _settings.CharacterSets.Remove(set);
        DeleteCharacterSetCommand.RaiseCanExecuteChanged();

        if (wasActive)
            SwitchToCharacterSet(_settings.CharacterSets[0].Id);
        else
        {
            Save();
            Log.Info($"Deleted character set '{set.Name}'.");
        }
    }
}
