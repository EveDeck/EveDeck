using System.Windows.Threading;
using EveDeck.Models;
using EveDeck.Services;
using EveDeck.Views;

namespace EveDeck.ViewModels;

// Live damage/logi/cap/mining readout drawn on each preview tile -- the rough equivalent of running
// one PELD window per character, except the numbers come from EveDeck's own gamelog tail rather than
// from another process.
//
// Why not actually drive PELD: a running PELD instance exposes NOTHING locally -- no HTTP server, no
// pipe, no socket, no export file. Its only outbound channel is a Socket.IO CLIENT aimed at a remote
// fleet server, which its own README says is disabled. So there is no seam to read numbers out of,
// and reformatting them "nicely" needs the raw values anyway. Meanwhile GameLogWatcherService already
// tails the exact files PELD parses, so the data was already in this process.
//
// Purely passive log reading. Nothing here sends input to EVE. See COMPLIANCE.md.
public sealed partial class MainWindowViewModel
{
    private readonly DpsMeterService _dpsMeter = new();
    private readonly DispatcherTimer _dpsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _dpsWired;

    // Colour per row. Damage out is the neutral bright one; incoming damage borrows the same red the
    // combat alert glow uses, so "I am being hit" reads consistently across the overlay.
    private const string DpsOutColor   = "#E5E7EB";
    private const string DpsInColor    = "#F87171";
    private const string DpsLogiColor  = "#4ADE80";
    private const string DpsCapColor   = "#22D3EE";
    private const string DpsMiningColor = "#FBBF24";
    // Received effects are dimmer variants of the outgoing colour: same meaning, other direction.
    private const string DpsLogiInColor = "#86EFAC";
    private const string DpsCapInColor  = "#67E8F9";
    // Neut/nos is cap being destroyed rather than moved, so it gets its own hue entirely -- reading it
    // as a shade of the cap-transfer colour would suggest the two are the same effect.
    private const string DpsNeutOutColor = "#C084FC";
    private const string DpsNeutInColor  = "#F0ABFC";

    private void StartDpsMeter()
    {
        _dpsMeter.WindowSeconds = _settings.DpsWindowSeconds;

        // Subscribed once for the process lifetime rather than per overlay rebuild: the overlay is
        // torn down and recreated on nearly every settings tweak, and re-subscribing there would
        // stack duplicate handlers and multiply every sample.
        if (!_dpsWired)
        {
            _gameLogWatcherService.LineRead += OnGameLogLineRead;
            _dpsWired = true;
        }

        if (!_settings.CornerOverlayShowDps) { StopDpsMeter(); return; }
        _dpsTimer.Tick -= OnDpsTick;
        _dpsTimer.Tick += OnDpsTick;
        _dpsTimer.Start();
    }

    private void StopDpsMeter()
    {
        _dpsTimer.Stop();
        _dpsTimer.Tick -= OnDpsTick;
        _labelSurface?.ClearAllDpsPanels();
    }

    // Raised off the watcher's file-change callback, i.e. NOT on the UI thread. The meter is plain
    // in-memory state with no dispatcher affinity, so ingest happens here and only the once-a-second
    // render hops to the UI thread -- bouncing every combat line through the dispatcher would put a
    // burst of hundreds of marshalled calls on the UI thread during a real fight.
    private void OnGameLogLineRead(string character, string line)
    {
        if (!_settings.CornerOverlayShowDps || string.IsNullOrEmpty(character)) return;
        lock (_dpsMeter) _dpsMeter.Ingest(character, line, DateTime.UtcNow);
    }

    private void OnDpsTick(object? sender, EventArgs e)
    {
        if (_labelSurface is null || !_settings.CornerOverlayShowDps) return;

        var now = DateTime.UtcNow;
        lock (_dpsMeter)
        {
            _dpsMeter.WindowSeconds = _settings.DpsWindowSeconds;
            _dpsMeter.Tick(now);
            ReportUnknownOres();
        }

        var style = CurrentDpsPanelStyle();
        foreach (var (position, rect) in AllDpsPanelRects())
        {
            var seat = OccupantAtPosition(position);
            var character = ResolveSeatCharacter(seat)?.CharacterName;
            if (string.IsNullOrWhiteSpace(character) || FindSeatWindow(seat) is null)
            {
                _labelSurface.ClearDpsPanel(position);
                continue;
            }

            DpsReading reading;
            lock (_dpsMeter) reading = _dpsMeter.GetReading(character);

            // Idleness is judged on the ENABLED rows, not on the whole reading. Judging it on the
            // reading meant a client being neuted or repped un-hid a panel whose every visible row
            // still read zero, because the rows carrying that data were switched off -- a panel of
            // noughts appearing for no visible reason, which is worse than staying hidden.
            var rows = BuildDpsRows(reading, out var anyNonZero);
            if (_settings.DpsHideWhenIdle && !anyNonZero)
            {
                _labelSurface.ClearDpsPanel(position);
                continue;
            }

            _labelSurface.SetDpsPanel(position, rect, rows, style);
        }
    }

