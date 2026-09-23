using Brush = System.Windows.Media.Brush;
using EveDeck.Models;

namespace EveDeck.ViewModels;

public sealed partial class MainWindowViewModel
{
    // One-line summary of the global default label font for the Options tab (e.g. "Segoe UI, 13px").
    public string LabelFontSummary
    {
        get
        {
            var family = string.IsNullOrWhiteSpace(_settings.CornerOverlayLabelFontFamily)
                ? "Segoe UI" : _settings.CornerOverlayLabelFontFamily;
            return $"{family}, {_settings.CornerOverlayLabelFontSize:0}px";
        }
    }

    // Current global default label font (family, WPF DIP size, colour hex) for seeding the font dialog.
    public (string family, double sizeDip, string color) GlobalLabelFont() =>
        (_settings.CornerOverlayLabelFontFamily ?? "", _settings.CornerOverlayLabelFontSize, _settings.CornerOverlayLabelColor ?? "");

    // Applies the global DEFAULT label font (family + size + colour) chosen in the WinForms font dialog.
    // Size is a WPF DIP value already (the caller converts from the dialog's points). Rebuilds overlays.
    public void ApplyGlobalLabelFont(string family, double sizeDip, string colorHex)
    {
        _settings.CornerOverlayLabelFontFamily = family ?? "";
        _settings.CornerOverlayLabelFontSize = Math.Clamp(sizeDip, 6.0, 72.0);
        if (!string.IsNullOrWhiteSpace(colorHex)) _settings.CornerOverlayLabelColor = colorHex;
        OnPropertyChanged(nameof(CornerOverlayLabelFontSize));
        OnPropertyChanged(nameof(LabelFontSummary));
        Save();
        if (PreviewModeActive && CornerOverlaysLive) StartCornerOverlays();
    }

    // Applies (or clears, when args are null) a single seat's label font overrides. Rebuilds overlays.
    public void ApplySeatLabelFont(SlotAssignment seat, string? family, double? sizeDip, string? colorHex)
    {
        seat.LabelFontFamily = string.IsNullOrWhiteSpace(family) ? null : family;
        seat.LabelFontSize = sizeDip.HasValue ? Math.Clamp(sizeDip.Value, 6.0, 72.0) : null;
        seat.LabelColor = string.IsNullOrWhiteSpace(colorHex) ? null : colorHex;
        Save();
        if (PreviewModeActive && CornerOverlaysLive) StartCornerOverlays();
    }

    // One-line summary of the MASTER label font for the Options tab — shows the concrete effective
    // values whether they're explicitly set or inherited from the normal default font.
    public string MasterLabelFontSummary
    {
        get
        {
            var family = string.IsNullOrWhiteSpace(_settings.CornerOverlayLabelFontFamilyMaster)
                ? (string.IsNullOrWhiteSpace(_settings.CornerOverlayLabelFontFamily) ? "Segoe UI" : _settings.CornerOverlayLabelFontFamily)
                : _settings.CornerOverlayLabelFontFamilyMaster;
            var size = _settings.CornerOverlayLabelFontSizeMaster ?? _settings.CornerOverlayLabelFontSize;
            return $"{family}, {size:0}px";
        }
    }

    // Current global MASTER label font (family, WPF DIP size, colour hex) for seeding the font
    // dialog — falls back to the normal default's concrete values when unset.
    public (string family, double sizeDip, string color) GlobalMasterLabelFont() =>
        (string.IsNullOrWhiteSpace(_settings.CornerOverlayLabelFontFamilyMaster) ? _settings.CornerOverlayLabelFontFamily : _settings.CornerOverlayLabelFontFamilyMaster,
         _settings.CornerOverlayLabelFontSizeMaster ?? _settings.CornerOverlayLabelFontSize,
         string.IsNullOrWhiteSpace(_settings.CornerOverlayLabelColorMaster) ? _settings.CornerOverlayLabelColor : _settings.CornerOverlayLabelColorMaster);

