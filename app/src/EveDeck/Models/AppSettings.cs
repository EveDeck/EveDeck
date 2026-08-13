using System.Collections.ObjectModel;

namespace EveDeck.Models;

public sealed class AppSettings
{
    public bool UsePhysicalPixels { get; set; } = true;
    public string LayoutTargetMonitorId { get; set; } = "";
    public bool UseMonitorWorkArea { get; set; }
    public bool IncludeNotepadTestWindows { get; set; } = false;
    public bool AutoRefresh { get; set; } = true;
    public string ActiveProfileId { get; set; } = "";
    public ObservableCollection<SlotAssignment> Assignments { get; set; } = new();
    public ObservableCollection<LayoutProfile> Profiles { get; set; } = new();
    public ObservableCollection<HotkeyBinding> Hotkeys { get; set; } = new();
    public ObservableCollection<CharacterSet> CharacterSets { get; set; } = new();
    public string ActiveCharacterSetId { get; set; } = "";

    // Config profiles bundle a layout profile + a character set + the overlay appearance settings
    // into one named switch ("Mining" / "PvP"). See ConfigProfile for why the first two are stored
    // as references rather than copies. Switched from the tray menu and, optionally, on startup.
    public ObservableCollection<ConfigProfile> ConfigProfiles { get; set; } = new();
    public string ActiveConfigProfileId { get; set; } = "";
    public bool ApplyConfigProfileOnStartup { get; set; } = false;
    public Dictionary<string, StyleSnapshot> StyleSnapshotsByTitle { get; set; } = new();

    public bool ActiveFrameEnabled { get; set; } = true;
    public int ActiveFrameThickness { get; set; } = 3;
    public int ActiveFrameGlowRadius { get; set; } = 3;
    public string ActiveFrameColor { get; set; } = "#FFFFFF";
    // "Snapshot" (glowing camera-viewfinder corner brackets, the original look), "Solid", "Dashed",
    // "Dotted" (a plain full-perimeter outline, no blur -- blur would smear a dash/dot pattern into a
    // fuzzy near-solid line and defeat the point of picking one), or "SnapshotFrame" (both at once --
    // corner brackets slightly thicker than a full-perimeter outline underneath them).
    public string ActiveFrameStyle { get; set; } = "Snapshot";
    // Whether the Snapshot style's corner brackets blur into a glow at all -- off gives crisp lines.
    // Has no effect on Solid/Dashed/Dotted, which never blur regardless.
    public bool ActiveFrameGlowEnabled { get; set; } = true;

    // 2g — Startup profile auto-apply
    public bool ApplyProfileOnStartup { get; set; }
    public string StartupProfileId { get; set; } = "";

    // Re-apply the active profile automatically when assigned EVE clients (re)appear — e.g. after
    // closing all clients and launching them again, without clicking Apply manually.
    public bool AutoApplyOnClientLaunch { get; set; } = true;

    // 2a — Minimize to tray
    public bool MinimizeToTray { get; set; } = true;

    // UI scale (1.0 = 100%, applied as LayoutTransform on the main window content)
    public double UiScale { get; set; } = 1.0;

    // Master slot for the swap-focused-with-master hotkey action.
    public int MasterSlotNumber { get; set; } = 1;

    // Corner overlay mode: all clients run at master resolution; corners show DWM thumbnails.
    // On by default — this is the primary grid experience. Profiles that can't form a grid
    // (single-client and stacked layouts) automatically fall back to plain window placement
    // (see LayoutProfile.SupportsCornerGrid).
    public bool CornerOverlaysEnabled { get; set; } = true;
    public bool CornerOverlayShowLabel { get; set; } = true;
    public bool CornerOverlayShowSlotNumber { get; set; } = false;
    public double CornerOverlayLabelFontSize { get; set; } = 27.0;
    public string CornerOverlayLabelStyle { get; set; } = "IconText";
    public string CornerOverlayLabelFontFamily { get; set; } = "Acens"; // bundled font, see Assets\Fonts\Acens-LICENSE.txt
    public string CornerOverlayLabelColor { get; set; } = "#E5E7EB"; // global default label text color
    public int CornerOverlayLabelHeight { get; set; } = 28; // WPF DIPs

