using System.Windows.Threading;
using NothingShell.App.Interop;
using NothingShell.Core.Bar;
using NothingShell.Core.Widgets;

namespace NothingShell.App.Bar;

/// <summary>
/// Elenca le finestre "da taskbar" e le osserva con SetWinEventHook, senza polling
/// (docs/05-bar.md); le raggruppa per AppUserModelID e le unisce alle app appuntate.
/// Rileva anche le app a schermo intero, per monitor.
/// </summary>
public sealed class WindowTracker : IDisposable
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
    };

    private readonly TaskbarModel _model = new();
    private readonly AppIdResolver _resolver;
    private readonly Dictionary<nint, string> _exePaths = [];
    private readonly Dictionary<nint, string?> _appIds = [];
    private readonly Dictionary<nint, nint> _monitors = [];
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly WindowNative.WinEventProc _callback; // tenuto in vita finché esistono gli hook
    private readonly List<nint> _hooks = [];
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private int _resolverVersion;
    private nint _fullscreenMonitor;

    public WindowTracker(AppIdResolver? resolver = null)
    {
        _resolver = resolver ?? new AppIdResolver();
        _callback = OnWinEvent;
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Refresh();
        };
    }

    public IReadOnlyList<AppGroup> Groups => _model.Groups;

    public nint Foreground { get; private set; }

    public event Action? GroupsChanged;

    public event Action<nint>? ForegroundChanged;

    /// <summary>Monitor (HMONITOR) su cui un'app a schermo intero è in primo piano; 0 = nessuno.</summary>
    public event Action<nint>? FullscreenChanged;

    public void Start()
    {
        Hook(WindowNative.EventSystemForeground, WindowNative.EventSystemForeground);
        Hook(WindowNative.EventSystemMinimizeStart, WindowNative.EventSystemMinimizeEnd);
        Hook(WindowNative.EventObjectCreate, WindowNative.EventObjectHide);
        Hook(WindowNative.EventObjectLocationChange, WindowNative.EventObjectNameChange);
        Hook(WindowNative.EventObjectCloaked, WindowNative.EventObjectUncloaked);
        Foreground = WindowNative.GetRootOwner(WindowNative.GetForegroundWindow());
        Refresh();
    }

    public string? ExePathOf(nint hwnd) => _exePaths.GetValueOrDefault(hwnd);

    /// <summary>AppUserModelID dell'app (per i recenti); null se non si conosce.</summary>
    public string? AppIdOf(AppGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.Pin?.AppId ?? group.Windows.Select(w => _appIds.GetValueOrDefault(w.Handle)).FirstOrDefault(id => id is not null);
    }

    /// <summary>Eseguibile dell'app, se è un'app desktop.</summary>
    public string? ExePathOf(AppGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.Windows.Select(w => ExePathOf(w.Handle)).FirstOrDefault(p => p?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true)
               ?? group.Pin?.ExePath;
    }

    public void SetPinned(IReadOnlyList<PinnedApp> pins)
    {
        if (_model.SetPinned(pins))
            GroupsChanged?.Invoke();
    }

    /// <summary>Rilegge le finestre (es. dopo che sono cambiate le fonti degli AppUserModelID).</summary>
    public void Refresh()
    {
        if (_resolverVersion != _resolver.Version)
        {
            _resolverVersion = _resolver.Version;
            _appIds.Clear();
        }

        var windows = new List<AppWindow>();
        var z = 0;
        WindowNative.EnumWindows((hwnd, _) =>
        {
            var position = z++;
            if (TryDescribe(hwnd, position) is { } window)
                windows.Add(window);
            return true;
        }, 0);

        var alive = windows.Select(w => w.Handle).ToHashSet();
        foreach (var gone in _exePaths.Keys.Where(h => !alive.Contains(h)).ToArray())
        {
            _exePaths.Remove(gone);
            _appIds.Remove(gone);
            _monitors.Remove(gone);
        }

        if (_model.Update(windows))
            GroupsChanged?.Invoke();
    }

    public static void Execute(ClickAction action)
    {
        switch (action.Kind)
        {
            case ClickKind.Activate:
                Activate(action.Handle);
                break;
            case ClickKind.Minimize:
                WindowNative.ShowWindow(action.Handle, WindowNative.SwMinimize);
                break;
        }
    }

    public static void Close(nint hwnd) => WindowNative.PostMessage(hwnd, WindowNative.WmClose, 0, 0);

    public void Dispose()
    {
        foreach (var hook in _hooks)
            WindowNative.UnhookWinEvent(hook);
        _hooks.Clear();
        _debounce.Stop();
    }

    private static void Activate(nint hwnd)
    {
        if (WindowNative.IsIconic(hwnd))
            WindowNative.ShowWindow(hwnd, WindowNative.SwRestore);
        WindowNative.BringToFront(hwnd);
    }

    private void Hook(uint min, uint max)
    {
        var hook = WindowNative.SetWinEventHook(min, max, 0, _callback, 0, 0,
            WindowNative.WinEventOutOfContext | WindowNative.WinEventSkipOwnProcess);
        if (hook != 0)
            _hooks.Add(hook);
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (eventType == WindowNative.EventSystemForeground)
        {
            // L'evento può riferirsi a una finestra interna (CoreWindow delle app UWP, dialoghi):
            // la finestra "da barra" è il proprietario radice di quella in primo piano.
            Foreground = WindowNative.GetRootOwner(WindowNative.GetForegroundWindow());
            ForegroundChanged?.Invoke(Foreground);
            CheckFullscreen();
            _debounce.Start();
            return;
        }

        if (idObject != 0 || idChild != 0) // solo eventi della finestra stessa, non di cursore o controlli
            return;

        if (eventType == WindowNative.EventObjectLocationChange)
        {
            if (hwnd == Foreground)
                CheckFullscreen();
            // Finestra spostata su un altro monitor: cambia la barra in cui compare.
            if (_monitors.TryGetValue(hwnd, out var monitor) && WindowNative.MonitorOf(hwnd) != monitor)
                _debounce.Start();
            return;
        }

        if (WindowNative.IsTopLevel(hwnd))
            _debounce.Start();
    }

    private void CheckFullscreen()
    {
        var hwnd = Foreground;
        nint fullscreen = 0;
        if (hwnd != 0 && WindowNative.GetWindowRect(hwnd, out var r))
        {
            var (bounds, _) = WindowNative.GetMonitor(hwnd);
            if (Fullscreen.IsFullscreen(ToRect(r), ToRect(bounds), WindowNative.GetClassName(hwnd)))
                fullscreen = WindowNative.MonitorOf(hwnd);
        }

        if (fullscreen == _fullscreenMonitor)
            return;
        _fullscreenMonitor = fullscreen;
        FullscreenChanged?.Invoke(fullscreen);
    }

    private AppWindow? TryDescribe(nint hwnd, int zOrder)
        => Describe(hwnd, zOrder, out _);

    /// <summary>Una riga per ogni finestra visibile, con il motivo dell'esclusione (diagnostica <c>--dump-windows</c>).</summary>
    public IEnumerable<string> Diagnose()
    {
        var lines = new List<string>();
        var z = 0;
        WindowNative.EnumWindows((hwnd, _) =>
        {
            var position = z++;
            if (!WindowNative.IsWindowVisible(hwnd))
                return true;
            var window = Describe(hwnd, position, out var reason);
            lines.Add($"{(window is null ? "-" : "+")} {hwnd,10} [{WindowNative.GetClassName(hwnd)}] '{WindowNative.GetTitle(hwnd)}' " +
                      $"{(window is null ? reason : window.AppKey)}");
            return true;
        }, 0);
        return lines;
    }

    private AppWindow? Describe(nint hwnd, int zOrder, out string reason)
    {
        reason = string.Empty;
        if (!WindowNative.IsWindowVisible(hwnd))
            return Reject("invisibile", out reason);
        if (WindowNative.IsCloaked(hwnd))
            return Reject("cloaked", out reason);

        var ex = WindowNative.GetExStyle(hwnd);
        var appWindow = (ex & WindowNative.WsExAppWindow) != 0;
        if (!appWindow && WindowNative.GetOwner(hwnd) != 0)
            return Reject("ha un proprietario", out reason);
        if (!appWindow && (ex & WindowNative.WsExToolWindow) != 0)
            return Reject("tool window", out reason);
        if (!appWindow && (ex & WindowNative.WsExNoActivate) != 0)
            return Reject("no-activate", out reason);

        WindowNative.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == _ownProcessId)
            return Reject("Nothing Shell", out reason);

        var title = WindowNative.GetTitle(hwnd);
        var className = WindowNative.GetClassName(hwnd);
        if (title.Length == 0)
            return Reject("senza titolo", out reason);
        if (IgnoredClasses.Contains(className))
            return Reject("classe della shell", out reason);

        if (!_exePaths.TryGetValue(hwnd, out var exe))
        {
            exe = WindowNative.GetProcessPath(pid) ?? $"pid:{pid}";
            _exePaths[hwnd] = exe;
        }

        if (!_appIds.TryGetValue(hwnd, out var appId))
        {
            appId = _resolver.Resolve(hwnd, exe);
            _appIds[hwnd] = appId;
        }

        // Senza AppUserModelID: per eseguibile; le app UWP (tutte in ApplicationFrameHost) per titolo.
        var key = appId is not null ? AppIds.Key(appId)
            : className == "ApplicationFrameWindow" ? $"{exe}|{title}"
            : exe.ToUpperInvariant();
        var monitor = WindowNative.MonitorOf(hwnd);
        _monitors[hwnd] = monitor;
        return new AppWindow(hwnd, key, title, WindowNative.IsIconic(hwnd), zOrder, monitor);
    }

    private static AppWindow? Reject(string why, out string reason)
    {
        reason = why;
        return null;
    }

    private static Rect2 ToRect(WindowNative.Rect r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
}
