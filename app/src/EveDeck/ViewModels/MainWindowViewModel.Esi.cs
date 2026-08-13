using System.Windows;
using MessageBox = System.Windows.MessageBox;
using EveDeck.Models;
using EveDeck.Services;

namespace EveDeck.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly EsiAuthService _esiAuth = new();
    private bool _esiLoginInProgress;

    // Encrypted refresh/access token store for linked ESI characters. Lazily created so tests that
    // never touch ESI don't write a tokens file. Keyed off the same app-data folder as settings.json.
    private EsiTokenStore? _tokenStore;
    public EsiTokenStore TokenStore => _tokenStore ??= new EsiTokenStore(_configService.AppDataFolder);

    // A character's ESI grant has stopped working in a way only re-linking fixes (EsiClient has
    // already retried with a freshly refreshed token before raising this, and has parked further
    // requests for that character). Fires at most once per character per session.
    //
    // This exists because the failure mode it replaces was completely silent to the user: every
    // ESI-backed feature -- seat health toasts above all -- simply stopped working while the only
    // trace was a [Warn] line per attempt, thousands a day, in a log nobody reads. A dead feature
    // has to announce itself.
    private void OnEsiReauthRequired(long characterId)
    {
        // Raised from a background ESI call; toasts and the log collection are UI-thread affine.
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var seat = Assignments.FirstOrDefault(a => a.EsiCharacters.Any(c => c.CharacterId == characterId));
            var name = seat?.EsiCharacters.FirstOrDefault(c => c.CharacterId == characterId)?.CharacterName
                       ?? characterId.ToString();

            Log.Warn($"ESI access for {name} needs re-authorisation -- requests are paused for this character until it is re-linked (Clients tab -> the seat's ESI character -> re-link).");
            ShowToast(name, "ESI access expired -- re-link this character to restore seat health alerts and info", "#F59E0B", seat);
        });
    }

    private async void AddEsiCharacter(object? parameter)
    {
        if (parameter is not SlotAssignment slot) return;

        if (slot.EsiCharacters.Count >= 3)
        {
            Log.Warn("A seat can hold at most 3 characters.");
            return;
        }
        if (_esiLoginInProgress)
        {
            Log.Warn("An ESI login is already in progress — check your browser.");
            return;
        }

        _esiLoginInProgress = true;
        try
        {
            Log.Info($"Opening EVE SSO login for seat {slot.SlotNumber} — sign in and authorise in your browser.");
            var token = await _esiAuth.AuthorizeAsync(CancellationToken.None);
            var characterId = token.CharacterId;
            var characterName = token.CharacterName;

            if (Assignments.Any(a => a.EsiCharacters.Any(c => c.CharacterId == characterId)))
            {
                Log.Warn($"{characterName} is already assigned to a seat.");
                return;
            }

            // Persist the (encrypted) token so the info flyout and skill queue can call ESI on this character's behalf.
            TokenStore.Put(token);
            // A fresh grant clears any "needs re-authorisation" park, so requests start flowing again
            // without an app restart -- re-linking IS the fix that park is waiting for.
            EsiClientShared.ClearReauth(characterId);
            if (!token.HasScope(EsiAuthService.ScopeSkills))
                Log.Warn($"{characterName} was linked without the skills scope — their info flyout won't show total SP. Re-link and keep all boxes ticked to fix.");

            // The first character linked anywhere becomes the app master (its name + portrait brand
            // the title bar). Detect BEFORE adding so we only promote on a truly empty roster.
            var isFirstEver = Assignments.Sum(a => a.EsiCharacters.Count) == 0;

            slot.EsiCharacters.Add(new EsiCharacter { CharacterId = characterId, CharacterName = characterName });

            if (slot.EsiCharacters.Count == 1)
                slot.Label = characterName;

            var windowTitle = $"EVE - {characterName}";
            if (!slot.AssignedWindows.Any(w => w.Title.Equals(windowTitle, StringComparison.OrdinalIgnoreCase)))
                slot.AssignedWindows.Add(new SlotWindowEntry { Title = windowTitle });

            Log.Info($"Added {characterName} (ID {characterId}) to seat {slot.SlotNumber} ({slot.Label}).");

            if (isFirstEver)
            {
                SetMasterSlot(slot);
                Log.Info($"{characterName} is the first linked character - set as app master.");
            }

            Save();
            RaiseIdentityDependents();
        }
        catch (OperationCanceledException)
        {
            Log.Info("ESI login cancelled.");
        }
        catch (Exception ex)
        {
            Log.Error($"ESI login failed: {ex.Message}");
        }
        finally
        {
            _esiLoginInProgress = false;
        }
    }

    // Re-runs the SSO login for an already-linked character to refresh its stored ESI token — e.g.
    // to pick up a newly-added scope without removing and re-adding the character. Does not touch
    // the seat/assignment, only the token in TokenStore.
    private async void ReauthEsiCharacter(object? parameter)
    {
        if (parameter is not EsiCharacter character) return;

        if (_esiLoginInProgress)
        {
            Log.Warn("An ESI login is already in progress — check your browser.");
            return;
        }

        _esiLoginInProgress = true;
        try
        {
            Log.Info($"Opening EVE SSO login to re-authorise {character.CharacterName} — sign in as the SAME character in your browser.");
            var token = await _esiAuth.AuthorizeAsync(CancellationToken.None);

            if (token.CharacterId != character.CharacterId)
            {
                Log.Warn($"Re-auth signed in as {token.CharacterName}, but this seat entry is {character.CharacterName} — ignored. Log in as {character.CharacterName} instead.");
                return;
            }

            TokenStore.Put(token);
            // THE path that clears a needs-reauth park -- this command exists precisely to fix a
            // character whose grant went bad, so forgetting it here left the park in place and the
            // character still blocked despite a perfectly good new token (seen live: 16 successful
            // re-auths, every character still parked until restart).
            EsiClientShared.ClearReauth(token.CharacterId);
            var missing = new List<string>();
            if (!token.HasScope(EsiAuthService.ScopeSkills)) missing.Add("skills");
            Log.Info(missing.Count == 0
                ? $"Re-authorised {character.CharacterName} — all scopes granted."
                : $"Re-authorised {character.CharacterName}, but the {string.Join(" and ", missing)} scope(s) are still missing — make sure every box is ticked on the SSO consent screen.");
        }
        catch (OperationCanceledException)
        {
            Log.Info("ESI re-auth cancelled.");
        }
        catch (Exception ex)
        {
            Log.Error($"ESI re-auth failed: {ex.Message}");
        }
        finally
        {
            _esiLoginInProgress = false;
        }
    }

    // Promote one of a seat's linked characters to be its MAIN, i.e. move it to EsiCharacters[0].
    // The main drives the seat portrait, the info flyout's character and the skill-queue fallbacks, so
    // on a seat holding a main plus two alts the user needs to say which one that is instead of being
    // stuck with whichever they happened to link first. The seat's typed Label is deliberately NOT
    // rewritten -- a custom label is the user's, same reasoning as LabelAlias in DisplayLabel.
    private void SetMainCharacter(object? parameter)
    {
        if (parameter is not EsiCharacter character) return;

        var slot = Assignments.FirstOrDefault(a => a.EsiCharacters.Contains(character));
        if (slot is null) return;

        var index = slot.EsiCharacters.IndexOf(character);
        if (index <= 0) return;   // already the main

        slot.EsiCharacters.Move(index, 0);

        Log.Info($"{character.CharacterName} is now the main character for seat {slot.SlotNumber} ({slot.Label}).");
        Save();
        RaiseIdentityDependents();
    }

    private void RemoveEsiCharacter(object? parameter)
    {
        if (parameter is not EsiCharacter character) return;

        var slot = Assignments.FirstOrDefault(a => a.EsiCharacters.Contains(character));
        if (slot is null) return;

        var result = MessageBox.Show(
            $"Remove {character.CharacterName} from seat {slot.SlotNumber}? You'll need to sign in again via EVE SSO to re-add it.",
            "Remove Character", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        slot.EsiCharacters.Remove(character);

        var windowTitle = $"EVE - {character.CharacterName}";
        var entry = slot.AssignedWindows.FirstOrDefault(w => w.Title.Equals(windowTitle, StringComparison.OrdinalIgnoreCase));
        if (entry is not null) slot.AssignedWindows.Remove(entry);

        if (slot.EsiCharacters.Count > 0 && slot.Label.Equals(character.CharacterName, StringComparison.OrdinalIgnoreCase))
            slot.Label = slot.EsiCharacters[0].CharacterName;

        TokenStore.Remove(character.CharacterId);
        // Drop cached ESI facts so a re-linked character (or a reused seat) can't inherit the removed
        // character's stale flyout data.
        _characterInfo?.Invalidate(character.CharacterId);

        Log.Info($"Removed {character.CharacterName} from seat {slot.SlotNumber}.");
        Save();
        RaiseIdentityDependents();
    }
}
