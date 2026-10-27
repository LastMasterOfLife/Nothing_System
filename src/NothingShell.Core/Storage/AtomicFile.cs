namespace NothingShell.Core.Storage;

public static class AtomicFile
{
    /// <summary>Cartella dei dati di Nothing Shell, indipendente dalla cartella di lavoro.</summary>
    public static string DataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nothing-shell");

    /// <summary>Scrittura atomica: file temporaneo + sostituzione, crea la cartella se manca.</summary>
    public static void WriteAllText(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Contenuto del file, o null se manca o non è leggibile.</summary>
    public static string? TryReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