    // Applies the global MASTER label font (family + size + colour) chosen in the WinForms font dialog.
    public void ApplyGlobalMasterLabelFont(string family, double sizeDip, string colorHex)
    {
        _settings.CornerOverlayLabelFontFamilyMaster = family ?? "";
        _settings.CornerOverlayLabelFontSizeMaster = Math.Clamp(sizeDip, 6.0, 72.0);
        if (!string.IsNullOrWhiteSpace(colorHex)) _settings.CornerOverlayLabelColorMaster = colorHex;
        OnPropertyChanged(nameof(MasterLabelFontSummary));
        Save();
        if (PreviewModeActive && CornerOverlaysLive) StartCornerOverlays();
    }

    // Clears the global MASTER label font override so it inherits the normal default again.
    public void ResetGlobalMasterLabelFont()
    {
        _settings.CornerOverlayLabelFontFamilyMaster = "";
        _settings.CornerOverlayLabelFontSizeMaster = null;
        _settings.CornerOverlayLabelColorMaster = "";
        OnPropertyChanged(nameof(MasterLabelFontSummary));
        Save();
        if (PreviewModeActive && CornerOverlaysLive) StartCornerOverlays();
    }

    // Applies (or clears, when args are null) a single seat's MASTER label font overrides.
    public void ApplySeatMasterLabelFont(SlotAssignment seat, string? family, double? sizeDip, string? colorHex)
    {
        seat.LabelFontFamilyMaster = string.IsNullOrWhiteSpace(family) ? null : family;
        seat.LabelFontSizeMaster = sizeDip.HasValue ? Math.Clamp(sizeDip.Value, 6.0, 72.0) : null;
        seat.LabelColorMaster = string.IsNullOrWhiteSpace(colorHex) ? null : colorHex;
        Save();
        if (PreviewModeActive && CornerOverlaysLive) StartCornerOverlays();
    }

    // -- Label style toggles (bold/italic/drop shadow/outline) --------------------------------

    private void RaiseLabelStyleChanged()
    {
        OnPropertyChanged(nameof(LabelBold));
        OnPropertyChanged(nameof(LabelItalic));
        OnPropertyChanged(nameof(LabelDropShadow));
        OnPropertyChanged(nameof(LabelOutline));
        OnPropertyChanged(nameof(LabelOpacity));
        OnPropertyChanged(nameof(MasterLabelBold));
        OnPropertyChanged(nameof(MasterLabelItalic));
        OnPropertyChanged(nameof(MasterLabelDropShadow));
        OnPropertyChanged(nameof(MasterLabelOutline));
        OnPropertyChanged(nameof(MasterLabelOpacity));
        OnPropertyChanged(nameof(MasterLabelBackgroundStyle));
        OnPropertyChanged(nameof(MasterLabelBackgroundColor));
        OnPropertyChanged(nameof(MasterLabelBackgroundColor2));
        OnPropertyChanged(nameof(MasterLabelBackgroundBrush));
        OnPropertyChanged(nameof(MasterLabelBackgroundBrush2));
        OnPropertyChanged(nameof(MasterLabelBackgroundOpacity));
        OnPropertyChanged(nameof(MasterLabelBackgroundTexture));
    }

    private void SaveAndRefreshOverlays()
    {
        Save();
        if (PreviewModeActive && CornerOverlaysLive) StartCornerOverlays();
    }

    public bool LabelBold
    {
        get => _settings.CornerOverlayLabelBold;
        set { if (_settings.CornerOverlayLabelBold == value) return; _settings.CornerOverlayLabelBold = value; OnPropertyChanged(); SaveAndRefreshOverlays(); }
    }

    public bool LabelItalic
    {
        get => _settings.CornerOverlayLabelItalic;
        set { if (_settings.CornerOverlayLabelItalic == value) return; _settings.CornerOverlayLabelItalic = value; OnPropertyChanged(); SaveAndRefreshOverlays(); }
    }

