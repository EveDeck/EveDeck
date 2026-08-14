namespace EveDeck.Views;

// Geometry for the small fatigue / jump-reactivation-timer badges drawn in each corner-preview
// tile's top-left corner (added 2026-07-28) -- OverlayInfoButton owns the top-right, so this anchors
// the opposite corner to guarantee the two never overlap. Two badges sit side by side (slot 0 =
// fatigue, slot 1 = reactivation); each keeps its own fixed slot regardless of whether the other is
// currently shown, so a lone badge never visually "jumps" position when its sibling appears.
internal static class OverlayJumpBadge
{
    // Shared overlay type scale -- these used to be 18px against the info button's 20px, so the two
    // top corners of the same tile carried visibly different badge sizes. SizePx remains the HEIGHT
    // (and so still matches the info button); WidthPx is the new horizontal extent.
    public const int SizePx = OverlayChrome.BadgeSizePx;
    public const int InsetPx = OverlayChrome.BadgeInsetPx;
    public const int GapPx = OverlayChrome.BadgeGapPx;

    // 2026-08-14: badges carry an inline countdown ("F 4:52") rather than a bare glyph, so they are
    // no longer square. The width is FIXED rather than measured from the current text, deliberately:
    // the text changes every second, and a width that tracked it would make both badges twitch and
    // slot 1 slide around as slot 0's digits changed. Sized for the widest string the formatter can
    // produce (see MainWindowViewModel.JumpStatus's FormatBadgeCountdown -- "F 9d23h" is the widest).
    public const int WidthPx = 58;

    // `scale` is AppSettings.CornerOverlayChromeScale -- see OverlayInfoButton.RectFor for why it's
    // independent of Windows' own DPI scale. Every caller must pass the same value.
    public static System.Drawing.Rectangle RectFor(System.Drawing.Rectangle tile, int slot, double scale = 1.0)
    {
        var heightPx = (int)System.Math.Round(SizePx * scale);
        var insetPx = (int)System.Math.Round(InsetPx * scale);
        var gapPx = (int)System.Math.Round(GapPx * scale);

        var height = System.Math.Min(heightPx, tile.Height);
        // Both badges plus the gap must fit inside the tile's width, or a narrow tile would push
        // slot 1 off the right-hand edge (and over the info button). Shrink both equally instead.
        var available = System.Math.Max(0, tile.Width - 2 * insetPx - gapPx);
        var width = System.Math.Min((int)System.Math.Round(WidthPx * scale), available / 2);

        var x = tile.Left + insetPx + slot * (width + gapPx);
        var y = tile.Top + insetPx;
        return new System.Drawing.Rectangle(x, y, width, height);
    }
}
