using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace NothingShell.Core.Network;

/// <summary>Tipi di record che la pagina DNS del notch sa chiedere e leggere.</summary>
public enum DnsType : ushort
{
    A = 1,
    NS = 2,
    CNAME = 5,
    SOA = 6,
#pragma warning disable CA1720 // PTR è il nome del record, come lo scrive chi usa dig
    PTR = 12,
#pragma warning restore CA1720
    MX = 15,
    TXT = 16,
    AAAA = 28,
    SRV = 33,
    DS = 43,
    DNSKEY = 48,
    CAA = 257,
}

/// <summary>Un record della risposta: nome, tipo, TTL e valore già scritto per esteso.</summary>
public sealed record DnsRecord(string Name, DnsType Type, int Ttl, string Value);

/// <summary>Cosa ha scritto l'utente: il nome, che cosa chiedere e a chi (<c>miosito.it MX @8.8.8.8</c>).</summary>
public sealed record DnsRequest(string Host, IReadOnlyList<DnsType> Types, string? Server)
{
    /// <summary>Di serie, come Google Dig, si chiede ciò che serve per capire un dominio.</summary>
    public static IReadOnlyList<DnsType> DefaultTypes { get; } =
        [DnsType.A, DnsType.AAAA, DnsType.CNAME, DnsType.MX, DnsType.NS, DnsType.TXT, DnsType.SOA, DnsType.CAA, DnsType.DS];

    private static readonly Regex Scheme = new(@"^[A-Za-z][A-Za-z0-9+.\-]*://", RegexOptions.Compiled);

    /// <summary>
    /// Il testo del campo: un indirizzo o un URL (<c>https://utente@miosito.it:8080/pagina?x=1</c> diventa <c>miosito.it</c>),
    /// poi, in qualsiasi ordine, un tipo di record (<c>MX</c>, <c>TXT</c>…, <c>ANY</c> o <c>TUTTI</c> per i principali) e un
    /// server DNS preceduto da <c>@</c>. Un indirizzo IP chiede il nome inverso (PTR). Null se non c'è un nome valido.
    /// </summary>
    public static DnsRequest? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        string? host = null;
        string? server = null;
        var types = new List<DnsType>();
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith('@') && token.Length > 1)
            {
                server = token[1..];
            }
            else if (host is not null && token.ToUpperInvariant() is "ANY" or "TUTTI" or "ALL")
            {
                types.AddRange(DefaultTypes);
            }
            else if (host is not null && Enum.TryParse<DnsType>(token, ignoreCase: true, out var type) && Enum.IsDefined(type))
            {
                types.Add(type);
            }
            else if (host is null)
            {
                host = HostOf(token);
            }
            else
            {
                return null; // una parola in più che non è né tipo né server
            }
        }
        if (host is null || host.Length == 0 || host.Length > 253 || !Regex.IsMatch(host, @"^[\p{L}\p{N}_.\-:]+$"))
            return null;
        if (server is not null && !Regex.IsMatch(server, @"^[\w.\-:\[\]]+$"))
            return null;
        if (types.Count == 0)
            types.AddRange(DefaultTypes);
        else
            types = types.Distinct().ToList();
        return new DnsRequest(host, types, server);
    }

    /// <summary>Il nome di dominio di un URL o di un indirizzo: senza schema, utente, porta, percorso né punto finale.</summary>
    public static string HostOf(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var s = Scheme.Replace(address.Trim(), string.Empty);
        var end = s.IndexOfAny(['/', '?', '#']);
        if (end >= 0)
            s = s[..end];
        var at = s.LastIndexOf('@');
        if (at >= 0)
            s = s[(at + 1)..];
        if (s.StartsWith('['))
            return s.Contains(']') ? s[1..s.IndexOf(']')] : s;
        if (s.Count(c => c == ':') == 1)
            s = s[..s.IndexOf(':')];
        return s.TrimEnd('.').ToLowerInvariant();
    }
}

/// <summary>Esito di una richiesta: i record per tipo, il server che ha risposto e quanto ci ha messo.</summary>
public sealed record DnsAnswer(string Host, string Server, TimeSpan Elapsed, IReadOnlyList<DnsRecord> Records, IReadOnlyList<string> Notes)
{
    public IEnumerable<DnsRecord> Of(DnsType type) => Records.Where(r => r.Type == type);
}

