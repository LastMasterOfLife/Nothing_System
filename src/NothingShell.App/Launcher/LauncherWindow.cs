using System.Windows;
using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NothingShell.App.Interop;
using NothingShell.App.Rendering;
using NothingShell.Core.Bar;
using NothingShell.Core.Design;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Launcher;
using NothingShell.Core.Widgets;

namespace NothingShell.App.Launcher;

/// <summary>Launcher Nothing: ricerca e avvio delle app (docs/06-launcher.md).</summary>
public sealed class LauncherWindow : Window
{
    private const double PanelWidth = 560;
    private const double PanelHeight = 660;
    private const double BottomGap = 12;
    private const double RowHeight = 40;
    private static readonly Duration OpenDuration = new(TimeSpan.FromMilliseconds(150));
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(3);

    private readonly AppCatalog _catalog;
    private readonly ShellIcons _icons;
    private readonly LauncherSettingsStore _store;

    private readonly Border _panel = new()
    {
        Background = Theme.BlackBrush,
        BorderBrush = Theme.OutlineBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(28),
        Padding = new Thickness(20),
    };
    private readonly TranslateTransform _slide = new();
    private readonly TextBox _search = new()
    {
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Foreground = Theme.WhiteBrush,
        CaretBrush = Theme.WhiteBrush,
        FontFamily = Theme.BodyFont,
        FontSize = 18,
        VerticalContentAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _placeholder = Label("Cerca app", 18, Theme.GreyBrush);
    private readonly StackPanel _content = new();
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };
    private readonly List<(Border Row, AppEntry App)> _navigable = [];

    private LauncherSettings _settings;
    private StackPanel? _allApps;
    private List<(Border Row, AppEntry App)> _allAppsRows = [];
    private int _allAppsVersion = -1;
    private int _selected = -1;
    private bool _closing;
    private bool _eagerIcons;

    public LauncherWindow(AppCatalog catalog, ShellIcons icons, LauncherSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(icons);
        ArgumentNullException.ThrowIfNull(store);
        _catalog = catalog;
        _icons = icons;
        _store = store;
        _settings = store.Load();

        Width = PanelWidth;
        Height = PanelHeight;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Title = "Nothing Shell — Launcher";
        Content = BuildVisualTree();

        SourceInitialized += (_, _) => MakeToolWindow();
        Deactivated += (_, _) => Dismiss();
        PreviewKeyDown += OnPreviewKeyDown;
        _search.TextChanged += (_, _) => ShowCurrentMode();
    }

    public bool UseWinKey
    {
        get => _settings.UseWinKey;
        set
        {
            _settings = _settings with { UseWinKey = value };
            Save();
            UseWinKeyChanged?.Invoke(value);
        }
    }

    public event Action<bool>? UseWinKeyChanged;

    /// <summary>Voce interna scelta (es. <see cref="AppCatalog.SettingsEntry"/>).</summary>
    public event Action<string>? CommandRequested;

    /// <summary>Pulsante Blocca: lo gestisce l'accesso Nothing (docs/09-access.md).</summary>
    public event Action? LockRequested;

    /// <summary>Prepara il contenuto senza mostrare la finestra, con icone caricate subito (per <c>--snapshot</c>).</summary>
    public void PrepareSnapshot(string? query)
    {
        _eagerIcons = true;
        _search.Text = query ?? string.Empty;
        ShowCurrentMode();
        var root = (FrameworkElement)Content;
        root.Width = PanelWidth;
        root.Height = PanelHeight;
    }

    /// <summary>Apre o chiude il launcher sopra la barra di <paramref name="monitor"/> (null: monitor del mouse).</summary>
    public void Toggle(DisplayMonitor? monitor = null)
    {
        if (IsVisible)
            Dismiss();
        else
            Open(monitor);
    }

