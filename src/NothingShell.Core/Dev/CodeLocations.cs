using System.Text.RegularExpressions;

namespace NothingShell.Core.Dev;

/// <summary>Un punto del codice: file (anche relativo), riga e colonna da 1; la colonna 0 se non è nota.</summary>
public sealed record CodeLocation(string File, int Line, int Column, string? Message)
{
    /// <summary>"Program.cs:42", per le righe corte del notch.</summary>
    public string Label => $"{Path.GetFileName(File.Replace('/', '\\'))}:{Line}";
}

/// <summary>
/// Errori dei compilatori e frame delle stack trace (docs/12-sviluppo.md): MSBuild/csc/tsc, gcc/clang/Go, rustc,
/// traceback Python, stack .NET e JavaScript. Le posizioni sono quelle scritte nel testo: si risolvono con
/// <see cref="Resolve"/> e si scelgono con <see cref="Pick"/> (che controlla l'esistenza dei file).
/// </summary>
public static partial class CodeLocations
{
    private static readonly string[] LibraryMarkers =
        ["node_modules", ".nuget", "site-packages", "dist-packages", @"lib\python", "lib/python", "internal/", @"\.cargo\", "/.cargo/", "<frozen"];

    /// <summary>Il primo errore di compilazione nell'output, o null.</summary>
    public static CodeLocation? FirstError(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        CodeLocation? best = null;
        var bestIndex = int.MaxValue;
        foreach (var regex in new[] { MsBuildError(), GenericError(), RustError() })
        {
            var match = regex.Match(output);
            if (!match.Success || match.Index >= bestIndex)
                continue;
            bestIndex = match.Index;
            var code = match.Groups["code"].Success && match.Groups["code"].Length > 0 ? match.Groups["code"].Value + ": " : string.Empty;
            best = new CodeLocation(match.Groups["file"].Value.Trim(), int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture),
                Column(match), code + match.Groups["msg"].Value.Trim());
        }
        return best;
    }

    /// <summary>Frame delle stack trace dal più interno al più esterno (per Python l'ordine del testo è al contrario).</summary>
    public static IReadOnlyList<CodeLocation> Frames(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var message = FirstLine(text);
        var frames = new List<CodeLocation>();
        foreach (Match m in DotNetFrame().Matches(text))
            frames.Add(new CodeLocation(m.Groups["file"].Value.Trim(), Number(m, "line"), 0, message));
        foreach (Match m in JsFrame().Matches(text))
            frames.Add(new CodeLocation(StripFileUrl(m.Groups["file"].Value.Trim()), Number(m, "line"), Number(m, "col"), message));
        var python = PythonFrame().Matches(text).Select(m => new CodeLocation(m.Groups["file"].Value, Number(m, "line"), 0, LastLine(text))).Reverse();
        frames.AddRange(python);
        return frames;
    }

    /// <summary>Errore o frame più utile: prima gli errori, poi i frame fuori dalle librerie, poi tutti; solo file che esistono.</summary>
    public static CodeLocation? Pick(string text, string? cwd, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(exists);
        var frames = Frames(text);
        IEnumerable<CodeLocation> candidates = FirstError(text) is { } error ? [error] : [];
        candidates = candidates.Concat(frames.Where(f => !IsLibrary(f.File))).Concat(frames.Where(f => IsLibrary(f.File)));
        foreach (var candidate in candidates)
        {
            var resolved = candidate with { File = Resolve(candidate.File, cwd) };
            if (exists(resolved.File))
                return resolved;
        }
        return null;
    }

    /// <summary>C'è qualcosa che somiglia a un errore o a una stack trace (senza controllare i file).</summary>
    public static bool Any(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return FirstError(text) is not null || Frames(text).Count > 0;
    }

    public static bool IsLibrary(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return LibraryMarkers.Any(m => path.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Percorso assoluto: i relativi si appoggiano alla cartella del comando.</summary>
    public static string Resolve(string file, string? cwd)
    {
        ArgumentNullException.ThrowIfNull(file);
        var normalized = file.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) || string.IsNullOrEmpty(cwd))
            return normalized;
        return Path.GetFullPath(Path.Combine(cwd, normalized));
    }

    private static int Column(Match match) => match.Groups["col"].Success ? Number(match, "col") : 0;

    private static int Number(Match match, string group) =>
        int.Parse(match.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture);

    private static string StripFileUrl(string file) =>
        file.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) ? Uri.UnescapeDataString(file[8..]) : file;

    private static string? FirstLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

    private static string? LastLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);

    // src\Program.cs(42,13): error CS1002: ; expected [C:\app\app.csproj]
    [GeneratedRegex(@"^\s*(?<file>[^\s(:][^(\r\n]*?|[A-Za-z]:[\\/][^(\r\n]*?)\((?<line>\d+)(?:,(?<col>\d+))?(?:,\d+,\d+)?\)\s*:\s*(?:fatal\s+)?error\s*(?<code>[A-Za-z]+\d+)?\s*:\s*(?<msg>.*?)(?:\s+\[[^\]\r\n]+\])?\s*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex MsBuildError();

    // main.c:10:5: error: expected ';'   ·   C:\app\main.go:3:2: error ...
    [GeneratedRegex(@"^\s*(?<file>(?:[A-Za-z]:[\\/])?[^:\r\n<>""|?*\s][^:\r\n<>""|?*]*?):(?<line>\d+)(?::(?<col>\d+))?:\s*(?:fatal\s+)?error\b(?:\[(?<code>\w+)\])?:?\s*(?<msg>.*)$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GenericError();

    // error[E0425]: cannot find value `x` in this scope
    //  --> src/main.rs:3:5
    [GeneratedRegex(@"^error(?:\[(?<code>\w+)\])?:\s*(?<msg>.+?)\r?\n\s*-->\s*(?<file>.+?):(?<line>\d+):(?<col>\d+)", RegexOptions.Multiline)]
    private static partial Regex RustError();

    // at App.Run() in C:\app\Program.cs:line 42   (anche localizzato: riga, Zeile, ligne, línea)
    [GeneratedRegex(@"^\s*(?:at|in|à|bei|en)\s+\S.*?\s+(?:in|dans|bei|en)\s+(?<file>(?:[A-Za-z]:[\\/]|/).+?):(?:line|riga|Zeile|ligne|línea)\s+(?<line>\d+)\s*$",
        RegexOptions.Multiline)]
    private static partial Regex DotNetFrame();

    // at run (C:\app\index.js:10:15)   ·   at C:\app\index.js:10:15   ·   at file:///C:/app/index.mjs:1:1
    [GeneratedRegex(@"^\s*at\s+(?:.*?\()?(?<file>(?:file:///)?(?:[A-Za-z]:[\\/]|/)[^():\r\n]+?):(?<line>\d+):(?<col>\d+)\)?\s*$", RegexOptions.Multiline)]
    private static partial Regex JsFrame();

    // File "C:\app\main.py", line 12, in run
    [GeneratedRegex(@"^\s*File ""(?<file>[^""]+)"", line (?<line>\d+)", RegexOptions.Multiline)]
    private static partial Regex PythonFrame();
}
