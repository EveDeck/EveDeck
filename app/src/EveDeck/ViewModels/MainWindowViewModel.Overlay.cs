using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using EveDeck.Models;
using EveDeck.Views;

namespace EveDeck.ViewModels;

public sealed partial class MainWindowViewModel
{
    // ── Frame overlay lifecycle ────────────────────────────────────────────────

    private void StartFrameOverlay()
    {
        _frameOverlay ??= new ActiveFrameOverlay();
        _frameTimer.Start();
    }

    private void StopFrameOverlay()
    {
        // The frame timer is shared with corner-overlay maintenance; only stop it when
        // corner overlays aren't relying on it.
        if (!CornerOverlaysLive) _frameTimer.Stop();
        _frameOverlay?.Hide();
    }

    // 1b — Skip repositioning when handle and rect are unchanged.
    // 3a — Use per-slot color for the active window.
    private void OnFrameTick(object? sender, EventArgs e)
    {
        // Corner-overlay upkeep runs independently of the active-frame feature.
        MaintainCornerOverlays();

        if (!ActiveFrameEnabled || _frameOverlay is null)
        {
            _frameOverlay?.Hide();
            return;
        }

        var fgHandle = _windowService.GetForegroundWindowHandle();
        if (fgHandle == 0 || Windows.All(w => w.Handle != fgHandle))
        {
            if (_frameOverlay.IsVisible) _frameOverlay.Hide();
            if (_lastFrameHandle != 0)
            {
                _lastFrameHandle = 0;
                _lastFrameRect = null;
            }
            return;
        }

        if (!_windowService.TryGetWindowRect(fgHandle, out var rect))
        {
            if (_frameOverlay.IsVisible) _frameOverlay.Hide();
            return;
        }

        var handleChanged = _lastFrameHandle != fgHandle;
        var rectChanged = _lastFrameRect is null
            || _lastFrameRect.X != rect.X || _lastFrameRect.Y != rect.Y
            || _lastFrameRect.Width != rect.Width || _lastFrameRect.Height != rect.Height;
        var styleChanged = _lastFrameStyle != _settings.ActiveFrameStyle
            || _lastFrameGlowEnabled != _settings.ActiveFrameGlowEnabled;

        if (handleChanged)
        {
            _lastFrameHandle = fgHandle;
        }

        if (handleChanged || rectChanged || styleChanged)
        {
            var brush = GetFrameBrushForWindow(fgHandle);
            if (!_frameOverlay.IsVisible) _frameOverlay.Show();
            _frameOverlay.ApplyFrame(rect.X, rect.Y, rect.Width, rect.Height, ActiveFrameThickness, ActiveFrameGlowRadius, ActiveFrameGlowEnabled, brush, ActiveFrameStyle);
            _lastFrameRect = rect;
            _lastFrameStyle = _settings.ActiveFrameStyle;
            _lastFrameGlowEnabled = _settings.ActiveFrameGlowEnabled;
        }
        else
        {
            // Rect/handle/style unchanged, so ApplyFrame (which re-asserts topmost) didn't run this
            // tick. Pinned clients / corner tiles may have been raised above the frame since the last
            // apply, so re-show if needed and re-raise it so it doesn't sink behind them -- kills the
            // flicker.
            if (!_frameOverlay.IsVisible) _frameOverlay.Show();
            _frameOverlay.BringToTop();
        }

        // The frame re-asserts itself HWND_TOPMOST on every tick (above), and the topmost band is
        // ordered by whoever asserted LAST -- so without a matching re-assert here the frame wins the
        // race continuously and sits above the preview surfaces. That is invisible in layouts whose
        // tiles sit outside the master rect, but in a Center-Master-style profile (tiles placed INSIDE
        // the master cell, which is a supported and deliberate setup) the frame covers the previews.
        //
        // This used to be gated on IsZoomed -- a hover-zoomed preview was the only case anyone had
        // noticed losing the race (2026-08-08). The gate was too narrow: the same race runs every tick
        // regardless of zoom. ReassertOwnOverlaySurfaces is the cheap variant (three SetWindowPos
        // calls, no EnumWindows), which is exactly why it is safe to run per tick -- see its comment.
        ReassertOwnOverlaySurfaces();
    }

