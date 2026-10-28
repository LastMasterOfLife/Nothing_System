using System.Windows.Threading;
using NothingShell.App.Interop;

namespace NothingShell.App.Bar;

/// <summary>
/// Nasconde le taskbar di Windows di tutti i monitor (<c>Shell_TrayWnd</c> sul principale,
/// <c>Shell_SecondaryTrayWnd</c> sugli altri) e le ripristina (docs/05-bar.md).
/// Nessuna modifica permanente: solo visibilità delle finestre; lo spazio resta riservato.
/// </summary>
public sealed class WindowsTaskbar
{
    private static readonly HashSet<string> TrayClasses = new(StringComparer.Ordinal) { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    private readonly DispatcherTimer _guard = new() { Interval = TimeSpan.FromSeconds(2) };

    public WindowsTaskbar()
    {
        // Explorer può rimostrarle (riavvio, cambio risoluzione, monitor collegato): le rinascondiamo.
        _guard.Tick += (_, _) => SetVisible(false, onlyIfVisible: true);
    }

    public bool IsHidden { get; private set; }

    public void Hide()
    {
        IsHidden = true;
        SetVisible(false, onlyIfVisible: false);
        _guard.Start();
    }

    public void Show()
    {
        IsHidden = false;
        _guard.Stop();
        ForceShow();
    }

    /// <summary>Ripristino senza stato: usato anche in caso di crash e da <c>--restore-taskbar</c>.</summary>
    public static void ForceShow() => SetVisible(true, onlyIfVisible: false);

    private static void SetVisible(bool visible, bool onlyIfVisible)
    {
        foreach (var tray in Trays())
            if (!onlyIfVisible || WindowNative.IsWindowVisible(tray))
                WindowNative.ShowWindow(tray, visible ? WindowNative.SwShow : WindowNative.SwHide);
    }

    private static List<nint> Trays()
    {
        var trays = new List<nint>();
        WindowNative.EnumWindows((hwnd, _) =>
        {
            if (TrayClasses.Contains(WindowNative.GetClassName(hwnd)))
                trays.Add(hwnd);
            return true;
        }, 0);
        return trays;
    }
}
