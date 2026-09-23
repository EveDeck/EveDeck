namespace EveDeck.ViewModels;

public sealed partial class MainWindowViewModel
{
    public bool CornerOverlaysEnabled
    {
        get => _settings.CornerOverlaysEnabled;
        set
        {
            if (_settings.CornerOverlaysEnabled == value) return;
            _settings.CornerOverlaysEnabled = value;
            OnPropertyChanged();
            UpdatePositionCodes();
            // This toggle is half of what decides preview vs flat mode, so the Layouts-tab readout
            // has to follow it -- otherwise it keeps advertising the mode that was in effect before.
            RebuildLayoutPreview();
            RaiseLayoutModeDependents();
            if (!value) StopCornerOverlays();
            Save();
        }
    }

    public bool CornerOverlayShowLabel
    {
        get => _settings.CornerOverlayShowLabel;
        set
        {
            if (_settings.CornerOverlayShowLabel == value) return;
            _settings.CornerOverlayShowLabel = value;
            OnPropertyChanged();
            Save();
            if (PreviewModeActive && CornerOverlaysLive) StartCornerOverlays();
        }
    }

    public bool FocusPreviewOnClick
    {
        get => _settings.FocusPreviewOnClick;
        set
        {
            if (_settings.FocusPreviewOnClick == value) return;
            _settings.FocusPreviewOnClick = value;
            OnPropertyChanged();
            Save();
        }
    }

    public bool HoverPreviewEnabled
    {
        get => _settings.HoverPreviewEnabled;
        set
        {
            if (_settings.HoverPreviewEnabled == value) return;
            _settings.HoverPreviewEnabled = value;
            OnPropertyChanged();
            Save();
        }
    }

