using System.Globalization;

namespace NothingShell.Core.PortalDebug;

public enum HighlightKind
{
    Error,
    ServerError,
    Slow,
    Check,
}

/// <summary>Una cosa da guardare nella pagina Debug del notch: un clic apre la dashboard su <see cref="Event"/>.</summary>
public sealed record DebugHighlight(HighlightKind Kind, string Text, string? Detail, DateTimeOffset? Time, DebugEvent? Event, bool Critical);

/// <summary>Un portale come lo mostra il notch: stato, una riga di numeri e le cose più importanti.</summary>
public sealed record DebugPortalView(string Id, string Name, PortalReach Reach, string? Problem, bool Paused, string Summary, IReadOnlyList<DebugHighlight> Highlights)
{
    /// <summary>Rosso se c'è un errore, un 5xx o un controllo rotto fra le cose da guardare, o se il portale non risponde.</summary>
    public bool Bad => Reach is PortalReach.Offline or PortalReach.Unauthorized || Highlights.Any(h => h.Critical);
}

/// <summary>
/// Sceglie cosa mostrare nel notch (docs/24-debug-potenziato.md): poco e solo ciò che conta, la dashboard ha il resto.
/// Nell'ordine: errori (uno per impronta, il più recente prima), 5xx senza il loro errore, la richiesta più lenta oltre
/// la soglia, i controlli che non sono a posto. Al massimo <see cref="MaxItems"/> per portale.
/// </summary>
public static class DebugHighlights
{
    public const int MaxItems = 4;

    /// <summary>Cosa conta come recente per il notch.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    public static IReadOnlyList<DebugHighlight> For(DebugLog log, PortalStatus? status, DebugAlertRules rules, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(rules);
        var since = now - Window;
        var items = new List<DebugHighlight>();

        var errors = log.ErrorGroups.Where(g => g.Last >= since).OrderByDescending(g => g.Last).ToList();
        var errorRequests = log.Events.OfType<DebugCrash>().Where(x => x.RequestId is not null).Select(x => x.RequestId!).ToHashSet(StringComparer.Ordinal);
        foreach (var g in errors.Take(2))
        {
            var where = g.Latest.Culprit is { } c ? $"{DebugWatch.ShortFile(c.File)}:{c.Line.ToString(CultureInfo.InvariantCulture)}" : g.Latest.Path;
            var count = g.Count > 1 ? $"×{g.Count.ToString(CultureInfo.InvariantCulture)} · " : string.Empty;
            items.Add(new DebugHighlight(HighlightKind.Error, g.Latest.Summary, count + where, g.Last, g.Latest, true));
        }
        if (errors.Count > 2)
        {
            var rest = errors.Skip(2).ToList();
            items.Add(new DebugHighlight(HighlightKind.Error, $"altri {rest.Count.ToString(CultureInfo.InvariantCulture)} errori diversi", null, rest[0].Last, rest[0].Latest, true));
        }

        var requests = log.Events.OfType<DebugRequest>().Where(r => r.Time >= since).ToList();
        var failed = requests.Where(r => r.Status >= 500 && (r.RequestId is null || !errorRequests.Contains(r.RequestId))).ToList();
        if (failed.Count > 0)
        {
            var last = failed[^1];
            var more = failed.Count > 1 ? $"×{failed.Count.ToString(CultureInfo.InvariantCulture)}" : null;
            items.Add(new DebugHighlight(HighlightKind.ServerError, $"{last.Status.ToString(CultureInfo.InvariantCulture)} {last.Method} {last.Path}", more, last.Time, last, true));
        }

        if (requests.Where(r => r.DurationMs >= rules.SlowMs).MaxBy(r => r.DurationMs) is { } slow)
        {
            var sql = slow.SqlCount > 0 ? $" · {slow.SqlCount.ToString(CultureInfo.InvariantCulture)} query" : string.Empty;
            items.Add(new DebugHighlight(HighlightKind.Slow, $"{slow.Method} {slow.Path}", DebugWatch.FormatMs(slow.DurationMs) + sql, slow.Time, slow, false));
        }

        foreach (var check in (status?.Checks ?? []).Where(c => c.State != CheckState.Ok).OrderByDescending(c => c.State))
            items.Add(new DebugHighlight(HighlightKind.Check, check.Name, check.Value, null, null, check.State == CheckState.Failing)); // il dettaglio lungo resta alla dashboard

        // se c'è troppo, si tengono prima le cose rosse, nell'ordine dato
        return [.. items.Select((item, index) => (item, index))
            .OrderByDescending(x => x.item.Critical).ThenBy(x => x.index)
            .Take(MaxItems)
            .OrderBy(x => x.index)
            .Select(x => x.item)];
    }

    /// <summary>La riga di numeri sotto il nome: richieste al minuto, tempo al 95%, errori, negli ultimi 5 minuti.</summary>
    public static string Summary(DebugLog log, PortalReach reach, string? problem, bool paused, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (paused)
            return "in pausa";
        if (reach == PortalReach.Unknown)
            return "collegamento…";
        if (reach != PortalReach.Online)
            return problem ?? "non risponde";
        var s = log.Stats(now, TimeSpan.FromMinutes(5));
        if (s.Requests == 0 && s.Exceptions == 0)
            return "nessuna richiesta negli ultimi 5 min";
        var parts = new List<string>
        {
            $"{s.RequestsPerMinute.ToString(s.RequestsPerMinute < 10 ? "0.#" : "0", CultureInfo.InvariantCulture)}/min",
        };
        if (s.Requests > 0)
            parts.Add($"p95 {DebugWatch.FormatMs(s.P95Ms)}");
        if (s.Exceptions > 0)
            parts.Add(s.Exceptions == 1 ? "1 errore" : $"{s.Exceptions.ToString(CultureInfo.InvariantCulture)} errori");
        else if (s.ServerErrors > 0)
            parts.Add($"{s.ServerErrors.ToString(CultureInfo.InvariantCulture)} × 5xx");
        return string.Join(" · ", parts);
    }

    /// <summary>"adesso", "4 min fa", "2 h fa".</summary>
    public static string Ago(DateTimeOffset time, DateTimeOffset now)
    {
        var ago = now - time;
        return ago.TotalMinutes < 1 ? "adesso"
            : ago.TotalHours < 1 ? $"{((int)ago.TotalMinutes).ToString(CultureInfo.InvariantCulture)} min fa"
            : $"{((int)ago.TotalHours).ToString(CultureInfo.InvariantCulture)} h fa";
    }
}
