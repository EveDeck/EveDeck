using System.Windows.Media;

namespace EveDeck.Utilities;

// Shared shape builders for the on-screen overlays. Kept in one place so the active-frame highlight
// (ActiveFrameOverlay) and the incoming-damage hit glow (LabelSurfaceWindow.AlertGlowElement) draw
// identical corner brackets.
public static class OverlayGeometry
{
    // Four L-shaped brackets marking the corners of the rect at (x, y, w, h). Each corner is one
    // open figure of two line segments; the returned geometry is frozen for cheap reuse as a
    // Path.Data / stroke source. `arm` is how far each bracket extends along both edges.
    public static Geometry CornerBrackets(double x, double y, double w, double h, double arm)
    {
        arm = System.Math.Min(arm, System.Math.Min(w, h) / 2.0);
        double r = x + w, b = y + h;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            // top-left
            g.BeginFigure(new System.Windows.Point(x, y + arm), false, false);
            g.LineTo(new System.Windows.Point(x, y), true, false);
            g.LineTo(new System.Windows.Point(x + arm, y), true, false);
            // top-right
            g.BeginFigure(new System.Windows.Point(r - arm, y), false, false);
            g.LineTo(new System.Windows.Point(r, y), true, false);
            g.LineTo(new System.Windows.Point(r, y + arm), true, false);
            // bottom-right
            g.BeginFigure(new System.Windows.Point(r, b - arm), false, false);
            g.LineTo(new System.Windows.Point(r, b), true, false);
            g.LineTo(new System.Windows.Point(r - arm, b), true, false);
            // bottom-left
            g.BeginFigure(new System.Windows.Point(x + arm, b), false, false);
            g.LineTo(new System.Windows.Point(x, b), true, false);
            g.LineTo(new System.Windows.Point(x, b - arm), true, false);
        }
        geo.Freeze();
        return geo;
    }

    // Rectangular perimeter (x, y, w, h) walked as 4 edges in ~stepPx segments, each interior vertex
    // pushed off the edge by amp*sin(t*7 + phase) along the edge normal -- a jagged "electric"
    // outline for the ElectricArc active-frame style. Frozen before returning.
    public static Geometry JaggedPerimeter(double x, double y, double w, double h, double amp, double phase, double stepPx = 9.0)
    {
        var corners = new[]
        {
            new System.Windows.Point(x, y),
            new System.Windows.Point(x + w, y),
            new System.Windows.Point(x + w, y + h),
            new System.Windows.Point(x, y + h),
        };
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (var i = 0; i < 4; i++)
            {
                var a = corners[i];
                var c = corners[(i + 1) % 4];
                var dx = c.X - a.X;
                var dy = c.Y - a.Y;
                var edgeLen = System.Math.Sqrt(dx * dx + dy * dy);
                if (edgeLen < 1e-6) continue;
                double ux = dx / edgeLen, uy = dy / edgeLen;
                double nx = -uy, ny = ux;
                g.BeginFigure(a, false, false);
                for (var pos = stepPx; pos < edgeLen; pos += stepPx)
                {
                    var off = amp * System.Math.Sin(pos / stepPx * 7.0 + phase);
                    g.LineTo(new System.Windows.Point(a.X + ux * pos + nx * off, a.Y + uy * pos + ny * off), true, false);
                }
                g.LineTo(c, true, false);
            }
        }
        geo.Freeze();
        return geo;
    }

    // Rectangular perimeter walked as a triangular zigzag wave, amplitude amp, wavelength ~waveLenPx,
    // alternating outward/inward along the edge normal. Static (no phase). Frozen before returning.
    public static Geometry ZigzagPerimeter(double x, double y, double w, double h, double amp, double waveLenPx = 14.0)
    {
        var corners = new[]
        {
            new System.Windows.Point(x, y),
            new System.Windows.Point(x + w, y),
            new System.Windows.Point(x + w, y + h),
            new System.Windows.Point(x, y + h),
        };
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (var i = 0; i < 4; i++)
            {
                var a = corners[i];
                var c = corners[(i + 1) % 4];
                var dx = c.X - a.X;
                var dy = c.Y - a.Y;
                var edgeLen = System.Math.Sqrt(dx * dx + dy * dy);
                if (edgeLen < 1e-6) continue;
                double ux = dx / edgeLen, uy = dy / edgeLen;
                double nx = -uy, ny = ux;
                g.BeginFigure(a, false, false);
                var step = waveLenPx / 2.0;
                for (var pos = step; pos < edgeLen; pos += step)
                {
                    var off = amp * (2.0 * System.Math.Abs(System.Math.Round(pos / waveLenPx) - pos / waveLenPx) - 0.5) * 2.0;
                    g.LineTo(new System.Windows.Point(a.X + ux * pos + nx * off, a.Y + uy * pos + ny * off), true, false);
                }
                g.LineTo(c, true, false);
            }
        }
        geo.Freeze();
        return geo;
    }

    // A plain full-perimeter rectangle outline, for the Solid/Dashed/Dotted active-frame styles --
    // the Snapshot style's corner brackets read as camera-viewfinder framing, these read as a
    // conventional border.
    public static Geometry FullRect(double x, double y, double w, double h)
    {
        var geo = new RectangleGeometry(new System.Windows.Rect(x, y, w, h));
        geo.Freeze();
        return geo;
    }
}