    // 3a — Resolve frame color: per-slot if set, otherwise global.
    private Brush GetFrameBrushForWindow(nint handle)
    {
        var window = Windows.FirstOrDefault(w => w.Handle == handle);
        if (window is null) return _frameBrush;

        foreach (var assignment in Assignments)
        {
            if (!string.IsNullOrWhiteSpace(assignment.FrameColor)
                && assignment.AssignedWindows.Any(e => e.Title.Equals(window.Title, StringComparison.OrdinalIgnoreCase)))
            {
                return ParseFrameBrush(assignment.FrameColor);
            }
        }

        return _frameBrush;
    }

    // ── Active frame + inactive preview border settings ─────────────────────────

    public bool ActiveFrameEnabled
    {
        get => _settings.ActiveFrameEnabled;
        set
        {
            if (_settings.ActiveFrameEnabled == value) return;
            _settings.ActiveFrameEnabled = value;
            OnPropertyChanged();
            if (value) StartFrameOverlay(); else StopFrameOverlay();
            Save();
        }
    }

    public int ActiveFrameThickness
    {
        get => _settings.ActiveFrameThickness;
        set
        {
            var clamped = Math.Clamp(value, 1, 20);
            if (_settings.ActiveFrameThickness == clamped) return;
            _settings.ActiveFrameThickness = clamped;
            OnPropertyChanged();
            Save();
        }
    }

    public int ActiveFrameGlowRadius
    {
        get => _settings.ActiveFrameGlowRadius;
        set
        {
            var clamped = Math.Clamp(value, 1, 40);
            if (_settings.ActiveFrameGlowRadius == clamped) return;
            _settings.ActiveFrameGlowRadius = clamped;
            OnPropertyChanged();
            Save();
        }
    }

    public string ActiveFrameStyle
    {
        get => _settings.ActiveFrameStyle;
        set
        {
            if (_settings.ActiveFrameStyle == value) return;
            _settings.ActiveFrameStyle = value;
            OnPropertyChanged();
            Save();
        }
    }

    public bool ActiveFrameGlowEnabled
    {
        get => _settings.ActiveFrameGlowEnabled;
        set
        {
            if (_settings.ActiveFrameGlowEnabled == value) return;
            _settings.ActiveFrameGlowEnabled = value;
            OnPropertyChanged();
            Save();
        }
    }

    public string ActiveFrameColor
    {
        get => _settings.ActiveFrameColor;
        set
        {
            if (_settings.ActiveFrameColor == value) return;
            _settings.ActiveFrameColor = value;
            _frameBrush = ParseFrameBrush(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveFrameBrush));
            Save();
        }
    }

    // Swatch preview for the Options-tab color picker button.
    public Brush ActiveFrameBrush => ParseFrameBrush(ActiveFrameColor);

    // Inactive-client preview border -- a plain outline around every corner tile whose client
    // is not in focus. Drawn by LabelSurfaceWindow; MaintainCornerOverlays reads these live.
    public bool InactivePreviewBorderEnabled
    {
        get => _settings.InactivePreviewBorderEnabled;
        set
        {
            if (_settings.InactivePreviewBorderEnabled == value) return;
            _settings.InactivePreviewBorderEnabled = value;
            OnPropertyChanged();
            Save();
        }
    }

    public string InactivePreviewBorderColor
    {
        get => _settings.InactivePreviewBorderColor;
        set
        {
            var v = value ?? "";
            if (_settings.InactivePreviewBorderColor == v) return;
            _settings.InactivePreviewBorderColor = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(InactivePreviewBorderBrush));
            Save();
        }
    }

    public int InactivePreviewBorderThickness
    {
        get => _settings.InactivePreviewBorderThickness;
        set
        {
            var clamped = Math.Clamp(value, 1, 12);
            if (_settings.InactivePreviewBorderThickness == clamped) return;
            _settings.InactivePreviewBorderThickness = clamped;
            OnPropertyChanged();
            Save();
        }
    }

    // Swatch preview for the Previews-tab color picker button.
    public Brush InactivePreviewBorderBrush => ParseFrameBrush(InactivePreviewBorderColor);
}