    public bool LabelDropShadow
    {
        get => _settings.CornerOverlayLabelDropShadow;
        set { if (_settings.CornerOverlayLabelDropShadow == value) return; _settings.CornerOverlayLabelDropShadow = value; OnPropertyChanged(); SaveAndRefreshOverlays(); }
    }

    public bool LabelOutline
    {
        get => _settings.CornerOverlayLabelOutline;
        set { if (_settings.CornerOverlayLabelOutline == value) return; _settings.CornerOverlayLabelOutline = value; OnPropertyChanged(); SaveAndRefreshOverlays(); }
    }

    public int LabelOpacity
    {
        get => _settings.CornerOverlayLabelOpacity;
        set
        {
            var clamped = Math.Clamp(value, 20, 100);
            if (_settings.CornerOverlayLabelOpacity == clamped) return;
            _settings.CornerOverlayLabelOpacity = clamped;
            OnPropertyChanged();
            SaveAndRefreshOverlays();
        }
    }

    // Pill chip backdrop: style (None/Solid/Gradient) + one or two colors + the chip's own opacity,
    // independent of LabelOpacity above (which fades the whole label, chip and text together). One
    // global setting, no per-seat/MASTER split -- see AppSettings.CornerOverlayLabelBackgroundStyle.
    public string LabelBackgroundStyle
    {
        get => _settings.CornerOverlayLabelBackgroundStyle;
        set
        {
            if (_settings.CornerOverlayLabelBackgroundStyle == value || string.IsNullOrEmpty(value)) return;
            _settings.CornerOverlayLabelBackgroundStyle = value;
            OnPropertyChanged();
            SaveAndRefreshOverlays();
        }
    }

