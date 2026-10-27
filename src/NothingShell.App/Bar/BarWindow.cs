using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using NothingShell.App.Interop;
using NothingShell.App.Launcher;
using NothingShell.App.Rendering;
using NothingShell.App.Services;
using NothingShell.Core.Bar;
using NothingShell.Core.Design;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Sensors;

namespace NothingShell.App.Bar;

/// <summary>Barra in basso al posto della taskbar (docs/05-bar.md).</summary>
public sealed class BarWindow : Window
{
    private const double FallbackHeight = 48;
    private const double MinReservedHeight = 24;
    private const byte VkLeftWin = 0x5B;
    private const byte VkA = 0x41;
    private const byte VkN = 0x4E;
    private const byte VkControl = 0x11;
    private const byte VkEscape = 0x1B;
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    private readonly WindowTracker _tracker;
    private readonly AppIcons _icons;
    private readonly WindowsTaskbar _taskbar;
    private readonly LauncherWindow _launcher;

    private readonly StackPanel _apps = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Center,
    };
    private readonly Dictionary<string, Ellipse> _indicators = new(StringComparer.Ordinal);
    private readonly DotMatrixView _network = new() { Pitch = 2.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly DotMatrixView _battery = new() { Pitch = 2, VerticalAlignment = VerticalAlignment.Center };
    private readonly DotMatrixView _time = new() { Pitch = 2.5, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly DotMatrixView _date = new() { Pitch = 2, DotColor = Theme.Grey, HorizontalAlignment = HorizontalAlignment.Right };

    public BarWindow(WindowTracker tracker, AppIcons icons, WindowsTaskbar taskbar, LauncherWindow launcher)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(icons);
        ArgumentNullException.ThrowIfNull(taskbar);
        ArgumentNullException.ThrowIfNull(launcher);
        _launcher = launcher;
        _tracker = tracker;
        _icons = icons;
        _taskbar = taskbar;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Theme.BlackBrush;
        Title = "Nothing Shell — Barra";
        Content = BuildVisualTree();

        _tracker.GroupsChanged += RebuildApps;
        _tracker.ForegroundChanged += _ => UpdateIndicators();
        SourceInitialized += (_, _) =>
            NativeMethods.MakeNonActivatingToolWindow(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => PositionOnPrimaryScreen();
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea))
                PositionOnPrimaryScreen();
        };
    }

    public event Action? SettingsRequested;

    public void SetTime(DateTime now)
    {
        _time.Dots = DotText.Render(now.ToString("HH:mm", Italian));
        _date.Dots = DotText.Render(now.ToString("dd.MM", Italian));
    }

    public void SetBattery(BatteryStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        _battery.Visibility = status.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        _battery.Dots = DotText.Render(status.Label);
        _battery.DotColor = status.IsLow ? Theme.Red : Theme.White;
    }

    public void SetNetwork(NetworkKind kind)
    {
        _network.Dots = Glyph(kind == NetworkKind.Ethernet ? DotIcons.Ethernet : DotIcons.Wifi);
        _network.DotColor = kind == NetworkKind.Offline ? Theme.Red : Theme.White;
    }

    private Grid BuildVisualTree()
    {
        var root = new Grid { Background = Theme.BlackBrush };

        var start = Button(new DotMatrixView { Pitch = 3, Dots = Glyph(DotIcons.Start) }, 44);
        start.HorizontalAlignment = HorizontalAlignment.Left;
        start.Margin = new Thickness(10, 0, 0, 0);
        start.ToolTip = "Launcher";
        start.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _launcher.Toggle();
        };
        root.Children.Add(start);

        root.Children.Add(_apps);

        var status = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 10, 0),
        };
        var quick = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 0) };
        _battery.Margin = new Thickness(12, 0, 0, 0);
        quick.Children.Add(_network);
        quick.Children.Add(_battery);
        var quickButton = Button(quick, double.NaN);
        quickButton.ToolTip = "Impostazioni rapide";
        quickButton.MouseLeftButtonUp += (_, e) => Press(e, VkLeftWin, VkA);

        var clock = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        _date.Margin = new Thickness(0, 4, 0, 0);
        clock.Children.Add(_time);
        clock.Children.Add(_date);
        var clockButton = Button(clock, double.NaN);
        clockButton.ToolTip = "Notifiche e calendario";
        clockButton.MouseLeftButtonUp += (_, e) => Press(e, VkLeftWin, VkN);

        status.Children.Add(quickButton);
        status.Children.Add(clockButton);
        root.Children.Add(status);

        root.ContextMenu = BuildContextMenu();
        return root;
    }

    private ContextMenu BuildContextMenu()
    {
        var showTaskbar = new MenuItem { Header = "Mostra taskbar di Windows", IsCheckable = true };
        showTaskbar.Click += (_, _) =>
        {
            if (showTaskbar.IsChecked)
                _taskbar.Show();
            else
                _taskbar.Hide();
        };
        var winKey = new MenuItem { Header = "Usa il tasto Win per il launcher", IsCheckable = true };
        winKey.Click += (_, _) => _launcher.UseWinKey = winKey.IsChecked;
        var windowsStart = new MenuItem { Header = "Apri lo Start di Windows" };
        windowsStart.Click += (_, _) => WindowNative.PressKeys(VkControl, VkEscape);
        var settings = new MenuItem { Header = "Impostazioni…" };
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        var exit = new MenuItem { Header = "Esci da Nothing Shell" };
        exit.Click += (_, _) => Application.Current.Shutdown();

        var menu = new ContextMenu();
        menu.Opened += (_, _) =>
        {
            showTaskbar.IsChecked = !_taskbar.IsHidden;
            winKey.IsChecked = _launcher.UseWinKey;
        };
        menu.Items.Add(settings);
        menu.Items.Add(winKey);
        menu.Items.Add(windowsStart);
        menu.Items.Add(new Separator());
        menu.Items.Add(showTaskbar);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        return menu;
    }

    private void RebuildApps()
    {
        _apps.Children.Clear();
        _indicators.Clear();
        foreach (var group in _tracker.Groups)
            _apps.Children.Add(AppButton(group));
        UpdateIndicators();
    }

    private Border AppButton(AppGroup group)
    {
        var first = group.Windows[0];
        UIElement glyph = _icons.Get(group.AppKey, first.Handle, _tracker.ExePathOf(first.Handle)) is { } icon
            ? new Image { Source = icon, Width = 24, Height = 24 }
            : new DotMatrixView { Pitch = 3, Dots = DotText.Render(Initial(group.Title)) };
        RenderOptions.SetBitmapScalingMode(glyph, BitmapScalingMode.HighQuality);
        if (glyph is FrameworkElement fe)
        {
            fe.HorizontalAlignment = HorizontalAlignment.Center;
            fe.VerticalAlignment = VerticalAlignment.Center;
        }

        var indicator = new Ellipse
        {
            Width = 4,
            Height = 4,
            Fill = Theme.GreyBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 3),
        };
        _indicators[group.AppKey] = indicator;

        var content = new Grid();
        content.Children.Add(glyph);
        content.Children.Add(indicator);

        var button = Button(content, 44);
        content.HorizontalAlignment = HorizontalAlignment.Stretch; // l'indicatore sta sul fondo del pulsante
        content.VerticalAlignment = VerticalAlignment.Stretch;
        button.Margin = new Thickness(2, 0, 2, 0);
        button.ToolTip = group.Windows.Count == 1 ? group.Title : $"{group.Title} (+{group.Windows.Count - 1})";
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            // Il gruppo va riletto: il riferimento catturato può essere vecchio
            if (_tracker.Groups.FirstOrDefault(g => g.AppKey == group.AppKey) is { } current)
                WindowTracker.Execute(TaskbarModel.Click(current, _tracker.Foreground));
        };
        button.ContextMenu = AppContextMenu(group.AppKey);
        return button;
    }

    private ContextMenu AppContextMenu(string appKey)
    {
        var close = new MenuItem { Header = "Chiudi finestra" };
        close.Click += (_, _) =>
        {
            if (_tracker.Groups.FirstOrDefault(g => g.AppKey == appKey) is not { } group)
                return;
            foreach (var w in group.Windows)
                WindowTracker.Close(w.Handle);
        };
        var menu = new ContextMenu();
        menu.Opened += (_, _) =>
        {
            var count = _tracker.Groups.FirstOrDefault(g => g.AppKey == appKey)?.Windows.Count ?? 0;
            close.Header = count > 1 ? $"Chiudi {count} finestre" : "Chiudi finestra";
        };
        menu.Items.Add(close);
        return menu;
    }

    private void UpdateIndicators()
    {
        var active = _tracker.Groups.FirstOrDefault(g => g.Contains(_tracker.Foreground))?.AppKey;
        foreach (var (key, dot) in _indicators)
        {
            var isActive = key == active;
            dot.Fill = isActive ? Theme.RedBrush : Theme.GreyBrush;
            dot.Width = isActive ? 6 : 4;
        }
    }

    private void PositionOnPrimaryScreen()
    {
        var screenHeight = SystemParameters.PrimaryScreenHeight;
        var reserved = screenHeight - SystemParameters.WorkArea.Bottom;
        var height = reserved >= MinReservedHeight ? reserved : FallbackHeight;
        Left = 0;
        Top = screenHeight - height;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = height;
    }

    private static void Press(MouseButtonEventArgs e, params byte[] keys)
    {
        e.Handled = true;
        WindowNative.PressKeys(keys);
    }

    private static string Initial(string title) =>
        title.FirstOrDefault(char.IsLetterOrDigit) is var c and not '\0' ? c.ToString() : "?";

    private static DotGrid Glyph(DotGlyph glyph) => DotGrid.From(glyph);

    private static Border Button(UIElement child, double width)
    {
        var button = new Border
        {
            Width = width,
            Height = 38,
            CornerRadius = new CornerRadius(19),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            Child = child,
        };
        if (child is FrameworkElement content)
        {
            content.HorizontalAlignment = HorizontalAlignment.Center;
            content.VerticalAlignment = VerticalAlignment.Center;
        }
        button.MouseEnter += (_, _) => button.Background = Theme.SurfaceBrush;
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        return button;
    }
}