    // Where the label sits WITHIN its tile, as a 3x3 anchor. One of:
    //   TopLeft    TopCenter    TopRight
    //   MiddleLeft Center       MiddleRight
    //   BottomLeft BottomCenter BottomRight
    // Labels are drawn inside the tile bounds rather than as a full-width strip above/below it,
    // matching how EVE-O Preview overlays its label on the thumbnail itself. Center is the default.
    // Unrecognised values fall back to Center -- see LabelSurfaceWindow.ParseAnchor.
    public string CornerOverlayLabelAnchor { get; set; } = "Center";

    // Inset in WPF DIPs between the label and the tile edge it is anchored to. Ignored on whichever
    // axis the anchor is centered. EVE-O Preview uses a fixed 8px inset; this is the same idea, made
    // adjustable because EveDeck's labels can be far larger than its 8.25pt one.
    public int CornerOverlayLabelInset { get; set; } = 5;

    // MASTER-pill override for the anchor above. Empty = inherit CornerOverlayLabelAnchor. Defaults
    // to TopCenter because the master rect is the client you are actually looking at: a label parked
    // in the middle of it sits over the ship/HUD, whereas the small corner tiles read better with the
    // name centered on the thumbnail. Per-seat SlotAssignment.LabelAnchorMaster beats this.
    public string CornerOverlayLabelAnchorMaster { get; set; } = "TopCenter";

    // Hide every preview tile while no EVE client (and not EveDeck itself) is the foreground app,
    // so alt-tabbing to a browser, Discord or a spreadsheet leaves the previews out of the way
    // instead of floating over them. Mirrors EVE-O Preview's HideThumbnailsOnLostFocus. The delay
    // stops a quick alt-tab, or the brief foreground gap during a seat swap, from flickering the
    // whole overlay off and straight back on.
    public bool HidePreviewsOnFocusLoss { get; set; } = false;
    public double HidePreviewsOnFocusLossDelaySeconds { get; set; } = 1.0;

    // Snap on-overlay tile drags/resizes to a pixel grid. 0 disables snapping. Mirrors EVE-O
    // Preview's EnableThumbnailSnap, but as a size rather than a bool so the grid can be tuned.
    public int CornerOverlaySnapGridPx { get; set; } = 0;

    // Which point a hover-zoomed tile grows FROM, as one of the same nine 3x3 names used by
    // CornerOverlayLabelAnchor. "Center" grows evenly in all directions (the original behavior);
    // an edge/corner anchor pins that edge so a tile near a screen edge expands inward instead of
    // being clamped. Per-seat SlotAssignment.ZoomAnchor overrides this.
    public string HoverZoomAnchor { get; set; } = "TopCenter";

    // Ask Windows to power-throttle (EcoQoS) EVE clients that are not the foreground window, on top
    // of the existing ThrottleBackgroundProcesses priority drop. EcoQoS parks those processes on
    // efficiency cores and lets the scheduler clock them down, which is the EULA-compliant way to
    // stop background clients burning CPU/GPU.
    //
    // Deliberately NOT a frame-rate limiter: capping another process's FPS means hooking its D3D
    // present chain via DLL injection, which AGENTS.md forbids outright. EcoQoS is pure OS-level
    // scheduling -- nothing is injected, read, or written in the EVE client. See COMPLIANCE.md.
    public bool EcoQosBackgroundClients { get; set; } = true;

    // Keep the next seat in the cycle order OUT of EcoQoS, so the client you are about to switch to
    // is already running at full speed when you get there. EVE-O Plus's "predictive" limiting idea,
    // done with scheduling instead of frame caps.
    public bool EcoQosExemptNextInCycle { get; set; } = true;

    // Hide a preview tile while its client is still sitting on the EVE login/character-select screen,
    // where the thumbnail shows nothing useful. Mirrors EVE-O Preview's HidePreviewAtLoginScreen.
    // Detection is by window title only (EVE titles the window "EVE" with no character name until a
    // character is selected) -- no memory reading, no injection.
    public bool HidePreviewsAtLoginScreen { get; set; } = true;

