using System.Globalization;
using System.Windows;
using System.Windows.Media;
using NothingShell.Core.Design;
using NothingShell.Core.DotMatrix;

namespace NothingShell.App.Rendering;

/// <summary>
/// Disegna una <see cref="DotGrid"/>: come cerchi su passo fisso (docs/02-design-system.md) o, nello stile Windows, come
/// testo Segoe, icona Fluent o barra piena, quando la griglia dice cosa rappresenta. Occupa quasi lo stesso spazio nei due
/// stili, così le pagine non cambiano impaginazione.
/// </summary>
public sealed class DotMatrixView : FrameworkElement
{
    private const int IntensityLevels = 16;
    private const double RadiusRatio = 0.38;
    private const double TextRatio = 5.4; // una riga di Segoe a questa misura è alta quanto 7 punti

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
    private Palette? _brushesPalette;
    private Palette _colorPalette = Theme.Current;
    private Palette _offPalette = Theme.Current;

    public DotMatrixView()
    {
        WeakEventManager<ThemeSource, EventArgs>.AddHandler(ThemeSource.Instance, nameof(ThemeSource.Changed), OnThemeChanged);
    }

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

    /// <summary>Misura del testo nello stile Windows; se manca si ricava dal passo.</summary>
    public double? TextSize { get; set; }

    /// <summary>Lo stesso colore nel tema corrente, anche se è stato scelto col tema di prima.</summary>
    private Color EffectiveColor => ReadLocalValue(DotColorProperty) == DependencyProperty.UnsetValue
        ? Theme.Ink
        : Theme.Follow(DotColor, _colorPalette);

    private Color? EffectiveOff => OffColor is { } off ? Theme.Follow(off, _offPalette) : null;

    protected override Size MeasureOverride(Size availableSize)
    {
        var dots = Dots;
        if (Theme.IsWindowsStyle)
        {
            if (dots.Text is { } text)
            {
                var written = Written(text, dots, availableSize.Width);
                return new Size(Math.Min(written.WidthIncludingTrailingWhitespace, availableSize.Width), written.Height);
            }
            if (dots.Glyph is { } glyph && FluentIcons.For(glyph) is not null)
            {
                var side = Math.Max(glyph.Width, glyph.Height) * Pitch;
                return new Size(side, side);
            }
        }
        return new Size(dots.Width * Pitch, dots.Height * Pitch);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dots = Dots;
        if (Theme.IsWindowsStyle)
        {
            if (dots.Text is { } text)
            {
                var written = Written(text, dots, RenderSize.Width);
                written.SetForegroundBrush(new SolidColorBrush(EffectiveColor));
                drawingContext.DrawText(written, new Point(0, 0));
                return;
            }
            if (dots.Glyph is { } glyph && FluentIcons.For(glyph) is { } icon)
            {
                DrawIcon(drawingContext, icon);
                return;
            }
            if (dots.Fraction is { } fraction)
            {
                DrawBar(drawingContext, fraction);
                return;
            }
        }
        DrawDots(drawingContext, dots);
    }

    private FormattedText Written(string text, DotGrid dots, double maxWidth)
    {
        var size = TextSize ?? Pitch * TextRatio;
        var written = new FormattedText(
            Sentence(text),
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(size >= 20 ? Theme.DisplayFont : Theme.BodyFont, FontStyles.Normal, size >= 20 ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size,
            Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        // Quello che a punti scorrerebbe qui finisce coi puntini: si legge per intero aprendo la pagina.
        var limit = Math.Min(maxWidth, dots.Width * Pitch * 1.4);
        if (!double.IsInfinity(limit) && limit > 0)
        {
            written.MaxTextWidth = limit;
            written.MaxLineCount = 1;
            written.Trimming = TextTrimming.CharacterEllipsis;
        }
        return written;
    }

    /// <summary>Solo le parole si scrivono con l'iniziale maiuscola: "12 min" o un host restano come sono.</summary>
    private static string Sentence(string text) => text.Length > 1 && text.Any(char.IsWhiteSpace) || text.All(char.IsLetter) ? DotText.Sentence(text) : DotText.Friendly(text);

    private void DrawIcon(DrawingContext drawingContext, char icon)
    {
        var size = RenderSize.Height * 0.92;
        var written = new FormattedText(
            icon.ToString(),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(Theme.IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            Math.Max(size, 1),
            new SolidColorBrush(EffectiveColor),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawingContext.DrawText(written, new Point((RenderSize.Width - written.Width) / 2, (RenderSize.Height - written.Height) / 2));
    }

    /// <summary>Barra di avanzamento di Windows: traccia sottile, parte piena nel colore d'accento (o in quello d'allarme).</summary>
    private void DrawBar(DrawingContext drawingContext, double fraction)
    {
        var height = Math.Min(RenderSize.Height, RenderSize.Height >= 12 ? 6 : 4);
        var radius = height / 2;
        var top = (RenderSize.Height - height) / 2;
        var track = new Rect(0, top, RenderSize.Width, height);
        drawingContext.DrawRoundedRectangle(new SolidColorBrush(Theme.DotOff), null, track, radius, radius);
        if (fraction <= 0)
            return;
        var color = EffectiveColor == Theme.Ink ? Theme.Accent : EffectiveColor;
        var fill = new Rect(0, top, Math.Max(height, RenderSize.Width * fraction), height);
        drawingContext.DrawRoundedRectangle(new SolidColorBrush(color), null, fill, radius, radius);
    }

    private void DrawDots(DrawingContext drawingContext, DotGrid dots)
    {
        var pitch = Pitch;
        var radius = pitch * RadiusRatio;
        if (!ReferenceEquals(_brushesPalette, Theme.Current))
        {
            _brushesPalette = Theme.Current;
            _onBrushes = null;
            _offBrush = null;
        }
        var onBrushes = _onBrushes ??= BuildOnBrushes(EffectiveColor);
        var offBrush = _offBrush ??= EffectiveOff is { } off ? Freeze(new SolidColorBrush(off)) : null;

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

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    private static void OnColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (DotMatrixView)d;
        view._onBrushes = null;
        view._offBrush = null;
        if (e.Property == DotColorProperty)
            view._colorPalette = Theme.Current;
        else
            view._offPalette = Theme.Current;
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

    /// <summary>Punto d'aggancio per l'evento debole: le viste a punti sono tante e vanno e vengono con le pagine.</summary>
    private sealed class ThemeSource
    {
        public static readonly ThemeSource Instance = new();

        private ThemeSource()
        {
            Theme.Changed += (_, e) => Changed?.Invoke(this, e);
        }

        public event EventHandler<EventArgs>? Changed;
    }
}
