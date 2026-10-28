using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using NothingShell.App.Interop;
using NothingShell.App.Rendering;
using NothingShell.Core.Bar;
using NothingShell.Core.Design;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Notch;
using NothingShell.Core.Widgets;

namespace NothingShell.App.Bar;

/// <summary>
/// Anteprime dal vivo delle finestre di un'app, al passaggio del mouse sulla sua icona nella barra
/// (docs/05-bar.md). Clic su un'anteprima: attiva la finestra; X: la chiude.
/// </summary>
public sealed class PreviewWindow : Window
{
    private const double PanelPadding = 8;
    private const double CardPadding = 8;
    private const double Gap = 4;
    private const double TitleHeight = 28;
    private const double BarGap = 8;
    private const double ScreenMargin = 8;
    private const uint OutlineColorRef = 0x00262626; // Outline del design system, come COLORREF (0x00BBGGRR)

    private readonly StackPanel _cards = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(PanelPadding) };
    private readonly List<(nint Thumbnail, FrameworkElement Area)> _thumbnails = [];
    private nint _hwnd;
    private double _scale = 1;

    public PreviewWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Theme.BlackBrush;
        Title = "Nothing Shell — Anteprime";
        Content = _cards;
        SourceInitialized += (_, _) =>
        {
            // Scatta dentro EnsureHandle, prima che _hwnd sia assegnato
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.MakeNonActivatingToolWindow(hwnd);
            DwmNative.RoundCorners(hwnd, OutlineColorRef);
        };
    }

    /// <summary>Chiave dell'app mostrata, null se chiusa.</summary>
    public string? AppKey { get; private set; }

    public event Action<nint>? ActivateRequested;

    public event Action<nint>? CloseRequested;

    /// <summary>
    /// Mostra (o aggiorna) le anteprime delle finestre di <paramref name="group"/> sopra la barra di
    /// <paramref name="monitor"/>, centrate su <paramref name="anchorX"/> (pixel fisici).
    /// </summary>
    public void Open(AppGroup group, ImageSource? icon, nint foreground, double anchorX, DisplayMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(monitor);
        if (group.Windows.Count == 0)
        {
            Dismiss();
            return;
        }

        AppKey = group.AppKey;
        _scale = monitor.Scale;
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        ReleaseThumbnails();

        // Le miniature si registrano subito: servono le proporzioni delle finestre per impaginarle.
        var thumbnails = group.Windows
            .Select(w => WindowNative.IsIconic(w.Handle) ? 0 : DwmNative.Register(_hwnd, w.Handle))
            .ToArray();
        var sources = thumbnails.Select(t => DwmNative.SourceSize(t) is var (w, h) && w > 0 ? new Size2(w, h) : new Size2(16, 10)).ToArray();
        var available = monitor.Bounds.Width / _scale - 2 * (ScreenMargin + PanelPadding) - group.Windows.Count * 2 * CardPadding;
        var sizes = WindowPreviews.Fit(sources, available, Gap + 2 * CardPadding);

        _cards.Children.Clear();
        for (var i = 0; i < group.Windows.Count; i++)
            _cards.Children.Add(Card(group.Windows[i], thumbnails[i], sizes[i], icon, group.Windows[i].Handle == foreground));

        _cards.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _cards.DesiredSize;
        var (width, height) = (Math.Round(size.Width * _scale), Math.Round(size.Height * _scale));
        var left = Math.Clamp(anchorX - width / 2, monitor.Bounds.X + ScreenMargin * _scale, monitor.Bounds.Right - width - ScreenMargin * _scale);
        var top = MultiMonitorBar.Place(monitor).Y - height - BarGap * _scale;
        var bounds = new Rect2(Math.Round(left), Math.Round(top), width, height);
        Width = size.Width;
        Height = size.Height;

        WindowNative.Place(_hwnd, bounds);
        if (!IsVisible)
            Show();
        WindowNative.Place(_hwnd, bounds); // WPF può riapplicare la sua posizione alla prima apparizione
        UpdateLayout();
        foreach (var (thumbnail, area) in _thumbnails)
            DwmNative.Show(thumbnail, AreaInPixels(area));
    }

    public void Dismiss()
    {
        AppKey = null;
        ReleaseThumbnails();
        if (IsVisible)
            Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        ReleaseThumbnails();
        base.OnClosed(e);
    }

    private Border Card(AppWindow window, nint thumbnail, Size2 size, ImageSource? icon, bool isForeground)
    {
        var header = new Grid { Height = TitleHeight, Width = size.Width };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Punto rosso: la finestra in primo piano, come l'indicatore sotto le icone della barra.
        var dot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = isForeground ? Theme.RedBrush : Theme.GreyBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 8, 0),
        };
        var title = new TextBlock
        {
            Text = window.Title,
            FontFamily = Theme.BodyFont,
            FontSize = 12,
            Foreground = Theme.WhiteBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var close = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = Brushes.Transparent,
            ToolTip = "Chiudi finestra",
            Child = new DotMatrixView
            {
                Pitch = 1.5,
                Dots = DotGrid.From(DotIcons.Close),
                DotColor = Theme.Grey,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        close.MouseEnter += (_, _) => close.Background = Theme.RedBrush;
        close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            CloseRequested?.Invoke(window.Handle);
        };
        Grid.SetColumn(title, 1);
        Grid.SetColumn(close, 2);
        header.Children.Add(dot);
        header.Children.Add(title);
        header.Children.Add(close);

        // Area della miniatura: DWM ci disegna sopra. Senza miniatura (finestra ridotta a icona) c'è l'icona.
        var area = new Border
        {
            Width = size.Width,
            Height = size.Height,
            CornerRadius = new CornerRadius(8),
            Background = thumbnail == 0 ? Theme.SurfaceBrush : Brushes.Transparent,
        };
        if (thumbnail != 0)
            _thumbnails.Add((thumbnail, area));
        else if (icon is not null)
            area.Child = new Image { Source = icon, Width = 32, Height = 32 };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(area);

        var card = new Border
        {
            Padding = new Thickness(CardPadding, 2, CardPadding, CardPadding),
            Margin = new Thickness(Gap / 2, 0, Gap / 2, 0),
            CornerRadius = new CornerRadius(12),
            Background = Brushes.Transparent,
            Child = stack,
            Cursor = Cursors.Hand,
        };
        card.MouseEnter += (_, _) => card.Background = Theme.SurfaceBrush;
        card.MouseLeave += (_, _) => card.Background = Brushes.Transparent;
        card.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ActivateRequested?.Invoke(window.Handle);
        };
        return card;
    }

    /// <summary>Posizione dell'area nella finestra, in pixel fisici (DWM non conosce i DIP).</summary>
    private Rect2 AreaInPixels(FrameworkElement area)
    {
        var origin = area.TransformToAncestor(this).Transform(new Point(0, 0));
        return new Rect2(Math.Round(origin.X * _scale), Math.Round(origin.Y * _scale),
            Math.Round(area.ActualWidth * _scale), Math.Round(area.ActualHeight * _scale));
    }

    private void ReleaseThumbnails()
    {
        foreach (var (thumbnail, _) in _thumbnails)
            DwmNative.Unregister(thumbnail);
        _thumbnails.Clear();
    }
}
