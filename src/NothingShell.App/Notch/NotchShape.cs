using System.Windows;
using System.Windows.Media;

namespace NothingShell.App.Notch;

/// <summary>Le forme del notch come geometrie WPF (docs/20-stile-nusa.md).</summary>
internal static class NotchShape
{
    /// <summary>
    /// Notch unito al bordo alto: corpo largo <paramref name="width"/> con gli angoli in basso di raggio <paramref name="radius"/>,
    /// e in alto due orecchie concave di raggio <paramref name="ear"/> che lo fondono col bordo. La figura è larga
    /// <paramref name="width"/> + 2 × <paramref name="ear"/> e parte in (<paramref name="x"/>, <paramref name="y"/>). Il lato alto
    /// non è tracciato: il bordo luminoso c'è solo dove la forma incontra lo schermo.
    /// </summary>
    public static Geometry Attached(double width, double height, double ear, double radius, double x = 0, double y = 0)
    {
        radius = Math.Max(0, Math.Min(radius, Math.Min(width / 2, height - ear)));
        var left = x + ear;
        var right = x + ear + width;
        var bottom = y + height;
        var figure = new PathFigure { StartPoint = new Point(x, y), IsClosed = false, IsFilled = true };
        figure.Segments.Add(new ArcSegment(new Point(left, y + ear), new Size(ear, ear), 0, false, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(new Point(left, bottom - radius), true));
        figure.Segments.Add(new ArcSegment(new Point(left + radius, bottom), new Size(radius, radius), 0, false, SweepDirection.Counterclockwise, true));
        figure.Segments.Add(new LineSegment(new Point(right - radius, bottom), true));
        figure.Segments.Add(new ArcSegment(new Point(right, bottom - radius), new Size(radius, radius), 0, false, SweepDirection.Counterclockwise, true));
        figure.Segments.Add(new LineSegment(new Point(right, y + ear), true));
        figure.Segments.Add(new ArcSegment(new Point(right + ear, y), new Size(ear, ear), 0, false, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Pillola staccata dal bordo: rettangolo con gli angoli tondi.</summary>
    public static Geometry Floating(double width, double height, double radius, double x = 0, double y = 0)
    {
        var geometry = new RectangleGeometry(new Rect(x, y, width, height), radius, radius);
        geometry.Freeze();
        return geometry;
    }
}
