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
    public static System.Drawing.Rectangle RectFor(System.Drawing.Rectangle tile)
    {
        var size = System.Math.Min(SizePx, System.Math.Min(tile.Width, tile.Height));
        var x = tile.Right - InsetPx - size;
        var y = tile.Top + InsetPx;
        return new System.Drawing.Rectangle(x, y, size, size);
    }
}