    // Global default label font/size/color for the MASTER (centered, near-full-size) seat's pill.
    // Empty/null = inherit the normal CornerOverlayLabelFontFamily/FontSize/LabelColor above, so
    // this is a no-op until explicitly customized. Per-seat overrides on SlotAssignment
    // (LabelFontFamilyMaster etc.) take precedence over these when set.
    public string CornerOverlayLabelFontFamilyMaster { get; set; } = "Acens";
    public double? CornerOverlayLabelFontSizeMaster { get; set; } = 27.0;
    public string CornerOverlayLabelColorMaster { get; set; } = "#E5E7EB";

    // Text style toggles for corner-overlay preview labels. Bold/Italic are plain font-weight/style
    // switches. DropShadow defaults true because the "IconText" label style has always rendered a
    // soft black shadow behind its plain-text name for legibility over bright video (this used to be
    // hardcoded); true preserves that exact look with no action needed, and additionally lets "Pill"
    // style labels opt in too (harmless there since Pill text already sits on an opaque dark chip).
    // Outline draws a black stroke around the glyphs; off by default (a new, purely additive look).
    public bool CornerOverlayLabelBold { get; set; } = true;
    public bool CornerOverlayLabelItalic { get; set; } = false;
    public bool CornerOverlayLabelDropShadow { get; set; } = true;
    public bool CornerOverlayLabelOutline { get; set; } = true;

    // Global MASTER-pill overrides for the style toggles above. null = inherit the normal toggle
    // (same fallback pattern as CornerOverlayLabelFontFamilyMaster etc.); per-seat overrides on
    // SlotAssignment (LabelBoldMaster etc.) take precedence over these when set.
    public bool? CornerOverlayLabelBoldMaster { get; set; } = null;
    public bool? CornerOverlayLabelItalicMaster { get; set; } = null;
    public bool? CornerOverlayLabelDropShadowMaster { get; set; } = null;
    public bool? CornerOverlayLabelOutlineMaster { get; set; } = null;

    // Global default label opacity (0-100%, WPF-Opacity-style whole-label fade) and its MASTER-pill
    // override. null master = inherit the normal opacity (same fallback pattern as the style toggles).
    public int CornerOverlayLabelOpacity { get; set; } = 100;
    public int? CornerOverlayLabelOpacityMaster { get; set; } = null;

    // The pill chip's own backdrop, independent of the whole-label opacity above (which fades text +
    // chip together). "None" drops the chip entirely -- just text, relying on DropShadow/Outline for
    // legibility over bright video. "Solid" is a single flat color (the historical hardcoded look).
    // "Gradient" blends top-to-bottom between Color and Color2. Colors are plain opaque RGB (same
    // WinForms ColorDialog convention as ActiveFrameColor -- no alpha channel in that picker); the
    // separate Opacity below supplies the chip's own alpha, replacing the old hardcoded ~80% (0xCC).
    // One global setting, not per-seat/master -- the chip is small chrome, not worth doubling every
    // option for.
    public string CornerOverlayLabelBackgroundStyle { get; set; } = "None";
    public string CornerOverlayLabelBackgroundColor { get; set; } = "#0000A0";
    public string CornerOverlayLabelBackgroundColor2 { get; set; } = "#000000";
    public int CornerOverlayLabelBackgroundOpacity { get; set; } = 50;

    // A subtle vector pattern drawn over the chip fill above (None/Diagonal/Dots/Noise) -- purely
    // decorative texture, independent of the Style/Color/Opacity that determine the underlying fill.
    // No-op when Style is "None" (nothing to texture).
    public string CornerOverlayLabelBackgroundTexture { get; set; } = "Diagonal";

    // Global MASTER-pill overrides for the background block above -- same ""/null = inherit fallback
    // pattern as CornerOverlayLabelBoldMaster etc., so the MASTER (home) pill's chip can look
    // different from the corner pills' shared normal settings without disturbing them.
    public string CornerOverlayLabelBackgroundStyleMaster { get; set; } = "None";
    public string CornerOverlayLabelBackgroundColorMaster { get; set; } = "#000000";
    public string CornerOverlayLabelBackgroundColor2Master { get; set; } = "#000052";
    public int? CornerOverlayLabelBackgroundOpacityMaster { get; set; } = 25;
    public string CornerOverlayLabelBackgroundTextureMaster { get; set; } = "Diagonal";

