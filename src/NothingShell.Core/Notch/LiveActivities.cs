using System.Globalization;

namespace NothingShell.Core.Notch;

/// <summary>Attività dal vivo del notch, accese e spente dalle impostazioni (docs/10-live-activities.md).</summary>
public enum LiveActivity
{
    Volume,
    Brightness,
    Privacy,
    Downloads,
    Screenshots,
    Bluetooth,

    /// <summary>Prossimo appuntamento: riga nella Home, pagina Agenda e promemoria (docs/11-agenda-pannello-rapido.md).</summary>
    Agenda,

    /// <summary>Riepilogo del mattino al primo accesso della giornata.</summary>
    MorningBrief,

    /// <summary>Comandi e build dal terminale (docs/12-sviluppo.md): pillola, scheda alla fine, pagina Lavoro.</summary>
    Tasks,

    /// <summary>GitHub Actions dopo un <c>git push</c>.</summary>
    Ci,

    /// <summary>Cronologia degli appunti e pagina Appunti.</summary>
    Clipboard,

    /// <summary>Avviso al momento della copia di JSON, errori, colori e timestamp.</summary>
    ClipboardHints,

    /// <summary>Segreti e token copiati: scheda e cancellazione dagli appunti dopo 30 s.</summary>
    ClipboardSecrets,

    /// <summary>Porte e server locali: avviso e pagina Server.</summary>
    Servers,
}

/// <summary>
/// Da dove arrivano gli screenshot oltre allo Strumento di cattura di Windows: Greenshot mette la cattura negli
/// appunti (se ne è proprietario) e la salva nella cartella scelta nelle sue impostazioni (<c>Greenshot.ini</c>).
/// </summary>
public static class ScreenshotSources
{
    /// <summary>Processi che scrivono una cattura negli appunti: Strumento di cattura e Greenshot.</summary>
    public static readonly string[] ClipboardTools = ["ScreenClippingHost", "SnippingTool", "Greenshot"];

    public const string GreenshotProcess = "Greenshot";

    /// <summary>Formati in cui Greenshot può salvare (il formato .greenshot è il suo, non un'immagine).</summary>
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif"];

    public static bool IsImage(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return ImageExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Cartella di salvataggio di Greenshot: <c>OutputFilePath</c> della sezione <c>[Core]</c>, con le variabili
    /// d'ambiente espanse da <paramref name="expand"/>; vuota o assente vale il Desktop, come in Greenshot.
    /// </summary>
    public static string GreenshotFolder(string ini, string desktop, Func<string, string> expand)
    {
        ArgumentNullException.ThrowIfNull(ini);
        ArgumentNullException.ThrowIfNull(desktop);
        ArgumentNullException.ThrowIfNull(expand);
        var section = string.Empty;
        foreach (var raw in ini.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }
            if (!section.Equals("Core", StringComparison.OrdinalIgnoreCase))
                continue;
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || !line[..eq].Trim().Equals("OutputFilePath", StringComparison.OrdinalIgnoreCase))
                continue;
            var value = expand(line[(eq + 1)..].Trim().Trim('"'));
            return value.Length > 0 ? value.TrimEnd('\\') : desktop;
        }
        return desktop;
    }
}

/// <summary>Una voce di <c>CapabilityAccessManager\ConsentStore</c>: tempi FILETIME di inizio e fine dell'ultimo uso.</summary>
public readonly record struct CapabilityUse(string Key, long LastUsedStart, long LastUsedStop, bool IsPackaged);

/// <summary>Chi usa microfono o webcam: Windows segna l'inizio dell'uso e azzera la fine finché dura.</summary>
public static class PrivacyUsage
{
    public static IReadOnlyList<CapabilityUse> InUse(IEnumerable<CapabilityUse> uses)
    {
        ArgumentNullException.ThrowIfNull(uses);
        return uses.Where(u => u.LastUsedStart > 0 && u.LastUsedStop == 0).ToArray();
    }

    /// <summary>Percorso dell'eseguibile per le app non dello Store (<c>C:#Programmi#Zoom#Zoom.exe</c>).</summary>
    public static string? ExePath(CapabilityUse use) =>
        use.IsPackaged ? null : use.Key.Replace('#', '\\');

    /// <summary>Nome leggibile se non se ne trova uno migliore: <c>Zoom</c>, <c>WindowsCamera</c>.</summary>
    public static string FallbackName(CapabilityUse use)
    {
        if (!use.IsPackaged)
            return Path.GetFileNameWithoutExtension(use.Key.Replace('#', '\\'));
        var family = use.Key.Split('_')[0];
        return family[(family.LastIndexOf('.') + 1)..];
    }
}

