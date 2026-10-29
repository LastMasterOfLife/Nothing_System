using System.IO;
using System.Windows;
using System.Windows.Controls;
using NothingShell.App.Services;
using NothingShell.Core.Connectors;
using NothingShell.Core.Notch;
using NothingShell.Core.Widgets;

namespace NothingShell.App.Widgets;

/// <summary>
/// Crea i widget (integrati e dei plugin), li collega ai servizi, ne gestisce visibilità e posizioni
/// (docs/04-widgets.md, docs/07-settings-plugins.md).
/// </summary>
public sealed class WidgetHost
{
    private readonly ClockService _clock;
    private readonly SystemMonitor _monitor;
    private readonly WidgetSettingsStore _store;
    private readonly Dictionary<string, WidgetWindow> _windows = new(StringComparer.Ordinal);
    /// <summary>Posizioni dei widget di plugin (per id del plugin) e connettori (per id del widget).</summary>
    private readonly Dictionary<string, (double X, double Y)> _pluginPositions = new(StringComparer.Ordinal);
    private Func<string, bool> _isEnabled = _ => true;
    private bool _locked;

    public WidgetHost(ClockService clock, SystemMonitor monitor, WidgetSettingsStore store)
    {
        _clock = clock;
        _monitor = monitor;
        _store = store;
    }

    public event Action? SettingsRequested;

    public bool Locked => _locked;

    /// <summary>Widget esistenti (id, nome leggibile) nell'ordine di creazione.</summary>
    public IEnumerable<(string Id, string Name)> Widgets =>
        _windows.Values.Select(w => (w.View.Id, DisplayName(w.View)));

    private static Rect2 WorkArea
    {
        get
        {
            var r = SystemParameters.WorkArea;
            return new Rect2(r.X, r.Y, r.Width, r.Height);
        }
    }

