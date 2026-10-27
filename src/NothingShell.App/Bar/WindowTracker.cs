using System.Windows.Threading;
using NothingShell.App.Interop;
using NothingShell.Core.Bar;
using NothingShell.Core.Widgets;

namespace NothingShell.App.Bar;

/// <summary>
/// Elenca le finestre "da taskbar" e le osserva con SetWinEventHook, senza polling
/// (docs/05-bar.md). Rileva anche le app a schermo intero sul monitor principale.
/// </summary>
public sealed class WindowTracker : IDisposable
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
    };

    private readonly TaskbarModel _model = new();
    private readonly Dictionary<nint, string> _exePaths = [];
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly WindowNative.WinEventProc _callback; // tenuto in vita finché esistono gli hook
    private readonly List<nint> _hooks = [];
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private bool _fullscreen;

    public WindowTracker()
    {
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

    /// <summary>Un'app a schermo intero è in primo piano sul monitor principale (true) o non più (false).</summary>
    public event Action<bool>? FullscreenChanged;

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
            return;
        }

        if (WindowNative.IsTopLevel(hwnd))
            _debounce.Start();
    }

    private void CheckFullscreen()
    {
        var hwnd = Foreground;
        var fullscreen = false;
        if (hwnd != 0 && WindowNative.GetWindowRect(hwnd, out var r))
        {
            var (monitor, isPrimary) = WindowNative.GetMonitor(hwnd);
            fullscreen = isPrimary && Fullscreen.IsFullscreen(
                ToRect(r), ToRect(monitor), WindowNative.GetClassName(hwnd));
        }

        if (fullscreen == _fullscreen)
            return;
        _fullscreen = fullscreen;
        FullscreenChanged?.Invoke(fullscreen);
    }

    private void Refresh()
    {
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
            _exePaths.Remove(gone);

        if (_model.Update(windows))
            GroupsChanged?.Invoke();
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

        // Le app UWP vivono tutte in ApplicationFrameHost: le distinguiamo per titolo.
        var key = className == "ApplicationFrameWindow" ? $"{exe}|{title}" : exe.ToUpperInvariant();
        return new AppWindow(hwnd, key, title, WindowNative.IsIconic(hwnd), zOrder);
    }

    private static AppWindow? Reject(string why, out string reason)
    {
        reason = why;
        return null;
    }

    private static Rect2 ToRect(WindowNative.Rect r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
}
