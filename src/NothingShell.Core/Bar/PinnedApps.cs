namespace NothingShell.Core.Bar;

/// <summary>
/// App appuntata alla taskbar di Windows. <see cref="LaunchPath"/> è il collegamento in
/// <c>User Pinned\TaskBar</c> o <c>shell:AppsFolder\&lt;AppId&gt;</c> per le app del Microsoft Store.
/// </summary>
public sealed record PinnedApp(string AppId, string Name, string LaunchPath, string? ExePath, bool HasArguments)
{
    public string Key => AppIds.Key(AppId);
}

/// <summary>Cartella nota di Windows (es. Programmi) e il suo GUID, usati negli AppUserModelID predefiniti.</summary>
public sealed record KnownFolder(string Path, string Id);

/// <summary>AppUserModelID: la chiave con cui Windows raggruppa le finestre, appunta le app e tiene i recenti.</summary>
public static class AppIds
{
    /// <summary>Chiave di raggruppamento: Windows confronta gli AppUserModelID senza distinguere le maiuscole.</summary>
    public static string Key(string appId)
    {
        ArgumentNullException.ThrowIfNull(appId);
        return appId.ToUpperInvariant();
    }

    /// <summary>
    /// ID che Windows assegna a un eseguibile che non ne dichiara uno: il percorso, con la cartella
    /// nota più specifica sostituita dal suo GUID (es. <c>{6D809377-…}\KeePass\KeePass.exe</c>).
    /// </summary>
    public static string Default(string exePath, IEnumerable<KnownFolder> knownFolders)
    {
        ArgumentNullException.ThrowIfNull(exePath);
        ArgumentNullException.ThrowIfNull(knownFolders);
        var best = knownFolders
            .Select(f => f with { Path = f.Path.TrimEnd('\\') })
            .Where(f => f.Path.Length > 0 && exePath.StartsWith(f.Path + "\\", StringComparison.OrdinalIgnoreCase))
            .MaxBy(f => f.Path.Length);
        return best is null ? exePath : best.Id + exePath[best.Path.Length..];
    }
}

/// <summary>
/// Valore <c>Favorites</c> di <c>HKCU\…\Explorer\Taskband</c>: le app appuntate in ordine, ognuna come
/// <c>[0x00][lunghezza uint32][ITEMIDLIST]</c>.
/// </summary>
public static class TaskbandFavorites
{
    public static IReadOnlyList<byte[]> Split(ReadOnlySpan<byte> blob)
    {
        var lists = new List<byte[]>();
        var pos = 0;
        while (pos + 5 <= blob.Length)
        {
            var size = BitConverter.ToInt32(blob.Slice(pos + 1, 4));
            if (size <= 0 || size > blob.Length - pos - 5)
                break; // formato inatteso: si tengono le voci già lette
            lists.Add(blob.Slice(pos + 5, size).ToArray());
            pos += 5 + size;
        }
        return lists;
    }
}
