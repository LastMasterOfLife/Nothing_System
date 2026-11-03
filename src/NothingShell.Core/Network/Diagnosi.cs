using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace NothingShell.Core.Network;

public enum DiagLevel
{
    Normal,
    Good,
    Warn,
    Bad,
    Muted,
}

/// <summary>Una riga del risultato: nome a sinistra, valore, e a destra un'annotazione (tempo, TTL).</summary>
public sealed record DiagLine(string Label, string Value, DiagLevel Level = DiagLevel.Normal, string? Extra = null);

public sealed record DiagReport(string Title, IReadOnlyList<DiagLine> Lines);

/// <summary>
/// La riga di comando della pagina Diagnosi del notch (docs/18-connettori-diagnostica.md): un indirizzo o un nome
/// (<c>miosito.it MX @8.8.8.8</c>) chiede i record DNS come prima; <c>http</c> misura le fasi di una richiesta,
/// <c>redirect</c> segue i salti, <c>propaga</c> confronta i server DNS, <c>porta</c> dice chi tiene una porta.
/// Il risultato è già scritto per righe; non solleva errori di rete: li mette nel risultato.
/// </summary>
public static class Diagnosi
{
    public const string Help =
        "Scrivi un nome per i record DNS (miosito.it, miosito.it MX @8.8.8.8). Oppure: " +
        "http miosito.it (tempi di una richiesta), redirect miosito.it (salti), propaga miosito.it (stessa risposta ovunque?), porta 5432 (chi la tiene).";

    public static async Task<DiagReport> RunAsync(string? text, CancellationToken token)
    {
        try
        {
            var (verb, rest) = Split(text);
            return verb switch
            {
                "" => new DiagReport(string.Empty, []),
                "http" => FormatHttp(await HttpProbe.SendAsync(UrlOf(rest), token)),
                "redirect" => FormatChain(await HttpProbe.ChainAsync(UrlOf(rest), token)),
                "propaga" => await PropagateAsync(rest, token),
                "porta" => FormatPort(PortOf(rest), PortOwners.Find(PortOf(rest))),
                _ => await DnsAsync(text!, token),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or SocketException or IOException or InvalidDataException or TimeoutException
                                       or AuthenticationException or ArgumentException or FormatException or UriFormatException)
        {
            return Failed(ex);
        }
    }

    /// <summary>La prima parola se è un comando, il resto dopo; senza comando il verbo è "dns" e il testo resta intero.</summary>
    public static (string Verb, string Tail) Split(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return (string.Empty, string.Empty);
        var parts = trimmed.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0].ToLowerInvariant();
        return verb switch
        {
            "http" or "tempi" => ("http", parts.Length > 1 ? parts[1] : string.Empty),
            "redirect" or "salti" => ("redirect", parts.Length > 1 ? parts[1] : string.Empty),
            "propaga" or "propagazione" => ("propaga", parts.Length > 1 ? parts[1] : string.Empty),
            "porta" or "port" => ("porta", parts.Length > 1 ? parts[1] : string.Empty),
            _ => ("dns", trimmed),
        };
    }

    /// <summary>L'indirizzo scritto, con https davanti se manca lo schema.</summary>
    public static Uri UrlOf(string text)
    {
        var s = text.Trim();
        if (s.Length == 0)
            throw new InvalidOperationException("manca l'indirizzo, es. http miosito.it");
        if (!s.Contains("://", StringComparison.Ordinal))
            s = "https://" + s;
        return Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0
            ? uri
            : throw new InvalidOperationException($"«{text.Trim()}» non è un indirizzo valido");
    }

    public static int PortOf(string text) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
            ? port
            : throw new InvalidOperationException("scrivi un numero di porta, es. porta 5432");

    private static async Task<DiagReport> DnsAsync(string text, CancellationToken token)
    {
        if (DnsRequest.Parse(text) is not { } request)
            throw new InvalidOperationException("Scrivi solo il nome, un URL, o un nome con il tipo e il server: miosito.it MX @8.8.8.8");
        return FormatDns(request, await DnsLookup.QueryAsync(request, token));
    }