/// <summary>Un download in corso, riconosciuto dal file temporaneo del browser.</summary>
public sealed record ActiveDownload(string PartialPath, string Name, long Bytes, double BytesPerSecond, TimeSpan LastGrowth);

/// <summary>
/// Download dei browser nella cartella Download (docs/10-live-activities.md): Chrome, Edge, Brave, Opera e
/// Vivaldi scrivono in <c>.crdownload</c>, Firefox in <c>.part</c>; alla fine rinominano il file nel nome vero.
/// Le dimensioni arrivano da fuori (controllo ogni secondo), il tempo pure: si testa senza file veri.
/// </summary>
public sealed class DownloadTracker
{
    public static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(2);

    private static readonly string[] PartialExtensions = [".crdownload", ".part", ".partial", ".opdownload", ".download"];

    private readonly Dictionary<string, ActiveDownload> _active = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ActiveDownload> Active => _active.Values.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

    public static bool IsPartial(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return PartialExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Nome da mostrare: senza l'estensione temporanea; i nomi provvisori di Chrome diventano "Download".</summary>
    public static string DisplayName(string partialPath)
    {
        ArgumentNullException.ThrowIfNull(partialPath);
        var name = Path.GetFileNameWithoutExtension(partialPath);
        return name.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("Non confermato ", StringComparison.OrdinalIgnoreCase)
            ? "Download"
            : name;
    }

    /// <returns>True se è iniziato un download.</returns>
    public bool OnCreated(string path, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!IsPartial(path) || _active.ContainsKey(path))
            return false;
        _active[path] = new ActiveDownload(path, DisplayName(path), 0, 0, now);
        return true;
    }

    /// <returns>Il file finito se un download temporaneo è diventato il file vero, altrimenti null.</returns>
    public string? OnRenamed(string oldPath, string newPath, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(oldPath);
        ArgumentNullException.ThrowIfNull(newPath);
        _active.Remove(oldPath, out var previous);
        if (IsPartial(newPath))
        {
            // Chrome passa da "Unconfirmed 123.crdownload" a "nome.pdf.crdownload": stesso download, nome vero
            _active[newPath] = (previous ?? new ActiveDownload(newPath, string.Empty, 0, 0, now)) with
            {
                PartialPath = newPath,
                Name = DisplayName(newPath),
            };
            return null;
        }
        return previous is not null || IsPartial(oldPath) ? newPath : null;
    }

    /// <summary>File temporaneo eliminato: download annullato.</summary>
    public bool OnDeleted(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return _active.Remove(path);
    }

    /// <summary>Nuova dimensione del file temporaneo; la velocità è una media mobile.</summary>
    public void OnSize(string path, long bytes, TimeSpan now, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!_active.TryGetValue(path, out var download))
            return;
        var grown = bytes - download.Bytes;
        var speed = elapsed > TimeSpan.Zero && grown > 0 ? grown / elapsed.TotalSeconds : 0;
        _active[path] = download with
        {
            Bytes = bytes,
            BytesPerSecond = download.BytesPerSecond == 0 ? speed : (0.7 * download.BytesPerSecond) + (0.3 * speed),
            LastGrowth = grown > 0 ? now : download.LastGrowth,
        };
    }

    /// <summary>Toglie i download fermi da troppo (messi in pausa o abbandonati dal browser).</summary>
    public bool Prune(TimeSpan now)
    {
        var stalled = _active.Values.Where(d => now - d.LastGrowth > StallTimeout).Select(d => d.PartialPath).ToArray();
        foreach (var path in stalled)
            _active.Remove(path);
        return stalled.Length > 0;
    }

    /// <summary>Per la pillola compatta, dove c'è posto per pochi punti: "850K", "4,2M", "124M", "1,2G".</summary>
    public static string FormatShort(long bytes)
    {
        var culture = CultureInfo.GetCultureInfo("it-IT");
        static string Scaled(double value, CultureInfo c) => value.ToString(value < 10 ? "0.0" : "0", c);
        return bytes switch
        {
            < 1024 * 1024 => $"{Math.Max(1, bytes / 1024)}K",
            < 1024L * 1024 * 1024 => Scaled(bytes / (1024.0 * 1024), culture) + "M",
            _ => Scaled(bytes / (1024.0 * 1024 * 1024), culture) + "G",
        };
    }

    /// <summary>"850 KB", "12,4 MB", "1,2 GB" (all'italiana).</summary>
    public static string FormatBytes(long bytes)
    {
        var culture = CultureInfo.GetCultureInfo("it-IT");
        return bytes switch
        {
            < 1024 * 1024 => $"{Math.Max(1, bytes / 1024)} KB",
            < 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString(bytes < 100L * 1024 * 1024 ? "0.0" : "0", culture) + " MB",
            _ => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", culture) + " GB",
        };
    }
}
