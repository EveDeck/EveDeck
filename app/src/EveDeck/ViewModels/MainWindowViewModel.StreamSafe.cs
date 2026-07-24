using EveDeck.Utilities;

namespace EveDeck.ViewModels;

// Stream-safe mode (added 2026-07-24): a single toggle that masks identifying information on the
// OVERLAYS ONLY (corner pills + the info flyout) so a streamer/content-creator can share their screen
// without leaking character names, current systems, or wallet balances. The main app UI (Clients tab,
// etc.) is never masked -- this is purely a broadcast-safety layer over what renders on top of EVE.
//
// The three masks (name -> "Alt N", system -> hidden, ISK -> hidden) all move together under this one
// switch; it can be flipped instantly from a hotkey (ToggleStreamSafe) mid-stream.
public sealed partial class MainWindowViewModel
{
    public bool StreamSafeMode
    {
        get => _settings.StreamSafeMode;
        set
        {
            if (_settings.StreamSafeMode == value) return;
            _settings.StreamSafeMode = value;
            OnPropertyChanged();
            Save();
            ApplyStreamSafeChange();
        }
    }

    private bool StreamSafe => _settings.StreamSafeMode;

    // Name to show on the overlays/pills/flyout ONLY. Aliased to "Alt N" (N = seat number) under
    // stream-safe mode; the seat's real label otherwise. Position codes ("TL ·") stay -- they're not
    // identifying.
    private string OverlayDisplayName(int seat) => StreamSafe ? $"Alt {seat}" : SeatLabel(seat);

    // Re-render everything that could be showing real data now that the mask flipped.
    private void ApplyStreamSafeChange()
    {
        CloseInfoFlyout();
        if (_settings.CornerOverlaysEnabled && CornerOverlaysLive) RefreshAllPills();
    }

    // Hotkey action: flip stream-safe mode. Display-only (masks what the overlay shows); never forwards
    // input to a client, so it stays within the EULA boundary (see SafetyGuard).
    private void ToggleStreamSafe()
    {
        StreamSafeMode = !StreamSafeMode;
        Log.Info($"Stream-safe mode {(StreamSafeMode ? "ON" : "OFF")}.");
    }
}
