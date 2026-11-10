using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NothingShell.App.Interop;
using NothingShell.App.Rendering;
using NothingShell.App.Services;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.PortalDebug;

namespace NothingShell.App.Notch;

/// <summary>
/// La dashboard di debug di un portale (docs/24-debug-potenziato.md): in alto i numeri degli ultimi 5 minuti, poi le
/// schede (tutto, richieste, errori, log, query, eventi, percorsi, controlli) con la ricerca, a destra il dettaglio
/// dell'evento scelto con la traccia della sua richiesta. Si aggiorna da sola; scorrendo l'elenco si ferma finché non si
/// torna in cima, così ciò che si sta leggendo non scappa.
/// </summary>
public sealed class DebugDashboardWindow : Window
{
    private const int MaxRows = 400;
    private static readonly TimeSpan Window5 = TimeSpan.FromMinutes(5);
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private enum Tab
    {
        All,
        Requests,
        Errors,
        Logs,
        Sql,
        Custom,
        Routes,
        Checks,
    }

    private static readonly (Tab Tab, string Title)[] Tabs =
    [
        (Tab.All, "Tutto"), (Tab.Requests, "Richieste"), (Tab.Errors, "Errori"), (Tab.Logs, "Log"), (Tab.Sql, "Query"),
        (Tab.Custom, "Eventi"), (Tab.Routes, "Percorsi"), (Tab.Checks, "Controlli"),
    ];

