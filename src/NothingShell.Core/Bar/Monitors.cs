using NothingShell.Core.Widgets;

namespace NothingShell.Core.Bar;

/// <summary>
/// Monitor in pixel fisici. <see cref="Scale"/> = DPI / 96. <see cref="Name"/> (es. <c>\\.\DISPLAY2</c>)
/// resta lo stesso finché il monitor è collegato; <see cref="Handle"/> no, dopo un cambio di configurazione.
/// </summary>
public sealed record DisplayMonitor(nint Handle, string Name, Rect2 Bounds, Rect2 WorkArea, bool IsPrimary, double Scale);

/// <summary>Impostazione di Windows "Mostra le app della barra delle applicazioni su" (<c>MMTaskbarMode</c>).</summary>
public enum MultiMonitorMode
{
    /// <summary>Tutte le barre mostrano tutte le app.</summary>
    AllTaskbars = 0,

    /// <summary>La barra principale mostra tutto, le altre solo le finestre aperte sul loro monitor.</summary>
    MainAndWhereOpen = 1,

    /// <summary>Ogni barra mostra solo le finestre aperte sul suo monitor.</summary>
    WhereOpen = 2,
}

/// <summary>Una barra per monitor, al posto di ogni taskbar di Windows (docs/05-bar.md).</summary>
public static class MultiMonitorBar
{
    /// <summary>Sotto questa fascia (DIP) la taskbar è in nascondimento automatico: nessuno spazio riservato.</summary>
    public const double MinReservedHeight = 24;

    public const double FallbackHeight = 48;

    /// <summary>
    /// Rettangolo della barra (pixel fisici): la fascia che Windows riserva alla taskbar in fondo al monitor,
    /// oppure gli ultimi 48 DIP sopra il desktop se non c'è spazio riservato.
    /// </summary>
    public static Rect2 Place(DisplayMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var reserved = monitor.Bounds.Bottom - monitor.WorkArea.Bottom;
        var height = reserved >= MinReservedHeight * monitor.Scale ? reserved : Math.Round(FallbackHeight * monitor.Scale);
        return new Rect2(monitor.Bounds.X, monitor.Bounds.Bottom - height, monitor.Bounds.Width, height);
    }

    /// <summary>
    /// App della barra di un monitor secondo <paramref name="mode"/>. Come in Windows, le app appuntate
    /// stanno su tutte le barre solo con <see cref="MultiMonitorMode.AllTaskbars"/>, altrimenti sulla principale.
    /// </summary>
    public static IReadOnlyList<AppGroup> GroupsFor(IReadOnlyList<AppGroup> groups, nint monitor, bool isMain, MultiMonitorMode mode)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var allWindows = mode == MultiMonitorMode.AllTaskbars || (mode == MultiMonitorMode.MainAndWhereOpen && isMain);
        var showPins = mode == MultiMonitorMode.AllTaskbars || isMain;
        if (allWindows && showPins)
            return groups;

        var result = new List<AppGroup>();
        foreach (var group in groups)
        {
            var windows = allWindows ? group.Windows : group.Windows.Where(w => w.Monitor == monitor).ToArray();
            if (windows.Count > 0 || (group.Pin is not null && showPins))
                result.Add(group with { Windows = windows });
        }
        return result;
    }
}