    private static async Task<DiagReport> PropagateAsync(string rest, CancellationToken token)
    {
        var request = DnsRequest.Parse(rest) ?? throw new InvalidOperationException("manca il nome, es. propaga miosito.it");
        var type = request.Types.Count == DnsRequest.DefaultTypes.Count ? DnsType.A : request.Types[0];
        return FormatPropagation(await DnsPropagation.RunAsync(request.Host, type, token));
    }

    private static DiagReport Failed(Exception ex)
    {
        var message = ex is AuthenticationException ? "TLS: " + ex.Message : ex.Message;
        return new DiagReport(string.Empty, [new DiagLine(string.Empty, message, DiagLevel.Bad)]);
    }

    public static DiagReport FormatDns(DnsRequest request, DnsAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(answer);
        var lines = answer.Notes.Select(n => new DiagLine(string.Empty, n, DiagLevel.Bad)).ToList();
        // un indirizzo IP chiede il nome inverso, qualunque tipo si sia scritto
        IReadOnlyList<DnsType> types = IPAddress.TryParse(answer.Host, out _) ? [DnsType.PTR] : request.Types;
        foreach (var type in types)
        {
            var records = answer.Of(type).ToList();
            if (records.Count == 0)
                lines.Add(new DiagLine(type.ToString(), "nessuno", DiagLevel.Muted));
            for (var i = 0; i < records.Count; i++)
                lines.Add(new DiagLine(i == 0 ? type.ToString() : string.Empty, records[i].Value, DiagLevel.Normal, $"{records[i].Ttl} s"));
        }
        return new DiagReport($"{answer.Host} · {answer.Elapsed.TotalMilliseconds:0} ms · @{answer.Server}", lines);
    }

    private static readonly string[] ShownHeaders =
        ["server", "content-type", "cache-control", "age", "x-cache", "cf-cache-status", "x-powered-by", "strict-transport-security", "content-length"];

    public static DiagReport FormatHttp(HttpHop hop)
    {
        ArgumentNullException.ThrowIfNull(hop);
        var phases = new List<(string Name, long Ms)>
        {
            ("DNS", hop.DnsMs), ("Connessione", hop.ConnectMs), ("TLS", hop.TlsMs), ("Primo byte", hop.FirstByteMs), ("Scarico", Math.Max(hop.TotalMs - hop.FirstByteMs, 0)),
        };
        var slowest = phases.OrderByDescending(p => p.Ms).First();
        var lines = new List<DiagLine>
        {
            new("Risposta", $"{hop.Status} {hop.Reason}".Trim(), hop.Status >= 400 ? DiagLevel.Bad : hop.Status >= 300 ? DiagLevel.Warn : DiagLevel.Good, hop.Address),
            new("DNS", Ms(hop.DnsMs), Slow(hop.DnsMs, 300, 1000)),
            new("Connessione", Ms(hop.ConnectMs), Slow(hop.ConnectMs, 300, 1000)),
        };
        if (hop.TlsProtocol is not null)
            lines.Add(new DiagLine("TLS", Ms(hop.TlsMs), Slow(hop.TlsMs, 500, 1500), TlsName(hop.TlsProtocol)));
        lines.Add(new DiagLine("Primo byte", Ms(hop.FirstByteMs), Slow(hop.FirstByteMs, 800, 3000), "attesa del server"));
        lines.Add(new DiagLine("Totale", Ms(hop.TotalMs), DiagLevel.Normal, hop.Truncated ? "letti i primi " + Size(hop.BodyBytes) : Size(hop.BodyBytes)));
        if (hop.Location is { } location)
            lines.Add(new DiagLine("Location", location, DiagLevel.Warn));
        foreach (var name in ShownHeaders.Where(hop.Headers.ContainsKey))
            lines.Add(new DiagLine(name, hop.Headers[name], DiagLevel.Muted));
        return new DiagReport($"{hop.Url.Host} · {slowest.Name} è la fase più lenta ({Ms(slowest.Ms)})", lines);
    }