    public string LabelBackgroundColor
    {
        get => _settings.CornerOverlayLabelBackgroundColor;
        set
        {
            if (_settings.CornerOverlayLabelBackgroundColor == value || string.IsNullOrEmpty(value)) return;
            _settings.CornerOverlayLabelBackgroundColor = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LabelBackgroundBrush));
            SaveAndRefreshOverlays();
        }
    }

    // Gradient end color -- only visible/used when LabelBackgroundStyle is "Gradient".
    public string LabelBackgroundColor2
    {
        get => _settings.CornerOverlayLabelBackgroundColor2;
        set
        {
            if (_settings.CornerOverlayLabelBackgroundColor2 == value || string.IsNullOrEmpty(value)) return;
            _settings.CornerOverlayLabelBackgroundColor2 = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LabelBackgroundBrush2));
            SaveAndRefreshOverlays();
        }
    }

    // Swatch previews for the Options-tab color picker buttons -- same pattern as ActiveFrameBrush.
    public Brush LabelBackgroundBrush => ParseFrameBrush(LabelBackgroundColor);
    public Brush LabelBackgroundBrush2 => ParseFrameBrush(LabelBackgroundColor2);

    public int LabelBackgroundOpacity
    {
        get => _settings.CornerOverlayLabelBackgroundOpacity;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (_settings.CornerOverlayLabelBackgroundOpacity == clamped) return;
            _settings.CornerOverlayLabelBackgroundOpacity = clamped;
            OnPropertyChanged();
            SaveAndRefreshOverlays();
        }
    }

    // Vector pattern drawn over the chip fill (None/Diagonal/Dots/Noise) -- see
    // AppSettings.CornerOverlayLabelBackgroundTexture.
    public string LabelBackgroundTexture
    {
        get => _settings.CornerOverlayLabelBackgroundTexture;
        set
        {
            if (_settings.CornerOverlayLabelBackgroundTexture == value || string.IsNullOrEmpty(value)) return;
            _settings.CornerOverlayLabelBackgroundTexture = value;
            OnPropertyChanged();
            SaveAndRefreshOverlays();
        }
    }

    // Chip corner roundness and text inset (horizontal/vertical padding) -- global only, no
    // per-seat/MASTER split (see AppSettings.CornerOverlayLabelCornerRadius/PaddingH/PaddingV).
    public int LabelCornerRadius
    {
        get => _settings.CornerOverlayLabelCornerRadius;
        set
        {
            var clamped = Math.Clamp(value, 0, 20);
            if (_settings.CornerOverlayLabelCornerRadius == clamped) return;
            _settings.CornerOverlayLabelCornerRadius = clamped;
            OnPropertyChanged();
            SaveAndRefreshOverlays();
        }
    }

    public int LabelPaddingH
    {
        get => _settings.CornerOverlayLabelPaddingH;
        set
        {
            var clamped = Math.Clamp(value, 0, 30);
            if (_settings.CornerOverlayLabelPaddingH == clamped) return;
            _settings.CornerOverlayLabelPaddingH = clamped;
            OnPropertyChanged();
            SaveAndRefreshOverlays();
        }
    }

    public int LabelPaddingV
    {
        get => _settings.CornerOverlayLabelPaddingV;
        set
        {
            var clamped = Math.Clamp(value, 0, 20);
            if (_settings.CornerOverlayLabelPaddingV == clamped) return;
            _settings.CornerOverlayLabelPaddingV = clamped;
            OnPropertyChanged();
            SaveAndRefreshOverlays();
        }
    }

    // Preview-tile opacity: one global slider, applied to every DWM preview tile (corners AND the
    // master/center one) via TileSurfaceWindow.SetOpacity. Unlike LabelOpacity there's no per-seat
    // or MASTER split -- StartCornerOverlays re-applies it on rebuild via SaveAndRefreshOverlays.
    public int PreviewOpacity
    {
        get => _settings.CornerOverlayPreviewOpacity;
        set
        {
            var clamped = Math.Clamp(value, 10, 100);
            if (_settings.CornerOverlayPreviewOpacity == clamped) return;
            _settings.CornerOverlayPreviewOpacity = clamped;
            OnPropertyChanged();
            if (CornerOverlaysLive) _tileSurface?.SetOpacity(clamped);
            Save();
        }
    }

    // MASTER-pill style toggles: always shows/sets the EFFECTIVE value (falls back to the normal
    // toggle above when no master override is set yet), mirroring MasterLabelFontSummary. Setting
    // one always writes an explicit master override; ResetGlobalMasterLabelStyle clears all four.
    public bool MasterLabelBold
    {
        get => _settings.CornerOverlayLabelBoldMaster ?? _settings.CornerOverlayLabelBold;
        set { _settings.CornerOverlayLabelBoldMaster = value; OnPropertyChanged(); SaveAndRefreshOverlays(); }
    }

    public bool MasterLabelItalic
    {
        get => _settings.CornerOverlayLabelItalicMaster ?? _settings.CornerOverlayLabelItalic;
        set { _settings.CornerOverlayLabelItalicMaster = value; OnPropertyChanged(); SaveAndRefreshOverlays(); }
    }

    public bool MasterLabelDropShadow
    {
        get => _settings.CornerOverlayLabelDropShadowMaster ?? _settings.CornerOverlayLabelDropShadow;
        set { _settings.CornerOverlayLabelDropShadowMaster = value; OnPropertyChanged(); SaveAndRefreshOverlays(); }
    }

    public bool MasterLabelOutline
    {
        get => _settings.CornerOverlayLabelOutlineMaster ?? _settings.CornerOverlayLabelOutline;
        set { _settings.CornerOverlayLabelOutlineMaster = value; OnPropertyChanged(); SaveAndRefreshOverlays(); }
    }

    public int MasterLabelOpacity
    {
        get => _settings.CornerOverlayLabelOpacityMaster ?? _settings.CornerOverlayLabelOpacity;
        set { _settings.CornerOverlayLabelOpacityMaster = Math.Clamp(value, 20, 100); OnPropertyChanged(); SaveAndRefreshOverlays(); }
    }

    // MASTER-pill background overrides: same "always shows/sets the effective value" pattern as the
    // style toggles above. Setting any of these always writes an explicit master override;
    // ResetGlobalMasterLabelStyle clears them all back to inheriting the normal background.
    public string MasterLabelBackgroundStyle
    {
        get => string.IsNullOrWhiteSpace(_settings.CornerOverlayLabelBackgroundStyleMaster)
            ? _settings.CornerOverlayLabelBackgroundStyle : _settings.CornerOverlayLabelBackgroundStyleMaster;
        set { if (string.IsNullOrEmpty(value)) return; _settings.CornerOverlayLabelBackgroundStyleMaster = value; OnPropertyChanged(); SaveAndRefreshOverlays(); }
    }

    public string MasterLabelBackgroundColor
    {
        get => string.IsNullOrWhiteSpace(_settings.CornerOverlayLabelBackgroundColorMaster)
            ? _settings.CornerOverlayLabelBackgroundColor : _settings.CornerOverlayLabelBackgroundColorMaster;
        set
        {
            if (string.IsNullOrEmpty(value)) return;
            _settings.CornerOverlayLabelBackgroundColorMaster = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MasterLabelBackgroundBrush));
            SaveAndRefreshOverlays();
        }
    }

    public string MasterLabelBackgroundColor2
    {
        get => string.IsNullOrWhiteSpace(_settings.CornerOverlayLabelBackgroundColor2Master)
            ? _settings.CornerOverlayLabelBackgroundColor2 : _settings.CornerOverlayLabelBackgroundColor2Master;
        set
        {
            if (string.IsNullOrEmpty(value)) return;
            _settings.CornerOverlayLabelBackgroundColor2Master = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MasterLabelBackgroundBrush2));
            SaveAndRefreshOverlays();
        }
    }

    public Brush MasterLabelBackgroundBrush => ParseFrameBrush(MasterLabelBackgroundColor);
    public Brush MasterLabelBackgroundBrush2 => ParseFrameBrush(MasterLabelBackgroundColor2);

    public int MasterLabelBackgroundOpacity
    {
        get => _settings.CornerOverlayLabelBackgroundOpacityMaster ?? _settings.CornerOverlayLabelBackgroundOpacity;
        set { _settings.CornerOverlayLabelBackgroundOpacityMaster = Math.Clamp(value, 0, 100); OnPropertyChanged(); SaveAndRefreshOverlays(); }
    }

    public string MasterLabelBackgroundTexture
    {
        get => string.IsNullOrWhiteSpace(_settings.CornerOverlayLabelBackgroundTextureMaster)
            ? _settings.CornerOverlayLabelBackgroundTexture : _settings.CornerOverlayLabelBackgroundTextureMaster;
        set { if (string.IsNullOrEmpty(value)) return; _settings.CornerOverlayLabelBackgroundTextureMaster = value; OnPropertyChanged(); SaveAndRefreshOverlays(); }
    }

    // Clears the global MASTER style overrides so all four (plus opacity) inherit the normal
    // toggles again.
    public void ResetGlobalMasterLabelStyle()
    {
        _settings.CornerOverlayLabelBoldMaster = null;
        _settings.CornerOverlayLabelItalicMaster = null;
        _settings.CornerOverlayLabelDropShadowMaster = null;
        _settings.CornerOverlayLabelOutlineMaster = null;
        _settings.CornerOverlayLabelOpacityMaster = null;
        _settings.CornerOverlayLabelBackgroundStyleMaster = "";
        _settings.CornerOverlayLabelBackgroundColorMaster = "";
        _settings.CornerOverlayLabelBackgroundColor2Master = "";
        _settings.CornerOverlayLabelBackgroundOpacityMaster = null;
        _settings.CornerOverlayLabelBackgroundTextureMaster = "";
        RaiseLabelStyleChanged();
        SaveAndRefreshOverlays();
    }

    // Applies (or clears, when value is null) one seat's style-flag override. isMaster picks the
    // seat's MASTER-pill override instead of its normal one. One flag per call so toggling e.g.
    // Bold never disturbs the seat's other (Italic/DropShadow/Outline) overrides.
    public void ApplySeatLabelBold(SlotAssignment seat, bool isMaster, bool? value)
    {
        if (isMaster) seat.LabelBoldMaster = value; else seat.LabelBold = value;
        SaveAndRefreshOverlays();
    }

    public void ApplySeatLabelItalic(SlotAssignment seat, bool isMaster, bool? value)
    {
        if (isMaster) seat.LabelItalicMaster = value; else seat.LabelItalic = value;
        SaveAndRefreshOverlays();
    }

    public void ApplySeatLabelDropShadow(SlotAssignment seat, bool isMaster, bool? value)
    {
        if (isMaster) seat.LabelDropShadowMaster = value; else seat.LabelDropShadow = value;
        SaveAndRefreshOverlays();
    }

    public void ApplySeatLabelOutline(SlotAssignment seat, bool isMaster, bool? value)
    {
        if (isMaster) seat.LabelOutlineMaster = value; else seat.LabelOutline = value;
        SaveAndRefreshOverlays();
    }

    public void ApplySeatLabelOpacity(SlotAssignment seat, bool isMaster, int? value)
    {
        var clamped = value.HasValue ? Math.Clamp(value.Value, 20, 100) : (int?)null;
        if (isMaster) seat.LabelOpacityMaster = clamped; else seat.LabelOpacity = clamped;
        SaveAndRefreshOverlays();
    }

    // Clears a seat's style overrides (normal or master tier) back to inherit.
    public void ResetSeatLabelStyle(SlotAssignment seat, bool isMaster)
    {
        if (isMaster)
        {
            seat.LabelBoldMaster = null;
            seat.LabelItalicMaster = null;
            seat.LabelDropShadowMaster = null;
            seat.LabelOutlineMaster = null;
            seat.LabelOpacityMaster = null;
        }
        else
        {
            seat.LabelBold = null;
            seat.LabelItalic = null;
            seat.LabelDropShadow = null;
            seat.LabelOutline = null;
            seat.LabelOpacity = null;
        }
        SaveAndRefreshOverlays();
    }

    // Current effective active-window frame colour for a seat (its own override, else the global) --
    // used to seed the per-seat colour picker.
    public string EffectiveSeatFrameColor(SlotAssignment seat)
        => string.IsNullOrWhiteSpace(seat.FrameColor) ? ActiveFrameColor : seat.FrameColor!;

    // Applies (or clears, when colorHex is null) a single seat's active-window frame colour override.
    public void ApplySeatFrameColor(SlotAssignment seat, string? colorHex)
    {
        seat.FrameColor = string.IsNullOrWhiteSpace(colorHex) ? null : colorHex;
        _lastFrameHandle = 0; // force the frame overlay to re-resolve this seat's colour on the next tick
        Save();
    }

    // Applies both halves of a pasted EVE theme string to a seat in one go: Primary -> frame colour,
    // Accent -> label colour (both the normal/alt pill and the Master pill, so the pasted theme
    // reads consistently regardless of which one is currently showing for this seat) (see
    // Utilities.EveThemeString, used by the "Paste Theme" seat button).
    public void ApplySeatTheme(SlotAssignment seat, string frameColorHex, string labelColorHex)
    {
        seat.FrameColor = frameColorHex;
        seat.LabelColor = labelColorHex;
        seat.LabelColorMaster = labelColorHex;
        _lastFrameHandle = 0;
        Save();
        if (PreviewModeActive && CornerOverlaysLive) StartCornerOverlays();
    }
}