    // Chip corner roundness (px) and text inset/padding (px, horizontal/vertical) -- shape of the
    // chip itself, global only (like CornerOverlayPreviewOpacity, no per-seat/master split; the chip
    // shape isn't worth doubling every option for).
    public int CornerOverlayLabelCornerRadius { get; set; } = 6;
    public int CornerOverlayLabelPaddingH { get; set; } = 15;
    public int CornerOverlayLabelPaddingV { get; set; } = 0;

    // Preview-tile opacity (0-100%, DWM thumbnail alpha) for every corner/master DWM preview. One
    // global slider applied uniformly -- no per-seat/master split like the label opacity above.
    public int CornerOverlayPreviewOpacity { get; set; } = 80;

    // Click a corner preview tile to bring that client to the center (focus switch). Pure window
    // management — the click is NOT forwarded into the EVE client, so it stays EULA-compliant (no
    // input injection). A convenient alternative to the center-seat hotkeys for users who haven't
    // set hotkeys up. See COMPLIANCE.md.
    public bool FocusPreviewOnClick { get; set; } = true;

    // Hover over a corner preview tile to peek at that client full-size over the master. The
    // hovered client temporarily moves to the master rect and is raised to the top of the Z-order
    // so it overlays the current master window; the master's window position is unchanged. On
    // mouse-leave the client is re-parked off-screen. Pure window management — no input forwarded.
    public bool HoverPreviewEnabled { get; set; } = true;

    // How long (ms) the mouse must rest on a corner tile before the peek triggers. A short delay
    // prevents accidental peeks when the cursor merely passes over a tile. 0 = instant.
    public int HoverPreviewDelayMs { get; set; } = 650;

    // What hovering a corner tile does: "Peek" temporarily raises the real client over the master
    // (the original behaviour); "Zoom" magnifies just the preview thumbnail in place — the real
    // window is never moved. Zoom is the eve-o-preview-style option.
    public string HoverPreviewStyle { get; set; } = "Zoom";

    // Preview magnification factor for the Zoom hover style (1.5–4x).
    public double HoverZoomFactor { get; set; } = 1.5;

    // Only fire hotkeys when an EVE client is in the foreground.
    public bool RequireEveFocusForHotkeys { get; set; } = true;

    // Throttle background EVE client processes to BELOW_NORMAL CPU priority while another is focused.
    // Reduces GPU/CPU competition so the active client gets more frame budget.
    public bool ThrottleBackgroundProcesses { get; set; } = true;

    // Auto-minimize EVE clients that are not foreground (skipping NeverMinimize seats). Flat
    // layouts only — corner-overlay mode needs unminimized windows for live thumbnails, so the
    // option is ignored while corner overlays are live.
    public bool AutoMinimizeInactiveClients { get; set; } = false;

    // Hide a corner tile's live preview while that seat's client IS the foreground window (it's
    // already on screen full-size, the tile is redundant clutter). eve-o-preview's
    // "hide active client thumbnail" equivalent.
    public bool HideActiveSeatTile { get; set; } = true;

    // First-run setup wizard has been completed (controls whether it auto-shows on launch).
    public bool SetupCompleted { get; set; } = false;

    // The app version the "What's New" changelog window was last shown for. Empty on a genuinely
    // fresh install (the Setup Wizard owns first-run, not this) -- MainWindowViewModel seeds it to
    // the current version right after setup completes, so the changelog only pops on a REAL future
    // update, not on the user's very first run. An existing settings.json from before this field
    // existed deserializes it as empty too, which is what makes the changelog show once for
    // upgrading users (SetupCompleted is already true for them) instead of just fresh installs.
    public string LastSeenChangelogVersion { get; set; } = "";

    // Last main-window position (screen coordinates). Null until the window has been shown once;
    // restored on launch when still within the visible virtual screen.
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    // User-supplied override for the EVE Launcher executable path, used when it isn't found at
    // either of the common install locations ClientLaunchService checks first.
    public string? EveLauncherPathOverride { get; set; }

    // Structured game-event alert rules watched by GameLogWatcherService (Gamelogs, not Chatlogs).
    // Seeded with defaults for fresh installs / pre-feature saves; JSON round-trip replaces the
    // collection, so user edits (including deleting every rule) persist as-is.
    public ObservableCollection<GameEventRule> GameEventRules { get; set; } = new(GameEventRule.Defaults());

