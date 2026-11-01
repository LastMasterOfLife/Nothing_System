using System.Globalization;
using System.Text;

namespace NothingShell.Core.Brain;

/// <summary>
/// Cosa fa un comando, in italiano, per chi deve decidere se fidarsi di una scorciatoia (docs/15-comandi-cervello.md). Sono
/// regole e non il modello: si riconosce solo ciò che è in elenco, il resto viene detto com'è invece di indovinarlo.
/// </summary>
public static class CommandExplainer
{
    private static readonly Dictionary<string, string> Reads = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Get-Service"] = "legge i servizi di Windows", ["gsv"] = "legge i servizi di Windows",
        ["Get-PSDrive"] = "legge lo spazio dei dischi", ["Get-Volume"] = "legge lo spazio dei dischi",
        ["Get-NetIPAddress"] = "legge gli indirizzi IP", ["ipconfig"] = "legge la configurazione di rete",
        ["Get-NetConnectionProfile"] = "legge lo stato della connessione a Internet",
        ["Get-NetTCPConnection"] = "legge le connessioni di rete", ["netstat"] = "legge le connessioni di rete",
        ["Get-Date"] = "legge la data e l'ora", ["Get-Location"] = "legge la cartella corrente", ["pwd"] = "legge la cartella corrente",
        ["hostname"] = "legge il nome del computer", ["whoami"] = "legge il nome dell'utente",
        ["systeminfo"] = "legge le informazioni di sistema", ["tasklist"] = "legge i programmi in esecuzione",
        ["Get-ComputerInfo"] = "legge le informazioni di sistema", ["Get-LocalUser"] = "legge gli account del PC",
        ["Get-PhysicalDisk"] = "legge i dischi fisici", ["Get-Disk"] = "legge i dischi fisici", ["Get-Partition"] = "legge le partizioni",
        ["Get-Printer"] = "legge le stampanti", ["Get-PnpDevice"] = "legge i dispositivi collegati",
        ["Get-HotFix"] = "legge gli aggiornamenti installati", ["Get-TimeZone"] = "legge il fuso orario", ["Get-Culture"] = "legge la lingua del sistema",
        ["Get-ItemProperty"] = "legge le proprietà di un file o di una voce del registro", ["Get-ScheduledTask"] = "legge le attività pianificate",
        ["Get-WinEvent"] = "legge il registro degli eventi", ["Get-Host"] = "legge la versione di PowerShell",
        ["Get-NetAdapter"] = "legge le schede di rete", ["Get-DnsClientServerAddress"] = "legge i server DNS", ["Get-NetRoute"] = "legge la tabella di routing",
        ["Get-NetUDPEndpoint"] = "legge le porte UDP", ["Get-NetFirewallProfile"] = "legge lo stato del firewall",
        ["Get-MpComputerStatus"] = "legge lo stato dell'antivirus", ["Get-SmbShare"] = "legge le cartelle condivise",
    };

    private static readonly Dictionary<string, string> CimClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Win32_OperatingSystem"] = "legge i dati di Windows e della memoria",
        ["Win32_Processor"] = "legge i dati del processore",
        ["Win32_Battery"] = "legge lo stato della batteria",
        ["Win32_ComputerSystem"] = "legge i dati del computer",
        ["Win32_LogicalDisk"] = "legge i dati dei dischi",
        ["Win32_PhysicalMemory"] = "legge i dati dei moduli di memoria",
        ["Win32_VideoController"] = "legge i dati della scheda video",
        ["Win32_BaseBoard"] = "legge i dati della scheda madre",
        ["Win32_BIOS"] = "legge i dati del BIOS",
        ["Win32_SoundDevice"] = "legge i dispositivi audio",
        ["Win32_StartupCommand"] = "legge i programmi che partono all'avvio",
    };

    private static readonly Dictionary<string, string> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Get"] = "legge", ["Test"] = "controlla", ["New"] = "crea", ["Add"] = "aggiunge", ["Remove"] = "cancella", ["Clear"] = "svuota",
        ["Set"] = "cambia", ["Copy"] = "copia", ["Move"] = "sposta", ["Rename"] = "rinomina", ["Stop"] = "ferma", ["Start"] = "avvia",
        ["Restart"] = "riavvia", ["Enable"] = "attiva", ["Disable"] = "disattiva", ["Install"] = "installa", ["Uninstall"] = "disinstalla",
        ["Invoke"] = "esegue",
    };

    private static readonly Dictionary<string, string> Nouns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Item"] = "un file o una cartella", ["Service"] = "un servizio", ["Process"] = "un programma", ["Computer"] = "il computer",
        ["Content"] = "il contenuto di un file", ["RecycleBin"] = "il cestino",
    };

    private static readonly Dictionary<string, string> Comparisons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["-eq"] = "uguale a", ["-ne"] = "diverso da", ["-gt"] = "maggiore di", ["-ge"] = "almeno", ["-lt"] = "minore di", ["-le"] = "al massimo",
        ["-like"] = "simile a", ["-match"] = "che contiene",
    };

    /// <summary>Una frase con i passaggi del comando, nell'ordine in cui avvengono. Un modello lascia <c>{0}</c> dov'è il valore.</summary>
    public static string Describe(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var steps = Split(command).Select(Step).Where(s => s.Length > 0).ToArray();
        if (steps.Length == 0)
            return string.Empty;
        var text = string.Join(", poi ", steps);
        return char.ToUpper(text[0], CultureInfo.GetCultureInfo("it-IT")) + text[1..] + ".";
    }

    /// <summary>Cosa succede quando parte: lo stesso calcolo delle decisioni, fatto adesso, così vale anche per l'elenco di oggi.</summary>
    public static string Effect(Shortcut shortcut)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        if (CommandSafety.Reason(shortcut.Command) is { } reason)
            return $"Critico: {reason}. Parte solo dopo una conferma esplicita: due clic nel notch, scrivere SI nel terminale.";
        if (CommandSafety.Classify(shortcut.Command) != CommandRisk.ReadOnly)
            return "Non è riconosciuto come sola lettura, anche se può esserlo: nel terminale chiede sempre Invio.";
        return shortcut.Successes >= ShortcutMemory.TrustedAfter && shortcut.Failures == 0
            ? "Riconosciuto come sola lettura e già riuscito abbastanza volte: nel terminale parte da solo, senza Invio."
            : $"Riconosciuto come sola lettura: nel terminale parte da solo dopo {ShortcutMemory.TrustedAfter} riuscite di fila.";
    }

    private static string Step(string segment)
    {
        var words = Words(segment);
        if (words.Count == 0)
            return string.Empty;
        var name = words[0];
        var rest = words.Skip(1).ToArray();
        switch (name.ToUpperInvariant())
        {
            case "GET-PROCESS" or "GPS" or "PS":
                return Value(rest, "-Name") is { } program ? $"legge il programma {program}" : "legge i programmi in esecuzione";
            case "GET-CHILDITEM" or "GCI" or "LS" or "DIR":
                return Listing(rest);
            case "GET-CONTENT" or "GC" or "CAT" or "TYPE":
                return Value(rest, "-Path") is { } file ? $"legge il contenuto di {file}" : "legge il contenuto di un file";
            case "GET-CIMINSTANCE" or "GET-WMIOBJECT":
                var cimClass = Value(rest, "-ClassName", "-Class") ?? string.Empty;
                return CimClasses.TryGetValue(cimClass, out var cim) ? cim : $"legge i dati di sistema {cimClass}".TrimEnd();
            case "GET-NETTCPCONNECTION" when Value(rest, "-State") is { } state && state.Equals("Listen", StringComparison.OrdinalIgnoreCase):
                return "legge le porte in ascolto";
            case "SELECT-OBJECT" or "SELECT":
                return Selection(rest);
            case "SORT-OBJECT" or "SORT":
                return Sorting(rest);
            case "WHERE-OBJECT" or "WHERE" or "?":
                return Filter(rest);
            case "MEASURE-OBJECT" or "MEASURE":
                return Has(rest, "-Sum") ? $"somma {Value(rest, "-Property") ?? "i valori"}" : "li conta";
            case "GROUP-OBJECT" or "GROUP":
                return Value(rest, "-Property") is { } group ? $"li raggruppa per {group}" : "li raggruppa";
            case "SELECT-STRING":
                return Value(rest, "-Pattern") is { } pattern ? $"cerca {pattern}" : "cerca un testo";
            case "FORMAT-TABLE" or "FT":
                return "li mostra in tabella";
            case "FORMAT-LIST" or "FL":
                return "li mostra in elenco";
            case "OUT-STRING":
                return string.Empty;
            case "GIT":
                return Git(rest);
        }
        if (Reads.TryGetValue(name, out var read))
            return read;

        var dash = name.IndexOf('-', StringComparison.Ordinal);
        if (dash > 0 && Verbs.TryGetValue(name[..dash], out var verb))
        {
            var noun = name[(dash + 1)..];
            var what = Nouns.GetValueOrDefault(noun, noun);
            return Value(rest, "-Name", "-Path") is { } target ? $"{verb} {what} ({target})" : $"{verb} {what}";
        }
        return $"avvia {name}, che non so descrivere: leggi il comando qui sotto";
    }

    private static string Listing(string[] words)
    {
        var text = new StringBuilder(Has(words, "-File") ? "elenca i file" : Has(words, "-Directory") ? "elenca le cartelle" : Has(words, "-Hidden") ? "elenca file e cartelle nascosti" : "elenca file e cartelle");
        if (Value(words, "-Path") is { } path)
            text.Append(" in ").Append(path);
        if (After(words, "-Filter") is { } filter)
            text.Append(" con il nome ").Append(filter);
        if (Has(words, "-Recurse"))
            text.Append(", anche nelle sottocartelle");
        return text.ToString();
    }

    private static string Selection(string[] words)
    {
        var first = After(words, "-First");
        var last = After(words, "-Last");
        var fields = Fields(words);
        var limit = first is not null ? $"tiene i primi {first}" : last is not null ? $"tiene gli ultimi {last}" : null;
        return (limit, fields.Length > 0) switch
        {
            (null, true) => $"tiene {string.Join(", ", fields)}",
            (not null, true) => $"{limit} ({string.Join(", ", fields)})",
            (not null, false) => limit,
            _ => "tiene solo alcuni dati",
        };
    }

    /// <summary>I nomi delle colonne: quelli calcolati (<c>@{n='LiberoGB';e={...}}</c>) col nome che hanno.</summary>
    private static string[] Fields(string[] words)
    {
        var fields = new List<string>();
        for (var i = 0; i < words.Length; i++)
        {
            if (words[i].StartsWith('-'))
            {
                if (words[i].Equals("-First", StringComparison.OrdinalIgnoreCase) || words[i].Equals("-Last", StringComparison.OrdinalIgnoreCase))
                    i++;
                continue;
            }
            foreach (var part in TopLevel(words[i], ','))
            {
                var item = part.Trim();
                if (item.StartsWith("@{", StringComparison.Ordinal))
                {
                    var open = item.IndexOf('\'', StringComparison.Ordinal);
                    var close = open < 0 ? -1 : item.IndexOf('\'', open + 1);
                    item = close > open ? item[(open + 1)..close] : string.Empty;
                }
                if (item.Length > 0)
                    fields.Add(item);
            }
        }
        return fields.ToArray();
    }

    private static string Sorting(string[] words)
    {
        var by = words.FirstOrDefault(w => !w.StartsWith('-'));
        var isDate = by is not null && (by.EndsWith("Time", StringComparison.OrdinalIgnoreCase) || by.EndsWith("On", StringComparison.Ordinal));
        var order = !Has(words, "-Descending") ? string.Empty : isDate ? ", dal più recente" : ", dal più grande";
        return by is null ? "li ordina" : $"li ordina per {by}{order}";
    }

    private static string Filter(string[] words)
    {
        if (words.Length == 3 && Comparisons.TryGetValue(words[1], out var comparison))
            return $"tiene quelli con {words[0]} {comparison} {words[2]}";
        return "ne tiene solo alcuni";
    }

    private static string Git(string[] words)
    {
        var rest = words.Length >= 2 && words[0] == "-C" ? words[2..] : words;
        var where = rest.Length != words.Length ? $" di {words[1]}" : string.Empty;
        return rest.FirstOrDefault()?.ToUpperInvariant() switch
        {
            "STATUS" => $"legge lo stato del repository{where}",
            "LOG" => $"legge gli ultimi commit{where}",
            "DIFF" => $"legge le modifiche{where}",
            "SHOW" => $"legge un commit{where}",
            "SHORTLOG" => $"legge gli autori dei commit{where}",
            "BRANCH" when CommandSafety.IsGitListing(rest) => $"legge i branch{where}",
            "TAG" when CommandSafety.IsGitListing(rest) => $"legge i tag{where}",
            "REMOTE" when CommandSafety.IsGitListing(rest) => $"legge i repository remoti{where}",
            null => "avvia git",
            _ => $"esegue git {rest[0]}{where}",
        };
    }

    private static bool Has(string[] words, string flag) => words.Any(w => w.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static string? After(string[] words, string flag)
    {
        var at = Array.FindIndex(words, w => w.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return at >= 0 && at + 1 < words.Length ? words[at + 1] : null;
    }

    /// <summary>Il valore di uno dei parametri, oppure il primo argomento scritto senza nome.</summary>
    private static string? Value(string[] words, params string[] flags)
    {
        foreach (var flag in flags)
        {
            if (After(words, flag) is { } named)
                return named;
        }
        for (var i = 0; i < words.Length; i++)
        {
            if (!words[i].StartsWith('-'))
                return words[i];
            i++; // si presume che ogni parametro abbia il suo valore
        }
        return null;
    }

    /// <summary>I comandi di una riga: separati da <c>|</c> o <c>;</c> fuori da virgolette e parentesi.</summary>
    private static IEnumerable<string> Split(string command) => TopLevel(command.Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ';'), '|', ';');

    /// <summary>Parole separate da spazi fuori da virgolette e parentesi; le virgole di un elenco non restano appese.</summary>
    private static List<string> Words(string segment)
    {
        var words = new List<string>();
        foreach (var part in TopLevel(segment, ' ', '\t'))
        {
            var word = part.Trim();
            if (word.Length == 0)
                continue;
            if (words.Count > 0 && (words[^1].EndsWith(',') || word.StartsWith(',')))
                words[^1] += word; // "Name, Id" è un argomento solo
            else if (word[0] is '\'' or '"' && word.Length > 1 && word[^1] == word[0])
                words.Add(word[1..^1]);
            else
                words.Add(word);
        }
        return words;
    }

    private static IEnumerable<string> TopLevel(string text, params char[] separators)
    {
        var part = new StringBuilder();
        var depth = 0;
        var quote = '\0';
        foreach (var c in text)
        {
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
            }
            else if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c is '{' or '(')
            {
                depth++;
            }
            else if (c is '}' or ')')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0 && separators.Contains(c))
            {
                yield return part.ToString();
                part.Clear();
                continue;
            }
            part.Append(c);
        }
        yield return part.ToString();
    }
}
