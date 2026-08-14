namespace EveDeck.Views;

// One shared type scale for the on-screen overlay chrome (added 2026-08-01).
//
// Before this existed every overlay window hand-picked its own corner radius, padding and badge
// size, so five surfaces that are meant to read as one system drifted apart: radii of 4, 6 and 8,
// four unrelated padding pairs, and two badge sizes (18px and 20px) sitting in opposite corners of
// the SAME tile at visibly different scales. These constants are the single source for those values.
//
// Scope note: this is chrome only. The character-name pill's font size, padding and corner radius are
// USER settings (AppSettings.CornerOverlayLabel*) and must keep reading from settings -- never route
// those through here.
//
// All values are DIP (device-independent pixels) except the *Px badge geometry, which is physical
// pixels: badges are drawn on the physical-pixel-pinned overlay surfaces and scaled by
// AppSettings.CornerOverlayChromeScale, not by Windows' DPI scale.
internal static class OverlayChrome
{
    // -- Corner radii ---------------------------------------------------------------------------
    // Sm is kept for completeness; the tightest surfaces (the hover tip) were unified up to Md so
    // every rectangular overlay card shares one silhouette.
    public const double RadiusSm = 4;
    public const double RadiusMd = 6;
    public const double RadiusLg = 8;

    // Fully round -- the circular badges. WPF clamps an oversized radius to half the smaller side.
    public const double RadiusPill = 999;

    // -- Padding tiers --------------------------------------------------------------------------
    // Tight: single-line transient tips. Snug: small multi-line info cards.
    // Card: standing readouts. CardUniform: the large toast card, which pads evenly on all four
    // sides (deliberately not folded into Card -- a 12x6 toast would crowd its avatar column).
    public const double PadTightH = 6;
    public const double PadTightV = 3;
    public const double PadSnugH = 10;
    public const double PadSnugV = 8;
    public const double PadCardH = 12;
    public const double PadCardV = 6;
    public const double PadCardUniform = 12;

    // -- Corner-tile badges ---------------------------------------------------------------------
    // One size for every tile badge (the "i" info affordance in the top-right and the jump-status
    // badges in the top-left), so the two corners of a tile match.
    public const int BadgeSizePx = 20;
    public const int BadgeInsetPx = 4;
    public const int BadgeGapPx = 4;
    public const double BadgeGlyphSize = 12;

    // Quadrant-aware placement for a card/tip anchored to a small badge, clamped to the badge's own
    // monitor work area. Used by InfoFlyoutWindow so a badge near a
    // screen edge can never push its popup off-screen.
    //
    // Quadrant-FIRST rather than a post-hoc overflow check against a measured width (which is fragile
    // around DPI/timing): a badge in a corner tile is very often already hugging a screen edge, so
    // this decides up-front to open away from that edge, then clamps as a last-resort safety net.
    // All coordinates are physical pixels.
    public static (int x, int y) EdgeAwarePosition(int badgeLeft, int badgeTop, int badgeRight, int badgeBottom,
                                                   int width, int height, int gap)
    {
        var wa = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(badgeLeft, badgeTop)).WorkingArea;
        var midX = wa.Left + wa.Width / 2;
        var midY = wa.Top + wa.Height / 2;

        var x = badgeRight > midX ? badgeRight - width : badgeLeft;
        var y = badgeTop > midY ? badgeTop - gap - height : badgeBottom + gap;

        if (x + width > wa.Right) x = wa.Right - width;
        if (x < wa.Left) x = wa.Left;
        if (y + height > wa.Bottom) y = wa.Bottom - height;
        if (y < wa.Top) y = wa.Top;

        return (x, y);
    }
}
