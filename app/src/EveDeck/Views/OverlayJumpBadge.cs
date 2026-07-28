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

    public static System.Drawing.Rectangle RectFor(System.Drawing.Rectangle tile, int slot)
    {
        var size = System.Math.Min(SizePx, System.Math.Min(tile.Width, tile.Height));
        var x = tile.Left + InsetPx + slot * (size + GapPx);
        var y = tile.Top + InsetPx;
        return new System.Drawing.Rectangle(x, y, size, size);
    }
}