    // ── Toast notification placement + native OS mirror ───────────────────────
    // Corner/edge the toast stack anchors to. One of: TopLeft, TopCenter, TopRight, BottomLeft,
    // BottomCenter, BottomRight. Bottom* stacks grow upward (newest nearest the anchored edge),
    // Top* stacks grow downward -- see ToastAnchor in ToastNotificationWindow.
    public string ToastPosition { get; set; } = "BottomRight";
    // Also mirrors every toast into the real Windows Notification Center (SuppressPopup -- no
    // second banner, EveDeck's own styled popup stays the only visible one) so alerts are still
    // reviewable from the system clock's flyout after EveDeck's popup has faded. Best-effort: OS
    // notification plumbing this app doesn't control (AUMID registration, the user's own Windows
    // notification settings) can silently no-op this without affecting the primary toast pipeline.
    public bool NativeNotificationCenterEnabled { get; set; } = true;

    // Abyss Mode: suppresses the SOUND on tile-glow (FlashOnTile) game events while enabled, but
    // keeps the visual glow. Abyssal Deadspace lets up to three characters take continuous combat
    // damage simultaneously in their own instances -- without this the Combat rule's default sound
    // would fire almost constantly for the whole run. A manual session toggle rather than a
    // permanent per-rule setting, since the user still wants the sound during normal play.
    public bool AbyssModeEnabled { get; set; }

    // Toast notifications assert the topmost slot ahead of the corner-overlay surfaces AND any
    // Overlay Allow List app, so an alert is never buried behind a preview tile or a docked
    // Discord/Mumble window. Off means toasts take their chances in the topmost band like any other
    // window -- allow-listed apps re-assert themselves every tick while EVE is focused, so they will
    // generally end up on top.
    public bool ToastsAboveOverlays { get; set; } = true;

    // Append the character's current solar system (tracked from Local chatlog headers) to the
    // corner-overlay labels.
    public bool CornerOverlayShowSystem { get; set; } = true;

    // Once every seat has been simultaneously offline (no live window for any seat) for this many
    // seconds, the corner overlay tears itself down instead of leaving a wall of stale "Name ·
    // offline" pills on screen after the whole session has ended. 0 = never auto-teardown.
    public int OfflineOverlayTimeoutSeconds { get; set; } = 5;

    // Hides a seat's "Name · offline" pill after it has been continuously offline for this many
    // seconds. 0 = hide immediately (no offline text ever shown). -1 (default) = never hide,
    // preserving the original always-on behavior. Independent of OfflineOverlayTimeoutSeconds,
    // which only tears down the WHOLE overlay once EVERY seat is offline at once.
    public int OfflinePillTimeoutSeconds { get; set; } = 0;

    // EveDeck-rendered Mumble talker overlay (fed by the EveDeck Mumble plugin over a named
    // pipe). Only Enabled/Locked/X/Y/OpacityPercent are used -- the window owns its own size.
    public UtilityOverlaySlot TalkerOverlay { get; set; } = new();

    // Profile Sync: manual character-id -> account (core_user) id overrides. Auto-pairing uses
    // file-mtime correlation, which the user can correct in the UI; corrections are kept here
    // so they survive restarts and stale mtimes.
    public Dictionary<string, string> ProfileCharAccountOverrides { get; set; } = new();

    // Apps allowed to visually sit above the corner-overlay tile/pill surfaces even while an EVE
    // client has focus (e.g. voice/intel tools the user keeps positioned over the game). Matched
    // by case-insensitive substring against the window's owning process name.
    public ObservableCollection<OverlayAllowedApp> OverlayAllowedApps { get; set; } = new()
    {
        new() { ProcessName = "mumble" },
        new() { ProcessName = "rift" },
        new() { ProcessName = "pyfa" },
        new() { ProcessName = "discord" },
        new() { ProcessName = "pidgin" },
    };

    // Non-EVE apps whose windows also show up as detected/assignable windows so they can be
    // previewed in a corner tile alongside EVE clients. Empty by default -- opt-in per app, unlike
    // OverlayAllowedApps above (which ships with sensible defaults for a DIFFERENT purpose).
    public ObservableCollection<PreviewableApp> PreviewableApps { get; set; } = new();



