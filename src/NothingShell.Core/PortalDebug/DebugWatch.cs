using System.Globalization;

namespace NothingShell.Core.PortalDebug;

/// <summary>Un portale collegato (in <c>settings.json</c>); il token è nel Gestore credenziali, mai qui.</summary>
public sealed record DebugPortal(string Id, string Name, string Address, bool Watch = true)
{
    /// <summary>Nome della credenziale col token del portale.</summary>
    public string CredentialTarget => $"NothingShell:debug:{Id}";
}

/// <summary>
/// Quando il notch avvisa da solo (docs/24-debug-potenziato.md). Un tipo di avviso si ripete al massimo ogni
/// <see cref="Cooldown"/> per portale, tranne gli errori nuovi: ognuno ha il suo avviso, la prima volta che compare.
/// </summary>
public sealed record DebugAlertRules(
    bool NewErrors = true,
    bool ServerErrors = true,
    bool SlowRequests = true,
    int SlowMs = 3000,
    bool Down = true,
    bool Checks = true,
    bool ErrorLogs = false,
    int CooldownSeconds = 120)
{
    public static DebugAlertRules Default { get; } = new();

    public TimeSpan Cooldown => TimeSpan.FromSeconds(Math.Max(10, CooldownSeconds));
}

/// <summary>Le preferenze della sezione Debug potenziato.</summary>
public sealed record DebugSettings(bool Enabled = true, IReadOnlyList<DebugPortal>? Portals = null, DebugAlertRules? Rules = null)
{
    public static DebugSettings Default { get; } = new();

    public IReadOnlyList<DebugPortal> PortalList => Portals ?? [];

    public DebugAlertRules AlertRules => Rules ?? DebugAlertRules.Default;

    public DebugSettings WithPortal(DebugPortal portal)
    {
        ArgumentNullException.ThrowIfNull(portal);
        return this with { Portals = [.. PortalList.Where(p => p.Id != portal.Id), portal] };
    }

    public DebugSettings WithoutPortal(string id) => this with { Portals = [.. PortalList.Where(p => p.Id != id)] };

    /// <summary>Id per un portale nuovo: dal nome, unico fra quelli collegati.</summary>
    public string NewId(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var slug = new string([.. name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]).Trim('-');
        if (slug.Length == 0)
            slug = "portale";
        if (slug.Length > 32)
            slug = slug[..32];
        var id = slug;
        for (var n = 2; PortalList.Any(p => p.Id == id); n++)
            id = $"{slug}-{n.ToString(CultureInfo.InvariantCulture)}";
        return id;
    }
}

public enum DebugAlertKind
{
    NewError,
    ServerError,
    Slow,
    Down,
    BackUp,
    Check,
    ErrorLog,
}

/// <summary>Un avviso per il notch: cosa, una riga di titolo, il dettaglio e l'evento da aprire nella dashboard.</summary>
public sealed record DebugAlert(DebugAlertKind Kind, string Title, string Detail, DebugEvent? Event = null)
{
    public bool IsBad => Kind != DebugAlertKind.BackUp;
}

/// <summary>Raggiungibilità del portale vista dalla shell.</summary>
public enum PortalReach
{
    /// <summary>Non ancora chiesto.</summary>
    Unknown,
    Online,

    /// <summary>Nessuna risposta (portale fermo, rete).</summary>
    Offline,

    /// <summary>Risponde ma non riconosce il token: va ricollegato con un codice nuovo.</summary>
    Unauthorized,
}

/// <summary>
/// L'attenzione autonoma: guarda gli eventi nuovi e la raggiungibilità di un portale e decide quando disturbare. Non
/// conosce il notch né il tempo vero: riceve l'ora, così si prova coi test.
/// </summary>
public sealed class DebugWatch
{
    /// <summary>Fallimenti di fila prima di dire che il portale è giù (un riavvio del runserver dura un attimo).</summary>
    public const int DownAfter = 3;

    private readonly HashSet<string> _knownErrors = new(StringComparer.Ordinal);
    private readonly Dictionary<DebugAlertKind, DateTimeOffset> _lastAlert = [];
    private readonly Dictionary<string, CheckState> _checks = new(StringComparer.Ordinal);
    private int _failures;
    private bool _down;
    private bool _seenOnline;

    public PortalReach Reach { get; private set; } = PortalReach.Unknown;

    /// <summary>
    /// Gli eventi appena arrivati: un errore con un'impronta mai vista, una risposta 5xx, una richiesta oltre la
    /// soglia, un log ERROR (se chiesto). Al primo giro (<paramref name="initial"/>) si imparano gli errori già presenti
    /// senza avvisare: aprendo la shell non arriva la valanga di ieri.
    /// </summary>
    public IReadOnlyList<DebugAlert> OnEvents(IReadOnlyList<DebugEvent> events, DebugAlertRules rules, DateTimeOffset now, bool initial = false)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(rules);
        var alerts = new List<DebugAlert>();
        var errorRequests = new HashSet<string>(StringComparer.Ordinal);
        foreach (var x in events.OfType<DebugCrash>())
        {
            if (x.RequestId is { } rid)
                errorRequests.Add(rid);
            if (!_knownErrors.Add(x.Fingerprint) || initial || !rules.NewErrors)
                continue;
            var where = x.Culprit is { } c ? $"{ShortFile(c.File)}:{c.Line.ToString(CultureInfo.InvariantCulture)} in {c.Function}" : x.Path ?? string.Empty;
            alerts.Add(new DebugAlert(DebugAlertKind.NewError, "ERRORE NUOVO", $"{x.Summary}\n{where}", x));
        }
        if (initial)
            return alerts;