/// <summary>
/// Client DNS minimo (RFC 1035): una domanda per tipo su UDP, di nuovo su TCP se la risposta è troncata. Non serve
/// PowerShell né nessun permesso; legge solo record pubblici (docs/17-dns-notch.md).
/// </summary>
public static class DnsLookup
{
    private const int MaxNameJumps = 32;

    /// <summary>L'ultimo server di sistema che ha risposto: la volta dopo si comincia da lui, senza aspettare quelli morti (VPN).</summary>
    private static IPAddress? _preferred;

    public static List<IPAddress> SystemServers() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().DnsAddresses)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork || (a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal))
            .Distinct()
            .ToList();

    public static async Task<DnsAnswer> QueryAsync(DnsRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        var notes = new List<string>();
        var servers = await ServersAsync(request.Server, token);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        // un indirizzo IP chiede il nome inverso; altrimenti una domanda per ogni tipo
        var isIp = IPAddress.TryParse(request.Host, out var ip);
        var name = isIp ? ReverseName(ip!) : request.Host;
        IReadOnlyList<DnsType> types = isIp ? [DnsType.PTR] : request.Types;

        // la prima domanda prova i server uno dopo l'altro (quello di una VPN spesso non risponde ai nomi pubblici),
        // le altre vanno in parallelo a quello che ha risposto
        var results = new List<(DnsType Type, ParsedAnswer? Answer, string? Error)>();
        IPAddress? server = null;
        foreach (var candidate in servers)
        {
            try
            {
                results.Add((types[0], await AskAsync(candidate, name, types[0], token), null));
                server = candidate;
                break;
            }
            catch (Exception ex) when (IsNetworkFailure(ex, token))
            {
                notes.Add($"{candidate}: {Describe(ex)}");
            }
        }
        if (server is null)
        {
            notes.Insert(0, "nessun server DNS ha risposto");
            return new DnsAnswer(request.Host, servers[^1].ToString(), System.Diagnostics.Stopwatch.GetElapsedTime(started), [], notes);
        }
        if (request.Server is null)
            _preferred = server;
        notes.Clear();
        results.AddRange(await Task.WhenAll(types.Skip(1).Select(async type =>
        {
            try
            {
                return (type, answer: await AskAsync(server, name, type, token), error: (string?)null);
            }
            catch (Exception ex) when (IsNetworkFailure(ex, token))
            {
                return (type, answer: (ParsedAnswer?)null, error: Describe(ex));
            }
        })));

        var records = new List<DnsRecord>();
        foreach (var (type, answer, error) in results)
        {
            if (answer is null)
            {
                notes.Add($"{type}: {error}");
                continue;
            }
            records.AddRange(answer.Records.Where(r => r.Type == type || (r.Type == DnsType.CNAME && type is DnsType.A or DnsType.AAAA)));
            if (answer.Code == 3)
                notes.Add("NXDOMAIN: il nome non esiste");
            else if (answer.Code != 0)
                notes.Add($"{type}: {ResponseName(answer.Code)}");
        }
        // i CNAME arrivano anche dalle domande A e AAAA: si tiene una copia sola
        var unique = records.DistinctBy(r => (r.Name, r.Type, r.Value)).ToList();
        return new DnsAnswer(request.Host, server.ToString(), System.Diagnostics.Stopwatch.GetElapsedTime(started), unique, notes.Distinct().ToList());
    }

    private static bool IsNetworkFailure(Exception ex, CancellationToken token) =>
        ex is SocketException or IOException or TimeoutException or InvalidDataException || (ex is OperationCanceledException && !token.IsCancellationRequested);

    private static string Describe(Exception ex) => ex is TimeoutException or OperationCanceledException ? "tempo scaduto" : ex.Message;

    /// <summary>Il server indicato, o quelli del PC nell'ordine in cui Windows li userebbe.</summary>
    private static async Task<List<IPAddress>> ServersAsync(string? requested, CancellationToken token)
    {
        if (requested is not null)
        {
            if (IPAddress.TryParse(requested.Trim('[', ']'), out var literal))
                return [literal];
            var found = await Dns.GetHostAddressesAsync(requested, token);
            return found.Length > 0 ? [found[0]] : throw new InvalidOperationException($"server DNS «{requested}» sconosciuto");
        }
        if (SystemServers() is not { Count: > 0 } system)
            throw new InvalidOperationException("nessun server DNS configurato");
        if (_preferred is { } preferred && system.Remove(preferred))
            system.Insert(0, preferred);
        return system;
    }

    private static async Task<ParsedAnswer> AskAsync(IPAddress server, string name, DnsType type, CancellationToken token)
    {
        var id = (ushort)Random.Shared.Next(ushort.MaxValue + 1);
        var query = BuildQuery(id, name, type);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var udp = new UdpClient(server.AddressFamily);
            udp.Connect(server, 53);
            await udp.SendAsync(query, timeout.Token);
            while (true)
            {
                var reply = (await udp.ReceiveAsync(timeout.Token)).Buffer;
                var parsed = ParseResponse(reply, id);
                if (parsed is null)
                    continue; // risposta di qualcun altro
                return parsed.Truncated ? await AskTcpAsync(server, query, id, timeout.Token) : parsed;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    private static async Task<ParsedAnswer> AskTcpAsync(IPAddress server, byte[] query, ushort id, CancellationToken token)
    {
        using var client = new TcpClient(server.AddressFamily);
        await client.ConnectAsync(server, 53, token);
        var stream = client.GetStream();
        var framed = new byte[query.Length + 2];
        framed[0] = (byte)(query.Length >> 8);
        framed[1] = (byte)query.Length;
        query.CopyTo(framed, 2);
        await stream.WriteAsync(framed, token);
        var length = new byte[2];
        await stream.ReadExactlyAsync(length, token);
        var body = new byte[(length[0] << 8) | length[1]];
        await stream.ReadExactlyAsync(body, token);
        return ParseResponse(body, id) ?? throw new InvalidDataException("risposta DNS non valida");
    }

    /// <summary><c>8.8.4.4</c> → <c>4.4.8.8.in-addr.arpa</c>; per IPv6 un nibble per etichetta (<c>ip6.arpa</c>).</summary>
    public static string ReverseName(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return string.Join('.', bytes.Reverse()) + ".in-addr.arpa";
        return string.Join('.', bytes.Reverse().SelectMany(b => new[] { (b & 0x0F).ToString("x", CultureInfo.InvariantCulture), (b >> 4).ToString("x", CultureInfo.InvariantCulture) })) + ".ip6.arpa";
    }

    public static string ResponseName(int code) => code switch
    {
        1 => "richiesta non valida (FORMERR)",
        2 => "il server non risponde (SERVFAIL)",
        3 => "il nome non esiste (NXDOMAIN)",
        4 => "tipo non supportato (NOTIMP)",
        5 => "rifiutata (REFUSED)",
        _ => $"codice {code}",
    };

    public static byte[] BuildQuery(ushort id, string name, DnsType type)
    {
        ArgumentNullException.ThrowIfNull(name);
        var packet = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }; // RD, una domanda
        var idn = new IdnMapping();
        foreach (var label in name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label.All(char.IsAscii) ? label : idn.GetAscii(label));
            if (bytes.Length is 0 or > 63)
                throw new InvalidDataException("etichetta del nome troppo lunga");
            packet.Add((byte)bytes.Length);
            packet.AddRange(bytes);
        }
        packet.Add(0);
        packet.Add((byte)((ushort)type >> 8));
        packet.Add((byte)(ushort)type);
        packet.Add(0);
        packet.Add(1); // classe IN
        return [.. packet];
    }

    public sealed record ParsedAnswer(int Code, bool Truncated, IReadOnlyList<DnsRecord> Records);

    /// <summary>Legge la risposta; null se non è la risposta alla domanda con quell'id.</summary>
    public static ParsedAnswer? ParseResponse(byte[] data, ushort id)
    {
        if (data.Length < 12 || ((data[0] << 8) | data[1]) != id || (data[2] & 0x80) == 0)
            return null;
        var truncated = (data[2] & 0x02) != 0;
        var code = data[3] & 0x0F;
        var questions = (data[4] << 8) | data[5];
        var answers = (data[6] << 8) | data[7];
        var offset = 12;
        for (var i = 0; i < questions; i++)
        {
            ReadName(data, ref offset);
            offset += 4;
        }
        var records = new List<DnsRecord>();
        // risposte e autorità (il SOA di un nome che non esiste); le informazioni aggiuntive non servono
        var count = answers + ((data[8] << 8) | data[9]);
        for (var i = 0; i < count && offset < data.Length; i++)
        {
            var name = ReadName(data, ref offset);
            if (offset + 10 > data.Length)
                throw new InvalidDataException("risposta DNS troncata");
            var type = (data[offset] << 8) | data[offset + 1];
            var ttl = (data[offset + 4] << 24) | (data[offset + 5] << 16) | (data[offset + 6] << 8) | data[offset + 7];
            var length = (data[offset + 8] << 8) | data[offset + 9];
            offset += 10;
            if (offset + length > data.Length)
                throw new InvalidDataException("risposta DNS troncata");
            if (Enum.IsDefined(typeof(DnsType), (ushort)type))
                records.Add(new DnsRecord(name, (DnsType)type, ttl, ReadData(data, offset, length, (DnsType)type)));
            offset += length;
        }
        return new ParsedAnswer(code, truncated, records);
    }

    private static string ReadData(byte[] data, int offset, int length, DnsType type)
    {
        var end = offset + length;
        switch (type)
        {
            case DnsType.A when length == 4:
            case DnsType.AAAA when length == 16:
                return new IPAddress(data.AsSpan(offset, length)).ToString();
            case DnsType.NS or DnsType.CNAME or DnsType.PTR:
                return ReadName(data, ref offset);
            case DnsType.MX:
            {
                var preference = (data[offset] << 8) | data[offset + 1];
                offset += 2;
                return $"{preference} {ReadName(data, ref offset)}";
            }
            case DnsType.SOA:
            {
                var primary = ReadName(data, ref offset);
                var admin = ReadName(data, ref offset);
                uint Next()
                {
                    var v = ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
                    offset += 4;
                    return v;
                }
                var (serial, refresh, retry, expire, minimum) = (Next(), Next(), Next(), Next(), Next());
                return $"{primary} {admin} {serial} {refresh} {retry} {expire} {minimum}";
            }
            case DnsType.TXT:
            {
                var text = new StringBuilder();
                while (offset < end)
                {
                    var n = data[offset++];
                    text.Append(Encoding.UTF8.GetString(data, offset, Math.Min(n, end - offset)));
                    offset += n;
                }
                return text.ToString();
            }
            case DnsType.SRV:
            {
                var (priority, weight, port) = ((data[offset] << 8) | data[offset + 1], (data[offset + 2] << 8) | data[offset + 3], (data[offset + 4] << 8) | data[offset + 5]);
                offset += 6;
                return $"{priority} {weight} {port} {ReadName(data, ref offset)}";
            }
            case DnsType.CAA when length >= 2:
            {
                var flags = data[offset];
                var tagLength = data[offset + 1];
                var tag = Encoding.ASCII.GetString(data, offset + 2, tagLength);
                return $"{flags} {tag} \"{Encoding.UTF8.GetString(data, offset + 2 + tagLength, end - offset - 2 - tagLength)}\"";
            }
            case DnsType.DS when length >= 4:
                return $"{(data[offset] << 8) | data[offset + 1]} {data[offset + 2]} {data[offset + 3]} {Convert.ToHexString(data.AsSpan(offset + 4, length - 4))}";
            case DnsType.DNSKEY when length >= 4:
                return $"{(data[offset] << 8) | data[offset + 1]} {data[offset + 2]} {data[offset + 3]} {Convert.ToBase64String(data.AsSpan(offset + 4, length - 4))}";
            default:
                return Convert.ToHexString(data.AsSpan(offset, length));
        }
    }

    /// <summary>Nome con le etichette e i puntatori di compressione; <paramref name="offset"/> avanza oltre il nome.</summary>
    public static string ReadName(byte[] data, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        for (var jumps = 0; ; )
        {
            if (position >= data.Length)
                throw new InvalidDataException("nome DNS troncato");
            var length = data[position];
            if (length == 0)
            {
                if (!jumped)
                    offset = position + 1;
                break;
            }
            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= data.Length || ++jumps > MaxNameJumps)
                    throw new InvalidDataException("nome DNS non valido");
                if (!jumped)
                    offset = position + 2;
                jumped = true;
                position = ((length & 0x3F) << 8) | data[position + 1];
                continue;
            }
            if (position + 1 + length > data.Length)
                throw new InvalidDataException("nome DNS troncato");
            labels.Add(Encoding.UTF8.GetString(data, position + 1, length));
            position += 1 + length;
        }
        return labels.Count == 0 ? "." : string.Join('.', labels);
    }
}
