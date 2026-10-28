using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NothingShell.App.Interop;
using NothingShell.App.Launcher;
using NothingShell.App.Rendering;
using NothingShell.App.Services;
using NothingShell.Core.Bar;
using NothingShell.Core.Design;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Sensors;

namespace NothingShell.App.Bar;

/// <summary>Barra in basso al posto della taskbar di Windows di un monitor (docs/05-bar.md).</summary>
public sealed class BarWindow : Window
{
    private const byte VkLeftWin = 0x5B;
    private const byte VkA = 0x41;
    private const byte VkN = 0x4E;
    private const byte VkControl = 0x11;
    private const byte VkEscape = 0x1B;
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    private readonly WindowTracker _tracker;
    private readonly AppIcons _icons;
    private readonly ShellIcons _shellIcons;
    private readonly AppCatalog _catalog;
    private readonly WindowsTaskbar _taskbar;
    private readonly LauncherWindow _launcher;
    private readonly JumpListWindow _jumpList = new();
    private readonly PreviewWindow _preview = new();
    private readonly DispatcherTimer _previewOpen = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _previewHide = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private (string AppKey, double AnchorX)? _previewTarget;

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

    private MultiMonitorMode _mode;

    public BarWindow(WindowTracker tracker, AppIcons icons, ShellIcons shellIcons, AppCatalog catalog,
        WindowsTaskbar taskbar, LauncherWindow launcher, DisplayMonitor monitor, MultiMonitorMode mode)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        Monitor = monitor;
        _mode = mode;
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(icons);
        ArgumentNullException.ThrowIfNull(shellIcons);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(taskbar);
        ArgumentNullException.ThrowIfNull(launcher);
        _launcher = launcher;
        _tracker = tracker;
        _icons = icons;
        _shellIcons = shellIcons;
        _catalog = catalog;
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
        _tracker.ForegroundChanged += OnForegroundChanged;
        SourceInitialized += (_, _) =>
        {
            NativeMethods.MakeNonActivatingToolWindow(new WindowInteropHelper(this).Handle);
            Place();
        };
        Closed += (_, _) =>
        {
            // Monitor scollegato: la barra sparisce e non deve più ricevere eventi.
            _tracker.GroupsChanged -= RebuildApps;
            _tracker.ForegroundChanged -= OnForegroundChanged;
            _previewOpen.Stop();
            _previewHide.Stop();
            _jumpList.Close();
            _preview.Close();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
                HidePreview(); // schermo intero o barra spenta
        };
        SetupPreview();
        RebuildApps();
    }

    public event Action? SettingsRequested;

    public DisplayMonitor Monitor { get; private set; }

    /// <summary>Monitor o impostazione di Windows cambiati: riposiziona la barra e rifà l'elenco delle app.</summary>
    public void SetMonitor(DisplayMonitor monitor, MultiMonitorMode mode)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var modeChanged = mode != _mode || monitor.Handle != Monitor.Handle || monitor.IsPrimary != Monitor.IsPrimary;
        Monitor = monitor;
        _mode = mode;
        Place();
        if (modeChanged)
            RebuildApps();
    }

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
            _launcher.Toggle(Monitor);
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
        foreach (var group in VisibleGroups())
            _apps.Children.Add(AppButton(group));
        UpdateIndicators();
        RefreshPreview();
    }

    private void SetupPreview()
    {
        _previewOpen.Tick += (_, _) =>
        {
            _previewOpen.Stop();
            ShowPreview();
        };
        _previewHide.Tick += (_, _) => HidePreview();
        // Il mouse passa dall'icona al pannello: il pannello resta aperto finché ci si sta sopra.
        _preview.MouseEnter += (_, _) => _previewHide.Stop();
        _preview.MouseLeave += (_, _) => _previewHide.Start();
        _preview.ActivateRequested += hwnd =>
        {
            HidePreview();
            WindowTracker.Execute(new ClickAction(ClickKind.Activate, hwnd));
        };
        _preview.CloseRequested += WindowTracker.Close; // il pannello si aggiorna quando la finestra sparisce
    }

    /// <summary>Mouse su un'icona: anteprime dopo una breve pausa, subito se un pannello è già aperto.</summary>
    private void OnAppHover(string appKey, FrameworkElement button)
    {
        _previewHide.Stop();
        _previewTarget = (appKey, AnchorX(button));
        if (_preview.AppKey is not null)
            ShowPreview();
        else
            _previewOpen.Start();
    }

    private void OnAppLeave()
    {
        _previewOpen.Stop();
        _previewHide.Start();
    }

    private void ShowPreview()
    {
        if (_previewTarget is not var (appKey, anchorX) || CurrentGroup(appKey) is not { IsRunning: true } group || _jumpList.IsVisible)
        {
            HidePreview();
            return;
        }
        _preview.Open(group, IconOf(group), _tracker.Foreground, anchorX, Monitor);
    }

    /// <summary>Finestre aperte o chiuse, titoli cambiati: il pannello aperto si aggiorna.</summary>
    private void RefreshPreview()
    {
        if (_preview.AppKey is not null)
            ShowPreview();
    }

    private void HidePreview()
    {
        _previewOpen.Stop();
        _previewHide.Stop();
        _previewTarget = null;
        _preview.Dismiss();
    }

    private Border AppButton(AppGroup group)
    {
        UIElement glyph = IconOf(group) is { } icon
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
            Visibility = group.IsRunning ? Visibility.Visible : Visibility.Hidden, // appuntata ma chiusa: nessun punto
        };
        _indicators[group.AppKey] = indicator;

        var content = new Grid();
        content.Children.Add(glyph);
        content.Children.Add(indicator);

        var button = Button(content, 44);
        content.HorizontalAlignment = HorizontalAlignment.Stretch; // l'indicatore sta sul fondo del pulsante
        content.VerticalAlignment = VerticalAlignment.Stretch;
        button.Margin = new Thickness(2, 0, 2, 0);
        // App aperta: al posto del suggerimento ci sono le anteprime delle finestre, come in Windows.
        if (!group.IsRunning)
            button.ToolTip = group.Title;
        button.MouseEnter += (_, _) => OnAppHover(group.AppKey, button);
        button.MouseLeave += (_, _) => OnAppLeave();
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            HidePreview();
            // Il gruppo va riletto: il riferimento catturato può essere vecchio
            if (CurrentGroup(group.AppKey) is not { } current)
                return;
            var action = TaskbarModel.Click(current, _tracker.Foreground);
            if (action.Kind == ClickKind.Launch)
                LaunchApp(current);
            else
                WindowTracker.Execute(action);
        };
        // Gestito qui: il menu della barra (spazio vuoto) non deve aprirsi sopra l'elenco dei recenti.
        button.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            HidePreview();
            if (CurrentGroup(group.AppKey) is { } current)
                _jumpList.Open(JumpListFor(current), AnchorX(button), Monitor);
        };
        return button;
    }

    private AppGroup? CurrentGroup(string appKey) => VisibleGroups().FirstOrDefault(g => g.AppKey == appKey);

    private JumpListModel JumpListFor(AppGroup group)
    {
        var appId = _tracker.AppIdOf(group);
        var exe = _tracker.ExePathOf(group);
        var appIcon = IconOf(group);
        var (items, frequent) = appId is null ? ((IReadOnlyList<JumpItem>)[], false) : ShellNative.ReadJumpList(appId);
        return new JumpListModel(
            AppName(group, appId, exe),
            appIcon,
            frequent,
            JumpLists.Clean(items),
            group.Windows.Count,
            item => item.IsLink ? appIcon : _icons.ForFile(item.Path),
            item => Run(JumpLists.Open(item, exe)),
            () => LaunchApp(group),
            () =>
            {
                foreach (var w in CurrentGroup(group.AppKey)?.Windows ?? [])
                    WindowTracker.Close(w.Handle);
            });
    }

    /// <summary>
    /// Icona dell'app: quella del collegamento appuntato (come la mostra Windows), poi quella della finestra
    /// o dell'eseguibile, infine quella dello Start (app UWP non appuntate).
    /// </summary>
    private ImageSource? IconOf(AppGroup group)
    {
        if (group.Pin is { } pin && _shellIcons.GetForPath(pin.LaunchPath) is { } pinned)
            return pinned;
        if (group.Windows.Count > 0 &&
            _icons.Get(group.AppKey, group.Windows[0].Handle, _tracker.ExePathOf(group.Windows[0].Handle)) is { } own)
            return own;
        return _tracker.AppIdOf(group) is { } appId ? _shellIcons.Get(appId) : null;
    }

    private string AppName(AppGroup group, string? appId, string? exe)
    {
        if (group.Pin is { } pin)
            return pin.Name;
        if (appId is not null && _catalog.Apps.FirstOrDefault(a => a.Id.Equals(appId, StringComparison.OrdinalIgnoreCase)) is { } app)
            return app.Name;
        if (exe is not null && FileVersionInfo.GetVersionInfo(exe).FileDescription is { Length: > 0 } description)
            return description;
        return exe is not null ? System.IO.Path.GetFileNameWithoutExtension(exe) : group.Title;
    }

    /// <summary>Nuova istanza dell'app: dal collegamento appuntato, dallo Start o dall'eseguibile.</summary>
    private void LaunchApp(AppGroup group)
    {
        var appId = _tracker.AppIdOf(group);
        if (group.Pin is { } pin)
            Run(pin.LaunchPath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                ? new LaunchCommand("explorer.exe", pin.LaunchPath)
                : new LaunchCommand(pin.LaunchPath, null));
        else if (appId is not null && _catalog.Apps.FirstOrDefault(a => a.Id.Equals(appId, StringComparison.OrdinalIgnoreCase)) is { } app)
            Run(new LaunchCommand("explorer.exe", $"shell:AppsFolder\\{app.Id}"));
        else if (_tracker.ExePathOf(group) is { } exe)
            Run(new LaunchCommand(exe, null));
    }

    private static void Run(LaunchCommand command)
    {
        try
        {
            Process.Start(new ProcessStartInfo(command.FileName, command.Arguments ?? string.Empty) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // file spostato o app disinstallata: come in Windows, l'elemento semplicemente non si apre
        }
    }

    /// <summary>Centro orizzontale del pulsante in pixel fisici, per allinearvi l'elenco dei recenti.</summary>
    private static double AnchorX(FrameworkElement button) =>
        button.PointToScreen(new Point(button.ActualWidth / 2, 0)).X;

    private void OnForegroundChanged(nint foreground) => UpdateIndicators();

    private void UpdateIndicators()
    {
        var active = VisibleGroups().FirstOrDefault(g => g.Contains(_tracker.Foreground))?.AppKey;
        foreach (var (key, dot) in _indicators)
        {
            var isActive = key == active;
            dot.Fill = isActive ? Theme.RedBrush : Theme.GreyBrush;
            dot.Width = isActive ? 6 : 4;
        }
    }

    /// <summary>App di questa barra secondo l'impostazione multi-monitor di Windows.</summary>
    private IReadOnlyList<AppGroup> VisibleGroups() =>
        MultiMonitorBar.GroupsFor(_tracker.Groups, Monitor.Handle, Monitor.IsPrimary, _mode);

    /// <summary>Nella fascia riservata alla taskbar del suo monitor, in pixel fisici (vale anche con DPI diversi).</summary>
    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0)
            WindowNative.Place(hwnd, MultiMonitorBar.Place(Monitor));
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
