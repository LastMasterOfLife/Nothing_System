using System.Text;
using System.Text.RegularExpressions;
using NothingShell.Core.Storage;

namespace NothingShell.Core.Brain;

/// <summary>Comandi con lo stesso effetto: quello che può andare storto, scritto per chi deve decidere.</summary>
public sealed record CriticalGroup(string Reason, IReadOnlyList<string> Commands);

/// <summary>
/// L'elenco dei comandi critici (docs/15-comandi-cervello.md): cancellano dati, resettano o rendono inutilizzabile la
/// macchina. Chiunque li proponga, scorciatoie comprese, partono solo dopo una conferma esplicita. Si scrivono come al
/// prompt: <c>git reset --hard</c>, con <c>*</c> in fondo a una parola per tutta una famiglia (<c>Remove-*</c>), e fra una
/// parola e l'altra possono esserci altri argomenti. L'elenco si allarga con <see cref="DefaultUserPath"/>.
/// </summary>
public static class CriticalCommands
{
    public const string UserReason = "È nel tuo elenco dei comandi critici";

    private const string SystemPathReason = "Tocca una cartella di sistema o del registro";
    private static readonly TimeSpan MatchLimit = TimeSpan.FromMilliseconds(100);

    /// <summary>Inizio di un comando: la riga, dopo un separatore, o dentro <c>cmd /c "..."</c>; il percorso conta poco.</summary>
    private const string CommandStart =
        """(?:^|[;|&({]|\b(?:cmd(?:\.exe)?\s+/[ck]|(?:powershell|pwsh)(?:\.exe)?(?:\s+-\w+)*|wsl(?:\.exe)?(?:\s+-\w+)*|sudo))\s*["']?\s*(?:[\w:.~-]*[\\/])*""";

    private const string WordEnd = """(?=$|[\s;|&)}"',])""";
    private const string WordGap = """\s+(?:[^|;&]*?\s)??""";

    public static IReadOnlyList<CriticalGroup> All { get; } =
    [
        new("Cancella file, cartelle o altri dati", [
            "Remove-*", "rm", "rmdir", "rd", "del", "erase", "ri", "Clear-Content", "Clear-Item", "Clear-ItemProperty", "Clear-RecycleBin",
            "robocopy /mir", "robocopy /purge", "robocopy /move", "cipher /w", "sdelete", "forfiles",
        ]),
        new("Cancella, formatta o ripartiziona un disco", [
            "Format-Volume", "format", "diskpart", "Clear-Disk", "Initialize-Disk", "New-Partition", "Set-Partition", "Resize-Partition",
            "Dismount-*", "mkfs", "dd", "fsutil", "mountvol", "manage-bde", "chkdsk /f", "chkdsk /r", "chkdsk /x",
        ]),
        new("Reimposta o ripristina il sistema, o azzera una memoria", [
            "systemreset", "Reset-*", "Restore-*", "Repair-*", "Initialize-*", "rstrui", "sysprep", "bcdedit", "bcdboot", "bootrec", "bootsect",
            "sfc", "dism", "vssadmin", "wbadmin", "Clear-Tpm", "Clear-History", "Clear-EventLog", "Clear-Variable", "wevtutil cl",
            "netsh winsock reset", "netsh int reset", "netsh interface reset", "dimentica",
        ]),
        new("Spegne, riavvia o chiude la sessione", ["Stop-Computer", "Restart-Computer", "Suspend-Computer", "shutdown", "logoff"]),
        new("Chiude processi o cambia i servizi di Windows", [
            "Stop-Process", "kill", "taskkill", "spps", "Stop-Service", "Restart-Service", "Suspend-Service", "Set-Service", "New-Service",
            "sc stop", "sc delete", "sc config", "sc create", "net stop", "schtasks /delete", "schtasks /create", "schtasks /change",
        ]),
        new("Cambia utenti, password o permessi", [
            "takeown", "icacls", "cacls", "Set-Acl", "net user", "net localgroup", "New-LocalUser", "Set-LocalUser", "Add-LocalGroupMember",
            "runas", "Set-ExecutionPolicy", "Unblock-File",
        ]),
        new("Abbassa le protezioni del sistema", [
            "Disable-*", "Set-MpPreference", "Add-MpPreference", "Set-NetFirewallProfile", "Set-NetFirewallRule", "New-NetFirewallRule",
            "netsh advfirewall", "netsh firewall",
        ]),
        new("Modifica il registro di sistema", ["reg add", "reg delete", "reg import", "reg restore", "regedit"]),
        new("Disinstalla programmi o cancella ambienti", [
            "Uninstall-*", "Unregister-*", "winget uninstall", "choco uninstall", "msiexec /x", "wsl --unregister", "wsl --uninstall",
            "docker rm", "docker rmi", "docker system prune", "docker volume prune", "docker volume rm",
        ]),
        new("Avvia codice che non si può controllare prima", [
            "Invoke-Expression", "iex", "Invoke-Command", "icm", "Invoke-CimMethod", "Invoke-WmiMethod", "Start-Process", "saps", "start",
            "Add-Type", "mshta", "regsvr32", "rundll32", "wscript", "cscript",
        ]),
        new("Può far perdere lavoro su git", [
            "git clean", "git reset --hard", "git push --force", "git push -f", "git push --force-with-lease", "git checkout --", "git restore",
            "git branch -D", "git stash drop", "git stash clear", "git filter-branch", "git reflog expire", "git gc --prune",
        ]),
    ];

