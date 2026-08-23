using System.Windows;
using EveDeck.Models;
using EveDeck.Services;
using EveDeck.Utilities;
using EveDeck.Views;
using MessageBox = System.Windows.MessageBox;

namespace EveDeck.ViewModels;

// The GLOBAL character roster: every character EveDeck has ever linked, available to every character
// set without re-authorising.
//
// ESI auth was already set-agnostic -- EsiTokenStore keys grants by character id in one shared file,
// not per set. The only thing that was per-set was the seat->character LINKAGE, which is why making a
// second set used to mean walking the whole browser login flow again for characters the app already
// held tokens for. The roster closes that gap without moving where anything is stored.
//
// The roster is DERIVED, never persisted: a character is known if it holds a token or already sits in
// some set. That means no migration for existing settings, no fourth collection to keep in sync, and
// no way for a stored roster to disagree with the tokens it claims to describe.
public sealed partial class MainWindowViewModel
{
    public RelayCommand ChooseSetCharactersCommand { get; private set; } = null!;

    private void InitCharacterRoster()
    {
        ChooseSetCharactersCommand = new RelayCommand(_ => ChooseSetCharacters());
    }

    // Every known character, with where it already sits. Ordered by name so the picker is scannable
    // at any roster size -- nothing here assumes a particular number of accounts.
    internal IReadOnlyList<CharacterPickItem> BuildCharacterRoster()
    {
        // Names can change in EVE, and a token's name is refreshed every time the character is
        // re-linked, so a token name wins over the copy a set recorded when it was first linked.
        var names = new Dictionary<long, string>();
        var setsByCharacter = new Dictionary<long, List<string>>();

        foreach (var set in _settings.CharacterSets)
        {
            // The ACTIVE set's saved copy is stale while it is loaded into the live collections --
            // read the live seats for it instead, or a character seated this session goes missing.
            var seats = set.Id == _settings.ActiveCharacterSetId
                ? (IEnumerable<SlotAssignment>)Assignments
                : set.Assignments;

            foreach (var character in seats.SelectMany(a => a.EsiCharacters))
            {
                names.TryAdd(character.CharacterId, character.CharacterName);
                if (!setsByCharacter.TryGetValue(character.CharacterId, out var list))
                    setsByCharacter[character.CharacterId] = list = new List<string>();
                if (!list.Contains(set.Name)) list.Add(set.Name);
            }
        }

        var tokenIds = new HashSet<long>();
        foreach (var token in TokenStore.All())
        {
            tokenIds.Add(token.CharacterId);
            if (!string.IsNullOrWhiteSpace(token.CharacterName)) names[token.CharacterId] = token.CharacterName;
            else names.TryAdd(token.CharacterId, token.CharacterId.ToString());
        }

        return names
            .Select(kv => new CharacterPickItem
            {
                CharacterId = kv.Key,
                CharacterName = kv.Value,
                HasToken = tokenIds.Contains(kv.Key),
                SetSummary = setsByCharacter.TryGetValue(kv.Key, out var sets) ? string.Join(", ", sets) : ""
            })
            .OrderBy(i => i.CharacterName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void ChooseSetCharacters()
    {
        var active = ActiveCharacterSet;
        if (active is null) return;

        var roster = BuildCharacterRoster();
        if (roster.Count == 0)
        {
            MessageBox.Show(
                "No characters have been linked yet. Link one to a seat first (the seat card's ESI button); after that it is available to every character set without signing in again.",
                "No Linked Characters", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var seated = Assignments.SelectMany(a => a.EsiCharacters).Select(c => c.CharacterId).ToHashSet();
        foreach (var item in roster) item.IsSelected = seated.Contains(item.CharacterId);

        var dialog = new CharacterPickerDialog(active.Name, roster) { Owner = System.Windows.Application.Current?.MainWindow };
        if (dialog.ShowDialog() != true) return;

        ApplySetRoster(dialog.SelectedCharacters);
    }

    // Rebuild the ACTIVE set's seats so they match the ticked characters, one seat per character.
    //
    // Seats already holding a ticked character are kept whole -- their label, colours, fonts, frame
    // and window bindings all survive -- because the common edit is "same crew minus one", and
    // rebuilding every seat from scratch would silently discard per-seat styling the user set up by
    // hand. Only the difference is applied.
    private void ApplySetRoster(IReadOnlyList<CharacterPickItem> selected)
    {
        var keep = selected.Select(c => c.CharacterId).ToHashSet();
        if (keep.Count == 0) return;

        // 1. Drop unticked characters from whichever seats hold them.
        foreach (var seat in Assignments.ToList())
            foreach (var character in seat.EsiCharacters.Where(c => !keep.Contains(c.CharacterId)).ToList())
                seat.EsiCharacters.Remove(character);

        // 2. A seat left holding nothing is no longer part of this set. This also removes seats that
        //    never had a character, which is intended: once a set is roster-driven its seat count IS
        //    the tick count, and the picker says so before Apply.
        foreach (var seat in Assignments.Where(a => a.EsiCharacters.Count == 0).ToList())
            Assignments.Remove(seat);

        // 3. Add a seat for each ticked character that has nowhere to sit, in the picker's order.
        var stillSeated = Assignments.SelectMany(a => a.EsiCharacters).Select(c => c.CharacterId).ToHashSet();
        foreach (var character in selected.Where(c => !stillSeated.Contains(c.CharacterId)))
        {
            var seat = new SlotAssignment { Label = character.CharacterName };
            seat.EsiCharacters.Add(new EsiCharacter
            {
                CharacterId = character.CharacterId,
                CharacterName = character.CharacterName
            });
            // Same title binding AddEsiCharacter creates, so the seat matches its client as soon as
            // that character logs in rather than waiting to be pointed at a window by hand.
            seat.AssignedWindows.Add(new SlotWindowEntry { Title = $"EVE - {character.CharacterName}" });
            Assignments.Add(seat);
        }

        // 4. Renumber 1..N over the surviving order so seat numbers stay dense -- focus hotkeys and
        //    every layout slot address seats by number, and a gap would leave a dead hotkey.
        var number = 1;
        foreach (var seat in Assignments) seat.SlotNumber = number++;

        // 5. Make sure the layouts can actually place the seats we just created. Mirrors what AddSlot
        //    does for a hand-added seat; family templates regenerate from TemplateCount instead and
        //    are left alone.
        foreach (var seat in Assignments) EnsureProfileSlotsFor(seat.SlotNumber, seat.Label);

        SelectedAssignment = Assignments.FirstOrDefault();
        EnsureValidMasterSeat();
        SyncMasterSlot();
        UpdatePositionCodes();
        RaiseIdentityDependents();
        RebuildLayoutPreview();
        OnPropertyChanged(nameof(Assignments));
        OnPropertyChanged(nameof(ActiveProfileSlots));
        Save();

        PortraitCacheService.Instance.Warm(Assignments.SelectMany(a => a.EsiCharacters).Select(c => c.CharacterId));
        Log.Info($"Character set '{ActiveCharacterSet?.Name}' now has {Assignments.Count} seat(s): {string.Join(", ", Assignments.Select(a => a.Label))}.");
    }
}
