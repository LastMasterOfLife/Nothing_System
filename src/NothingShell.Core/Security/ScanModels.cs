namespace NothingShell.Core.Security;

/// <summary>Stato complessivo di una sessione di sicurezza.</summary>
public enum SessionStatus
{
    /// <summary>Nessun bersaglio.</summary>
    Idle,

    /// <summary>C'è un bersaglio ma manca la conferma di proprietà/autorizzazione: non parte nulla.</summary>
    NeedsAuthorization,

    /// <summary>In corso.</summary>
    Running,

    /// <summary>Finita regolarmente.</summary>
    Completed,

    /// <summary>Fermata dall'utente.</summary>
    Aborted,

    /// <summary>Interrotta da un errore.</summary>
    Failed,
}

/// <summary>Com'è andata una singola fase.</summary>
public enum StepStatus
{
    Pending,
    Running,
    Done,
    Skipped,
    Error,
}

/// <summary>Una fase prevista dal piano: uno strumento e cosa fa (recon passivo, nuclei, ZAP…).</summary>
public sealed record ScanStep(string Id, string Tool, string Label, string Description);

/// <summary>Una fase così com'è andata: quando è iniziata e finita (dall'avvio della sessione), l'esito, una nota, quante debolezze ha trovato.</summary>
public sealed record ScanAction(
    string StepId,
    string Tool,
    string Label,
    StepStatus Status,
    TimeSpan? Started = null,
    TimeSpan? Ended = null,
    string? Detail = null,
    int Findings = 0);

/// <summary>La conferma esplicita dell'utente: «questo host è mio, o sono autorizzato a provarlo».</summary>
public sealed record ScanAuthorization(string Host, DateTimeOffset At);

/// <summary>Uno strumento esterno di scansione e se è installato su questo PC.</summary>
public sealed record ScanTool(string Id, string Name, string Why, bool Installed, string? Version = null, string? Path = null);

/// <summary>Istantanea immutabile della sessione, per il notch (dal vivo) e per il report (alla fine).</summary>
public sealed record SecuritySnapshot(
    SessionStatus Status,
    SecurityTarget? Target,
    string Activity,
    IReadOnlyList<ScanAction> Actions,
    IReadOnlyList<Finding> Findings,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    string? Error)
{
    /// <summary>Fasi concluse (fatte, saltate o in errore) sul totale.</summary>
    public int Done => Actions.Count(a => a.Status is StepStatus.Done or StepStatus.Skipped or StepStatus.Error);

    public int Total => Actions.Count;

    public bool IsRunning => Status == SessionStatus.Running;

    public int Count(Severity severity) => Findings.Count(f => f.Severity == severity);

    /// <summary>La gravità più alta trovata finora (Info se non c'è nulla): colora il notch.</summary>
    public Severity Peak => Findings.Count == 0 ? Severity.Info : Findings.Max(f => f.Severity);

    /// <summary>Le debolezze dalla più grave alla meno grave, poi per titolo.</summary>
    public IReadOnlyList<Finding> Ordered =>
        Findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase).ToArray();

    public TimeSpan? Duration => StartedAt is { } a && EndedAt is { } b ? b - a : null;

    public static SecuritySnapshot Empty { get; } =
        new(SessionStatus.Idle, null, string.Empty, [], [], null, null, null);
}

/// <summary>Le fasi disponibili e come si mette insieme un piano. Il piano vero dipende da quali strumenti sono installati.</summary>
public static class ScanPlan
{
    /// <summary>Sempre presente: non serve nessuno strumento esterno, non manda payload, solo osserva la risposta.</summary>
    public static readonly ScanStep Recon =
        new("recon", "recon", "Ricognizione passiva", "Intestazioni HTTP, TLS, cookie e software esposto — senza payload.");

    public static readonly ScanStep Nuclei =
        new("nuclei", "nuclei", "nuclei", "Template di vulnerabilità note (CVE, misconfig) in sola rilevazione, senza tag distruttivi.");

    public static readonly ScanStep Zap =
        new("zap", "zap", "OWASP ZAP (baseline)", "Scansione passiva e attiva sicura dell'app web, report in JSON.");

    public static readonly ScanStep Nikto =
        new("nikto", "nikto", "Nikto", "File e configurazioni rischiose note del server web.");

    public static readonly ScanStep TestSsl =
        new("testssl", "testssl", "testssl.sh", "Protocolli e cifrari TLS deboli, certificato, vulnerabilità note del TLS.");

    /// <summary>Tutte le fasi conosciute, nell'ordine in cui conviene eseguirle.</summary>
    public static IReadOnlyList<ScanStep> All { get; } = [Recon, TestSsl, Nuclei, Nikto, Zap];
}