    /// <summary>Quello che un elenco di nomi non vede: metodi .NET, codice scaricato o nascosto, nomi calcolati.</summary>
    private static readonly (string Reason, Regex Pattern)[] Forms =
    [
        ("Cancella file, cartelle o altri dati", Pattern(@"\.(?:Delete|MoveTo)\s*\(|::(?:Delete|Move|Replace)\s*\(")),
        ("Chiude processi o cambia i servizi di Windows", Pattern(@"\.(?:Kill|Terminate)\s*\(")),
        ("Avvia codice che non si può controllare prima", Pattern(
            @"https?://[^\s|]+\s*\|\s*(?:iex|Invoke-Expression|sh|bash|cmd|powershell|pwsh)\b|DownloadString|DownloadFile|FromBase64String|\s-enc(?:odedcommand)?\b|\]::Start\s*\(|(?:^|[;|({\s])&\s*[$(]")),
    ];

    private static readonly (string Reason, Regex Pattern)[] Compiled = All.Select(g => (g.Reason, Compile(g.Commands))).ToArray();

    private static readonly Regex SystemPaths = Pattern(
        """[A-Za-z]:[\\/](?:Windows|Program Files|ProgramData|System Volume Information|Recovery|Boot|\$Recycle\.Bin)\b|\$env:(?:windir|SystemRoot|ProgramFiles|ProgramData)\b|\bSystem32\b|\bHKLM:|\bHKEY_LOCAL_MACHINE\b|\.nothing-shell\b|\bbootmgr\b|[A-Za-z]:[\\/]?\*?(?=$|[\s"'])""");

    private static Regex? s_user;

    /// <summary>File dell'utente: una voce per riga, come nell'elenco; le righe con <c>#</c> sono commenti.</summary>
    public static string DefaultUserPath { get; } = Path.Combine(AtomicFile.DataFolder, "brain", "comandi-critici.txt");

    /// <summary>Le voci dell'utente si aggiungono a quelle di serie, che non si possono togliere.</summary>
    public static void SetUserCommands(IEnumerable<string> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var list = commands.Select(c => c.Trim()).Where(c => c.Length > 0 && !c.StartsWith('#')).ToArray();
        s_user = list.Length == 0 ? null : Compile(list);
    }