        // una 500 che ha già il suo errore nuovo non chiede un secondo avviso
        if (rules.ServerErrors && events.OfType<DebugRequest>().LastOrDefault(r => r.Status >= 500 && (r.RequestId is null || !errorRequests.Contains(r.RequestId))) is { } failed
            && Allowed(DebugAlertKind.ServerError, rules, now))
        {
            var count = events.OfType<DebugRequest>().Count(r => r.Status >= 500);
            var detail = count > 1 ? $"{failed.Summary}  e altre {(count - 1).ToString(CultureInfo.InvariantCulture)}" : failed.Summary;
            alerts.Add(new DebugAlert(DebugAlertKind.ServerError, $"RISPOSTA {failed.Status.ToString(CultureInfo.InvariantCulture)}", detail, failed));
        }
        if (rules.SlowRequests && events.OfType<DebugRequest>().Where(r => r.DurationMs >= rules.SlowMs).MaxBy(r => r.DurationMs) is { } slow
            && Allowed(DebugAlertKind.Slow, rules, now))
        {
            alerts.Add(new DebugAlert(DebugAlertKind.Slow, "RICHIESTA LENTA",
                $"{slow.Method} {slow.Path} · {FormatMs(slow.DurationMs)}{(slow.SqlCount > 0 ? $" · {slow.SqlCount.ToString(CultureInfo.InvariantCulture)} query" : string.Empty)}", slow));
        }
        if (rules.ErrorLogs && events.OfType<DebugLogLine>().LastOrDefault(l => l.Severity >= 40 && (l.RequestId is null || !errorRequests.Contains(l.RequestId))) is { } log
            && Allowed(DebugAlertKind.ErrorLog, rules, now))
        {
            alerts.Add(new DebugAlert(DebugAlertKind.ErrorLog, $"LOG {log.Level}", $"{log.Logger}: {log.Message}", log));
        }
        return alerts;
    }

    /// <summary>Esito di una richiesta al portale: avvisa quando cade (dopo <see cref="DownAfter"/> fallimenti) e quando torna.</summary>
    public DebugAlert? OnReach(PortalReach reach, DebugAlertRules rules, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var before = Reach;
        Reach = reach;
        if (reach == PortalReach.Online)
        {
            _seenOnline = true;
            _failures = 0;
            if (!_down)
                return null;
            _down = false;
            return rules.Down ? new DebugAlert(DebugAlertKind.BackUp, "PORTALE DI NUOVO SU", "Risponde di nuovo") : null;
        }
        if (reach == PortalReach.Unauthorized)
        {
            if (before == PortalReach.Unauthorized)
                return null;
            return new DebugAlert(DebugAlertKind.Down, "COLLEGAMENTO RIFIUTATO", "Il portale non riconosce più la shell: ricollegalo con un codice nuovo");
        }
        if (reach != PortalReach.Offline || _down || ++_failures < DownAfter)
            return null;
        _down = true;
        _lastAlert[DebugAlertKind.Down] = now;
        return rules.Down && _seenOnline ? new DebugAlert(DebugAlertKind.Down, "PORTALE GIÙ", "Non risponde") : null;
    }

    /// <summary>Un controllo del portale che peggiora (ok → da guardare → rotto); quando torna ok non si avvisa.</summary>
    public IReadOnlyList<DebugAlert> OnChecks(IReadOnlyList<PortalCheck> checks, DebugAlertRules rules)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(rules);
        var alerts = new List<DebugAlert>();
        foreach (var check in checks)
        {
            var known = _checks.TryGetValue(check.Name, out var before);
            _checks[check.Name] = check.State;
            if (!rules.Checks || check.State == CheckState.Ok || (known && before >= check.State))
                continue;
            alerts.Add(new DebugAlert(DebugAlertKind.Check, check.State == CheckState.Failing ? "CONTROLLO ROTTO" : "CONTROLLO DA GUARDARE",
                $"{check.Name}: {check.Value}{(check.Detail is { Length: > 0 } d ? $" · {d}" : string.Empty)}"));
        }
        return alerts;
    }

    public static string FormatMs(double ms) => ms >= 1000
        ? $"{(ms / 1000).ToString("0.0", CultureInfo.InvariantCulture)} s"
        : $"{ms.ToString("0", CultureInfo.InvariantCulture)} ms";

    /// <summary>Le ultime due parti del percorso: <c>core/views.py</c>.</summary>
    public static string ShortFile(string file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var parts = file.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 ? string.Join('/', parts) : string.Join('/', parts[^2..]);
    }

    private bool Allowed(DebugAlertKind kind, DebugAlertRules rules, DateTimeOffset now)
    {
        if (_lastAlert.TryGetValue(kind, out var last) && now - last < rules.Cooldown)
            return false;
        _lastAlert[kind] = now;
        return true;
    }
}
