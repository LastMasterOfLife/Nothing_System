using System.Text.RegularExpressions;

namespace NothingShell.Core.Dev;

/// <summary>
/// Un clone chiesto all'assistente: l'indirizzo, la cartella e --recursive se sono già stati detti, le altre opzioni
/// (-b, --depth…) com'erano scritte.
/// </summary>
public sealed record GitCloneRequest(string Url, string? Folder = null, bool? Recursive = null, IReadOnlyList<string>? Options = null);

/// <summary>Quello che l'utente ha scelto nella scheda del clone.</summary>
public sealed record GitCloneChoice(string Folder, bool Recursive);

/// <summary>
/// Clone di un repository (docs/15-comandi-cervello.md): prima di partire chiede sempre dove e se con i sottomoduli.
/// git parte con gli argomenti separati, mai dentro una riga di PowerShell, così indirizzo e cartella non si interpretano.
/// </summary>
public static partial class GitClone
{
    private static readonly string[] RecursiveFlags = ["--recursive", "--recurse-submodules"];

    /// <summary>Un comando <c>git clone …</c> semplice (senza pipe né altri comandi), o null.</summary>
    public static GitCloneRequest? FromCommand(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var text = command.Trim();
        if (!CloneCommand().IsMatch(text) || ShellSyntax().IsMatch(text))
            return null;
        var tokens = Tokens().Matches(text).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Value).Skip(2).ToList();
        string? url = null;
        string? folder = null;
        bool? recursive = null;
        var options = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (RecursiveFlags.Contains(token, StringComparer.OrdinalIgnoreCase) || token.StartsWith("--recurse-submodules=", StringComparison.OrdinalIgnoreCase))
                recursive = true;
            else if (token.StartsWith('-'))
            {
                options.Add(token);
                if (!token.Contains('=', StringComparison.Ordinal) && OptionsWithValue.Contains(token) && i + 1 < tokens.Count)
                    options.Add(tokens[++i]);
            }
            else if (url is null)
                url = token;
            else
                folder ??= token;
        }
        return url is null || !LooksLikeRepository(url) ? null : new GitCloneRequest(url, folder, recursive, options);
    }

    /// <summary>"clona https://…", "fai il clone di git@…": una frase che chiede un clone e contiene l'indirizzo, o null.</summary>
    public static GitCloneRequest? FromPhrase(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!CloneWord().IsMatch(text))
            return null;
        var url = Url().Match(text);
        if (!url.Success)
            return null;
        bool? recursive = NotRecursive().IsMatch(text) ? false : Recursive().IsMatch(text) ? true : null;
        return new GitCloneRequest(url.Value.TrimEnd('.', ',', ';', ')', '"', '\''), null, recursive);
    }

    /// <summary>La richiesta, che sia scritta come comando o come frase.</summary>
    public static GitCloneRequest? From(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return CloneCommand().IsMatch(input.Trim()) ? FromCommand(input) : FromPhrase(input);
    }

    /// <summary>"https://github.com/a/progetto.git" → "progetto", come la cartella che crea git.</summary>
    public static string RepositoryName(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var name = url.TrimEnd('/', '\\');
        name = name[(name.LastIndexOfAny(['/', '\\', ':']) + 1)..];
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        name = string.Concat(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        return name.Length == 0 ? "repository" : name;
    }

    /// <summary>La cartella in cui clonare se nelle impostazioni non ce n'è una: source\repos se c'è, altrimenti quella dell'utente.</summary>
    public static string DefaultBase(string home)
    {
        ArgumentNullException.ThrowIfNull(home);
        var repos = Path.Combine(home, "source", "repos");
        return Directory.Exists(repos) ? repos : home;
    }

    /// <summary>Dove proporre il clone: la cartella detta nella richiesta, altrimenti quella di base più il nome del repository.</summary>
    public static string SuggestedFolder(GitCloneRequest request, string baseFolder)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(baseFolder);
        if (request.Folder is { Length: > 0 } folder)
            return Path.IsPathRooted(folder) ? folder : Path.Combine(baseFolder, folder);
        return Path.Combine(baseFolder, RepositoryName(request.Url));
    }

    /// <summary>Perché non si può clonare lì, o null. git vuole una cartella che non c'è o è vuota.</summary>
    public static string? Problem(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        folder = folder.Trim();
        if (folder.Length == 0)
            return "Scrivi la cartella in cui clonare.";
        if (!Path.IsPathFullyQualified(folder))
            return "Serve un percorso completo, per esempio C:\\progetti\\nome.";
        try
        {
            if (File.Exists(folder))
                return "C'è già un file con quel nome.";
            if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
                return "La cartella esiste già e non è vuota: scegline un'altra.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ex.Message;
        }
        return null;
    }

    /// <summary>Gli argomenti di git, uno per uno.</summary>
    public static IReadOnlyList<string> Arguments(GitCloneRequest request, GitCloneChoice choice)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(choice);
        var arguments = new List<string> { "clone" };
        if (choice.Recursive)
            arguments.Add("--recursive");
        arguments.AddRange(request.Options ?? []);
        arguments.Add("--");
        arguments.Add(request.Url);
        arguments.Add(choice.Folder.Trim());
        return arguments;
    }

    /// <summary>Il comando com'è da leggere (e da rifare in PowerShell): gli argomenti con spazi tra apici.</summary>
    public static string Command(GitCloneRequest request, GitCloneChoice choice) =>
        "git " + string.Join(' ', Arguments(request, choice).Where(a => a != "--").Select(a => a.Any(char.IsWhiteSpace) || a.Contains('\'') ? $"'{a.Replace("'", "''", StringComparison.Ordinal)}'" : a));

    private static bool LooksLikeRepository(string url) =>
        Url().IsMatch(url) || Path.IsPathFullyQualified(url) || url.StartsWith("file://", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> OptionsWithValue = new(StringComparer.Ordinal)
    {
        "-b", "--branch", "--depth", "-o", "--origin", "--reference", "--shallow-since", "--shallow-exclude", "-c", "--config", "--filter", "-j", "--jobs", "--separate-git-dir", "--template", "-u", "--upload-pack",
    };

    [GeneratedRegex(@"^git(?:\.exe)?\s+clone(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CloneCommand();

    /// <summary>Pipe, più comandi, variabili o sottoespressioni: non è più un clone semplice, decide il cancello.</summary>
    [GeneratedRegex(@"[|;&`<>]|\$", RegexOptions.CultureInvariant)]
    private static partial Regex ShellSyntax();

    [GeneratedRegex(@"""([^""]*)""|'([^']*)'|\S+", RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();

    [GeneratedRegex(@"\b(?:clon\w*|clone)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CloneWord();

    [GeneratedRegex(@"(?:https?|ssh|git)://[^\s""'<>]+|[\w.-]+@[\w.-]+:[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Url();

    [GeneratedRegex(@"--recursive|--recurse|sottomoduli|submodule", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Recursive();

    [GeneratedRegex(@"senza\s+(?:i\s+)?(?:sottomoduli|submodule|--recursive|ricorsiv)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotRecursive();
}
