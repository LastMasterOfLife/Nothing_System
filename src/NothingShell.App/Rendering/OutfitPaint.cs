using System.Windows;
using System.Windows.Media;
using NothingShell.Core.Design;

namespace NothingShell.App.Rendering;

/// <summary>
/// I pennelli di un vestito del notch (docs/22-vestiti-notch.md): il fondo, pieno o sfumato, e il motivo ripetuto. Si usano sul
/// notch vero e nelle anteprime della pagina Aspetto, così quello che si sceglie è quello che si vede.
/// </summary>
public static class OutfitPaint
{
    /// <summary>Il fondo del vestito, con i colori già regolati sul testo di <paramref name="palette"/>.</summary>
    public static Brush Fill(NotchOutfit outfit, Palette palette)
    {
        ArgumentNullException.ThrowIfNull(outfit);
        ArgumentNullException.ThrowIfNull(palette);
        var colors = outfit.Resolve(palette.Ink, palette.Muted);
        Brush brush = outfit.Gradient switch
        {
            OutfitGradient.Horizontal => Gradient(colors, new Point(0, 0.5), new Point(1, 0.5)),
            OutfitGradient.Vertical => Gradient(colors, new Point(0.5, 0), new Point(0.5, 1)),
            OutfitGradient.Diagonal => Gradient(colors, new Point(0, 0), new Point(1, 1)),
            _ => new SolidColorBrush(Theme.ToColor(colors.Fill)),
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>Il motivo come pennello a piastrelle; null senza motivo.</summary>
    public static Brush? Pattern(NotchOutfit outfit, Palette palette)
    {
        ArgumentNullException.ThrowIfNull(outfit);
        ArgumentNullException.ThrowIfNull(palette);
        if (outfit.Pattern == OutfitPattern.None)
            return null;
        var color = new SolidColorBrush(Theme.ToColor(outfit.Resolve(palette.Ink).Pattern));
        color.Freeze();
        Brush brush = outfit.Pattern == OutfitPattern.Stripes ? Stripes(color.Color) : Tiled(outfit.Pattern, color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Il bordo scelto per il vestito; null tiene quello del tema.</summary>
    public static Brush? Rim(NotchOutfit outfit)
    {
        ArgumentNullException.ThrowIfNull(outfit);
        if (Argb.TryParse(outfit.Rim) is not { } rim)
            return null;
        var brush = new SolidColorBrush(Theme.ToColor(rim));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush Gradient(OutfitColors colors, Point start, Point end) =>
        new(new GradientStopCollection { new(Theme.ToColor(colors.Fill), 0), new(Theme.ToColor(colors.Fill2), 1) }, start, end);

    /// <summary>Righe diagonali: una sfumatura a scatti che si ripete, più leggera di una piastrella.</summary>
    private static LinearGradientBrush Stripes(Color color) => new(new GradientStopCollection
    {
        new(color, 0), new(color, 0.45), new(Colors.Transparent, 0.45), new(Colors.Transparent, 1),
    }, new Point(0, 0), new Point(6, 6))
    {
        MappingMode = BrushMappingMode.Absolute,
        SpreadMethod = GradientSpreadMethod.Repeat,
    };

    private static DrawingBrush Tiled(OutfitPattern pattern, Brush color)
    {
        var (size, geometry, stroke) = pattern switch
        {
            OutfitPattern.Dots => (12.0, Group(new EllipseGeometry(new Point(3, 3), 1.6, 1.6), new EllipseGeometry(new Point(9, 9), 1.6, 1.6)), false),
            OutfitPattern.Checks => (12.0, Group(new RectangleGeometry(new Rect(0, 0, 6, 6)), new RectangleGeometry(new Rect(6, 6, 6, 6))), false),
            OutfitPattern.Grid => (10.0, Group(new RectangleGeometry(new Rect(0, 0, 10, 1)), new RectangleGeometry(new Rect(0, 0, 1, 10))), false),
            OutfitPattern.Waves => (16.0, Geometry.Parse("M0,8 Q4,4 8,8 T16,8"), true),
            OutfitPattern.Stars => (24.0, Group(Star(6, 6, 3.4), Star(17, 15, 2.2), new EllipseGeometry(new Point(19, 4), 0.8, 0.8), new EllipseGeometry(new Point(5, 19), 0.7, 0.7)), false),
            _ => (18.0, Heart(9, 9, 4.2), false), // cuori
        };
        var drawing = new DrawingGroup();
        // un riquadro invisibile grande quanto la piastrella: i bordi del disegno non cambiano il passo del motivo
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, size, size))));
        drawing.Children.Add(stroke
            ? new GeometryDrawing(null, new Pen(color, 1.3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry)
            : new GeometryDrawing(color, null, geometry));
        var tile = new Rect(0, 0, size, size);
        return new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewbox = tile,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill,
        };
    }

    private static GeometryGroup Group(params Geometry[] parts)
    {
        var group = new GeometryGroup();
        foreach (var part in parts)
            group.Children.Add(part);
        return group;
    }

    /// <summary>Stellina a quattro punte, come un luccichio.</summary>
    private static PathGeometry Star(double x, double y, double r)
    {
        var inner = r * 0.28;
        var figure = new PathFigure { StartPoint = new Point(x, y - r), IsClosed = true };
        foreach (var point in new[]
                 {
                     new Point(x + inner, y - inner), new Point(x + r, y), new Point(x + inner, y + inner), new Point(x, y + r),
                     new Point(x - inner, y + inner), new Point(x - r, y), new Point(x - inner, y - inner),
                 })
            figure.Segments.Add(new LineSegment(point, false));
        return new PathGeometry([figure]);
    }

    private static PathGeometry Heart(double x, double y, double r)
    {
        var figure = new PathFigure { StartPoint = new Point(x, y + r), IsClosed = true };
        figure.Segments.Add(new BezierSegment(new Point(x - (r * 1.6), y), new Point(x - (r * 0.9), y - (r * 1.3)), new Point(x, y - (r * 0.45)), false));
        figure.Segments.Add(new BezierSegment(new Point(x + (r * 0.9), y - (r * 1.3)), new Point(x + (r * 1.6), y), new Point(x, y + r), false));
        return new PathGeometry([figure]);
    }
}