    // An ore with no volume-table entry is converted at a fallback rate, which would otherwise be an
    // invisible approximation sitting inside a number the user reads as exact. Logged once per ore
    // per session so the table can be corrected rather than quietly drifting.
    private readonly HashSet<string> _reportedUnknownOres = new(StringComparer.OrdinalIgnoreCase);

    private void ReportUnknownOres()
    {
        foreach (var ore in _dpsMeter.UnknownOres)
        {
            if (!_reportedUnknownOres.Add(ore)) continue;
            Log.Warn($"Mining: no volume on record for '{ore}', converting at 0.1 m3 per unit. "
                   + "The m3 figure for that ore is an estimate until its volume is added.");
        }
    }

    // Every position that currently has somewhere to draw: the preview tiles, plus any dominant
    // master area (Center Master and friends) which has a rect but no tile of its own.
    private IEnumerable<(int Position, WindowRect Rect)> AllDpsPanelRects()
    {
        foreach (var (position, rect) in _cornerRects) yield return (position, rect);
        foreach (var (position, rect) in _groupCenterRects)
            if (!_cornerRects.ContainsKey(position)) yield return (position, rect);
    }

    private DpsPanelStyle CurrentDpsPanelStyle() => new(
        LabelSurfaceWindow.ParseAnchor(_settings.DpsPanelAnchor),
        _settings.DpsPanelInset,
        _settings.DpsPanelScale,
        _settings.DpsPanelFontFamily,
        _settings.DpsPanelFontSize,
        _settings.DpsPanelTextColor,
        _settings.DpsPanelBold,
        _settings.DpsPanelItalic,
        _settings.DpsPanelDropShadow,
        _settings.DpsPanelOutline,
        _settings.DpsPanelBackgroundColor,
        _settings.DpsPanelBackgroundOpacity,
        _settings.DpsPanelCornerRadius);

    // -- Font, via the same dialog the character labels use ----------------------------------------
    // Mirrors GlobalLabelFont/ApplyGlobalLabelFont so the Options row is the familiar
    // "Choose font..." button plus a summary, not a bespoke set of boxes.

    internal (string Family, double SizeDip, string ColorHex) GlobalDpsPanelFont()
        => (_settings.DpsPanelFontFamily, _settings.DpsPanelFontSize, _settings.DpsPanelTextColor);

    internal void ApplyDpsPanelFont(string family, double sizeDip, string colorHex)
    {
        _settings.DpsPanelFontFamily = string.IsNullOrWhiteSpace(family) ? "Segoe UI" : family;
        _settings.DpsPanelFontSize = Math.Clamp(sizeDip, 6.0, 48.0);
        _settings.DpsPanelTextColor = string.IsNullOrWhiteSpace(colorHex) ? "#E5E7EB" : colorHex;
        OnPropertyChanged(nameof(DpsPanelFontSummary));
        OnPropertyChanged(nameof(DpsPanelTextBrush));
        Save();
    }

    public string DpsPanelFontSummary =>
        $"{(string.IsNullOrWhiteSpace(_settings.DpsPanelFontFamily) ? "Segoe UI" : _settings.DpsPanelFontFamily)}"
        + $", {_settings.DpsPanelFontSize:0.#}px";

    // Swatch brushes for the Options colour rows, same pattern as ActiveFrameBrush / LabelBackgroundBrush.
    public System.Windows.Media.Brush DpsPanelTextBrush => SwatchBrush(_settings.DpsPanelTextColor, "#E5E7EB");
    public System.Windows.Media.Brush DpsPanelBackgroundBrush => SwatchBrush(_settings.DpsPanelBackgroundColor, "#0B0F17");

