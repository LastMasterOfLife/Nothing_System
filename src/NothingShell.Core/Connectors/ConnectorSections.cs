using NothingShell.Core.Notch;

namespace NothingShell.Core.Connectors;

/// <summary>
/// Dove compare un connettore nel notch: i server (database, FTP) hanno una pagina tutta loro, che c'è finché ne esiste
/// uno; tutti gli altri stanno nella pagina Dispositivi (docs/14-agenti-connettori.md).
/// </summary>
public static class ConnectorSections
{
    /// <summary>Le pagine che esistono solo se c'è almeno un connettore da mostrarci.</summary>
    public static IReadOnlyList<NotchPage> Pages { get; } = [NotchPage.Database, NotchPage.Ftp];

    public static bool IsSection(NotchPage page) => Pages.Contains(page);

    /// <summary>La pagina dedicata del connettore (dal modello della libreria da cui nasce), null se va in Dispositivi.</summary>
    public static NotchPage? Of(ConnectorManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Template switch
        {
            "database" or "db-conteggio" or "db-freschezza" or "db-sessioni" or "db-backup" => NotchPage.Database,
            "ftp" => NotchPage.Ftp,
            _ => null,
        };
    }

    /// <summary>La pagina in cui si vede il connettore.</summary>
    public static NotchPage PageOf(ConnectorManifest manifest) => Of(manifest) ?? NotchPage.Devices;
}
