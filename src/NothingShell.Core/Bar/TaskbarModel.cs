using NothingShell.Core.Widgets;

namespace NothingShell.Core.Bar;

/// <summary>Finestra elencabile nella barra. <see cref="ZOrder"/>: 0 = più in alto (più recente).</summary>
public sealed record AppWindow(nint Handle, string AppKey, string Title, bool IsMinimized, int ZOrder);

/// <summary>
/// Finestre della stessa app, in ordine stabile (ordine di comparsa). Un'app appuntata ha <see cref="Pin"/>
/// e resta nella barra anche senza finestre.
/// </summary>
public sealed record AppGroup(string AppKey, IReadOnlyList<AppWindow> Windows, PinnedApp? Pin = null)
{
    public string Title => Windows.Count > 0 ? Windows[0].Title : Pin?.Name ?? string.Empty;

    public bool IsRunning => Windows.Count > 0;

    public bool Contains(nint handle) => Windows.Any(w => w.Handle == handle);
}

public enum ClickKind
{
    None,
    Activate,
    Minimize,
    Launch,
}

public readonly record struct ClickAction(ClickKind Kind, nint Handle)
{
    public static ClickAction None => new(ClickKind.None, 0);
}

/// <summary>
/// Stato delle app nella barra (docs/05-bar.md): prima le app appuntate nell'ordine di Windows,
/// poi le altre app aperte con ordine stabile — le app e le finestre già viste mantengono la posizione,
/// le nuove vanno in fondo.
/// </summary>
public sealed class TaskbarModel
{
    private readonly List<string> _appOrder = [];
    private readonly List<nint> _windowOrder = [];
    private AppWindow[] _windows = [];
    private PinnedApp[] _pins = [];

    public IReadOnlyList<AppGroup> Groups { get; private set; } = [];

    /// <summary>App appuntate, in ordine. Restituisce true se la barra va ridisegnata.</summary>
    public bool SetPinned(IReadOnlyList<PinnedApp> pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        _pins = pins.DistinctBy(p => p.Key, StringComparer.Ordinal).ToArray();
        return Compose();
    }

    /// <summary>Aggiorna con le finestre attuali (in qualsiasi ordine). Restituisce true se la barra va ridisegnata.</summary>
    public bool Update(IReadOnlyList<AppWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var byZ = windows.OrderBy(w => w.ZOrder).ToArray();

        var alive = byZ.Select(w => w.Handle).ToHashSet();
        _windowOrder.RemoveAll(h => !alive.Contains(h));
        foreach (var w in byZ)
            if (!_windowOrder.Contains(w.Handle))
                _windowOrder.Add(w.Handle);

        var appsAlive = byZ.Select(w => w.AppKey).ToHashSet(StringComparer.Ordinal);
        _appOrder.RemoveAll(k => !appsAlive.Contains(k));
        foreach (var w in byZ)
            if (!_appOrder.Contains(w.AppKey))
                _appOrder.Add(w.AppKey);

        _windows = byZ;
        return Compose();
    }

    /// <summary>Regole del clic su un'app (tabella in docs/05-bar.md).</summary>
    public static ClickAction Click(AppGroup group, nint foreground)
    {
        ArgumentNullException.ThrowIfNull(group);
        var windows = group.Windows;
        if (windows.Count == 0)
            return group.Pin is null ? ClickAction.None : new ClickAction(ClickKind.Launch, 0);

        if (windows.Count == 1)
        {
            var only = windows[0];
            return only.Handle == foreground && !only.IsMinimized
                ? new ClickAction(ClickKind.Minimize, only.Handle)
                : new ClickAction(ClickKind.Activate, only.Handle);
        }

        for (var i = 0; i < windows.Count; i++)
            if (windows[i].Handle == foreground)
                return new ClickAction(ClickKind.Activate, windows[(i + 1) % windows.Count].Handle);

        var mostRecent = windows.MinBy(w => w.ZOrder)!;
        return new ClickAction(ClickKind.Activate, mostRecent.Handle);
    }

    private bool Compose()
    {
        var position = _windowOrder.Select((h, i) => (h, i)).ToDictionary(x => x.h, x => x.i);
        AppWindow[] WindowsOf(string key) =>
            _windows.Where(w => w.AppKey == key).OrderBy(w => position[w.Handle]).ToArray();

        var pinned = _pins.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var groups = _pins
            .Select(p => new AppGroup(p.Key, WindowsOf(p.Key), p))
            .Concat(_appOrder.Where(k => !pinned.Contains(k)).Select(k => new AppGroup(k, WindowsOf(k))))
            .ToArray();

        var changed = !SameGroups(Groups, groups);
        Groups = groups;
        return changed;
    }

    private static bool SameGroups(IReadOnlyList<AppGroup> a, AppGroup[] b) =>
        a.Count == b.Length && a.Zip(b).All(p =>
            p.First.AppKey == p.Second.AppKey &&
            Equals(p.First.Pin, p.Second.Pin) &&
            p.First.Windows.Count == p.Second.Windows.Count &&
            p.First.Windows.Zip(p.Second.Windows).All(w =>
                w.First.Handle == w.Second.Handle &&
                w.First.Title == w.Second.Title &&
                w.First.IsMinimized == w.Second.IsMinimized));
}

public static class Fullscreen
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    /// <summary>La finestra copre tutto il monitor e non è il desktop o la shell.</summary>
    public static bool IsFullscreen(Rect2 window, Rect2 monitor, string className) =>
        !ShellClasses.Contains(className) &&
        window.X <= monitor.X && window.Y <= monitor.Y &&
        window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;
}

public enum NetworkKind
{
    Offline,
    Wifi,
    Ethernet,
}
