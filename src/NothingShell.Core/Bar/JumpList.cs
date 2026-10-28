namespace NothingShell.Core.Bar;

/// <summary>
/// Elemento recente di un'app. Un file o una cartella ha solo <see cref="Path"/>; un collegamento
/// (<see cref="IsLink"/>, es. i progetti di Android Studio) ha anche gli argomenti da passare all'eseguibile.
/// </summary>
public sealed record JumpItem(string Title, string Path, string? Arguments, bool IsLink);

/// <summary>Comando da avviare: eseguibile o documento, con argomenti opzionali.</summary>
public sealed record LaunchCommand(string FileName, string? Arguments);

/// <summary>Elenco dei recenti mostrato col clic destro su un'app della barra (docs/05-bar.md).</summary>
public static class JumpLists
{
    public const int MaxItems = 10;

    /// <summary>
    /// Solo file e cartelle (niente <c>mailto:</c>, <c>tel:</c> e altri URI interni), senza doppioni,
    /// con un titolo leggibile, al massimo <see cref="MaxItems"/>.
    /// </summary>
    public static IReadOnlyList<JumpItem> Clean(IEnumerable<JumpItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items
            .Where(i => !string.IsNullOrWhiteSpace(i.Path) && System.IO.Path.IsPathFullyQualified(i.Path))
            .DistinctBy(i => (i.Path.ToUpperInvariant(), i.Arguments))
            .Select(i => string.IsNullOrWhiteSpace(i.Title) ? i with { Title = NameOf(i.Path) } : i)
            .Take(MaxItems)
            .ToArray();
    }

    /// <summary>
    /// Come aprire un elemento recente: con l'app a cui appartiene (Chrome apre il PDF, VS Code la cartella),
    /// non con l'app predefinita del tipo di file. Senza eseguibile noto, o per Esplora file, si apre il percorso.
    /// </summary>
    public static LaunchCommand Open(JumpItem item, string? appExe)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsLink)
            return new LaunchCommand(item.Path, string.IsNullOrEmpty(item.Arguments) ? null : item.Arguments);
        if (appExe is null
            || !appExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || System.IO.Path.GetFileName(appExe).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            return new LaunchCommand(item.Path, null);
        return new LaunchCommand(appExe, Quote(item.Path));
    }

    /// <summary>Argomento tra virgolette; una barra finale va raddoppiata, altrimenti chiuderebbe la stringa (<c>"C:\"</c>).</summary>
    public static string Quote(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.EndsWith('\\') ? $"\"{path}\\\"" : $"\"{path}\"";
    }

    private static string NameOf(string path)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
        return name.Length > 0 ? name : path;
    }
}