    // Preview info flyout (added 2026-07-24): a small "i" badge on each corner preview opens a compact
    // card of live ESI facts for that seat's character. On by default; stays empty until a character
    // is linked via ESI (needs the location/ship-type/fatigue/skill-queue scopes). Each line is
    // independently toggleable so the card stays as small as the user wants.
    public bool CornerOverlayInfoButtonEnabled { get; set; } = true;
    // Independent of Windows' own per-monitor DPI scaling (added 2026-07-29): scales the info
    // badge, jump-status badges, and their popup text so they can be read on a small/high-density
    // display (e.g. a tablet used as a monitor) without cranking Windows scaling up, which affects
    // the whole desktop including EVE itself.
    public double CornerOverlayChromeScale { get; set; } = 1.0;
    public bool InfoFlyoutShowWallet { get; set; } = true;
    public bool InfoFlyoutShowShip { get; set; } = true;
    public bool InfoFlyoutShowLocation { get; set; } = true;
    public bool InfoFlyoutShowFatigue { get; set; } = true;
    public bool InfoFlyoutShowSkill { get; set; } = true;
    // Kill activity in the character's current system, from zKillboard (last 1h / 24h).
    public bool InfoFlyoutShowDanger { get; set; } = true;
    // Total + unallocated skill points.
    public bool InfoFlyoutShowSp { get; set; } = true;

    // Jump-status badges on each corner preview (added 2026-07-28): small "F"/"R" badges in the
    // tile's top-left corner, shown only while a linked character has active jump fatigue or is on
    // its jump-reactivation cooldown; hover for the exact remaining time. On by default; stays
    // invisible until a character is linked via ESI (needs the fatigue/skills scopes, already
    // covered by the standard link). One toggle for both, since the badges are meant to be glanced
    // at together rather than tuned independently.
    public bool CornerOverlayShowJumpBadges { get; set; } = true;

    // Seat health alerts (added 2026-07-28): toast notifications for a linked character's ship
    // flipping to a capsule (correlated against zKillboard to distinguish a real kill from a
    // deliberate bare-pod flight or an in-station ship swap) and for an ESI online/offline mismatch
    // against the seat's window (catches a silently disconnected client whose window is still open).
    // Independent toggles -- these are unrelated failure modes, on their own polling timer, not tied
    // to corner overlays being enabled. On by default; the online check needs a character linked with
    // a fresh online scope (see EsiAuthService.ScopeOnline) to actually fire.
    public bool SeatHealthAlertPodded { get; set; } = true;
    public bool SeatHealthAlertDisconnected { get; set; } = true;

    // Stream-safe mode (added 2026-07-24): masks identifying info on the overlays / pills / info flyout
    // for streamers -- aliases character names to "Alt N", drops system names, and hides wallet ISK.
    // Flip it mid-stream with the ToggleStreamSafe hotkey. Never touches the main app UI.
    public bool StreamSafeMode { get; set; } = false;

    // Downtime countdown (added 2026-07-24): a small always-on-top readout counting down the final
    // DowntimeLeadMinutes before EVE's daily downtime, plus one toast when that window opens. Time is
    // How long after downtime starts to stop running seat-health checks. ESI goes down with the
    // cluster, so every check in that window fails with a 502/504 and logs a warning per seat -- 55 of
    // them in one five-minute stretch on an ordinary day. A normal restart is 5-10 minutes; 30 gives
    // it room. Patch days run far longer, which the automatic outage backoff in
    // MainWindowViewModel.SeatHealth handles rather than this needing to be set to an hour.
    // 0 disables the scheduled skip entirely.
    public int SeatHealthDowntimeSkipMinutes { get; set; } = 30;

    // UTC "HH:mm" (EVE's historical DT is 11:00 UTC). On by default.
    public bool DowntimeCountdownEnabled { get; set; } = true;
    public string DowntimeUtcTime { get; set; } = "11:00";
    public int DowntimeLeadMinutes { get; set; } = 60;
    // Where the countdown readout sits (same six anchors as ToastPosition). TopCenter collides with
    // EVE's own top-centre UI, so default to a top corner instead.
    public string DowntimePosition { get; set; } = "TopLeft";

}