    /// <summary>Legge il file dell'utente; un file che non c'è non cambia nulla.</summary>
    public static void LoadUserFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (AtomicFile.TryReadAllText(path) is { } text)
            SetUserCommands(text.Split('\n'));
    }

    /// <summary>Il testo con cui si crea il file dell'utente, da aprire con le impostazioni.</summary>
    public static string UserFileTemplate { get; } =
        "# Comandi critici aggiuntivi di Nothing Shell: uno per riga, come al prompt, * in fondo a una parola per una famiglia.\n" +
        "# Si aggiungono a quelli di serie, che non si possono togliere. Scritti qui a mano valgono dal prossimo avvio.\n" +
        "# Per esempio:\n# kubectl delete\n# terraform destroy\n";

    /// <summary>
    /// La voce dell'elenco per il comando di una scorciatoia: il comando esatto, scritto come lo confronta <see cref="Reason"/>;
    /// il valore di un modello ("Get-Process {0}") diventa <c>*</c>. Null se non si può scrivere come voce.
    /// </summary>
    public static string? UserEntry(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var entry = Normalize(command).Replace(ShortcutMemory.Slot, "*", StringComparison.Ordinal);
        return entry.Length == 0 || entry.StartsWith('#') ? null : entry;
    }

    /// <summary>True se la voce di <paramref name="command"/> è nel file dell'utente.</summary>
    public static bool IsInUserFile(string path, string command)
    {
        ArgumentNullException.ThrowIfNull(path);
        return UserEntry(command) is { } entry && (AtomicFile.TryReadAllText(path) ?? string.Empty).Split('\n').Any(line => IsEntry(line, entry));
    }

    /// <summary>Aggiunge la voce al file dell'utente e vale da subito; false se il comando non si può scrivere come voce.</summary>
    public static bool AddToUserFile(string path, string command)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (UserEntry(command) is not { } entry)
            return false;
        if (!IsInUserFile(path, command))
        {
            var text = AtomicFile.TryReadAllText(path) ?? UserFileTemplate;
            AtomicFile.WriteAllText(path, $"{text}{(text.EndsWith('\n') ? string.Empty : "\n")}{entry}\n");
        }
        LoadUserFile(path);
        return true;
    }

    /// <summary>Toglie la voce dal file dell'utente e vale da subito. Quelle di serie non stanno lì e restano.</summary>
    public static void RemoveFromUserFile(string path, string command)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (UserEntry(command) is not { } entry || AtomicFile.TryReadAllText(path) is not { } text)
            return;
        AtomicFile.WriteAllText(path, string.Join('\n', text.Split('\n').Where(line => !IsEntry(line, entry))));
        LoadUserFile(path);
    }

    /// <summary>True se <paramref name="entry"/>, scritta come una voce dell'elenco, vale per <paramref name="command"/>.</summary>
    public static bool Covers(string entry, string command)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(command);
        return Compile([entry]).IsMatch(Normalize(command));
    }

    private static bool IsEntry(string line, string entry) => line.Trim().Equals(entry, StringComparison.OrdinalIgnoreCase);

    /// <summary>Il motivo per cui <paramref name="normalized"/> è critico, o null. Il testo è quello di <see cref="Normalize"/>.</summary>
    public static string? Reason(string normalized, bool readOnly)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        try
        {
            if (s_user?.IsMatch(normalized) == true)
                return UserReason;
            foreach (var (reason, pattern) in Compiled)
            {
                if (pattern.IsMatch(normalized))
                    return reason;
            }
            foreach (var (reason, pattern) in Forms)
            {
                if (pattern.IsMatch(normalized))
                    return reason;
            }
            return !readOnly && SystemPaths.IsMatch(normalized) ? SystemPathReason : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return "Il comando è troppo complicato da controllare";
        }
    }

    /// <summary>
    /// Per il confronto: PowerShell legge i trattini lunghi come trattini e le virgolette curve come virgolette, ignora
    /// gli apici inversi e le coppie vuote (<c>r''m</c>), e <c>cmd</c> ignora <c>^</c>: scritture diverse, stesso comando.
    /// </summary>
    public static string Normalize(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var text = new StringBuilder(command.Length);
        foreach (var c in command)
        {
            switch (c)
            {
                case '`' or '^':
                    break;
                case '–' or '—' or '―':
                    text.Append('-');
                    break;
                case '‘' or '’' or '‚' or '‛':
                    text.Append('\'');
                    break;
                case '“' or '”' or '„':
                    text.Append('"');
                    break;
                case '\r' or '\n':
                    text.Append(';');
                    break;
                default:
                    text.Append(c);
                    break;
            }
        }
        return text.ToString().Replace("''", string.Empty, StringComparison.Ordinal).Replace("\"\"", string.Empty, StringComparison.Ordinal).Trim();
    }

    private static Regex Compile(IEnumerable<string> commands) =>
        Pattern(string.Join('|', commands.Select(Entry)));

    /// <summary>Verbo-Nome conta ovunque, anche fra virgolette; le parole corte (rm, del) solo dove comincia un comando.</summary>
    private static string Entry(string command)
    {
        var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var body = string.Join(WordGap, words.Select(Word));
        var isCmdlet = words.Length == 1 && CmdletName().IsMatch(words[0]);
        return isCmdlet ? $@"(?<![\w-]){body}{WordEnd}" : $@"{CommandStart}{body}(?:\.(?:exe|com|cmd|bat))?{WordEnd}";
    }

    /// <summary><c>*</c> vale anche per un <c>*</c> scritto davvero nel comando: la voce di <c>dir *.txt</c> deve coprire <c>dir *.txt</c>.</summary>
    private static string Word(string word) =>
        string.Join(@"[\w.*-]*", word.Split('*').Select(Regex.Escape));

    private static Regex CmdletName() => new(@"^[A-Za-z]+-[A-Za-z*]+$", RegexOptions.CultureInvariant);

    private static Regex Pattern(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, MatchLimit);
}
