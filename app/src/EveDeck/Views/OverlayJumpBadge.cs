namespace EveDeck.Views;

// Geometry for the small fatigue / jump-reactivation-timer badges drawn in each corner-preview
// tile's top-left corner (added 2026-07-28) -- OverlayInfoButton owns the top-right, so this anchors
// the opposite corner to guarantee the two never overlap. Two badges sit side by side (slot 0 =
// fatigue, slot 1 = reactivation); each keeps its own fixed slot regardless of whether the other is
// currently shown, so a lone badge never visually "jumps" position when its sibling appears.
internal static class OverlayJumpBadge
{
    public const int SizePx = 18;
    public const int InsetPx = 4;
    public const int GapPx = 4;

    // `scale` is AppSettings.CornerOverlayChromeScale -- see OverlayInfoButton.RectFor for why it's
    // independent of Windows' own DPI scale. Every caller must pass the same value.
    public static System.Drawing.Rectangle RectFor(System.Drawing.Rectangle tile, int slot, double scale = 1.0)
    {
        var sizePx = (int)System.Math.Round(SizePx * scale);
        var insetPx = (int)System.Math.Round(InsetPx * scale);
        var gapPx = (int)System.Math.Round(GapPx * scale);
        var size = System.Math.Min(sizePx, System.Math.Min(tile.Width, tile.Height));
        var x = tile.Left + insetPx + slot * (size + gapPx);
        var y = tile.Top + insetPx;
        return new System.Drawing.Rectangle(x, y, size, size);
    }
}