    public void Open(DisplayMonitor? monitor = null)
    {
        _closing = false;
        _search.Text = string.Empty;
        ShowCurrentMode();
        _scroll.ScrollToTop();

        var bounds = PlacementAboveBar(monitor);
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (bounds is { } b)
            WindowNative.Place(hwnd, b);
        Opacity = 0;
        _slide.Y = 12;
        Show();
        if (bounds is { } again)
            WindowNative.Place(hwnd, again); // WPF può riapplicare la sua posizione alla prima apparizione
        WindowNative.BringToFront(hwnd);
        Activate();
        _search.Focus();
        Keyboard.Focus(_search);
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, OpenDuration));
        _slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, OpenDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        _ = RefreshCatalogAsync();
    }

    public void Dismiss()
    {
        if (!IsVisible || _closing)
            return;
        _closing = true;
        Hide();
    }

    private async Task RefreshCatalogAsync()
    {
        var before = _catalog.Version;
        try
        {
            await _catalog.RefreshAsync();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return; // si continua con l'ultimo catalogo valido
        }
        if (_catalog.Version != before && IsVisible)
            ShowCurrentMode();
    }

    private Grid BuildVisualTree()
    {
        var searchRow = new Grid { Height = 44, Margin = new Thickness(4, 0, 4, 12) };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition());
        var glyph = new DotMatrixView
        {
            Pitch = 3,
            Dots = DotGrid.From(DotIcons.Search),
            DotColor = Theme.Grey,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        };
        _placeholder.VerticalAlignment = VerticalAlignment.Center;
        _placeholder.IsHitTestVisible = false;
        Grid.SetColumn(_search, 1);
        Grid.SetColumn(_placeholder, 1);
        searchRow.Children.Add(glyph);
        searchRow.Children.Add(_search);
        searchRow.Children.Add(_placeholder);

        _scroll.Content = _content;

        var dock = new DockPanel();
        DockPanel.SetDock(searchRow, Dock.Top);
        var footer = BuildFooter();
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(searchRow);
        dock.Children.Add(footer);
        dock.Children.Add(_scroll);
        _panel.Child = dock;
        _panel.RenderTransform = _slide;

        var root = new Grid();
        root.Children.Add(_panel);
        return root;
    }

    private Grid BuildFooter()
    {
        var footer = new Grid { Margin = new Thickness(4, 12, 0, 0) };
        var user = Label(Environment.UserName.ToLowerInvariant(), 13, Theme.GreyBrush);
        user.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(user);

        var power = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        power.Children.Add(PowerButton(DotIcons.Lock, "Blocca", confirm: false, () => LockRequested?.Invoke()));
        power.Children.Add(PowerButton(DotIcons.Sleep, "Sospendi", confirm: true, PowerActions.Sleep));
        power.Children.Add(PowerButton(DotIcons.Restart, "Riavvia", confirm: true, PowerActions.Restart));
        power.Children.Add(PowerButton(DotIcons.Power, "Spegni", confirm: true, PowerActions.PowerOff));
        footer.Children.Add(power);
        return footer;
    }

    /// <summary>Pulsante di alimentazione; con <paramref name="confirm"/> il primo clic arma, il secondo esegue.</summary>
    private Border PowerButton(DotGlyph glyph, string name, bool confirm, Action action)
    {
        var button = HoverBorder(new DotMatrixView { Pitch = 2.5, Dots = DotGrid.From(glyph) }, 40, 36);
        button.ToolTip = name;
        DispatcherTimer? armed = null;

        void Disarm()
        {
            armed?.Stop();
            armed = null;
            button.Background = Brushes.Transparent;
            button.ToolTip = name;
        }

        button.MouseLeave += (_, _) =>
        {
            if (armed is not null)
                button.Background = Theme.RedBrush;
        };
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (confirm && armed is null)
            {
                armed = new DispatcherTimer { Interval = ConfirmWindow };
                armed.Tick += (_, _) => Disarm();
                armed.Start();
                button.Background = Theme.RedBrush;
                button.ToolTip = $"{name}: clicca di nuovo per confermare";
                return;
            }
            Disarm();
            Dismiss();
            action();
        };
        return button;
    }

    private void ShowCurrentMode()
    {
        var query = _search.Text;
        _placeholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _content.Children.Clear();
        _navigable.Clear();
        _selected = -1;

        if (string.IsNullOrWhiteSpace(query))
            ShowHome();
        else
            ShowResults(query);
    }

    private void ShowHome()
    {
        var recents = AppSearch.Recents(_catalog.Apps, _settings.Usage);
        if (recents.Count > 0)
        {
            _content.Children.Add(SectionTitle("RECENTI"));
            var tiles = new UniformGrid { Columns = AppSearch.RecentCount, Margin = new Thickness(0, 0, 0, 16) };
            foreach (var app in recents)
                tiles.Children.Add(Tile(app));
            _content.Children.Add(tiles);
        }

        _content.Children.Add(SectionTitle("TUTTE LE APP"));
        if (_allApps is null || _allAppsVersion != _catalog.Version)
            BuildAllApps();
        _content.Children.Add(_allApps!);
        _navigable.AddRange(_allAppsRows);
        if (_catalog.Apps.Count == 0)
            _content.Children.Add(Label("Caricamento delle app…", 13, Theme.GreyBrush));
    }

    private void BuildAllApps()
    {
        _allApps = new StackPanel();
        _allAppsRows = [];
        foreach (var section in AppSearch.Sections(_catalog.Apps))
        {
            _allApps.Children.Add(new DotMatrixView
            {
                Pitch = 2,
                DotColor = Theme.Red,
                Dots = DotText.Render(section.Letter.ToString()),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(10, 10, 0, 6),
            });
            foreach (var app in section.Apps)
            {
                var row = Row(app, lazyIcon: true);
                _allApps.Children.Add(row);
                _allAppsRows.Add((row, app));
            }
        }
        _allAppsVersion = _catalog.Version;
    }

    private void ShowResults(string query)
    {
        var results = AppSearch.Search(_catalog.Apps, query, _settings.Usage);
        if (results.Count == 0)
        {
            var none = Label("Nessun risultato", 14, Theme.GreyBrush);
            none.Margin = new Thickness(10, 8, 0, 0);
            _content.Children.Add(none);
            return;
        }
        foreach (var app in results)
        {
            var row = Row(app, lazyIcon: false);
            _content.Children.Add(row);
            _navigable.Add((row, app));
        }
        Select(0);
    }

    private Border Row(AppEntry app, bool lazyIcon)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = IconHost(app, 26, lazyIcon);
        var name = Label(app.Name, 14, Theme.WhiteBrush);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.Margin = new Thickness(12, 0, 0, 0);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(name, 1);
        grid.Children.Add(icon);
        grid.Children.Add(name);

        var row = HoverBorder(grid, double.NaN, RowHeight);
        row.Padding = new Thickness(10, 0, 10, 0);
        row.CornerRadius = new CornerRadius(12);
        grid.HorizontalAlignment = HorizontalAlignment.Stretch;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Launch(app);
        };
        return row;
    }

    private Border Tile(AppEntry app)
    {
        var stack = new StackPanel();
        var icon = IconHost(app, 32, lazy: false);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        var name = Label(app.Name, 11, Theme.GreyBrush);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.Margin = new Thickness(4, 6, 4, 0);
        stack.Children.Add(icon);
        stack.Children.Add(name);

        var tile = HoverBorder(stack, double.NaN, 72);
        tile.CornerRadius = new CornerRadius(16);
        tile.ToolTip = app.Name;
        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Launch(app);
        };
        return tile;
    }

    /// <summary>Icona dell'app o, finché non è disponibile, la sua iniziale a punti.</summary>
    private Grid IconHost(AppEntry app, double size, bool lazy)
    {
        var host = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        if (_icons.TryGetCached(app.Id, out var cached) || !lazy || _eagerIcons)
        {
            SetIcon(host, app, lazy && !_eagerIcons ? cached : _icons.Get(app.Id));
            return host;
        }

        SetIcon(host, app, null);
        // Le icone dell'elenco completo si caricano una alla volta a bassa priorità: l'apertura resta immediata.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => SetIcon(host, app, _icons.Get(app.Id)));
        return host;
    }

    private static void SetIcon(Grid host, AppEntry app, BitmapSource? icon)
    {
        host.Children.Clear();
        if (icon is not null)
        {
            var image = new Image { Source = icon };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            host.Children.Add(image);
            return;
        }
        var letter = AppSearch.SectionLetter(app.Name);
        host.Children.Add(new DotMatrixView
        {
            Pitch = 2.5,
            Dots = DotText.Render(letter == '#' ? app.Name[..1] : letter.ToString()),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Dismiss();
                e.Handled = true;
                break;
            case Key.Down:
                Select(Math.Min(_selected + 1, _navigable.Count - 1));
                e.Handled = true;
                break;
            case Key.Up:
                Select(Math.Max(_selected - 1, 0));
                e.Handled = true;
                break;
            case Key.Enter when _selected >= 0 && _selected < _navigable.Count:
                Launch(_navigable[_selected].App);
                e.Handled = true;
                break;
        }
    }

    private void Select(int index)
    {
        if (_navigable.Count == 0)
            return;
        if (_selected >= 0 && _selected < _navigable.Count)
            _navigable[_selected].Row.Background = Brushes.Transparent;
        _selected = index;
        var row = _navigable[_selected].Row;
        row.Background = Theme.SurfaceBrush;
        row.BringIntoView();
    }

    private void Launch(AppEntry app)
    {
        _settings = _settings.RecordLaunch(app.Id, DateTimeOffset.Now);
        Save();
        Dismiss();
        if (app.Id.StartsWith(AppCatalog.InternalPrefix, StringComparison.Ordinal))
        {
            CommandRequested?.Invoke(app.Id);
            return;
        }
        try
        {
            AppCatalog.Launch(app);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // app disinstallata nel frattempo: alla prossima apertura il catalogo si aggiorna
        }
    }

    private void Save()
    {
        try
        {
            _store.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // l'uso non salvato non deve impedire l'avvio delle app
        }
    }

    /// <summary>Centrato sopra la barra del monitor, in pixel fisici; null se non si trovano monitor.</summary>
    private static Rect2? PlacementAboveBar(DisplayMonitor? monitor)
    {
        if (monitor is null)
        {
            var monitors = WindowNative.Monitors();
            var atCursor = WindowNative.MonitorAtCursor();
            monitor = monitors.FirstOrDefault(m => m.Handle == atCursor) ?? monitors.FirstOrDefault(m => m.IsPrimary);
            if (monitor is null)
                return null;
        }
        var s = monitor.Scale;
        var (width, height) = (PanelWidth * s, PanelHeight * s);
        var left = monitor.Bounds.X + (monitor.Bounds.Width - width) / 2;
        var top = MultiMonitorBar.Place(monitor).Y - height - BottomGap * s;
        return new Rect2(Math.Round(left), Math.Round(top), Math.Round(width), Math.Round(height));
    }

    private void MakeToolWindow()
    {
        // Solo tool window (fuori da Alt+Tab): il launcher deve poter ricevere il focus per la tastiera.
        NativeMethods.AddExStyle(new WindowInteropHelper(this).Handle, WindowNative.WsExToolWindow);
    }

    private static TextBlock SectionTitle(string text)
    {
        var title = Label(text, 11, Theme.GreyBrush);
        title.Margin = new Thickness(10, 4, 0, 10);
        return title;
    }

    private static Border HoverBorder(UIElement child, double width, double height)
    {
        var border = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(height / 2),
            Background = Brushes.Transparent,
            Child = child,
        };
        if (child is FrameworkElement fe && !double.IsNaN(width))
        {
            fe.HorizontalAlignment = HorizontalAlignment.Center;
            fe.VerticalAlignment = VerticalAlignment.Center;
        }
        border.MouseEnter += (_, _) =>
        {
            if (border.Background != Theme.RedBrush)
                border.Background = Theme.SurfaceBrush;
        };
        border.MouseLeave += (_, _) =>
        {
            if (border.Background == Theme.SurfaceBrush)
                border.Background = Brushes.Transparent;
        };
        return border;
    }

    private static TextBlock Label(string text, double size, Brush foreground) => new()
    {
        Text = text,
        FontFamily = Theme.BodyFont,
        FontSize = size,
        Foreground = foreground,
    };
}
