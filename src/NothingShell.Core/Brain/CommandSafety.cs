using System.Text.RegularExpressions;

namespace NothingShell.Core.Brain;

/// <summary>Quanto è pericoloso un comando proposto, cioè se può partire senza che qualcuno prema Invio.</summary>
public enum CommandRisk
{
    /// <summary>Riconosciuto come sola lettura: è l'unico livello che una scorciatoia fidata può eseguire da sola.</summary>
    ReadOnly,

    /// <summary>Tutto il resto, compreso ciò che non si riesce a capire: serve Invio.</summary>
    Changes,

    /// <summary>È nell'elenco dei comandi critici (<see cref="CriticalCommands"/>): serve sempre una conferma esplicita.</summary>
    Dangerous,
}

/// <summary>
/// Classificazione prudente del testo di un comando (docs/15-comandi-cervello.md). La sola lettura è un elenco di ciò
/// che è permesso: quello che non è riconosciuto conta come modifica, quello critico è nell'elenco a parte. Non è una
/// difesa contro chi vuole ingannarla: a far partire da solo ciò che propone il modello ci pensa l'esecutore limitato
/// (tools/NothingShell.Agent.ps1), che guarda l'albero sintattico vero.
/// </summary>
public static partial class CommandSafety
{
    private static readonly HashSet<string> ReadVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Get", "Test", "Measure", "Select", "Where", "Sort", "Format", "Group", "Compare", "Find", "Resolve", "Join",
        "Split", "ConvertTo", "ConvertFrom",
    };

    /// <summary>Alias e programmi di sola lettura, nella forma senza opzioni con la barra.</summary>
    private static readonly HashSet<string> ReadPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "ls", "dir", "gci", "gc", "cat", "type", "pwd", "gl", "ps", "gps", "gsv", "gcm", "where", "select", "sort", "ft",
        "fl", "fw", "group", "measure", "?", "hostname", "whoami", "netstat", "systeminfo", "tasklist", "date", "uptime",
    };

    private static readonly HashSet<string> ReadGit = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "log", "diff", "show", "blame", "shortlog", "describe", "rev-parse",
    };

    /// <summary>Separatori di comandi, reindirizzamenti, sottoespressioni, assegnazioni e chiamate a .NET.</summary>
    [GeneratedRegex(@"[;&<>`]|\$\(|::|\.\w+\(|\$[\w:]+\s*=|@\{", RegexOptions.CultureInvariant)]
    private static partial Regex CompoundPattern();

    [GeneratedRegex(@"(?<![\w.-])([A-Za-z]+)-[A-Za-z][A-Za-z0-9]+(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex CmdletPattern();

    public static CommandRisk Classify(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var text = command.Trim();
        if (text.Length == 0)
            return CommandRisk.Changes;
        if (Reason(text) is not null)
            return CommandRisk.Dangerous;
        return !text.Contains('\n', StringComparison.Ordinal) && IsReadOnly(text) ? CommandRisk.ReadOnly : CommandRisk.Changes;
    }

    /// <summary>Perché il comando è critico, da mostrare a chi deve confermare; null se non lo è.</summary>
    public static string? Reason(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var text = command.Trim();
        var readOnly = !text.Contains('\n', StringComparison.Ordinal) && IsReadOnly(text);
        return CriticalCommands.Reason(CriticalCommands.Normalize(text), readOnly);
    }

    private static bool IsReadOnly(string text)
    {
        if (CompoundPattern().IsMatch(text.Replace("[math]::", string.Empty, StringComparison.OrdinalIgnoreCase)))
            return false;
        foreach (Match cmdlet in CmdletPattern().Matches(StripQuoted(text)))
        {
            // I parametri (-Name) non corrispondono: il prefisso è escluso dal lookbehind.
            if (!ReadVerbs.Contains(cmdlet.Groups[1].Value))
                return false;
        }
        return text.Split('|').All(IsReadOnlySegment);
    }

    private static bool IsReadOnlySegment(string segment)
    {
        var words = segment.Trim().TrimStart('(').Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
            return false;
        var name = words[0];
        var dash = name.IndexOf('-', StringComparison.Ordinal);
        if (dash > 0 && ReadVerbs.Contains(name[..dash]))
            return !words.Any(w => w.StartsWith("-OutFile", StringComparison.OrdinalIgnoreCase));
        if (name.Equals("git", StringComparison.OrdinalIgnoreCase))
            return words.Length > 1 && ReadGit.Contains(words[1]);
        if (name.Equals("ipconfig", StringComparison.OrdinalIgnoreCase))
            return words.Length == 1 || (words.Length == 2 && words[1].Equals("/all", StringComparison.OrdinalIgnoreCase));
        return ReadPrograms.Contains(name) && !words.Skip(1).Any(w => w.StartsWith('/'));
    }

    private static string StripQuoted(string text) => QuotedPattern().Replace(text, "''");

    [GeneratedRegex("""'[^']*'|"[^"]*"|\{[^{}]*-(?:eq|ne|match|like|gt|lt|ge|le)\b""", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedPattern();
}
