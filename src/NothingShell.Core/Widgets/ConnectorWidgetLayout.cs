using NothingShell.Core.Notch;

namespace NothingShell.Core.Widgets;

/// <summary>
/// Widget di un connettore sul desktop (docs/04-widgets.md): id, quanti valori mostra e con che taglia.
/// Un valore → riquadro piccolo; fino a 4 → largo (2 colonne); fino a 8 → grande (2 colonne, 4 righe).
/// </summary>
public static class ConnectorWidgetLayout
{
    public const string IdPrefix = "connector:";
    public const int MaxReadings = 8;

    public static string WidgetId(string connectorId) => IdPrefix + connectorId;

    public static bool IsConnectorWidget(string widgetId) => widgetId.StartsWith(IdPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Quanti valori tenere in conto per la taglia: quelli dichiarati nel file o, se sono di più, quelli arrivati
    /// (un ping ne dà due senza dichiararli). Così la taglia non salta quando arrivano i primi dati.
    /// </summary>
    public static int Count(int declared, int received) => Math.Clamp(Math.Max(declared, received), 1, MaxReadings);

    public static int Columns(int count) => count <= 1 ? 1 : 2;

    public static int Rows(int count) => (Math.Clamp(count, 1, MaxReadings) + Columns(count) - 1) / Columns(count);

    public static Size2 Size(int count)
    {
        var wide = WidgetSizes.Cell * 2 + WidgetSizes.Gap;
        return count switch
        {
            <= 1 => new(WidgetSizes.Cell, WidgetSizes.Cell),
            <= 4 => new(wide, WidgetSizes.Cell),
            _ => new(wide, wide),
        };
    }
}
