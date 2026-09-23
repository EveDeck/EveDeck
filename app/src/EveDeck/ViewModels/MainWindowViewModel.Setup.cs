using EveDeck.Models;
using EveDeck.Services;

namespace EveDeck.ViewModels;

public sealed partial class MainWindowViewModel
{
    public bool NeedsSetup => !_settings.SetupCompleted;

    // Recompute each assignment's positional code (Master / TL / TR / BL / BR / TC / BC / ...) from the
    // active profile's slot geometry. Used in the UI and overlay pills instead of a bare slot number.
    // Falls back to slot numbers when codes would collide (e.g. a stacked layout, all slots at 0,0).
    internal void UpdatePositionCodes()
    {
        var profile = SelectedProfile;
        if (profile is null || profile.Slots.Count == 0)
        {
            foreach (var a in Assignments) a.PositionCode = CircledNumeral(a.SlotNumber);
            return;
        }

        // Pure geometric codes per position slot — no seat-identity overrides. Computed per swap
        // group via GroupGridCodes (shared with CornerCode) so a collision or a skewed bounding box
        // in one group's ring (e.g. a different-monitor master) can't affect another group's
        // otherwise-clean codes.
        var codes = new Dictionary<int, string>();
        foreach (var group in EffectiveGroups())
            foreach (var (slotNum, code) in GroupGridCodes(profile, group))
                codes[slotNum] = code;

        // In corner-overlay mode with live occupancy, each seat card shows WHERE THAT SEAT'S
        // WINDOW CURRENTLY IS on screen: "Master" if centered, or the corner's geometric arrow.
        // This is correct even after swaps (e.g. Seat 1 sent to BL still shows ↙, not ↖).
        if (PreviewModeActive && _cornerSeatByGroup.Count > 0)
        {
            // Build a merged seat->currentPosition map across all groups.
            var seatToPosition = new Dictionary<int, int>();
            foreach (var (_, corners) in _cornerSeatByGroup)
                foreach (var (pos, seat) in corners)
                    seatToPosition[seat] = pos;

            // Determine which seats are centered in their respective group.
            var centeredSeats = new HashSet<int>(_centeredSeatByGroup.Values);

            foreach (var a in Assignments)
            {
                if (centeredSeats.Contains(a.SlotNumber))
                    a.PositionCode = "★";
                else if (seatToPosition.TryGetValue(a.SlotNumber, out var pos)
                         && codes.TryGetValue(pos, out var posCode))
                    a.PositionCode = posCode;
                else
                    a.PositionCode = CircledNumeral(a.SlotNumber);
            }
            return;
        }

        // Flat / no-overlay: seat numbers double as position keys (identity home arrangement).
        foreach (var a in Assignments)
            a.PositionCode = codes.TryGetValue(a.SlotNumber, out var code) ? code : CircledNumeral(a.SlotNumber);
    }

    // Map a slot to a directional arrow symbol from its center within the layout bounds.
    private static string GridCode(LayoutSlot slot, int minX, int minY, int totalW, int totalH)
    {
        var normX = (slot.X + slot.Width / 2.0 - minX) / totalW;
        var normY = (slot.Y + slot.Height / 2.0 - minY) / totalH;

        var col = normX < 1.0 / 3 ? "L" : normX < 2.0 / 3 ? "C" : "R";
        var row = normY < 1.0 / 3 ? "T" : normY < 2.0 / 3 ? "M" : "B";

        // Corners get bracket glyphs that echo the physical screen corner; edges get heavy arrows
        // pointing at the window; the geometric center gets the Master star. Kept as single glyphs so
        // they render crisp in the 26px pills / slot cards without a custom font.
        return (row, col) switch
        {
            ("T", "L") => "⌜",
            ("T", "C") => "▲",
            ("T", "R") => "⌝",
            ("M", "L") => "◀",
            ("M", "C") => "★",
            ("M", "R") => "▶",
            ("B", "L") => "⌞",
            ("B", "C") => "▼",
            ("B", "R") => "⌟",
            _ => row + col,
        };
    }

    // Returns the 3x3 zone bucket (row: T/M/B, col: L/C/R) for a slot's center within the layout bounds.
    // Used by FocusDirection to resolve which slot occupies a given screen direction at runtime.
    private static (string row, string col) GridBucket(LayoutSlot slot, int minX, int minY, int totalW, int totalH)
    {
        var normX = (slot.X + slot.Width / 2.0 - minX) / totalW;
        var normY = (slot.Y + slot.Height / 2.0 - minY) / totalH;
        var col = normX < 1.0 / 3 ? "L" : normX < 2.0 / 3 ? "C" : "R";
        var row = normY < 1.0 / 3 ? "T" : normY < 2.0 / 3 ? "M" : "B";
        return (row, col);
    }

    // User dismissed the wizard without finishing — stop auto-showing it (re-runnable from Settings).
    public void DismissSetup()
    {
        if (_settings.SetupCompleted) return;
        _settings.SetupCompleted = true;
        Save();
        OnPropertyChanged(nameof(NeedsSetup));
    }

