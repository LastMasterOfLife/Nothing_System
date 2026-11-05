using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NothingShell.App.Rendering;
using NothingShell.Core.Design;

namespace NothingShell.App.Settings;

/// <summary>
/// Selettore colore come quello di Figma: nel quadrato si sceglie saturazione (verso destra) e luminosità (verso l'alto), sulla
/// banda sotto la tinta. Mentre si trascina avvisa con <see cref="Changed"/>, al rilascio con <see cref="Committed"/>; il codice
/// esadecimale resta per chi lo conosce già, ma non serve scriverlo.
/// </summary>
internal sealed class ColorPicker : StackPanel
{
    private const double FieldWidth = 260;
    private const double FieldHeight = 150;
    private const double BandHeight = 12;
    private const double ThumbSize = 16;
    private const double Corner = 6;

    private readonly SolidColorBrush _hueBrush = new();
    private readonly SolidColorBrush _colorBrush = new();
    private readonly SolidColorBrush _pureHueBrush = new();
    private readonly TranslateTransform _fieldThumb = new();
    private readonly TranslateTransform _bandThumb = new();
    private readonly TextBox _hex;
    private Hsv _hsv;

    /// <param name="hue">La tinta da tenere se <paramref name="color"/> è un grigio, che di tinta non ne ha.</param>
    public ColorPicker(Argb color, double? hue = null)
    {
        _hsv = Hsv.From(color);
        if (_hsv.S == 0 && hue is { } kept)
            _hsv = _hsv with { H = kept };
        Margin = new Thickness(0, 10, 0, 4);
        HorizontalAlignment = HorizontalAlignment.Left;
        Children.Add(Field());
        Children.Add(Band());

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(Theme.RadiusSmall),
            Background = _colorBrush,
            BorderBrush = Theme.OutlineBrush,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 0),
        });
        _hex = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Theme.InkBrush,
            CaretBrush = Theme.InkBrush,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13,
            Width = 76,
            MaxLength = 7,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Codice del colore, se lo conosci già: Invio per usarlo",
        };
        var frame = new Border
        {
            Background = Theme.SurfaceBrush,
            BorderBrush = Theme.OutlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Theme.RadiusSmall),
            Padding = new Thickness(8, 3, 8, 3),
            Child = _hex,
        };
        _hex.GotKeyboardFocus += (_, _) => frame.BorderBrush = Theme.AccentBrush;
        _hex.LostKeyboardFocus += (_, _) =>
        {
            frame.BorderBrush = Theme.OutlineBrush;
            CommitHex();
        };
        _hex.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            CommitHex();
        };
        row.Children.Add(frame);
        Children.Add(row);
        Render();
    }

    /// <summary>Un trascinamento comincia: chi ridisegna la pagina aspetti il rilascio.</summary>
    public event Action? DragStarted;

    /// <summary>Il colore mentre si trascina.</summary>
    public event Action<Argb>? Changed;

    /// <summary>Il colore scelto: mouse rilasciato o codice confermato.</summary>
    public event Action<Argb>? Committed;

    /// <summary>La tinta della banda: si ricorda anche quando il colore è un grigio.</summary>
    public double Hue => _hsv.H;

    public Argb Value => _hsv.ToArgb();

    private Canvas Field()
    {
        var area = new Canvas { Width = FieldWidth, Height = FieldHeight, Background = Brushes.Transparent, Cursor = Cursors.Cross };
        area.Children.Add(new Rectangle { Width = FieldWidth, Height = FieldHeight, RadiusX = Corner, RadiusY = Corner, Fill = _hueBrush });
        area.Children.Add(new Rectangle
        {
            Width = FieldWidth,
            Height = FieldHeight,
            RadiusX = Corner,
            RadiusY = Corner,
            Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0),
        });
        area.Children.Add(new Rectangle
        {
            Width = FieldWidth,
            Height = FieldHeight,
            RadiusX = Corner,
            RadiusY = Corner,
            Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90),
        });
        area.Children.Add(Thumb(_colorBrush, _fieldThumb));
        Drag(area, p => _hsv = _hsv with { S = Math.Clamp(p.X / FieldWidth, 0, 1), V = 1 - Math.Clamp(p.Y / FieldHeight, 0, 1) });
        return area;
    }

    private Canvas Band()
    {
        var stops = new GradientStopCollection();
        for (var i = 0; i <= 6; i++)
            stops.Add(new GradientStop(Theme.ToColor(new Hsv(i * 60, 1, 1).ToArgb()), i / 6.0));
        var band = new Canvas
        {
            Width = FieldWidth,
            Height = BandHeight,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 14, 0, 0),
            ToolTip = "Tinta",
        };
        band.Children.Add(new Rectangle
        {
            Width = FieldWidth,
            Height = BandHeight,
            RadiusX = BandHeight / 2,
            RadiusY = BandHeight / 2,
            Fill = new LinearGradientBrush(stops, 0),
        });
        band.Children.Add(Thumb(_pureHueBrush, _bandThumb));
        Drag(band, p => _hsv = _hsv with { H = Math.Clamp(p.X / FieldWidth, 0, 1) * 359.99 });
        return band;
    }

    /// <summary>Cerchietto bianco col colore dentro, come in Figma; il centro sta sul punto scelto.</summary>
    private static Grid Thumb(Brush fill, TranslateTransform position)
    {
        var thumb = new Grid { Width = ThumbSize, Height = ThumbSize, RenderTransform = position, IsHitTestVisible = false };
        thumb.Children.Add(new Ellipse { Fill = fill, Stroke = Brushes.White, StrokeThickness = 2.5 });
        thumb.Children.Add(new Ellipse { Stroke = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), StrokeThickness = 1 });
        return thumb;
    }

    /// <summary>Clic e trascinamento su <paramref name="area"/>: <paramref name="pick"/> legge il punto, poi si ridisegna e si avvisa.</summary>
    private void Drag(Canvas area, Action<Point> pick)
    {
        area.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            DragStarted?.Invoke();
            area.CaptureMouse();
            pick(e.GetPosition(area));
            Render();
            Changed?.Invoke(Value);
        };
        area.MouseMove += (_, e) =>
        {
            if (!area.IsMouseCaptured)
                return;
            pick(e.GetPosition(area));
            Render();
            Changed?.Invoke(Value);
        };
        area.MouseLeftButtonUp += (_, e) =>
        {
            if (!area.IsMouseCaptured)
                return;
            e.Handled = true;
            area.ReleaseMouseCapture();
            Committed?.Invoke(Value);
        };
    }

    private void CommitHex()
    {
        if (Argb.TryParse(_hex.Text) is not { } typed)
        {
            _hex.Text = Value.ToHex(); // un codice sbagliato torna quello di prima
            return;
        }
        if (typed == Value)
            return;
        var hsv = Hsv.From(typed);
        _hsv = hsv.S == 0 ? hsv with { H = _hsv.H } : hsv;
        Render();
        Committed?.Invoke(Value);
    }

    private void Render()
    {
        _hueBrush.Color = Theme.ToColor(new Hsv(_hsv.H, 1, 1).ToArgb());
        _pureHueBrush.Color = _hueBrush.Color;
        _colorBrush.Color = Theme.ToColor(Value);
        _fieldThumb.X = (_hsv.S * FieldWidth) - (ThumbSize / 2);
        _fieldThumb.Y = ((1 - _hsv.V) * FieldHeight) - (ThumbSize / 2);
        _bandThumb.X = (_hsv.H / 360 * FieldWidth) - (ThumbSize / 2);
        _bandThumb.Y = (BandHeight - ThumbSize) / 2;
        if (!_hex.IsKeyboardFocused)
            _hex.Text = Value.ToHex();
    }
}
