using System.Text.RegularExpressions;

namespace NothingShell.Core.Brain;

/// <summary>Quanto è pericoloso un comando proposto, cioè se può partire senza che qualcuno prema Invio.</summary>
public enum CommandRisk
{
    /// <summary>Riconosciuto come sola lettura: è l'unico livello che una scorciatoia fidata può eseguire da sola.</summary>
    ReadOnly,

    /// <summary>Tutto il resto, compreso ciò che non si riesce a capire: serve Invio.</summary>
    Changes,

    /// <summary>Cancella, spegne, scarica ed esegue: serve Invio e il comando non si impara.</summary>
    Dangerous,
}

/// <summary>
/// Classificazione prudente del testo di un comando (docs/15-comandi-cervello.md). La sola lettura è un elenco di ciò
/// che è permesso: quello che non è riconosciuto conta come modifica. Non è una difesa contro chi vuole ingannarla:
/// serve a decidere cosa può partire senza conferma, e lì passano solo comandi già confermati in passato.
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

    /// <summary>
    /// Anche dentro un blocco di script o come parametro di un altro comando. Il confine di parola esclude i casi come
    /// <c>Get-Format</c>; il rischio è solo chiedere una conferma in più.
    /// </summary>
    [GeneratedRegex("""
        (?:^|[\s;|&({])(?:
            Remove-\w+|rm|rmdir|rd|del|erase|ri|Clear-(?:Content|RecycleBin|Disk|EventLog)|Format-Volume|format|diskpart|
            bcdedit|cipher|vssadmin|wbadmin|Stop-Computer|Restart-Computer|shutdown|Invoke-Expression|iex|Invoke-Command|icm|
            Start-Process|saps|Set-ExecutionPolicy|Add-MpPreference|Set-MpPreference|Disable-\w+|Unregister-\w+|Uninstall-\w+|
            Stop-Process|kill|taskkill|Stop-Service|takeown|icacls|net\s+(?:user|localgroup)|reg\s+(?:delete|add)|
            git\s+(?:clean|reset\s+--hard)|git\s+push\s+[^|;]*--force
        )(?=$|[\s;|&)}])
        |https?://[^\s|]+\s*\|\s*(?:iex|Invoke-Expression|sh|bash|cmd|powershell|pwsh)\b
        |DownloadString|FromBase64String|-EncodedCommand\b
        """, RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant)]
    private static partial Regex DangerousPattern();

    /// <summary>Separatori di comandi, reindirizzamenti, sottoespressioni, assegnazioni e chiamate a .NET.</summary>
    [GeneratedRegex(@"[;&<>`]|\$\(|::|\.\w+\(|\$[\w:]+\s*=|@\{", RegexOptions.CultureInvariant)]
    private static partial Regex CompoundPattern();

    [GeneratedRegex(@"(?<![\w.-])([A-Za-z]+)-[A-Za-z][A-Za-z0-9]+(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex CmdletPattern();

    public static CommandRisk Classify(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var text = command.Trim();
        if (text.Length == 0 || text.Contains('\n', StringComparison.Ordinal))
            return text.Length == 0 ? CommandRisk.Changes : Classify(text.ReplaceLineEndings(" ")) == CommandRisk.Dangerous ? CommandRisk.Dangerous : CommandRisk.Changes;
        if (DangerousPattern().IsMatch(text))
            return CommandRisk.Dangerous;
        return IsReadOnly(text) ? CommandRisk.ReadOnly : CommandRisk.Changes;
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