    // Apply the choices collected by the setup wizard: target monitor, an appropriately-sized
    // built-in profile for the client count, and (whenever that profile is a Center Master ring)
    // the corner-master configuration.
    public void RunInitialSetup(int clientCount, string monitorId, bool focusPreviewOnClick = true)
    {
        Refresh(); // make sure the monitor list is current before we resolve the selection

        if (!string.IsNullOrWhiteSpace(monitorId) && Monitors.Any(m => m.Id == monitorId))
            LayoutTargetMonitorId = monitorId;

        var mon = Monitors.FirstOrDefault(m => m.Id == LayoutTargetMonitorId)
            ?? Monitors.FirstOrDefault(m => m.IsPrimary)
            ?? Monitors.FirstOrDefault();
        var width = mon?.Bounds.Width ?? 2560;
        var height = mon?.Bounds.Height ?? 1440;

        var profile = ResolveAndApplyBestProfile(clientCount, width, height);

        // Gate on the profile that was actually applied, not on the raw client count. The wizard
        // promises "a ring with a master client centered" for anything IsCenterMaster (4..15) and
        // ResolveAndApplyBestProfile hands back the Center Master family for that whole range, so
        // keying overlays off clientCount == 5 left every other ring user with the layout applied
        // and no preview tiles at all. TemplateCount is the count the family snapped to (the
        // dropdown offers 4,5,6,7,8,9,10,12,15), and PopulateCenterMasterSlots always makes the
        // last slot the largest, centered master -- so the master seat is TemplateCount, not 5.
        if (profile is not null && profile.Category == "Center Master" && profile.TemplateCount >= 4)
        {
            // Corner-master layout: full-screen, master = the centered slot, overlays on.
            UseMonitorWorkArea = false;
            FocusPreviewOnClick = focusPreviewOnClick;
            ActiveMasterSeat = profile.TemplateCount;
            SyncMasterSlot();
            CornerOverlaysEnabled = true;
        }
        else
        {
            CornerOverlaysEnabled = false;
            ActiveMasterSeat = 1;
            SyncMasterSlot();
        }

        UpdatePositionCodes();
        _settings.SetupCompleted = true;
        Save();
        OnPropertyChanged(nameof(NeedsSetup));
        Log.Info($"Setup complete: {clientCount} client(s) on '{mon?.DeviceName ?? "?"}', profile '{profile?.Name ?? "none"}'.");

        // Rebuild corner occupancy with the new master and restart overlays so hover-peek works immediately.
        if (PreviewModeActive)
        {
            ResetCornerOccupancy();
            UpdatePositionCodes();
            StartCornerOverlays();
        }
    }

    // Spawn one Notepad window per slot so the user can test layouts without EVE running.
    // Each window is titled "EveDeck TEST - Slot N" so it's visually distinct in the window list.
    public void SpawnTestWindows()
    {
        var count = Assignments.Count;
        if (count == 0) { Status = "No slots configured — add slots first."; return; }

        IncludeNotepadTestWindows = true;

        var spawned = new List<System.Diagnostics.Process>();
        for (var i = 0; i < count; i++)
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe") { UseShellExecute = true });
            if (p is not null) spawned.Add(p);
        }

        // Wait for all windows to become ready, then rename them.
        Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(300);
                var allReady = spawned.All(p => { try { p.Refresh(); return p.MainWindowHandle != 0; } catch { return false; } });
                if (allReady) break;
            }

            for (var i = 0; i < spawned.Count; i++)
            {
                try
                {
                    spawned[i].Refresh();
                    var hwnd = spawned[i].MainWindowHandle;
                    if (hwnd != 0)
                        Utilities.Win32Native.SetWindowText(hwnd, $"EveDeck TEST - Slot {i + 1}");
                }
                catch { /* process may have exited */ }
            }

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Refresh();
                Status = $"Spawned {spawned.Count} Notepad test window(s).";
                Log.Info($"Spawned {spawned.Count} Notepad test window(s) for layout testing.");
            });
        });
    }

    // Select the built-in profile that best matches the current slot count and target monitor.
    public void AutoSelectBestProfile()
    {
        var count = Assignments.Count;
        var mon = Monitors.FirstOrDefault(m => m.Id == LayoutTargetMonitorId)
            ?? Monitors.FirstOrDefault(m => m.IsPrimary)
            ?? Monitors.FirstOrDefault();
        var w = mon?.Bounds.Width ?? 2560;
        var h = mon?.Bounds.Height ?? 1440;

        var profile = ResolveAndApplyBestProfile(count, w, h);
        if (profile is null)
        {
            Status = $"No built-in profile found for {count} slot(s) at {w}x{h}.";
            return;
        }
        Status = $"Auto-selected profile: {profile.Name}";
        Log.Info($"Auto-selected profile '{profile.Name}' for {count} slot(s) on {w}x{h}.");
    }

    // Shared by AutoSelectBestProfile and RunInitialSetup: for 1 client, pick the fixed "1-Char {res}"
    // profile by name; for 2+, point the Grid/Center Master family template at the nearest curated
    // resolution+count and regenerate its slots, then select it. Returns null if nothing suitable exists.
    private LayoutProfile? ResolveAndApplyBestProfile(int clientCount, int monitorWidth, int monitorHeight)
    {
        if (clientCount == 1)
        {
            var name = PresetFactory.BestProfileName(clientCount, monitorWidth, monitorHeight);
            var solo = name is null ? null : Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (solo is not null) SelectedProfile = solo;
            return solo;
        }

        var sel = PresetFactory.ResolveFamilySelection(clientCount, monitorWidth, monitorHeight);
        if (sel is null) return null;

        var family = Profiles.FirstOrDefault(p => p.IsFamilyTemplate && p.Category == sel.Value.Category);
        if (family is null) return null;

        family.TemplateWidth = sel.Value.Width;
        family.TemplateHeight = sel.Value.Height;
        family.TemplateCount = sel.Value.Count;
        PresetFactory.RegenerateFamilySlots(family);
        SelectedProfile = family;
        OnPropertyChanged(nameof(SelectedFamilyResolution));
        OnPropertyChanged(nameof(SelectedFamilyCount));
        Save();
        return family;
    }
}
