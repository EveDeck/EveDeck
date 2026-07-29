namespace EveDeck.Views;

// Geometry for the small "i" info affordance drawn in each corner-preview tile (added 2026-07-24).
//
// The button is a genuine cross-surface split: it must be DRAWN on LabelSurfaceWindow (that surface
// composites ABOVE the DWM thumbnails, so anything on the tile surface itself would be hidden behind
// the live preview) but HIT-TESTED on TileSurfaceWindow (LabelSurfaceWindow is input-transparent, so
// the click falls through to the tile surface). Both surfaces therefore have to agree on exactly the
// same rect -- so both derive it from this one helper rather than each hard-coding the corner maths.
internal static class OverlayInfoButton
{
    // Physical-pixel size + edge inset of the badge, drawn in the tile's top-right corner.
    public const int SizePx = 20;
    public const int InsetPx = 4;

    // The button's rect for a given tile rect. Pure geometry, coordinate-space-agnostic: the tile
    // surface passes its surface-relative tile rect, the label surface its own surface-relative rect,
    // and the flyout its absolute physical rect -- each gets the corner rect back in the same space.
    //
    // `scale` is AppSettings.CornerOverlayChromeScale, independent of Windows' own per-monitor DPI
    // scale -- it lets the badge grow on a small/high-density display without touching the desktop's
    // scaling. Every caller must pass the same value or the drawn badge and its hit-test rect diverge.
    public static System.Drawing.Rectangle RectFor(System.Drawing.Rectangle tile, double scale = 1.0)
    {
        var sizePx = (int)System.Math.Round(SizePx * scale);
        var insetPx = (int)System.Math.Round(InsetPx * scale);
        var size = System.Math.Min(sizePx, System.Math.Min(tile.Width, tile.Height));
        var x = tile.Right - insetPx - size;
        var y = tile.Top + insetPx;
        return new System.Drawing.Rectangle(x, y, size, size);
    }
}
