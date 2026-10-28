using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using NothingShell.App.Interop;
using NothingShell.App.Rendering;
using NothingShell.Core.Bar;
using NothingShell.Core.Design;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Widgets;

namespace NothingShell.App.Bar;

/// <summary>Contenuto dell'elenco dei recenti di un'app e azioni sulle sue voci.</summary>
public sealed record JumpListModel(
    string AppName,
    ImageSource? AppIcon,
    bool Frequent,
    IReadOnlyList<JumpItem> Items,
    int OpenWindows,
    Func<JumpItem, ImageSource?> IconOf,
    Action<JumpItem> OpenItem,
    Action LaunchApp,
    Action CloseWindows);

/// <summary>
/// Clic destro su un'app della barra: i recenti dell'app come nella jump list di Windows,
/// poi l'app (nuova finestra) e la chiusura delle finestre (docs/05-bar.md).
/// </summary>
public sealed class JumpListWindow : Window
{
    private const double PanelWidth = 320;
    private const double RowHeight = 36;
    private const double Gap = 8;
    private const double ScreenMargin = 8;
    private static readonly Duration OpenDuration = new(TimeSpan.FromMilliseconds(150));

    private readonly Border _panel = new()
    {
        Background = Theme.BlackBrush,
        BorderBrush = Theme.OutlineBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(24),
        Padding = new Thickness(10, 14, 10, 10),
        Width = PanelWidth,
    };
    private readonly TranslateTransform _slide = new();
    private bool _closing;

    public JumpListWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Title = "Nothing Shell — Recenti";
        Width = PanelWidth;
        _panel.RenderTransform = _slide;
        Content = _panel;

        SourceInitialized += (_, _) =>
            NativeMethods.AddExStyle(new WindowInteropHelper(this).Handle, WindowNative.WsExToolWindow);
        Deactivated += (_, _) => Dismiss();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;
            e.Handled = true;
            Dismiss();
        };
    }

    /// <summary>
    /// Apre l'elenco sopra la barra di <paramref name="monitor"/>, centrato su <paramref name="anchorX"/>
    /// (pixel fisici).
    /// </summary>
    public void Open(JumpListModel model, double anchorX, DisplayMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(monitor);
        _closing = false;
        Build(model);
        _panel.Measure(new Size(PanelWidth, double.PositiveInfinity));
        Height = _panel.DesiredSize.Height;

        var s = monitor.Scale;
        var (width, height) = (PanelWidth * s, Height * s);
        var left = Math.Clamp(anchorX - width / 2, monitor.Bounds.X + ScreenMargin * s, monitor.Bounds.Right - width - ScreenMargin * s);
        var top = MultiMonitorBar.Place(monitor).Y - height - Gap * s;
        var bounds = new Rect2(Math.Round(left), Math.Round(top), Math.Round(width), Math.Round(height));
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        WindowNative.Place(hwnd, bounds);

        Opacity = 0;
        _slide.Y = 10;
        Show();
        WindowNative.Place(hwnd, bounds); // WPF può riapplicare la sua posizione alla prima apparizione
        // La barra non prende il focus: senza attivazione l'elenco non si chiuderebbe cliccando altrove.
        WindowNative.BringToFront(hwnd);
        Activate();
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, OpenDuration));
        _slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, OpenDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    public void Dismiss()
    {
        if (!IsVisible || _closing)
            return;
        _closing = true;
        Hide();
    }

    /// <summary>Contenuto pronto da disegnare senza mostrare la finestra (per <c>--snapshot</c>).</summary>
    public FrameworkElement PrepareSnapshot(JumpListModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        Build(model);
        _panel.Measure(new Size(PanelWidth, double.PositiveInfinity));
        _panel.Height = _panel.DesiredSize.Height;
        return _panel;
    }

    private void Build(JumpListModel model)
    {
        var stack = new StackPanel();
        stack.Children.Add(new DotMatrixView
        {
            Pitch = 2,
            DotColor = Theme.Red,
            Dots = DotText.Render(model.Frequent ? "FREQUENTI" : "RECENTI"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(10, 0, 0, 10),
        });

        if (model.Items.Count == 0)
        {
            var none = Label("Nessun elemento recente", Theme.GreyBrush);
            none.Margin = new Thickness(10, 0, 0, 8);
            stack.Children.Add(none);
        }
        foreach (var item in model.Items)
        {
            var row = Row(IconView(model.IconOf(item)), item.Title, Theme.WhiteBrush, () => model.OpenItem(item));
            row.ToolTip = item.IsLink && !string.IsNullOrEmpty(item.Arguments) ? $"{item.Path} {item.Arguments}" : item.Path;
            stack.Children.Add(row);
        }

        stack.Children.Add(new Rectangle { Height = 1, Fill = Theme.OutlineBrush, Margin = new Thickness(10, 8, 10, 8) });
        stack.Children.Add(Row(IconView(model.AppIcon), model.AppName, Theme.WhiteBrush, model.LaunchApp));
        if (model.OpenWindows > 0)
        {
            var close = new DotMatrixView
            {
                Pitch = 2,
                Dots = DotGrid.From(DotIcons.Close),
                DotColor = Theme.Red,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var label = model.OpenWindows > 1 ? $"Chiudi {model.OpenWindows} finestre" : "Chiudi finestra";
            stack.Children.Add(Row(close, label, Theme.WhiteBrush, model.CloseWindows));
        }
        _panel.Child = stack;
    }

    private Border Row(UIElement icon, string text, Brush foreground, Action action)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var label = Label(text, foreground);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(12, 0, 0, 0);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(label, 1);
        grid.Children.Add(icon);
        grid.Children.Add(label);

        var row = new Border
        {
            Height = RowHeight,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 0, 10, 0),
            Background = Brushes.Transparent,
            Child = grid,
        };
        row.MouseEnter += (_, _) => row.Background = Theme.SurfaceBrush;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Dismiss();
            action();
        };
        return row;
    }

    private static FrameworkElement IconView(ImageSource? source)
    {
        if (source is null)
            return new Ellipse { Width = 6, Height = 6, Fill = Theme.GreyBrush };
        var image = new Image { Source = source, Width = 20, Height = 20 };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    private static TextBlock Label(string text, Brush foreground) => new()
    {
        Text = text,
        FontFamily = Theme.BodyFont,
        FontSize = 13,
        Foreground = foreground,
    };
}
