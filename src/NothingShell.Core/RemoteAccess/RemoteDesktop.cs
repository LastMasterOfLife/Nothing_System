using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using NothingShell.Core.Brain;

namespace NothingShell.Core.RemoteAccess;

/// <summary>
/// Un computer raggiungibile con Desktop remoto (docs/16-assistente-notch.md). La password non è qui: sta nel Gestore
/// credenziali di Windows, nella voce <c>TERMSRV/indirizzo</c> che <c>mstsc</c> legge da solo.
/// </summary>
/// <param name="Name">Il nome della scorciatoia: "Computer di Luca".</param>
/// <param name="Aliases">Altre frasi confermate per questo computer, ridotte alle parole che contano.</param>
/// <param name="Successes">Connessioni riuscite.</param>
public sealed record RemoteComputer(
    string Name,
    string Host,
    int Port = RemoteDesktop.DefaultPort,
    string? UserName = null,
    IReadOnlyList<string>? Aliases = null,
    int Successes = 0,
    DateTimeOffset LastUsed = default)
{
    /// <summary>Indirizzo come lo scrive una persona: la porta solo se non è quella normale.</summary>
    public string Address
    {
        get
        {
            var host = Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]" : Host;
            return Port == RemoteDesktop.DefaultPort ? host : $"{host}:{Port}";
        }
    }
}

/// <summary>I comandi veri di una connessione, quelli che la finestra dell'assistente mostra ed esegue.</summary>
public static partial class RemoteDesktop
{
    public const int DefaultPort = 3389;

    /// <summary>Attesa della risposta del computer: in rete locale arriva subito, spento non arriverebbe mai.</summary>
    public const int ReachMilliseconds = 3000;

    /// <summary>Quanto si aspetta che <c>mstsc</c> apra la connessione prima di dire che non si vede.</summary>
    public const int WaitSeconds = 15;

    public const string Program = "mstsc.exe";

    public static string CredentialTarget(RemoteComputer computer)
    {
        ArgumentNullException.ThrowIfNull(computer);
        return $"TERMSRV/{computer.Host}";
    }

    public static string MstscArgument(RemoteComputer computer)
    {
        ArgumentNullException.ThrowIfNull(computer);
        return $"/v:{computer.Address}";
    }

    /// <summary>Il comando come si scriverebbe a mano.</summary>
    public static string MstscCommand(RemoteComputer computer) => $"mstsc {MstscArgument(computer)}";

    /// <summary>Il computer risponde sulla porta di Desktop remoto? Exit code 1 se non risponde in tempo.</summary>
    public static string ReachScript(RemoteComputer computer)
    {
        ArgumentNullException.ThrowIfNull(computer);
        return $"$tcp = New-Object Net.Sockets.TcpClient; if ($tcp.ConnectAsync({PowerShellText.Quote(computer.Host)}, {computer.Port}).Wait({ReachMilliseconds})) " +
               $"{{ 'raggiungibile' }} else {{ 'nessuna risposta'; exit 1 }}";
    }

    /// <summary>C'è una password salvata? <c>cmdkey</c> mostra l'utente, mai la password.</summary>
    public static string CredentialScript(RemoteComputer computer) => $"cmdkey /list:{CredentialTarget(computer)}";

    /// <summary>L'elenco di <c>cmdkey</c> è vuoto se contiene solo la riga "* NONE *", tradotta nella lingua di Windows.</summary>
    public static bool HasCredential(IEnumerable<string> cmdkeyOutput)
    {
        ArgumentNullException.ThrowIfNull(cmdkeyOutput);
        return !cmdkeyOutput.Any(line => EmptyCredentials().IsMatch(line));
    }

    /// <summary>
    /// Il processo di <c>mstsc</c> ha la connessione aperta? Dice che il computer ha accettato il collegamento, non che
    /// l'accesso è riuscito: quello si vede nella finestra di Desktop remoto.
    /// </summary>
    public static string WaitScript(RemoteComputer computer, int processId)
    {
        ArgumentNullException.ThrowIfNull(computer);
        return $"1..{WaitSeconds * 2} | ForEach-Object {{ if (Get-NetTCPConnection -OwningProcess {processId} -RemotePort {computer.Port} -State Established -ErrorAction Ignore) " +
               "{ 'connessione aperta'; exit 0 }; Start-Sleep -Milliseconds 500 }; exit 1";
    }

    /// <summary>
    /// Separa indirizzo e porta da "192.168.1.50", "pc-luca:3390" o "[fe80::1]:3390"; restituisce il problema, o null. Il
    /// valore finisce nella riga di comando di <c>mstsc</c>: si accettano solo nomi e indirizzi veri, niente parametri.
    /// </summary>
    public static string? ParseAddress(string? text, out string host, out int port)
    {
        host = string.Empty;
        port = DefaultPort;
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
            return "Scrivi l'indirizzo IP o il nome del computer.";

        var name = value;
        string? portText = null;
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
                return "Indirizzo non valido.";
            name = value[1..close];
            if (close < value.Length - 1)
            {
                if (value[close + 1] != ':')
                    return "Indirizzo non valido.";
                portText = value[(close + 2)..];
            }
            if (!IsIpv6(name))
                return "Indirizzo non valido.";
        }
        else if (value.Count(c => c == ':') > 1)
        {
            if (!IsIpv6(name))
                return "Indirizzo non valido.";
        }
        else if (value.IndexOf(':', StringComparison.Ordinal) is var colon and >= 0)
        {
            name = value[..colon];
            portText = value[(colon + 1)..];
        }

        if (!IsIpv6(name) && !HostName().IsMatch(name))
            return "L'indirizzo può contenere solo lettere, cifre, punti e trattini (es. 192.168.1.50 o pc-luca).";
        if (portText is not null && (!int.TryParse(portText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
        {
            port = DefaultPort;
            return "La porta deve essere un numero da 1 a 65535.";
        }
        host = name;
        return null;
    }

    /// <summary>Il computer da salvare, o il problema da mostrare accanto al modulo.</summary>
    public static (RemoteComputer? Computer, string? Problem) Build(string? name, string? address, string? userName, bool hasPassword, RemoteComputer? replacing = null)
    {
        var cleanName = (name ?? string.Empty).Trim();
        if (cleanName.Length == 0)
            return (null, "Dai un nome alla scorciatoia, es. Computer di Luca.");
        if (cleanName.Length > 80)
            return (null, "Il nome è troppo lungo.");
        if (ParseAddress(address, out var host, out var port) is { } problem)
            return (null, problem);
        var user = string.IsNullOrWhiteSpace(userName) ? null : userName.Trim();
        if (user is { Length: > 256 } || user?.Any(char.IsControl) == true)
            return (null, "Nome utente non valido.");
        if (hasPassword && user is null)
            return (null, "Con la password serve anche il nome utente.");
        var computer = replacing is null
            ? new RemoteComputer(cleanName, host, port, user)
            : replacing with { Name = cleanName, Host = host, Port = port, UserName = user };
        return (computer, null);
    }

    private static bool IsIpv6(string text) =>
        IPAddress.TryParse(text, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6;

    [GeneratedRegex(@"^\s*\*\s*\S+\s*\*\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex EmptyCredentials();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,253}[A-Za-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex HostName();
}