    public int HoverPreviewDelayMs
    {
        get => _settings.HoverPreviewDelayMs;
        set
        {
            var clamped = Math.Max(0, Math.Min(2000, value));
            if (_settings.HoverPreviewDelayMs == clamped) return;
            _settings.HoverPreviewDelayMs = clamped;
            _hoverPeekTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, clamped));
            OnPropertyChanged();
            Save();
        }
    }

    public string HoverPreviewStyle
    {
        get => _settings.HoverPreviewStyle;
        set
        {
            if (_settings.HoverPreviewStyle == value || string.IsNullOrEmpty(value)) return;
            _settings.HoverPreviewStyle = value;
            OnPropertyChanged();
            Save();
        }
    }

    public double HoverZoomFactor
    {
        get => _settings.HoverZoomFactor;
        set
        {
            // Ceiling raised from 4x to 8x: scaling the destination rect is the only legal way to
            // make a small HUD readable (cropping the client is forbidden -- see SafetyGuard), and
            // DWM re-composites the live window at the larger size rather than upscaling.
            var clamped = Math.Clamp(value, 1.5, 8.0);
            if (Math.Abs(_settings.HoverZoomFactor - clamped) < 0.01) return;
            _settings.HoverZoomFactor = clamped;
            OnPropertyChanged();
            Save();
        }
    }

    public bool CornerOverlayShowSlotNumber
    {
        get => _settings.CornerOverlayShowSlotNumber;
        set
        {
            if (_settings.CornerOverlayShowSlotNumber == value) return;
            _settings.CornerOverlayShowSlotNumber = value;
            OnPropertyChanged();
            Save();
            if (PreviewModeActive && CornerOverlaysLive) RefreshAllPills();
        }
    }

    public bool CornerOverlayShowSystem
    {
        get => _settings.CornerOverlayShowSystem;
        set
        {
            if (_settings.CornerOverlayShowSystem == value) return;
            _settings.CornerOverlayShowSystem = value;
            OnPropertyChanged();
            Save();
            if (PreviewModeActive && CornerOverlaysLive) RefreshAllPills();
        }
    }

    public double CornerOverlayLabelFontSize
    {
        get => _settings.CornerOverlayLabelFontSize;
        set
        {
            var clamped = Math.Clamp(value, 6.0, 72.0);
            if (Math.Abs(_settings.CornerOverlayLabelFontSize - clamped) < 0.1) return;
            _settings.CornerOverlayLabelFontSize = clamped; OnPropertyChanged(); Save();
            if (PreviewModeActive && CornerOverlaysLive) StartCornerOverlays();
        }
    }

    public string CornerOverlayLabelStyle
    {
        get => _settings.CornerOverlayLabelStyle;
        set
        {
            if (_settings.CornerOverlayLabelStyle == value || string.IsNullOrEmpty(value)) return;
            _settings.CornerOverlayLabelStyle = value;
            OnPropertyChanged();
            Save();
            if (PreviewModeActive && CornerOverlaysLive) StartCornerOverlays();
        }
    }

    // Suspend the live corner previews without tearing the overlay down -- hides both overlay
    // surfaces so DWM stops compositing every registered thumbnail, then shows them again to resume
    // (no re-register blink, since nothing is unregistered). Runtime-only, deliberately NOT persisted
    // for the same reason as HotkeysSuspended: a tool that silently stayed "previews off" across a
    // restart would read as broken. ApplyPreviewsSuspended (partial, in CornerOverlays) does the
    // show/hide and is re-run whenever the overlay is rebuilt so a rebuild stays suspended.
    private bool _previewsSuspended;
    public bool PreviewsSuspended
    {
        get => _previewsSuspended;
        set
        {
            if (_previewsSuspended == value) return;
            _previewsSuspended = value;
            OnPropertyChanged();
            Log.Info(value ? "Corner previews suspended." : "Corner previews resumed.");
            ApplyPreviewsSuspended();
        }
    }

    public bool ThrottleBackgroundProcesses
    {
        get => _settings.ThrottleBackgroundProcesses;
        set
        {
            if (_settings.ThrottleBackgroundProcesses == value) return;
            _settings.ThrottleBackgroundProcesses = value;
            OnPropertyChanged();
            if (!value) RestoreAllProcessPriorities();
            Save();
        }
    }

    public bool AutoMinimizeInactiveClients
    {
        get => _settings.AutoMinimizeInactiveClients;
        set
        {
            if (_settings.AutoMinimizeInactiveClients == value) return;
            _settings.AutoMinimizeInactiveClients = value;
            OnPropertyChanged();
            Save();
        }
    }

    public bool HideActiveSeatTile
    {
        get => _settings.HideActiveSeatTile;
        set
        {
            if (_settings.HideActiveSeatTile == value) return;
            _settings.HideActiveSeatTile = value;
            OnPropertyChanged();
            Save();
        }
    }

    // EVE-O Preview's HideThumbnailsOnLostFocus. Takes effect on the next overlay tick, so no
    // rebuild is needed -- see UpdateFocusLossHiding.
    // Compliant substitute for EVE-O Plus's DirectX frame limiting -- see AppSettings for why frame
    // limiting itself is off the table. Applied on the next foreground change; turning it OFF must
    // clear throttling immediately or clients stay parked on efficiency cores.
    public bool EcoQosBackgroundClients
    {
        get => _settings.EcoQosBackgroundClients;
        set
        {
            if (_settings.EcoQosBackgroundClients == value) return;
            _settings.EcoQosBackgroundClients = value;
            OnPropertyChanged();
            if (!value) RestoreAllProcessPriorities();
            else ApplyProcessPriorities(_windowService.GetForegroundWindowHandle());
            Save();
        }
    }

    public bool EcoQosExemptNextInCycle
    {
        get => _settings.EcoQosExemptNextInCycle;
        set
        {
            if (_settings.EcoQosExemptNextInCycle == value) return;
            _settings.EcoQosExemptNextInCycle = value;
            OnPropertyChanged();
            // Re-evaluate now: the previously-exempt client should get throttled (or un-throttled)
            // without waiting for the next foreground change.
            if (_settings.EcoQosBackgroundClients) ApplyProcessPriorities(_windowService.GetForegroundWindowHandle());
            Save();
        }
    }

    // Vendor-specific pointer to the OS-level way to cap BACKGROUND EVE-client frame rate -- the
    // compliant alternative to EveDeck ever hooking the client. Shown under Performance options.
    public string BackgroundFpsCapTip => Utilities.GpuInfo.DetectVendor() switch
    {
        Utilities.GpuVendor.Nvidia =>
            "NVIDIA GPU detected. In NVIDIA Control Panel > Manage 3D Settings > Program Settings, add EVE (exefile.exe) and set \"Background Application Max Frame Rate\" to about 15-30 FPS. That caps alt clients whenever they're not focused, with no change to the game itself.",
        Utilities.GpuVendor.Amd =>
            "AMD GPU detected. In AMD Software > Gaming, select EVE (add exefile.exe if it's not listed) and enable Radeon Chill with a low min/max, or set a Frame Rate Target Control cap. AMD has no dedicated \"background\" cap, so a per-game Chill range is the closest equivalent.",
        Utilities.GpuVendor.Intel =>
            "Intel GPU detected. Intel's control panel has limited per-app frame control -- use EVE's own frame-rate limit (Esc > Settings > Display & Graphics), or a tool like RTSS, to hold background clients down.",
        _ =>
            "Set a per-application frame-rate cap for EVE (exefile.exe) in your GPU's control panel -- around 15-30 FPS for background clients. EVE also has its own limit under Esc > Settings > Display & Graphics.",
    };

    public bool SuspendPreviewsUnderGpuLoad
    {
        get => _settings.SuspendPreviewsUnderGpuLoad;
        set
        {
            if (_settings.SuspendPreviewsUnderGpuLoad == value) return;
            _settings.SuspendPreviewsUnderGpuLoad = value;
            OnPropertyChanged();
            Save();
        }
    }

    // Opt-in GPU capture for preview tiles. DWM thumbnails stay the default: they composite
    // themselves, so they are lighter and lower-latency. WGC trades that for a sharper image at
    // small tile sizes, and is what VR capture needs. Baked into the surface, so changing it
    // rebuilds; any per-tile failure still falls back to a DWM thumbnail on its own.
    public bool UseWgcPreviewCapture
    {
        get => _settings.UseWgcPreviewCapture;
        set
        {
            if (_settings.UseWgcPreviewCapture == value) return;
            _settings.UseWgcPreviewCapture = value;
            OnPropertyChanged();
            Save();
            if (PreviewModeActive && CornerOverlaysLive) StartCornerOverlays();
        }
    }

    // Caps WGC capture AND the redraw pump that feeds it, so this is the latency dial: 15 costs up
    // to ~130ms before a frame is composited, 30 about half that, at more CPU/GPU per tile.
    public int WgcPreviewMaxFps
    {
        get => _settings.WgcPreviewMaxFps;
        set
        {
            var clamped = Math.Clamp(value, 5, 60);
            if (_settings.WgcPreviewMaxFps == clamped) return;
            _settings.WgcPreviewMaxFps = clamped;
            OnPropertyChanged();
            Save();
            if (PreviewModeActive && CornerOverlaysLive) StartCornerOverlays();
        }
    }

    // Which point a hover-zoomed tile grows from. Baked into the surface, so changing it rebuilds.
    public string HoverZoomAnchor
    {
        get => _settings.HoverZoomAnchor;
        set
        {
            var chosen = string.IsNullOrWhiteSpace(value) ? "Center" : value;
            if (_settings.HoverZoomAnchor == chosen) return;
            _settings.HoverZoomAnchor = chosen;
            OnPropertyChanged();
            Save();
            if (PreviewModeActive && CornerOverlaysLive) StartCornerOverlays();
        }
    }

    public bool HidePreviewsOnFocusLoss
    {
        get => _settings.HidePreviewsOnFocusLoss;
        set
        {
            if (_settings.HidePreviewsOnFocusLoss == value) return;
            _settings.HidePreviewsOnFocusLoss = value;
            OnPropertyChanged();
            Save();
        }
    }

    public double HidePreviewsOnFocusLossDelaySeconds
    {
        get => _settings.HidePreviewsOnFocusLossDelaySeconds;
        set
        {
            var clamped = Math.Clamp(value, 0, 60);
            if (Math.Abs(_settings.HidePreviewsOnFocusLossDelaySeconds - clamped) < 0.001) return;
            _settings.HidePreviewsOnFocusLossDelaySeconds = clamped;
            OnPropertyChanged();
            Save();
        }
    }

    // Label placement within the tile (3x3 anchor) and its inset off the edge it hugs. Both are baked
    // into LabelSurfaceWindow at construction, so changing either rebuilds the overlay.
    public string CornerOverlayLabelAnchor
    {
        get => _settings.CornerOverlayLabelAnchor;
        set
        {
            var chosen = string.IsNullOrWhiteSpace(value) ? "Center" : value;
            if (_settings.CornerOverlayLabelAnchor == chosen) return;
            _settings.CornerOverlayLabelAnchor = chosen;
            OnPropertyChanged();
            Save();
            if (PreviewModeActive && CornerOverlaysLive) StartCornerOverlays();
        }
    }

    // Master-pill override for the label anchor. Empty falls back to CornerOverlayLabelAnchor; the
    // shipped default is TopCenter so the big center client's name stays clear of its ship/HUD.
    public string CornerOverlayLabelAnchorMaster
    {
        get => _settings.CornerOverlayLabelAnchorMaster;
        set
        {
            var chosen = value ?? "";
            if (_settings.CornerOverlayLabelAnchorMaster == chosen) return;
            _settings.CornerOverlayLabelAnchorMaster = chosen;
            OnPropertyChanged();
            Save();
            if (PreviewModeActive && CornerOverlaysLive) StartCornerOverlays();
        }
    }

    public int CornerOverlayLabelInset
    {
        get => _settings.CornerOverlayLabelInset;
        set
        {
            var clamped = Math.Clamp(value, 0, 200);
            if (_settings.CornerOverlayLabelInset == clamped) return;
            _settings.CornerOverlayLabelInset = clamped;
            OnPropertyChanged();
            Save();
            if (PreviewModeActive && CornerOverlaysLive) StartCornerOverlays();
        }
    }

    // EVE-O Preview's EnableThumbnailSnap, as a grid size rather than a bool. Pushed straight at the
    // live surface -- no rebuild, so the next drag snaps immediately.
    public int CornerOverlaySnapGridPx
    {
        get => _settings.CornerOverlaySnapGridPx;
        set
        {
            var clamped = Math.Clamp(value, 0, 500);
            if (_settings.CornerOverlaySnapGridPx == clamped) return;
            _settings.CornerOverlaySnapGridPx = clamped;
            OnPropertyChanged();
            Save();
            if (_tileSurface is not null) _tileSurface.SnapGridPx = clamped;
        }
    }

    public int OfflineOverlayTimeoutSeconds
    {
        get => _settings.OfflineOverlayTimeoutSeconds;
        set
        {
            var clamped = Math.Max(0, value);
            if (_settings.OfflineOverlayTimeoutSeconds == clamped) return;
            _settings.OfflineOverlayTimeoutSeconds = clamped;
            OnPropertyChanged();
            Save();
        }
    }

    public int OfflinePillTimeoutSeconds
    {
        get => _settings.OfflinePillTimeoutSeconds;
        set
        {
            var clamped = Math.Max(-1, value);
            if (_settings.OfflinePillTimeoutSeconds == clamped) return;
            _settings.OfflinePillTimeoutSeconds = clamped;
            OnPropertyChanged();
            Save();
        }
    }

    internal void ApplyProcessPriorities(nint focusedHandle)
    {
        if (_settings.ThrottleBackgroundProcesses)
        {
            foreach (var w in Windows)
                _windowService.SetProcessPriority((uint)w.ProcessId, w.Handle != focusedHandle);
        }

        if (_settings.EcoQosBackgroundClients)
        {
            // Optional: also spare the client the user is about to switch to, so it's already at full
            // speed when they land on it. 0 when the toggle is off, there are fewer than two cyclable
            // windows, or focus isn't currently on one of them -- see NextWindowInCycle, which reads
            // the SAME ordering the Cycle hotkeys walk so the prediction can't disagree with reality.
            var exemptHandle = _settings.EcoQosExemptNextInCycle ? NextWindowInCycle(focusedHandle) : 0;
            foreach (var w in Windows)
            {
                var keepFullSpeed = w.Handle == focusedHandle || (exemptHandle != 0 && w.Handle == exemptHandle);
                _windowService.SetProcessEcoQos((uint)w.ProcessId, !keepFullSpeed);
            }
        }
    }

    private void RestoreAllProcessPriorities()
    {
        foreach (var w in Windows)
        {
            _windowService.SetProcessPriority((uint)w.ProcessId, false);
            // Unconditional (not gated on EcoQosBackgroundClients): this is the teardown path for both
            // the setting being switched off and app exit, so it must clear EcoQoS regardless of the
            // toggle's current value -- otherwise a client throttled while the setting was on stays
            // parked on efficiency cores after EveDeck stops managing it.
            _windowService.SetProcessEcoQos((uint)w.ProcessId, false);
        }
    }


    // Tracks the last EVE-foreground state so repeated foreground changes (e.g. tabbing between two EVE
    // clients) don't re-issue redundant SetWindowPos calls; only real EVE<->non-EVE transitions apply.
    private bool? _lastEveForeground;

    // Force a fresh apply next call (after layout apply / window (re)assignment the cache is stale).
    internal void ApplyTopmostState()
    {
        _lastEveForeground = null;
        RefreshTopmostForForeground(_windowService.GetForegroundWindowHandle());
    }

    // Focus-gated always-on-top: a pinned seat's window is HWND_TOPMOST only while an EVE client is the
    // foreground app, so pinned windows float over EVE but drop out of the way when you switch to a
    // non-EVE app. Driven by the foreground WinEvent hook (HotkeyService.ForegroundChanged).
    internal void RefreshTopmostForForeground(nint foregroundHwnd)
    {
        var eveForeground = foregroundHwnd != 0 && Windows.Any(w => w.Handle == foregroundHwnd);
        if (_lastEveForeground == eveForeground) return;
        _lastEveForeground = eveForeground;

        foreach (var seat in Assignments)
        {
            if (!seat.IsTopmost) continue;
            foreach (var w in FindAssignedWindows(seat))
                _windowService.SetWindowTopmost(w.Handle, eveForeground);
        }
    }

    private void ToggleTopmostForActive()
    {
        var fg = _windowService.GetForegroundWindowHandle();
        if (fg == 0) return;
        var seat = Assignments.FirstOrDefault(a => FindAssignedWindows(a).Any(w => w.Handle == fg));
        if (seat is null) return;
        seat.IsTopmost = !seat.IsTopmost;
        _windowService.SetWindowTopmost(fg, seat.IsTopmost);
        Save();
        Log.Info($"Seat {seat.SlotNumber} ({seat.Label}) always-on-top: {seat.IsTopmost}.");
    }
}