    private static Rect2 VirtualScreen => new(
        SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

    /// <summary>Crea le viste integrate e le collega ai servizi; non mostra nulla. Usato anche dallo snapshot.</summary>
    public static IReadOnlyList<WidgetView> CreateViews(ClockService clock, SystemMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(monitor);
        var clockView = new ClockWidget();
        var battery = new BatteryWidget();
        var system = new SystemWidget();
        var calendar = new CalendarWidget();

        clock.MinuteChanged += clockView.Update;
        clock.MinuteChanged += calendar.Update;
        monitor.BatteryChanged += battery.Update;
        monitor.StatsChanged += system.Update;
        return [clockView, battery, system, calendar];
    }

    public void Show(Func<string, bool> isEnabled)
    {
        ArgumentNullException.ThrowIfNull(isEnabled);
        _isEnabled = isEnabled;
        var settings = _store.Load();
        _locked = settings.Locked;
        foreach (var p in settings.Plugins ?? [])
            _pluginPositions[p.Id] = (p.X, p.Y);

        var placements = WidgetLayout.Normalize(settings.Widgets, WorkArea, VirtualScreen).ToDictionary(p => p.Kind);
        foreach (var view in CreateViews(_clock, _monitor))
        {
            var placement = placements[view.Kind!.Value];
            AddWindow(view, placement.X, placement.Y);
        }
    }

    /// <summary>Aggiunge il widget di un plugin: posizione salvata oppure primo spazio libero.</summary>
    public void AddPlugin(PluginWidget view)
    {
        ArgumentNullException.ThrowIfNull(view);
        AddPlaced(view);
    }

    /// <summary>
    /// Allinea i widget dei connettori a quelli attivi: crea i nuovi (visibili solo se accesi nelle impostazioni),
    /// aggiorna i valori, chiude quelli dei connettori tolti o spenti.
    /// </summary>
    public void SyncConnectors(IReadOnlyList<ConnectorState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        var ids = states.Select(s => ConnectorWidgetLayout.WidgetId(s.Manifest.Id)).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, window) in _windows.Where(w => w.Value.View is ConnectorWidget && !ids.Contains(w.Key)).ToArray())
        {
            window.Close();
            _windows.Remove(id);
        }
        foreach (var state in states)
        {
            if (_windows.TryGetValue(ConnectorWidgetLayout.WidgetId(state.Manifest.Id), out var window) && window.View is ConnectorWidget existing)
            {
                existing.Update(state);
                continue;
            }
            var view = new ConnectorWidget(state.Manifest);
            view.Update(state);
            view.Resized += OnConnectorResized;
            AddPlaced(view);
        }
    }

    /// <summary>Aggiorna "da quanto" dei widget dei connettori (ogni minuto).</summary>
    public void RefreshConnectors()
    {
        foreach (var w in _windows.Values)
            if (w.View is ConnectorWidget c)
                c.Refresh();
    }

    private void AddPlaced(WidgetView view)
    {
        var key = PlacementKey(view)!;
        var size = new Size2(view.Width, view.Height);
        var (x, y) = _pluginPositions.TryGetValue(key, out var saved)
            ? WidgetLayout.Settle(new Rect2(saved.X, saved.Y, size.Width, size.Height), VirtualScreen)
            : WidgetLayout.FindFreeSlot(size, OccupiedRects(), WorkArea);
        AddWindow(view, x, y);
        _pluginPositions[key] = (x, y);
    }

    private void OnConnectorResized(ConnectorWidget view)
    {
        if (!_windows.TryGetValue(view.Id, out var window))
            return;
        window.Width = view.Width;
        window.Height = view.Height;
        var (x, y) = WidgetLayout.Settle(Bounds(window), VirtualScreen);
        Move(window, x, y);
    }

    /// <summary>Chiave della posizione salvata per i widget senza posto fisso; null per quelli integrati.</summary>
    private static string? PlacementKey(WidgetView view) => view switch
    {
        PluginWidget p => p.Manifest.Id,
        ConnectorWidget c => c.Id,
        _ => null,
    };

    /// <summary>Chiude tutti i widget dei plugin (prima di una ricarica). Le posizioni restano salvate.</summary>
    public void RemovePlugins()
    {
        foreach (var (id, window) in _windows.Where(w => w.Value.View is PluginWidget).ToArray())
        {
            window.Close();
            _windows.Remove(id);
        }
    }

    public void SetVisible(string id, bool visible)
    {
        if (!_windows.TryGetValue(id, out var window))
            return;
        if (visible)
        {
            // un widget di plugin o connettore acceso adesso non deve finire sopra un altro già visibile
            if (PlacementKey(window.View) is not null && !window.IsVisible && OccupiedRects().Any(Bounds(window).IntersectsWith))
            {
                var (x, y) = WidgetLayout.FindFreeSlot(new Size2(window.Width, window.Height), OccupiedRects(), WorkArea);
                Move(window, x, y);
                Save();
            }
            window.Show();
        }
        else
            window.Hide();
    }

    public void SetLocked(bool locked)
    {
        _locked = locked;
        foreach (var w in _windows.Values)
            w.Locked = locked;
        Save();
    }

    public void ResetLayout()
    {
        var defaults = WidgetLayout.Default(WorkArea).ToDictionary(p => p.Kind);
        foreach (var w in _windows.Values.Where(w => w.View.Kind is not null))
            Move(w, defaults[w.View.Kind!.Value].X, defaults[w.View.Kind!.Value].Y);

        _pluginPositions.Clear();
        var occupied = _windows.Values.Where(w => w.View.Kind is not null).Select(Bounds).ToList();
        foreach (var w in _windows.Values.Where(w => w.View.Kind is null).OrderByDescending(w => w.IsVisible).ToArray())
        {
            var (x, y) = WidgetLayout.FindFreeSlot(new Size2(w.Width, w.Height), occupied, WorkArea);
            Move(w, x, y);
            occupied.Add(Bounds(w));
        }
        Save();
    }

    private void AddWindow(WidgetView view, double x, double y)
    {
        var window = new WidgetWindow(view) { Left = x, Top = y, Locked = _locked };
        window.DragCompleted += OnDragCompleted;
        window.ContextMenu = BuildContextMenu();
        _windows[view.Id] = window;
        if (_isEnabled(view.Id))
            window.Show();
    }

    private IEnumerable<Rect2> OccupiedRects() => _windows.Values.Where(w => w.IsVisible).Select(Bounds);

    private static Rect2 Bounds(WidgetWindow w) => new(w.Left, w.Top, w.Width, w.Height);

    private void Move(WidgetWindow window, double x, double y)
    {
        window.Left = x;
        window.Top = y;
        if (PlacementKey(window.View) is { } key)
            _pluginPositions[key] = (x, y);
    }

    private void OnDragCompleted(WidgetWindow window)
    {
        var (x, y) = WidgetLayout.Settle(Bounds(window), VirtualScreen);
        Move(window, x, y);
        Save();
    }

    private void Save()
    {
        var builtIns = _windows.Values
            .Where(w => w.View.Kind is not null)
            .Select(w => new WidgetPlacement(w.View.Kind!.Value, w.Left, w.Top))
            .ToArray();
        var plugins = _pluginPositions.Select(p => new PluginPlacement(p.Key, p.Value.X, p.Value.Y)).ToArray();
        try
        {
            _store.Save(new WidgetSettings(_locked, builtIns, plugins));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // le posizioni non salvate non devono interrompere la shell
        }
    }

    private static string DisplayName(WidgetView view) => view switch
    {
        PluginWidget p => $"{p.Manifest.Name} (plugin)",
        ConnectorWidget c => $"{c.Manifest.Name} (connettore)",
        _ => view.Kind switch
        {
            WidgetKind.Clock => "Orologio",
            WidgetKind.Battery => "Batteria",
            WidgetKind.System => "Sistema",
            WidgetKind.Calendar => "Calendario",
            _ => view.Id,
        },
    };

    private ContextMenu BuildContextMenu()
    {
        var lockItem = new MenuItem { Header = "Blocca posizione", IsCheckable = true };
        lockItem.Click += (_, _) => SetLocked(lockItem.IsChecked);
        var reset = new MenuItem { Header = "Ripristina disposizione" };
        reset.Click += (_, _) => ResetLayout();
        var settings = new MenuItem { Header = "Impostazioni…" };
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        var exit = new MenuItem { Header = "Esci da Nothing Shell" };
        exit.Click += (_, _) => Application.Current.Shutdown();

        var menu = new ContextMenu();
        menu.Opened += (_, _) => lockItem.IsChecked = _locked;
        menu.Items.Add(lockItem);
        menu.Items.Add(reset);
        menu.Items.Add(settings);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        return menu;
    }
}
