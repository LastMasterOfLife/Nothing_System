using System.Windows;
using System.Windows.Media;
using NothingShell.Core.DotMatrix;

namespace NothingShell.App.Rendering;

/// <summary>Disegna una <see cref="DotGrid"/> come cerchi su passo fisso (docs/02-design-system.md).</summary>
public sealed class DotMatrixView : FrameworkElement
{
    private const int IntensityLevels = 16;
    private const double RadiusRatio = 0.38;

    public static readonly DependencyProperty DotsProperty = DependencyProperty.Register(
        nameof(Dots), typeof(DotGrid), typeof(DotMatrixView),
        new FrameworkPropertyMetadata(DotGrid.Empty,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PitchProperty = DependencyProperty.Register(
        nameof(Pitch), typeof(double), typeof(DotMatrixView),
        new FrameworkPropertyMetadata(3.0,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DotColorProperty = DependencyProperty.Register(
        nameof(DotColor), typeof(Color), typeof(DotMatrixView),
        new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.AffectsRender, OnColorChanged));

    public static readonly DependencyProperty OffColorProperty = DependencyProperty.Register(
        nameof(OffColor), typeof(Color?), typeof(DotMatrixView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnColorChanged));

    private Brush[]? _onBrushes;
    private Brush? _offBrush;

    public DotGrid Dots
    {
        get => (DotGrid)GetValue(DotsProperty);
        set => SetValue(DotsProperty, value);
    }

    public double Pitch
    {
        get => (double)GetValue(PitchProperty);
        set => SetValue(PitchProperty, value);
    }

    public Color DotColor
    {
        get => (Color)GetValue(DotColorProperty);
        set => SetValue(DotColorProperty, value);
    }

    /// <summary>Colore dei punti spenti; null = non disegnati (es. nel notch).</summary>
    public Color? OffColor
    {
        get => (Color?)GetValue(OffColorProperty);
        set => SetValue(OffColorProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Dots.Width * Pitch, Dots.Height * Pitch);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dots = Dots;
        var pitch = Pitch;
        var radius = pitch * RadiusRatio;
        var onBrushes = _onBrushes ??= BuildOnBrushes(DotColor);
        var offBrush = _offBrush ??= OffColor is { } off ? Freeze(new SolidColorBrush(off)) : null;

        for (var y = 0; y < dots.Height; y++)
            for (var x = 0; x < dots.Width; x++)
            {
                var v = dots[x, y];
                var brush = v > 0f
                    ? onBrushes[Math.Clamp((int)Math.Ceiling(v * IntensityLevels), 1, IntensityLevels) - 1]
                    : offBrush;
                if (brush is not null)
                    drawingContext.DrawEllipse(brush, null, new Point((x + 0.5) * pitch, (y + 0.5) * pitch), radius, radius);
            }
    }

    private static void OnColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (DotMatrixView)d;
        view._onBrushes = null;
        view._offBrush = null;
    }

    private static Brush[] BuildOnBrushes(Color color)
    {
        var brushes = new Brush[IntensityLevels];
        for (var i = 0; i < IntensityLevels; i++)
            brushes[i] = Freeze(new SolidColorBrush(color) { Opacity = (i + 1) / (double)IntensityLevels });
        return brushes;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