    private static System.Windows.Media.Brush SwatchBrush(string hex, string fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
                return new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        }
        catch { /* fall through to the documented default */ }
        return new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(fallback));
    }

    public bool DpsPanelBold
    {
        get => _settings.DpsPanelBold;
        set { if (_settings.DpsPanelBold == value) return; _settings.DpsPanelBold = value; OnPropertyChanged(); Save(); }
    }

    public bool DpsPanelItalic
    {
        get => _settings.DpsPanelItalic;
        set { if (_settings.DpsPanelItalic == value) return; _settings.DpsPanelItalic = value; OnPropertyChanged(); Save(); }
    }

    public bool DpsPanelDropShadow
    {
        get => _settings.DpsPanelDropShadow;
        set { if (_settings.DpsPanelDropShadow == value) return; _settings.DpsPanelDropShadow = value; OnPropertyChanged(); Save(); }
    }

    public bool DpsPanelOutline
    {
        get => _settings.DpsPanelOutline;
        set { if (_settings.DpsPanelOutline == value) return; _settings.DpsPanelOutline = value; OnPropertyChanged(); Save(); }
    }

    // `anyNonZero` reports whether any ENABLED row actually carries a value, which is what the
    // hide-while-idle decision has to be made on -- see OnDpsTick.
    private List<DpsPanelRow> BuildDpsRows(DpsReading reading, out bool anyNonZero)
    {
        var rows = new List<DpsPanelRow>(7);
        var sawValue = false;   // a local function cannot capture an out parameter

        void Add(bool enabled, string label, double value, string color, string suffix = "")
        {
            if (!enabled) return;
            if (value > 0) sawValue = true;
            rows.Add(new DpsPanelRow(label, FormatRate(value) + suffix, color));
        }

        Add(_settings.DpsShowDamageOut, "OUT", reading.DamageOut, DpsOutColor);
        Add(_settings.DpsShowDamageIn, "IN", reading.DamageIn, DpsInColor);
        Add(_settings.DpsShowLogistics, "LOGI", reading.Logistics, DpsLogiColor);
        Add(_settings.DpsShowLogisticsIn, "REPD", reading.LogisticsIn, DpsLogiInColor);
        Add(_settings.DpsShowCapTransferOut, "CAP+", reading.CapTransferOut, DpsCapColor);
        Add(_settings.DpsShowCapTransferIn, "CAP-", reading.CapTransferIn, DpsCapInColor);
        // "NEUT" rather than "CAP OUT/IN": neut and nos take cap AWAY, which is the opposite of what
        // a cap transfer does, so they must never share a label with it.
        Add(_settings.DpsShowNeutOut, "NEUT", reading.NeutOut, DpsNeutOutColor);
        Add(_settings.DpsShowNeutIn, "NEUTD", reading.NeutIn, DpsNeutInColor);
        // Mining is m3 per MINUTE, not per second -- the "/m" suffix keeps that different timescale
        // visible in the panel rather than leaving it a silent inconsistency between rows.
        Add(_settings.DpsShowMining, "ORE", reading.MiningM3PerMinute, DpsMiningColor, "/m");

        anyNonZero = sawValue;
        return rows;
    }

    // -- Settings-bound properties ----------------------------------------------------------------
    //
    // Style changes need no overlay rebuild: the once-a-second tick re-reads the style and re-applies
    // it, so a slider drag updates live instead of tearing the surfaces down on every value change.

    public bool CornerOverlayShowDps
    {
        get => _settings.CornerOverlayShowDps;
        set
        {
            if (_settings.CornerOverlayShowDps == value) return;
            _settings.CornerOverlayShowDps = value;
            OnPropertyChanged();
            Save();
            if (value && CornerOverlaysLive) StartDpsMeter();
            else StopDpsMeter();
        }
    }

    public int DpsWindowSeconds
    {
        get => _settings.DpsWindowSeconds;
        set
        {
            var clamped = Math.Clamp(value, 2, 120);
            if (_settings.DpsWindowSeconds == clamped) return;
            _settings.DpsWindowSeconds = clamped;
            OnPropertyChanged();
            Save();
        }
    }

    public bool DpsShowDamageOut
    {
        get => _settings.DpsShowDamageOut;
        set { if (_settings.DpsShowDamageOut == value) return; _settings.DpsShowDamageOut = value; OnPropertyChanged(); Save(); }
    }

    public bool DpsShowDamageIn
    {
        get => _settings.DpsShowDamageIn;
        set { if (_settings.DpsShowDamageIn == value) return; _settings.DpsShowDamageIn = value; OnPropertyChanged(); Save(); }
    }

    public bool DpsShowLogistics
    {
        get => _settings.DpsShowLogistics;
        set { if (_settings.DpsShowLogistics == value) return; _settings.DpsShowLogistics = value; OnPropertyChanged(); Save(); }
    }

    public bool DpsShowCapTransferOut
    {
        get => _settings.DpsShowCapTransferOut;
        set { if (_settings.DpsShowCapTransferOut == value) return; _settings.DpsShowCapTransferOut = value; OnPropertyChanged(); Save(); }
    }

    public bool DpsShowCapTransferIn
    {
        get => _settings.DpsShowCapTransferIn;
        set { if (_settings.DpsShowCapTransferIn == value) return; _settings.DpsShowCapTransferIn = value; OnPropertyChanged(); Save(); }
    }

    public bool DpsShowNeutOut
    {
        get => _settings.DpsShowNeutOut;
        set { if (_settings.DpsShowNeutOut == value) return; _settings.DpsShowNeutOut = value; OnPropertyChanged(); Save(); }
    }

    public bool DpsShowMining
    {
        get => _settings.DpsShowMining;
        set { if (_settings.DpsShowMining == value) return; _settings.DpsShowMining = value; OnPropertyChanged(); Save(); }
    }

    public bool DpsShowLogisticsIn
    {
        get => _settings.DpsShowLogisticsIn;
        set { if (_settings.DpsShowLogisticsIn == value) return; _settings.DpsShowLogisticsIn = value; OnPropertyChanged(); Save(); }
    }

    public bool DpsShowNeutIn
    {
        get => _settings.DpsShowNeutIn;
        set { if (_settings.DpsShowNeutIn == value) return; _settings.DpsShowNeutIn = value; OnPropertyChanged(); Save(); }
    }

    public bool DpsHideWhenIdle
    {
        get => _settings.DpsHideWhenIdle;
        set { if (_settings.DpsHideWhenIdle == value) return; _settings.DpsHideWhenIdle = value; OnPropertyChanged(); Save(); }
    }

    public string DpsPanelAnchor
    {
        get => _settings.DpsPanelAnchor;
        set { if (_settings.DpsPanelAnchor == value) return; _settings.DpsPanelAnchor = value ?? "BottomRight"; OnPropertyChanged(); Save(); }
    }

    public int DpsPanelInset
    {
        get => _settings.DpsPanelInset;
        set { var c = Math.Clamp(value, 0, 200); if (_settings.DpsPanelInset == c) return; _settings.DpsPanelInset = c; OnPropertyChanged(); Save(); }
    }

    public double DpsPanelScale
    {
        get => _settings.DpsPanelScale;
        set { var c = Math.Clamp(value, 0.4, 4.0); if (Math.Abs(_settings.DpsPanelScale - c) < 0.001) return; _settings.DpsPanelScale = c; OnPropertyChanged(); Save(); }
    }

    public double DpsPanelFontSize
    {
        get => _settings.DpsPanelFontSize;
        set { var c = Math.Clamp(value, 6.0, 48.0); if (Math.Abs(_settings.DpsPanelFontSize - c) < 0.001) return; _settings.DpsPanelFontSize = c; OnPropertyChanged(); Save(); }
    }

    public string DpsPanelTextColor
    {
        get => _settings.DpsPanelTextColor;
        set
        {
            if (_settings.DpsPanelTextColor == value) return;
            _settings.DpsPanelTextColor = value ?? "#E5E7EB";
            OnPropertyChanged();
            OnPropertyChanged(nameof(DpsPanelTextBrush));
            Save();
        }
    }

    public string DpsPanelBackgroundColor
    {
        get => _settings.DpsPanelBackgroundColor;
        set
        {
            if (_settings.DpsPanelBackgroundColor == value) return;
            _settings.DpsPanelBackgroundColor = value ?? "#0B0F17";
            OnPropertyChanged();
            OnPropertyChanged(nameof(DpsPanelBackgroundBrush));
            Save();
        }
    }

    public int DpsPanelBackgroundOpacity
    {
        get => _settings.DpsPanelBackgroundOpacity;
        set { var c = Math.Clamp(value, 0, 100); if (_settings.DpsPanelBackgroundOpacity == c) return; _settings.DpsPanelBackgroundOpacity = c; OnPropertyChanged(); Save(); }
    }

    public int DpsPanelCornerRadius
    {
        get => _settings.DpsPanelCornerRadius;
        set { var c = Math.Clamp(value, 0, 40); if (_settings.DpsPanelCornerRadius == c) return; _settings.DpsPanelCornerRadius = c; OnPropertyChanged(); Save(); }
    }

    // Compact enough to sit in a tile corner: thousands collapse to "1.2k" so the panel keeps a
    // stable width instead of jumping about as the numbers swing during a fight.
    internal static string FormatRate(double value)
    {
        if (value <= 0) return "0";
        if (value < 10) return value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        if (value < 1000) return value.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        if (value < 10000) return (value / 1000).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "k";
        return (value / 1000).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "k";
    }
}
