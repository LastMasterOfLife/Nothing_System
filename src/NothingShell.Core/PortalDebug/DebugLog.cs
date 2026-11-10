using System.Text.RegularExpressions;

namespace NothingShell.Core.PortalDebug;

/// <summary>Un percorso con lo stesso schema (<c>GET /ordini/{id}</c>): quante volte, quanto ci mette, quanti errori.</summary>
public sealed record RouteStats(string Route, int Count, int ServerErrors, double AverageMs, double P95Ms, double MaxMs);

/// <summary>Errori con la stessa impronta: quante volte, la prima e l'ultima, l'ultimo arrivato.</summary>
public sealed record ErrorGroup(string Fingerprint, int Count, DateTimeOffset First, DateTimeOffset Last, DebugCrash Latest);

/// <summary>I numeri della dashboard su una finestra di tempo (di serie gli ultimi 5 minuti).</summary>
public sealed record DebugStats(
    int Requests,
    double RequestsPerMinute,
    int ServerErrors,
    int ClientErrors,
    int Exceptions,
    int Warnings,
    double P50Ms,
    double P95Ms,
    double SqlPerRequest,
    DateTimeOffset? LastActivity)
{
    public double ServerErrorRate => Requests == 0 ? 0 : (double)ServerErrors / Requests;
}

/// <summary>Che cosa mostra un elenco della dashboard.</summary>
public sealed record DebugFilter(DebugEventKind? Kind = null, string? Text = null, bool ProblemsOnly = false, string? RequestId = null)
{
    public bool Matches(DebugEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (Kind is { } kind && e.Kind != kind)
            return false;
        if (ProblemsOnly && !e.IsProblem && !(e is DebugRequest { IsClientError: true }))
            return false;
        if (RequestId is { Length: > 0 } rid && !string.Equals(e.RequestId, rid, StringComparison.Ordinal))
            return false;
        if (Text is not { Length: > 0 } text)
            return true;
        // più parole: devono esserci tutte; "500" o "post" trovano anche codice e metodo
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => e.Summary.Contains(word, StringComparison.OrdinalIgnoreCase) ||
                         (e is DebugRequest r && (r.View?.Contains(word, StringComparison.OrdinalIgnoreCase) == true ||
                                                  r.User?.Contains(word, StringComparison.OrdinalIgnoreCase) == true)) ||
                         (e is DebugCrash x && x.Frames.Any(f => f.File.Contains(word, StringComparison.OrdinalIgnoreCase))));
    }
}

/// <summary>
/// Gli eventi di un portale tenuti in memoria (al massimo <see cref="Capacity"/>, i più vecchi escono), con i conti della
/// dashboard. Quando il portale si riavvia (<see cref="DebugPage.Boot"/> diverso) si continua a tenere quello che c'era,
/// ma il cursore riparte da zero.
/// </summary>
public sealed partial class DebugLog(int capacity = 5000)
{
    private readonly LinkedList<DebugEvent> _events = new();
    private readonly Dictionary<string, ErrorGroup> _errors = new(StringComparer.Ordinal);

    public int Capacity { get; } = capacity;

    public string? Boot { get; private set; }

    /// <summary>L'ultimo numero ricevuto: la prossima richiesta chiede gli eventi dopo questo.</summary>
    public long Cursor { get; private set; }

    /// <summary>Quante volte il portale si è riavviato da quando lo si guarda.</summary>
    public int Restarts { get; private set; }

    public int Count => _events.Count;

    /// <summary>Gli eventi dal più vecchio al più recente.</summary>
    public IEnumerable<DebugEvent> Events => _events;

    public IReadOnlyCollection<ErrorGroup> ErrorGroups => _errors.Values;