    private readonly PortalLink _link;
    private readonly PortalDebugService _service;
    private readonly Action<string, int>? _openInEditor;
    private readonly Border _frame = Theme.Panel(new Border { Padding = new Thickness(20, 16, 20, 14) });
    private readonly TextBlock _title = new() { FontFamily = Theme.BodyFont, FontSize = 17, Foreground = Theme.InkBrush, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _subtitle = new() { FontFamily = Theme.BodyFont, FontSize = 11, Foreground = Theme.MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly System.Windows.Shapes.Ellipse _reachDot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly UniformGrid _kpis = new() { Rows = 1, Margin = new Thickness(0, 14, 0, 12) };
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private readonly TextBox _search = new();
    private readonly StackPanel _rows = new();
    private readonly ScrollViewer _listScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly StackPanel _detail = new();
    private readonly TextBlock _newBadge = new() { FontFamily = Theme.BodyFont, FontSize = 11, Foreground = Theme.AccentBrush, Cursor = Cursors.Hand, Visibility = Visibility.Collapsed, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _pauseButton;
    private readonly DispatcherTimer _throttle = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(5) };

    private Tab _tab = Tab.All;
    private bool _problemsOnly;
    private string? _traceRequest;
    private DebugEvent? _selected;
    private ErrorGroup? _selectedGroup;
    private bool _pendingRows;

    public DebugDashboardWindow(PortalLink link, PortalDebugService service, Action<string, int>? openInEditor)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(service);
        _link = link;
        _service = service;
        _openInEditor = openInEditor;
        PortalId = link.Portal.Id;

        var area = SystemParameters.WorkArea;
        Width = Math.Min(1180, area.Width - 60);
        Height = Math.Min(760, area.Height - 60);
        MinWidth = 760;
        MinHeight = 480;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = $"Debug — {link.Portal.Name}";

        _pauseButton = HeaderButton("PAUSA", TogglePause);
        _frame.Child = BuildLayout();
        Content = _frame;
        GlassBackdrop.Attach(this, _frame);
        SourceInitialized += (_, _) => NativeMethods.AddExStyle(new WindowInteropHelper(this).Handle, WindowNative.WsExToolWindow);

        PreviewKeyDown += OnKey;
        _throttle.Tick += (_, _) =>
        {
            _throttle.Stop();
            Refresh(rows: true);
        };
        _clock.Tick += (_, _) => Refresh(rows: false);
        _link.Changed += OnChanged;
        _listScroll.ScrollChanged += (_, e) =>
        {
            if (e.VerticalOffset <= 1 && _pendingRows)
                Refresh(rows: true);
        };
        _newBadge.MouseLeftButtonUp += (_, _) =>
        {
            _listScroll.ScrollToTop();
            Refresh(rows: true);
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                _clock.Start();
            else
                _clock.Stop();
        };
        Closed += (_, _) =>
        {
            _link.Changed -= OnChanged;
            _throttle.Stop();
            _clock.Stop();
        };
        Refresh(rows: true);
    }

    public string PortalId { get; }

    /// <summary>Apre la dashboard (o la porta davanti); con <paramref name="focus"/> mostra subito quell'evento.</summary>
    public void Present(DebugEvent? focus = null)
    {
        if (focus is not null)
        {
            _tab = focus is DebugCrash ? Tab.Errors : Tab.All;
            _traceRequest = null;
            _search.Text = string.Empty;
            Select(focus);
        }
        Refresh(rows: true);
        if (!IsVisible)
        {
            Show();
            WindowNative.BringToFront(new WindowInteropHelper(this).EnsureHandle());
            Motion.Open(_frame);
        }
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Il contenuto com'è, senza mostrare la finestra (per <c>--snapshot</c>).</summary>
    public FrameworkElement PrepareSnapshot()
    {
        Motion.Suspended = true;
        Refresh(rows: true);
        _frame.Width = Width;
        _frame.Height = Height;
        return _frame;
    }

    // ── Struttura ───────────────────────────────────────────────────────────────

    private DockPanel BuildLayout()
    {
        var root = new DockPanel();

        var header = new DockPanel { Background = Brushes.Transparent };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        actions.Children.Add(_pauseButton);
        actions.Children.Add(HeaderButton("AGGIORNA", () =>
        {
            _link.RefreshStatusSoon();
            _ = _link.PollAsync();
        }));
        actions.Children.Add(HeaderButton("SVUOTA", () =>
        {
            _selected = null;
            _selectedGroup = null;
            _ = _service.ClearAsync(_link);
            Refresh(rows: true);
        }));
        actions.Children.Add(HeaderButton("APRI PORTALE", () => OpenUri(_link.Portal.Address)));
        var close = new TextBlock { Text = "×", FontFamily = Theme.BodyFont, FontSize = 22, Foreground = Theme.MutedBrush, Background = Brushes.Transparent, Cursor = Cursors.Hand, Padding = new Thickness(8, 0, 4, 4), Margin = new Thickness(8, -6, 0, 0), ToolTip = "Chiudi (Esc)" };
        close.MouseEnter += (_, _) => close.Foreground = Theme.CriticalBrush;
        close.MouseLeave += (_, _) => close.Foreground = Theme.MutedBrush;
        // la pressione va fermata qui: arrivando all'intestazione avvierebbe DragMove, che cattura il mouse fino al
        // rilascio, e il clic non arriverebbe mai (come nelle impostazioni)
        close.MouseLeftButtonDown += (_, e) => e.Handled = true;
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Close();
        };
        actions.Children.Add(close);
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);

        var titles = new StackPanel();
        var caption = new StackPanel { Orientation = Orientation.Horizontal };
        caption.Children.Add(_reachDot);
        caption.Children.Add(new DotMatrixView { Pitch = 2, Dots = DotText.Render("DEBUG"), DotColor = Theme.Accent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        caption.Children.Add(_title);
        titles.Children.Add(caption);
        _subtitle.Margin = new Thickness(16, 4, 0, 0);
        titles.Children.Add(_subtitle);
        header.Children.Add(titles);
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        DockPanel.SetDock(_kpis, Dock.Top);
        root.Children.Add(_kpis);

        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        var problems = new Border();
        void PaintProblems() => problems.Child = Chip("Solo problemi", _problemsOnly, () =>
        {
            _problemsOnly = !_problemsOnly;
            PaintProblems();
            Refresh(rows: true);
        });
        PaintProblems();
        tools.Children.Add(problems);
        tools.Children.Add(SearchBox());
        DockPanel.SetDock(tools, Dock.Right);
        toolbar.Children.Add(tools);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(_tabs);
        left.Children.Add(_newBadge);
        toolbar.Children.Add(left);
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star), MinWidth = 320 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 280 });
        _listScroll.Content = _rows;
        body.Children.Add(Box(_listScroll));
        var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetColumn(splitter, 1);
        body.Children.Add(splitter);
        var detailScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _detail, Padding = new Thickness(14, 10, 14, 10) };
        var detailBox = Box(detailScroll);
        Grid.SetColumn(detailBox, 2);
        body.Children.Add(detailBox);
        root.Children.Add(body);
        return root;
    }

    private Border SearchBox()
    {
        _search.Background = Brushes.Transparent;
        _search.BorderThickness = new Thickness(0);
        _search.Foreground = Theme.InkBrush;
        _search.CaretBrush = Theme.InkBrush;
        _search.FontFamily = Theme.BodyFont;
        _search.FontSize = 12;
        _search.Width = 200;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.ToolTip = "Cerca in percorsi, messaggi, query e file dello stack (Ctrl+F)";
        _search.TextChanged += (_, _) => Refresh(rows: true);
        var hint = Text("Cerca…  (Ctrl+F)", Theme.MutedBrush, 12);
        hint.IsHitTestVisible = false;
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.Margin = new Thickness(2, 0, 0, 0);
        _search.TextChanged += (_, _) => hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var layered = new Grid();
        layered.Children.Add(hint);
        layered.Children.Add(_search);
        var frame = new Border
        {
            Background = Theme.SurfaceBrush,
            BorderBrush = Theme.OutlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(8, 0, 0, 0),
            Child = layered,
        };
        _search.GotKeyboardFocus += (_, _) => frame.BorderBrush = Theme.AccentBrush;
        _search.LostKeyboardFocus += (_, _) => frame.BorderBrush = Theme.OutlineBrush;
        return frame;
    }

    private static Border Box(UIElement child) => new()
    {
        Background = Theme.CardBrush,
        BorderBrush = Theme.OutlineBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(Theme.RadiusLarge),
        Child = child,
    };

    // ── Aggiornamento ───────────────────────────────────────────────────────────

    private void OnChanged()
    {
        if (!_throttle.IsEnabled)
            _throttle.Start();
    }

    private void Refresh(bool rows)
    {
        var portal = _link.Portal;
        _title.Text = portal.Name;
        var info = _link.Status?.Info;
        var parts = new List<string> { portal.Address };
        if (info?.Framework is { } framework)
            parts.Add(framework);
        if (info?.Pid is { } pid)
            parts.Add($"pid {pid.ToString(CultureInfo.InvariantCulture)}");
        if (_link.Log.Restarts > 0)
            parts.Add(_link.Log.Restarts == 1 ? "1 riavvio" : $"{_link.Log.Restarts.ToString(CultureInfo.InvariantCulture)} riavvii");
        parts.Add(ReachText());
        _subtitle.Text = string.Join("  ·  ", parts);
        _reachDot.Fill = _link.Reach switch
        {
            PortalReach.Online => new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C)),
            PortalReach.Unknown => Theme.MutedBrush,
            _ => Theme.CriticalBrush,
        };
        _pauseButton.Text = _link.Paused ? "RIPRENDI" : "PAUSA";
        BuildKpis();
        BuildTabs();
        if (!rows)
            return;
        if (_listScroll.VerticalOffset > 1 && _tab is not (Tab.Routes or Tab.Checks))
        {
            _pendingRows = true;
            _newBadge.Text = "NUOVI EVENTI ↑";
            _newBadge.Visibility = Visibility.Visible;
            return;
        }
        _pendingRows = false;
        _newBadge.Visibility = Visibility.Collapsed;
        BuildRows();
        BuildDetail();
    }

    private string ReachText() => _link.Paused ? "in pausa" : _link.Reach switch
    {
        PortalReach.Online => _link.LastContact is { } t ? $"collegato · {t.ToLocalTime():HH:mm:ss}" : "collegato",
        PortalReach.Unknown => "collegamento…",
        _ => _link.Problem ?? "non risponde",
    };

    private void BuildKpis()
    {
        var s = _link.Log.Stats(DateTimeOffset.Now, Window5);
        _kpis.Children.Clear();
        _kpis.Children.Add(Kpi(s.RequestsPerMinute.ToString(s.RequestsPerMinute < 10 ? "0.#" : "0", CultureInfo.InvariantCulture), "richieste/min", false));
        _kpis.Children.Add(Kpi(s.ServerErrors.ToString(CultureInfo.InvariantCulture), s.Requests == 0 ? "risposte 5xx" : $"5xx · {(s.ServerErrorRate * 100).ToString("0.#", CultureInfo.InvariantCulture)}%", s.ServerErrors > 0));
        _kpis.Children.Add(Kpi(s.Exceptions.ToString(CultureInfo.InvariantCulture), "eccezioni", s.Exceptions > 0));
        _kpis.Children.Add(Kpi(s.ClientErrors.ToString(CultureInfo.InvariantCulture), "risposte 4xx", false));
        _kpis.Children.Add(Kpi(s.Requests == 0 ? "—" : DebugWatch.FormatMs(s.P95Ms), s.Requests == 0 ? "tempo al 95%" : $"p95 · mediana {DebugWatch.FormatMs(s.P50Ms)}", s.P95Ms >= DebugAlertRules.Default.SlowMs));
        _kpis.Children.Add(Kpi(s.Requests == 0 ? "—" : s.SqlPerRequest.ToString("0.#", CultureInfo.InvariantCulture), "query/richiesta", s.SqlPerRequest >= 30));
        _kpis.Children.Add(Kpi(s.Warnings.ToString(CultureInfo.InvariantCulture), "warning", false));
        var failing = _link.Status?.Checks.Count(c => c.State != CheckState.Ok) ?? 0;
        _kpis.Children.Add(Kpi(_link.Status is null ? "—" : failing.ToString(CultureInfo.InvariantCulture), "controlli da guardare", failing > 0));
    }

    private static Border Kpi(string value, string label, bool bad)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = value, FontFamily = Theme.DisplayFont, FontSize = 22, Foreground = bad ? Theme.CriticalBrush : Theme.InkBrush });
        stack.Children.Add(new TextBlock { Text = label, FontFamily = Theme.BodyFont, FontSize = 10, Foreground = Theme.MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis });
        return new Border
        {
            Background = Theme.SurfaceBrush,
            BorderBrush = bad ? Theme.CriticalBrush : Theme.OutlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Ultimi 5 minuti",
            Child = stack,
        };
    }

    private void BuildTabs()
    {
        _tabs.Children.Clear();
        foreach (var (tab, title) in Tabs)
        {
            var count = tab switch
            {
                Tab.Errors => _link.Log.ErrorGroups.Count,
                Tab.Checks => _link.Status?.Checks.Count(c => c.State != CheckState.Ok) ?? 0,
                _ => 0,
            };
            var label = count > 0 ? $"{title} {count.ToString(CultureInfo.InvariantCulture)}" : title;
            _tabs.Children.Add(Chip(label, tab == _tab, () =>
            {
                _tab = tab;
                _traceRequest = null;
                _listScroll.ScrollToTop();
                Refresh(rows: true);
            }));
        }
    }

    private void BuildRows()
    {
        _rows.Children.Clear();
        switch (_tab)
        {
            case Tab.Routes:
                RouteRows();
                return;
            case Tab.Checks:
                CheckRows();
                return;
            case Tab.Errors when _traceRequest is null:
                ErrorGroupRows();
                return;
        }
        if (_traceRequest is { } rid)
        {
            _rows.Children.Add(Banner($"Traccia della richiesta {rid}", "TORNA ALL'ELENCO", () =>
            {
                _traceRequest = null;
                Refresh(rows: true);
            }));
        }
        var kind = _tab switch
        {
            Tab.Requests => DebugEventKind.Request,
            Tab.Logs => DebugEventKind.Log,
            Tab.Sql => DebugEventKind.Sql,
            Tab.Custom => DebugEventKind.Custom,
            Tab.Errors => DebugEventKind.Error,
            _ => (DebugEventKind?)null,
        };
        var events = _traceRequest is { } trace
            ? _link.Log.Trace(trace).Reverse().ToList()
            : _link.Log.Find(new DebugFilter(kind, _search.Text.Trim(), _problemsOnly), MaxRows);
        if (events.Count == 0)
        {
            _rows.Children.Add(Empty(_link.Log.Count == 0
                ? _link.Reach == PortalReach.Online ? "Collegato. Apri una pagina del portale e qui compaiono richieste, log ed errori." : ReachText()
                : "Niente con questo filtro."));
            return;
        }
        foreach (var e in events)
            _rows.Children.Add(EventRow(e));
    }

    private Border EventRow(DebugEvent e)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var (tag, tagBrush, text, right) = e switch
        {
            DebugRequest r => (r.Method, Theme.MutedBrush, r.Path + (r.QueryString is { Length: > 0 } q ? "?" + q : string.Empty),
                $"{r.Status.ToString(CultureInfo.InvariantCulture)}  {DebugWatch.FormatMs(r.DurationMs)}"),
            DebugLogLine l => (l.Level.Length > 5 ? l.Level[..4] : l.Level, l.Severity >= 40 ? Theme.CriticalBrush : l.Severity >= 30 ? Theme.AccentBrush : Theme.MutedBrush,
                $"{l.Logger}  {FirstLine(l.Message)}", string.Empty),
            DebugCrash x => ("ERR", Theme.CriticalBrush, x.Summary, x.Culprit is { } c ? $"{DebugWatch.ShortFile(c.File)}:{c.Line.ToString(CultureInfo.InvariantCulture)}" : string.Empty),
            DebugSql s => ("SQL", s.DurationMs >= 100 ? Theme.AccentBrush : Theme.MutedBrush, OneLine(s.Sql), DebugWatch.FormatMs(s.DurationMs)),
            DebugCustom c => ("EVT", Theme.InkBrush, c.Summary, string.Empty),
            _ => ("?", Theme.MutedBrush, e.Summary, string.Empty),
        };
        var rightBrush = e is DebugRequest { Status: >= 500 } ? Theme.CriticalBrush
            : e is DebugRequest { IsClientError: true } ? Theme.AccentBrush
            : e is DebugRequest { DurationMs: >= 1000 } ? Theme.AccentBrush
            : Theme.MutedBrush;
        AddCell(grid, 0, e.Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), Theme.MutedBrush, mono: true);
        AddCell(grid, 1, tag, tagBrush, mono: true);
        AddCell(grid, 2, text, Theme.InkBrush, mono: e is DebugSql);
        AddCell(grid, 3, right, rightBrush, mono: true);
        return RowFrame(grid, ReferenceEquals(e, _selected) || (e.Seq == _selected?.Seq && e.Kind == _selected?.Kind), () =>
        {
            _selectedGroup = null;
            Select(e);
            BuildRows();
            BuildDetail();
        });
    }

    private void ErrorGroupRows()
    {
        var text = _search.Text.Trim();
        var groups = _link.Log.ErrorGroups
            .Where(g => text.Length == 0 || new DebugFilter(Text: text).Matches(g.Latest))
            .OrderByDescending(g => g.Last)
            .ToList();
        if (groups.Count == 0)
        {
            _rows.Children.Add(Empty("Nessun errore. Quando il portale ne solleva uno, qui c'è una riga per ogni errore diverso."));
            return;
        }
        foreach (var g in groups)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            AddCell(grid, 0, $"×{g.Count.ToString(CultureInfo.InvariantCulture)}", Theme.CriticalBrush, mono: true);
            var where = g.Latest.Culprit is { } c ? $"{DebugWatch.ShortFile(c.File)}:{c.Line.ToString(CultureInfo.InvariantCulture)} in {c.Function}" : g.Latest.Path ?? string.Empty;
            var stack = new StackPanel();
            stack.Children.Add(Text(g.Latest.Summary, Theme.InkBrush, 12));
            stack.Children.Add(Text(where, Theme.MutedBrush, 11, mono: true));
            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);
            AddCell(grid, 2, Ago(g.Last), Theme.MutedBrush, mono: false);
            _rows.Children.Add(RowFrame(grid, _selectedGroup?.Fingerprint == g.Fingerprint, () =>
            {
                _selectedGroup = g;
                Select(g.Latest);
                BuildRows();
                BuildDetail();
            }));
        }
    }

    private void RouteRows()
    {
        var routes = _link.Log.Routes(DateTimeOffset.Now, TimeSpan.FromMinutes(30), 50);
        if (routes.Count == 0)
        {
            _rows.Children.Add(Empty("Nessuna richiesta negli ultimi 30 minuti."));
            return;
        }
        _rows.Children.Add(RouteLine("PERCORSO (ultimi 30 min)", "N", "5XX", "MEDIA", "P95", "MAX", header: true));
        foreach (var r in routes)
            _rows.Children.Add(RouteLine(r.Route, r.Count.ToString(CultureInfo.InvariantCulture), r.ServerErrors.ToString(CultureInfo.InvariantCulture),
                DebugWatch.FormatMs(r.AverageMs), DebugWatch.FormatMs(r.P95Ms), DebugWatch.FormatMs(r.MaxMs), header: false, bad: r.ServerErrors > 0, slow: r.P95Ms >= 1000,
                click: () =>
                {
                    _tab = Tab.Requests;
                    _search.Text = r.Route.Split(' ', 2)[^1].Replace("{id}", string.Empty, StringComparison.Ordinal).TrimEnd('/');
                    _listScroll.ScrollToTop();
                    Refresh(rows: true);
                }));
    }

    private static Border RouteLine(string route, string count, string errors, string avg, string p95, string max, bool header, bool bad = false, bool slow = false, Action? click = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var width in new[] { 44, 44, 70, 70, 70 })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        var muted = header ? Theme.MutedBrush : Theme.InkBrush;
        AddCell(grid, 0, route, muted, mono: !header);
        AddCell(grid, 1, count, muted, mono: true);
        AddCell(grid, 2, errors, bad ? Theme.CriticalBrush : muted, mono: true);
        AddCell(grid, 3, avg, muted, mono: true);
        AddCell(grid, 4, p95, slow ? Theme.AccentBrush : muted, mono: true);
        AddCell(grid, 5, max, muted, mono: true);
        return header ? new Border { Padding = new Thickness(12, 8, 12, 4), Child = grid } : RowFrame(grid, false, click ?? (() => { }));
    }

    private void CheckRows()
    {
        if (_link.Status is not { } status)
        {
            _rows.Children.Add(Empty("I controlli arrivano dal portale ogni 15 secondi."));
            return;
        }
        if (status.Checks.Count == 0)
            _rows.Children.Add(Empty("Il portale non ha controlli. Aggiungili con @nothing_debug.check(\"Nome\")."));
        foreach (var check in status.Checks)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var brush = check.State switch
            {
                CheckState.Failing => Theme.CriticalBrush,
                CheckState.Warning => Theme.AccentBrush,
                _ => new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C)),
            };
            var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = brush, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(dot);
            var stack = new StackPanel();
            stack.Children.Add(Text(check.Name, Theme.InkBrush, 13));
            if (check.Detail is { Length: > 0 } detail)
                stack.Children.Add(Text(detail, Theme.MutedBrush, 11));
            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);
            AddCell(grid, 2, check.Value, check.State == CheckState.Ok ? Theme.InkBrush : brush, mono: true);
            _rows.Children.Add(RowFrame(grid, false, () => Clipboard.SetText($"{check.Name}: {check.Value}")));
        }
        var info = status.Info;
        var facts = new List<string>();
        if (info.UptimeSeconds is { } up)
            facts.Add($"acceso da {Duration(TimeSpan.FromSeconds(up))}");
        if (info.DebugMode is { } debug)
            facts.Add(debug ? "DEBUG = True" : "DEBUG = False");
        if (info.Root is { } root)
            facts.Add(root);
        if (facts.Count > 0)
            _rows.Children.Add(Empty(string.Join("  ·  ", facts)));
    }

    // ── Dettaglio ───────────────────────────────────────────────────────────────

    private void Select(DebugEvent e) => _selected = e;

    private void BuildDetail()
    {
        _detail.Children.Clear();
        if (_selected is not { } e)
        {
            DetailTitle("Scegli una riga", Theme.MutedBrush);
            DetailNote("Richieste, log, errori e query del portale arrivano qui da soli. Un clic su una riga mostra il dettaglio " +
                       "e tutto quello che è successo nella stessa richiesta.");
            return;
        }
        switch (e)
        {
            case DebugRequest r:
                RequestDetail(r);
                break;
            case DebugCrash x:
                CrashDetail(x);
                break;
            case DebugLogLine l:
                DetailTitle($"{l.Level} · {l.Logger}", l.Severity >= 40 ? Theme.CriticalBrush : Theme.InkBrush);
                Facts(("Ora", Clock(l.Time)));
                Block(l.Message);
                Buttons(("COPIA", () => Clipboard.SetText(l.Message)));
                break;
            case DebugSql s:
                DetailTitle($"Query · {DebugWatch.FormatMs(s.DurationMs)}", s.DurationMs >= 100 ? Theme.AccentBrush : Theme.InkBrush);
                Facts(("Ora", Clock(s.Time)), ("Database", s.Database ?? "default"));
                Block(s.Sql);
                Buttons(("COPIA", () => Clipboard.SetText(s.Sql)));
                break;
            case DebugCustom c:
                DetailTitle(c.Name, Theme.InkBrush);
                Facts([("Ora", Clock(c.Time)), .. c.Data.Select(d => (d.Key, d.Value))]);
                break;
        }
        if (e.RequestId is { } rid && e is not DebugRequest)
            TraceSection(rid, e);
    }

    private void RequestDetail(DebugRequest r)
    {
        DetailTitle($"{r.Method} {r.Path}", r.Status >= 500 ? Theme.CriticalBrush : Theme.InkBrush);
        var facts = new List<(string, string)>
        {
            ("Risposta", r.Status.ToString(CultureInfo.InvariantCulture)),
            ("Tempo", DebugWatch.FormatMs(r.DurationMs)),
            ("Query", r.SqlCount == 0 ? "nessuna" : $"{r.SqlCount.ToString(CultureInfo.InvariantCulture)} · {DebugWatch.FormatMs(r.SqlMs)}"),
            ("Ora", Clock(r.Time)),
        };
        if (r.View is { } view)
            facts.Add(("Vista", view));
        if (r.User is { } user)
            facts.Add(("Utente", user));
        if (r.Size is { } size)
            facts.Add(("Dimensione", Bytes(size)));
        if (r.ClientIp is { } ip)
            facts.Add(("Da", ip));
        if (r.QueryString is { Length: > 0 } q)
            facts.Add(("Parametri", q));
        Facts([.. facts]);
        if (r.SqlCount >= 30)
            DetailNote($"{r.SqlCount.ToString(CultureInfo.InvariantCulture)} query per una pagina: probabile N+1 (select_related / prefetch_related).", Theme.AccentBrush);
        Buttons(("COPIA cURL", () => Clipboard.SetText(Curl(r))), ("APRI", () => OpenUri(_link.Portal.Address + r.Path + (r.QueryString is { Length: > 0 } qs ? "?" + qs : string.Empty))));
        if (r.RequestId is { } rid)
            TraceSection(rid, r);
        if (r.Body is { Length: > 0 } body)
        {
            DetailHeading("CORPO");
            Block(body);
        }
        if (r.RequestHeaders is { Count: > 0 } headers)
        {
            DetailHeading("INTESTAZIONI DELLA RICHIESTA");
            Facts([.. headers.Select(h => (h.Key, h.Value))]);
        }
        if (r.ResponseHeaders is { Count: > 0 } response)
        {
            DetailHeading("INTESTAZIONI DELLA RISPOSTA");
            Facts([.. response.Select(h => (h.Key, h.Value))]);
        }
    }

    private void CrashDetail(DebugCrash x)
    {
        DetailTitle(x.Type, Theme.CriticalBrush);
        Block(x.Message.Length == 0 ? "(nessun messaggio)" : x.Message);
        var facts = new List<(string, string)> { ("Ora", Clock(x.Time)) };
        if (x.Path is { } path)
            facts.Add(("Richiesta", $"{x.Method} {path}"));
        if (_link.Log.ErrorGroups.FirstOrDefault(g => g.Fingerprint == x.Fingerprint) is { } group)
            facts.Add(("Volte", group.Count == 1 ? "la prima" : $"{group.Count.ToString(CultureInfo.InvariantCulture)} · prima {Clock(group.First)}"));
        Facts([.. facts]);
        Buttons(("COPIA STACK", () => Clipboard.SetText(StackText(x))));
        DetailHeading("STACK · dall'esterno all'errore");
        var local = PortalAddress.IsLocal(_link.Portal.Address) && _openInEditor is not null;
        foreach (var f in x.Frames)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 3, 0, 3), Opacity = f.InApp ? 1 : 0.55 };
            var where = Text($"{(f.InApp ? DebugWatch.ShortFile(f.File) : f.File)}:{f.Line.ToString(CultureInfo.InvariantCulture)}  in {f.Function}",
                f.InApp ? Theme.InkBrush : Theme.MutedBrush, 11, mono: true);
            where.TextWrapping = TextWrapping.Wrap;
            stack.Children.Add(where);
            if (f.Code is { Length: > 0 } code)
            {
                var line = Text("    " + code.Trim(), f == x.Culprit ? Theme.CriticalBrush : Theme.MutedBrush, 11, mono: true);
                line.TextWrapping = TextWrapping.Wrap;
                stack.Children.Add(line);
            }
            if (local && File.Exists(f.File))
            {
                stack.Cursor = Cursors.Hand;
                stack.ToolTip = "Apri nell'editor";
                var frame = f;
                stack.MouseLeftButtonUp += (_, _) => _openInEditor!(frame.File, frame.Line);
            }
            _detail.Children.Add(stack);
        }
    }

    /// <summary>Gli altri eventi della stessa richiesta: cosa è successo prima e dopo.</summary>
    private void TraceSection(string requestId, DebugEvent current)
    {
        var trace = _link.Log.Trace(requestId);
        if (trace.Count <= 1)
            return;
        DetailHeading($"NELLA STESSA RICHIESTA · {trace.Count.ToString(CultureInfo.InvariantCulture)}");
        foreach (var e in trace.Take(60))
        {
            var mine = ReferenceEquals(e, current);
            var line = Text($"{Prefix(e)}  {OneLine(e is DebugLogLine l ? $"{l.Logger}: {l.Message}" : e.Summary)}", mine ? Theme.AccentBrush : e.IsProblem ? Theme.CriticalBrush : Theme.InkBrush, 11, mono: true);
            line.TextTrimming = TextTrimming.CharacterEllipsis;
            line.Margin = new Thickness(0, 2, 0, 2);
            if (!mine)
            {
                line.Cursor = Cursors.Hand;
                line.MouseLeftButtonUp += (_, _) =>
                {
                    Select(e);
                    BuildDetail();
                };
            }
            _detail.Children.Add(line);
        }
        Buttons(("MOSTRA NELL'ELENCO", () =>
        {
            _traceRequest = requestId;
            _tab = Tab.All;
            _listScroll.ScrollToTop();
            Refresh(rows: true);
        }));
    }

    private void DetailTitle(string text, Brush brush)
    {
        var title = Text(text, brush, 16);
        title.TextWrapping = TextWrapping.Wrap;
        title.Margin = new Thickness(0, 0, 0, 8);
        _detail.Children.Add(title);
    }

    private void DetailHeading(string text)
    {
        var heading = Text(text, Theme.MutedBrush, 10);
        heading.Margin = new Thickness(0, 16, 0, 6);
        _detail.Children.Add(heading);
    }

    private void DetailNote(string text, Brush? brush = null)
    {
        var note = Text(text, brush ?? Theme.MutedBrush, 12);
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(0, 4, 0, 4);
        _detail.Children.Add(note);
    }

    private void Facts(params (string Key, string Value)[] facts)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < facts.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = Text(facts[i].Key, Theme.MutedBrush, 11);
            key.Margin = new Thickness(0, 2, 8, 2);
            key.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetRow(key, i);
            grid.Children.Add(key);
            var value = new TextBox
            {
                Text = facts[i].Value,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Theme.InkBrush,
                CaretBrush = Theme.InkBrush,
                FontFamily = Mono,
                FontSize = 11,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 2, 0, 2),
            };
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
        }
        _detail.Children.Add(grid);
    }

    private void Block(string text) => _detail.Children.Add(new Border
    {
        Background = Theme.SurfaceBrush,
        BorderBrush = Theme.OutlineBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(10, 8, 10, 8),
        Margin = new Thickness(0, 4, 0, 6),
        Child = new TextBox
        {
            Text = text.Length > 20000 ? text[..20000] + "…" : text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Theme.InkBrush,
            CaretBrush = Theme.InkBrush,
            FontFamily = Mono,
            FontSize = 11,
        },
    });

    private void Buttons(params (string Text, Action Click)[] buttons)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        foreach (var (text, click) in buttons)
        {
            var label = Text(text, Theme.InkBrush, 12);
            var button = new Border
            {
                BorderBrush = Theme.OutlineBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 4, 10, 5),
                Margin = new Thickness(0, 0, 6, 0),
                Background = Theme.SurfaceBrush,
                Cursor = Cursors.Hand,
                Child = label,
            };
            button.MouseEnter += (_, _) => button.Background = Theme.SurfaceHoverBrush;
            button.MouseLeave += (_, _) => button.Background = Theme.SurfaceBrush;
            button.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                click();
            };
            row.Children.Add(button);
        }
        _detail.Children.Add(row);
    }

    // ── Mattoni ─────────────────────────────────────────────────────────────────

    private static Border RowFrame(FrameworkElement content, bool selected, Action click)
    {
        var row = new Border
        {
            Padding = new Thickness(12, 6, 12, 6),
            Background = selected ? Theme.SurfaceHoverBrush : Brushes.Transparent,
            BorderBrush = Theme.OutlineBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Cursor = Cursors.Hand,
            Child = content,
        };
        if (!selected)
        {
            row.MouseEnter += (_, _) => row.Background = Theme.SurfaceBrush;
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        }
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return row;
    }

    private static Border Banner(string text, string action, Action click)
    {
        var grid = new DockPanel();
        var button = Text(action, Theme.AccentBrush, 11);
        button.Cursor = Cursors.Hand;
        button.MouseLeftButtonUp += (_, _) => click();
        DockPanel.SetDock(button, Dock.Right);
        grid.Children.Add(button);
        grid.Children.Add(Text(text, Theme.MutedBrush, 11));
        return new Border { Padding = new Thickness(12, 8, 12, 8), BorderBrush = Theme.OutlineBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
    }

    private static TextBlock Empty(string text)
    {
        var note = Text(text, Theme.MutedBrush, 12);
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(14, 14, 14, 14);
        return note;
    }

    private static void AddCell(Grid grid, int column, string text, Brush brush, bool mono)
    {
        var cell = Text(text, brush, mono ? 11.5 : 12, mono);
        cell.TextTrimming = TextTrimming.CharacterEllipsis;
        cell.VerticalAlignment = VerticalAlignment.Center;
        cell.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private static TextBlock Text(string text, Brush brush, double size, bool mono = false) => new()
    {
        Text = text,
        FontFamily = mono ? Mono : Theme.BodyFont,
        FontSize = size,
        Foreground = brush,
    };

    private static Border Chip(string text, bool selected, Action click)
    {
        var chip = new Border
        {
            BorderBrush = selected ? Theme.AccentBrush : Theme.OutlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 4, 10, 5),
            Margin = new Thickness(0, 0, 6, 0),
            Background = selected ? Theme.AccentBrush : Theme.SurfaceBrush,
            Cursor = Cursors.Hand,
            Child = Text(text, selected ? Theme.OnAccentBrush : Theme.InkBrush, 12),
        };
        chip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return chip;
    }

    private static TextBlock HeaderButton(string text, Action click)
    {
        var button = Text(text, Theme.MutedBrush, 11);
        button.Cursor = Cursors.Hand;
        button.Margin = new Thickness(14, 2, 0, 0);
        button.MouseEnter += (_, _) => button.Foreground = Theme.InkBrush;
        button.MouseLeave += (_, _) => button.Foreground = Theme.MutedBrush;
        button.MouseLeftButtonDown += (_, e) => e.Handled = true; // niente DragMove dell'intestazione
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return button;
    }

    private void TogglePause()
    {
        _link.Paused = !_link.Paused;
        Refresh(rows: false);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _search.Focus();
            _search.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_search.IsKeyboardFocused && _search.Text.Length > 0)
                _search.Text = string.Empty;
            else if (_traceRequest is not null)
            {
                _traceRequest = null;
                Refresh(rows: true);
            }
            else
                Close();
            e.Handled = true;
        }
    }

    // ── Testi ───────────────────────────────────────────────────────────────────

    private string Curl(DebugRequest r)
    {
        var url = _link.Portal.Address + r.Path + (r.QueryString is { Length: > 0 } q ? "?" + q : string.Empty);
        var builder = new StringBuilder($"curl -X {r.Method} \"{url}\"");
        foreach (var (key, value) in r.RequestHeaders ?? new Dictionary<string, string>())
        {
            if (value == "[nascosto]" || key.Equals("Host", StringComparison.OrdinalIgnoreCase) || key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;
            builder.Append(CultureInfo.InvariantCulture, $" -H \"{key}: {value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"");
        }
        if (r.Body is { Length: > 0 } body)
            builder.Append(CultureInfo.InvariantCulture, $" --data-raw '{body.Replace("'", "'\\''", StringComparison.Ordinal)}'");
        return builder.ToString();
    }

    private static string StackText(DebugCrash x)
    {
        var builder = new StringBuilder("Traceback (most recent call last):\n");
        foreach (var f in x.Frames)
        {
            builder.Append(CultureInfo.InvariantCulture, $"  File \"{f.File}\", line {f.Line}, in {f.Function}\n");
            if (f.Code is { Length: > 0 } code)
                builder.Append(CultureInfo.InvariantCulture, $"    {code.Trim()}\n");
        }
        builder.Append(x.Summary);
        return builder.ToString();
    }

    private static string Prefix(DebugEvent e) => e switch
    {
        DebugRequest => "REQ",
        DebugLogLine l => l.Level.Length > 4 ? l.Level[..4] : l.Level,
        DebugCrash => "ERR",
        DebugSql => "SQL",
        _ => "EVT",
    };

    private static string FirstLine(string text) => text.Split('\n', 2)[0];

    private static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Clock(DateTimeOffset time) => time.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static string Ago(DateTimeOffset time)
    {
        var ago = DateTimeOffset.Now - time;
        return ago.TotalSeconds < 60 ? "adesso" : $"{Duration(ago)} fa";
    }

    private static string Duration(TimeSpan span) => span.TotalMinutes < 1 ? $"{span.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s"
        : span.TotalHours < 1 ? $"{span.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} min"
        : $"{span.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} h";

    private static string Bytes(long size) => size < 1024 ? $"{size.ToString(CultureInfo.InvariantCulture)} B"
        : size < 1024 * 1024 ? $"{(size / 1024.0).ToString("0.#", CultureInfo.InvariantCulture)} KB"
        : $"{(size / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture)} MB";

    private static void OpenUri(string uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // nessun browser predefinito
        }
    }
}
