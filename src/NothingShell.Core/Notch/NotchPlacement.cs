using NothingShell.Core.Bar;
using NothingShell.Core.Design;
using NothingShell.Core.Widgets;

namespace NothingShell.Core.Notch;

/// <summary>Su quale schermo sta il notch e dove, in pixel fisici: centrato contro il bordo alto.</summary>
public static class NotchPlacement
{
    /// <summary>
    /// Lo schermo del notch fra <paramref name="monitors"/>: il principale, quello sotto il mouse (<paramref name="cursor"/>) o
    /// quello scelto per nome. Se manca, il principale; null solo senza schermi.
    /// </summary>
    public static DisplayMonitor? Pick(IReadOnlyList<DisplayMonitor> monitors, NotchScreen screen, string? chosen, nint cursor)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        var wanted = screen switch
        {
            NotchScreen.Mouse => monitors.FirstOrDefault(m => m.Handle == cursor),
            NotchScreen.Chosen => monitors.FirstOrDefault(m => string.Equals(m.Name, chosen, StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };
        return wanted ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? (monitors.Count > 0 ? monitors[0] : null);
    }

    /// <summary>La finestra del notch (<paramref name="host"/> in DIP) su <paramref name="monitor"/>, in pixel fisici.</summary>
    public static Rect2 Place(DisplayMonitor monitor, Size2 host)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var width = Math.Round(host.Width * monitor.Scale);
        var height = Math.Round(host.Height * monitor.Scale);
        var x = monitor.Bounds.X + Math.Round((monitor.Bounds.Width - width) / 2);
        return new Rect2(x, monitor.Bounds.Y, width, height);
    }

    /// <summary>Schermi da sinistra a destra, come li numera chi li guarda (le Impostazioni di Windows fanno lo stesso a occhio).</summary>
    public static IReadOnlyList<DisplayMonitor> InOrder(IReadOnlyList<DisplayMonitor> monitors) =>
        [.. monitors.OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y)];
}
