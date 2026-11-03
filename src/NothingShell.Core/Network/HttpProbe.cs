using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace NothingShell.Core.Network;

/// <summary>Una richiesta HTTP vista da vicino: quanto ci mette ogni fase (DNS, connessione, TLS, primo byte, fine).</summary>
public sealed record HttpHop(
    Uri Url, string? Address, int Status, string Reason,
    long DnsMs, long ConnectMs, long TlsMs, long FirstByteMs, long TotalMs, long BodyBytes, bool Truncated,
    string? TlsProtocol, IReadOnlyDictionary<string, string> Headers)
{
    public bool IsRedirect => Status is 301 or 302 or 303 or 307 or 308 && Headers.ContainsKey("location");

    public string? Location => Headers.TryGetValue("location", out var l) ? l : null;
}

/// <summary>La catena di redirect da un indirizzo alla pagina finale.</summary>
public sealed record RedirectChain(IReadOnlyList<HttpHop> Hops, bool Loop, bool TooMany, bool Downgrade)
{
    public HttpHop Last => Hops[^1];
}

/// <summary>
/// Client HTTP/1.1 scritto a mano per misurare le fasi di una richiesta, che HttpClient non separa: risoluzione del nome,
/// connessione TCP, handshake TLS, primo byte e fine della risposta (docs/18-connettori-diagnostica.md). Una sola
/// richiesta GET, senza cookie né redirect automatici; non invia nulla di più di un GET.
/// </summary>
public static class HttpProbe
{
    private const int MaxHeadBytes = 64 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Una richiesta GET: legge al massimo <paramref name="maxBody"/> byte del corpo.</summary>
    public static async Task<HttpHop> SendAsync(Uri url, CancellationToken token, int maxBody = 2 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("solo indirizzi http e https");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(Timeout);
        var clock = Stopwatch.StartNew();
        try
        {
            return await SendCoreAsync(url, clock, maxBody, timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException($"nessuna risposta in {Timeout.TotalSeconds:0} secondi");
        }
    }

    /// <summary>Segue i redirect (al massimo <paramref name="limit"/>) e dice se c'è un ciclo o un passaggio da https a http.</summary>
    public static async Task<RedirectChain> ChainAsync(Uri start, CancellationToken token, int limit = 10)
    {
        ArgumentNullException.ThrowIfNull(start);
        var hops = new List<HttpHop>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = start;
        bool loop = false, tooMany = false, downgrade = false;
        while (true)
        {
            if (!seen.Add(current.AbsoluteUri))
            {
                loop = true;
                break;
            }
            var hop = await SendAsync(current, token, maxBody: 16 * 1024);
            hops.Add(hop);
            if (!hop.IsRedirect)
                break;
            if (hops.Count > limit)
            {
                tooMany = true;
                break;
            }
            if (!Uri.TryCreate(current, hop.Location, out var next) || next.Scheme is not ("http" or "https"))
                break;
            downgrade |= current.Scheme == "https" && next.Scheme == "http";
            current = next;
        }
        return new RedirectChain(hops, loop, tooMany, downgrade);
    }

    private static async Task<HttpHop> SendCoreAsync(Uri url, Stopwatch clock, int maxBody, CancellationToken token)
    {
        var host = url.DnsSafeHost;
        IPAddress address;
        long dnsMs = 0;
        if (IPAddress.TryParse(host, out var literal))
        {
            address = literal;
        }
        else
        {
            var found = await Dns.GetHostAddressesAsync(host, token);
            address = found.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? found.FirstOrDefault()
                ?? throw new InvalidOperationException($"«{host}» non esiste");
            dnsMs = clock.ElapsedMilliseconds;
        }

        var mark = clock.ElapsedMilliseconds;
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(address, url.Port, token);
        var connectMs = clock.ElapsedMilliseconds - mark;

        Stream stream = new NetworkStream(socket, ownsSocket: false);
        string? tlsProtocol = null;
        long tlsMs = 0;
        if (url.Scheme == "https")
        {
            mark = clock.ElapsedMilliseconds;
            var ssl = new SslStream(stream);
            stream = ssl;
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, token);
            tlsProtocol = ssl.SslProtocol.ToString();
            tlsMs = clock.ElapsedMilliseconds - mark;
        }

        await using (stream)
        {
            var authority = url.IsDefaultPort ? url.Authority : $"{url.DnsSafeHost}:{url.Port}";
            var request = $"GET {url.PathAndQuery} HTTP/1.1\r\nHost: {authority}\r\nUser-Agent: NothingShell-diagnosi\r\nAccept: */*\r\nAccept-Encoding: identity\r\nConnection: close\r\n\r\n";
            var sent = clock.ElapsedMilliseconds;
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), token);

            var buffer = new byte[16 * 1024];
            var head = new MemoryStream();
            long firstByte = -1;
            long bodyBytes = 0;
            var headDone = false;
            var truncated = false;
            while (true)
            {
                var n = await stream.ReadAsync(buffer, token);
                if (n == 0)
                    break;
                if (firstByte < 0)
                    firstByte = clock.ElapsedMilliseconds - sent;
                if (!headDone)
                {
                    head.Write(buffer, 0, n);
                    var bytes = head.GetBuffer();
                    var end = IndexOfHeadEnd(bytes, (int)head.Length);
                    if (end < 0)
                    {
                        if (head.Length > MaxHeadBytes)
                            throw new InvalidDataException("intestazioni troppo lunghe");
                        continue;
                    }
                    headDone = true;
                    bodyBytes = head.Length - end;
                    head.SetLength(end);
                }
                else
                {
                    bodyBytes += n;
                }
                if (bodyBytes >= maxBody)
                {
                    truncated = true;
                    break;
                }
            }
            if (!headDone)
                throw new InvalidDataException("risposta vuota o non HTTP");
            var total = clock.ElapsedMilliseconds - sent;
            var (status, reason, headers) = ParseHead(Encoding.Latin1.GetString(head.GetBuffer(), 0, (int)head.Length));
            return new HttpHop(url, address.ToString(), status, reason, dnsMs, connectMs, tlsMs, Math.Max(firstByte, 0), total, bodyBytes, truncated, tlsProtocol, headers);
        }
    }

    private static int IndexOfHeadEnd(byte[] data, int length)
    {
        for (var i = 0; i + 3 < length; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                return i + 4;
        }
        return -1;
    }

    /// <summary>Riga di stato e intestazioni (nomi in minuscolo, valori ripetuti uniti da virgola).</summary>
    public static (int Status, string Reason, IReadOnlyDictionary<string, string> Headers) ParseHead(string head)
    {
        ArgumentNullException.ThrowIfNull(head);
        var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var parts = lines.Length > 0 ? lines[0].Split(' ', 3) : [];
        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/", StringComparison.Ordinal) || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status))
            throw new InvalidDataException("la risposta non è HTTP");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
                continue;
            var name = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out var old) ? $"{old}, {value}" : value;
        }
        return (status, parts.Length > 2 ? parts[2] : string.Empty, headers);
    }
}