    /// <summary>Aggiunge una pagina; restituisce solo gli eventi davvero nuovi (quelli già visti si scartano).</summary>
    public IReadOnlyList<DebugEvent> Add(DebugPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (Boot is not null && page.Boot.Length > 0 && !string.Equals(page.Boot, Boot, StringComparison.Ordinal))
        {
            Restarts++;
            Cursor = 0;
        }
        if (page.Boot.Length > 0)
            Boot = page.Boot;

        var added = new List<DebugEvent>();
        foreach (var e in page.Events.OrderBy(e => e.Seq))
        {
            if (e.Seq != 0 && e.Seq <= Cursor)
                continue;
            added.Add(e);
            _events.AddLast(e);
            if (e is DebugCrash x)
                Group(x);
            if (e.Seq > Cursor)
                Cursor = e.Seq;
        }
        if (page.Seq > Cursor)
            Cursor = page.Seq;
        while (_events.Count > Capacity)
            _events.RemoveFirst();
        return added;
    }

    /// <summary>Il portale ha perso gli eventi chiesti (cursore oltre il suo ultimo): si riparte da ciò che ha.</summary>
    public void ResetCursor() => Cursor = 0;

    public void Clear()
    {
        _events.Clear();
        _errors.Clear();
    }

    public IReadOnlyList<DebugEvent> Find(DebugFilter filter, int limit = 1000)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var found = new List<DebugEvent>();
        for (var node = _events.Last; node is not null && found.Count < limit; node = node.Previous)
        {
            if (filter.Matches(node.Value))
                found.Add(node.Value);
        }
        return found; // dal più recente
    }

    /// <summary>Tutto ciò che appartiene a una richiesta (log, query, errore), in ordine.</summary>
    public IReadOnlyList<DebugEvent> Trace(string requestId)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        return [.. _events.Where(e => string.Equals(e.RequestId, requestId, StringComparison.Ordinal))];
    }

    public DebugStats Stats(DateTimeOffset now, TimeSpan window)
    {
        var since = now - window;
        var recent = _events.Where(e => e.Time >= since).ToList();
        var requests = recent.OfType<DebugRequest>().ToList();
        var durations = requests.Select(r => r.DurationMs).Order().ToList();
        return new DebugStats(
            requests.Count,
            window.TotalMinutes <= 0 ? 0 : requests.Count / window.TotalMinutes,
            requests.Count(r => r.Status >= 500),
            requests.Count(r => r.IsClientError),
            recent.OfType<DebugCrash>().Count(),
            recent.OfType<DebugLogLine>().Count(l => l.Severity == 30),
            Percentile(durations, 0.50),
            Percentile(durations, 0.95),
            requests.Count == 0 ? 0 : requests.Average(r => r.SqlCount),
            _events.Last?.Value.Time);
    }

    /// <summary>I percorsi più lenti (per tempo al 95%), con almeno una richiesta nella finestra.</summary>
    public IReadOnlyList<RouteStats> Routes(DateTimeOffset now, TimeSpan window, int top = 10)
    {
        var since = now - window;
        return [.. _events.OfType<DebugRequest>().Where(r => r.Time >= since)
            .GroupBy(r => $"{r.Method} {RoutePattern(r.Path)}", StringComparer.Ordinal)
            .Select(g =>
            {
                var d = g.Select(r => r.DurationMs).Order().ToList();
                return new RouteStats(g.Key, d.Count, g.Count(r => r.Status >= 500), d.Average(), Percentile(d, 0.95), d[^1]);
            })
            .OrderByDescending(r => r.P95Ms)
            .Take(top)];
    }

    /// <summary>
    /// Lo schema di un percorso: numeri, UUID, codici esadecimali lunghi e slug con cifre diventano <c>{id}</c>, così
    /// <c>/ordini/12</c> e <c>/ordini/13</c> contano insieme.
    /// </summary>
    public static string RoutePattern(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var clean = path.Split('?', 2)[0];
        var parts = clean.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0 && IdSegment().IsMatch(parts[i]))
                parts[i] = "{id}";
        }
        return string.Join('/', parts);
    }

    private void Group(DebugCrash x)
    {
        var key = x.Fingerprint;
        _errors[key] = _errors.TryGetValue(key, out var g)
            ? g with { Count = g.Count + 1, Last = x.Time, Latest = x }
            : new ErrorGroup(key, 1, x.Time, x.Time, x);
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0)
            return 0;
        var index = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    [GeneratedRegex(@"^(\d+|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|[0-9a-fA-F]{16,}|(?=[^/]*\d)[A-Za-z0-9_-]{20,})$")]
    private static partial Regex IdSegment();
}
