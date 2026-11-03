using System.Net;

namespace NothingShell.Core.Network;

/// <summary>Cosa risponde un server alla stessa domanda.</summary>
public sealed record PropagationRow(string Name, string Server, IReadOnlyList<string> Values, long Ms, string? Error, bool Authoritative, string? Serial);

/// <summary>Le risposte di più server: <see cref="Consistent"/> se i valori sono uguali ovunque.</summary>
public sealed record PropagationResult(string Host, DnsType Type, IReadOnlyList<PropagationRow> Rows, bool Consistent, bool SerialsMatch);

/// <summary>
/// Propagazione DNS: la stessa domanda ai resolver pubblici, a quello del PC e ai server autorevoli del dominio (gli <c>NS</c>),
/// per vedere se un cambiamento è arrivato dappertutto (docs/18-connettori-diagnostica.md).
/// </summary>
public static class DnsPropagation
{
    private static readonly (string Name, string? Server)[] Resolvers =
    [
        ("Questo PC", null), ("Google", "8.8.8.8"), ("Cloudflare", "1.1.1.1"), ("Quad9", "9.9.9.9"),
    ];

    public static async Task<PropagationResult> RunAsync(string host, DnsType type, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var tasks = Resolvers.Select(r => AskAsync(r.Name, r.Server, host, type, authoritative: false, token)).ToList();

        // i server autorevoli: gli NS del dominio (si risalgono i genitori finché ne trova, per sito.example.com)
        foreach (var zone in Zones(host))
        {
            var ns = await DnsLookup.QueryAsync(new DnsRequest(zone, [DnsType.NS], "8.8.8.8"), token);
            var names = ns.Of(DnsType.NS).Select(r => r.Value.TrimEnd('.')).Distinct().Take(6).ToList();
            if (names.Count == 0)
                continue;
            foreach (var name in names)
            {
                var address = await ResolveAsync(name, token);
                tasks.Add(address is null
                    ? Task.FromResult(new PropagationRow(name, "?", [], 0, "indirizzo del server sconosciuto", true, null))
                    : AskAsync(name, address.ToString(), host, type, authoritative: true, token));
            }
            break;
        }

        var rows = await Task.WhenAll(tasks);
        var answered = rows.Where(r => r.Error is null).ToList();
        var consistent = answered.Select(r => string.Join('|', r.Values)).Distinct().Count() <= 1;
        var serials = answered.Where(r => r.Serial is not null).Select(r => r.Serial).Distinct().Count() <= 1;
        return new PropagationResult(host, type, rows, consistent, serials);
    }

    /// <summary><c>www.sinapsi.it</c> → <c>www.sinapsi.it</c>, <c>sinapsi.it</c>: dal nome intero alla zona più alta con due etichette.</summary>
    public static IEnumerable<string> Zones(string host)
    {
        var labels = host.Trim('.').Split('.');
        for (var i = 0; i <= labels.Length - 2; i++)
            yield return string.Join('.', labels.Skip(i));
    }

    private static async Task<PropagationRow> AskAsync(string name, string? server, string host, DnsType type, bool authoritative, CancellationToken token)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var types = authoritative ? new[] { type, DnsType.SOA } : [type];
            var answer = await DnsLookup.QueryAsync(new DnsRequest(host, types, server), token);
            var values = answer.Of(type).Select(r => r.Value).OrderBy(v => v, StringComparer.Ordinal).ToList();
            // il numero di serie del SOA (terzo campo) dice se il server ha l'ultima versione della zona
            var serial = authoritative ? answer.Of(DnsType.SOA).Select(r => r.Value.Split(' ').ElementAtOrDefault(2)).FirstOrDefault() : null;
            var error = values.Count == 0 && answer.Notes.Count > 0 ? answer.Notes[0] : null;
            return new PropagationRow(name, answer.Server, values, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, error, authoritative, serial);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Sockets.SocketException or IOException)
        {
            return new PropagationRow(name, server ?? "?", [], (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, ex.Message, authoritative, null);
        }
    }

    private static async Task<IPAddress?> ResolveAsync(string name, CancellationToken token)
    {
        try
        {
            var found = await Dns.GetHostAddressesAsync(name, token);
            return found.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? found.FirstOrDefault();
        }
        catch (System.Net.Sockets.SocketException)
        {
            return null;
        }
    }
}
