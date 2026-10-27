using System.Windows.Threading;
using NothingShell.App.Interop;

namespace NothingShell.App.Bar;

/// <summary>
/// Nasconde la taskbar di Windows del monitor principale e la ripristina (docs/05-bar.md).
/// Nessuna modifica permanente: solo visibilità della finestra Shell_TrayWnd.
/// </summary>
public sealed class WindowsTaskbar
{
    private readonly DispatcherTimer _guard = new() { Interval = TimeSpan.FromSeconds(2) };

    public WindowsTaskbar()
    {
        // Explorer può rimostrarla (riavvio, cambio risoluzione): la rinascondiamo.
        _guard.Tick += (_, _) =>
        {
            var tray = Tray;
            if (tray != 0 && WindowNative.IsWindowVisible(tray))
                WindowNative.ShowWindow(tray, WindowNative.SwHide);
        };
    }

    public bool IsHidden { get; private set; }

    private static nint Tray => NativeMethods.FindWindow("Shell_TrayWnd", null);

    public void Hide()
    {
        IsHidden = true;
        if (Tray is var tray and not 0)
            WindowNative.ShowWindow(tray, WindowNative.SwHide);
        _guard.Start();
    }

    public void Show()
    {
        IsHidden = false;
        _guard.Stop();
        ForceShow();
    }

    /// <summary>Ripristino senza stato: usato anche in caso di crash e da <c>--restore-taskbar</c>.</summary>
    public static void ForceShow()
    {
        if (Tray is var tray and not 0)
            WindowNative.ShowWindow(tray, WindowNative.SwShow);
    }
}