    public static DiagReport FormatChain(RedirectChain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var lines = new List<DiagLine>();
        for (var i = 0; i < chain.Hops.Count; i++)
        {
            var hop = chain.Hops[i];
            var level = hop.Status >= 400 ? DiagLevel.Bad : hop.IsRedirect ? DiagLevel.Warn : DiagLevel.Good;
            var next = hop.IsRedirect ? $" → {hop.Location}" : string.Empty;
            lines.Add(new DiagLine((i + 1).ToString(CultureInfo.InvariantCulture), $"{hop.Status} {hop.Url.AbsoluteUri}{next}", level, Ms(hop.TotalMs)));
        }
        if (chain.Downgrade)
            lines.Add(new DiagLine(string.Empty, "passa da https a http: la connessione non è più cifrata", DiagLevel.Bad));
        if (chain.Loop)
            lines.Add(new DiagLine(string.Empty, "i redirect girano in tondo", DiagLevel.Bad));
        if (chain.TooMany)
            lines.Add(new DiagLine(string.Empty, "troppi redirect (oltre 10)", DiagLevel.Bad));
        var saltati = chain.Hops.Count(h => h.IsRedirect);
        return new DiagReport($"{saltati} {(saltati == 1 ? "salto" : "salti")} · {chain.Last.Url.Host} · {Ms(chain.Hops.Sum(h => h.TotalMs))}", lines);
    }

    public static DiagReport FormatPropagation(PropagationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var lines = new List<DiagLine>
        {
            result.Consistent
                ? new DiagLine("Esito", "uguale su tutti i server che hanno risposto", DiagLevel.Good)
                : new DiagLine("Esito", "risposte diverse: la modifica non è arrivata dappertutto (o i TTL non sono scaduti)", DiagLevel.Warn),
        };
        if (!result.SerialsMatch)
        {
            var serials = string.Join(", ", result.Rows.Where(r => r.Serial is not null).Select(r => r.Serial).Distinct());
            lines.Add(new DiagLine("SOA", $"i server autorevoli hanno numeri di serie diversi: {serials}", DiagLevel.Warn));
        }
        foreach (var row in result.Rows)
        {
            var value = row.Error is not null ? row.Error : row.Values.Count == 0 ? "nessuno" : string.Join(", ", row.Values);
            lines.Add(new DiagLine(row.Name, value + (row.Serial is not null ? $"  · serial {row.Serial}" : string.Empty),
                row.Error is not null ? DiagLevel.Bad : row.Values.Count == 0 ? DiagLevel.Muted : DiagLevel.Normal, (row.Authoritative ? "autorevole · " : string.Empty) + $"{row.Ms} ms"));
        }
        return new DiagReport($"{result.Host} {result.Type}", lines);
    }

    public static DiagReport FormatPort(int port, IReadOnlyList<PortOwner> owners)
    {
        ArgumentNullException.ThrowIfNull(owners);
        var lines = new List<DiagLine>();
        if (owners.Count == 0)
            lines.Add(new DiagLine(string.Empty, $"nessuno usa la porta {port} su questo PC", DiagLevel.Muted));
        foreach (var o in owners)
        {
            var listening = o.State == "In ascolto";
            lines.Add(new DiagLine(o.State, $"{o.Process} (PID {o.Pid})", listening ? DiagLevel.Good : DiagLevel.Normal, listening ? o.Address : o.Remote));
        }
        var listeners = owners.Count(o => o.State == "In ascolto");
        return new DiagReport($"porta {port} · {listeners} in ascolto · {owners.Count - listeners} connessioni", lines);
    }

    /// <summary>"Tls13" → "TLS 1.3".</summary>
    private static string TlsName(string protocol) => protocol switch
    {
        "Tls13" => "TLS 1.3",
        "Tls12" => "TLS 1.2",
        "Tls11" => "TLS 1.1 (obsoleto)",
        "Tls" => "TLS 1.0 (obsoleto)",
        _ => protocol,
    };

    private static string Ms(long ms) => ms.ToString(CultureInfo.InvariantCulture) + " ms";

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d:0.#} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d / 1024:0.#} MB"),
    };

    private static DiagLevel Slow(long ms, long warn, long bad) => ms >= bad ? DiagLevel.Bad : ms >= warn ? DiagLevel.Warn : DiagLevel.Normal;
}
