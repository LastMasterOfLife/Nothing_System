namespace NothingShell.Core.PortalDebug;

/// <summary>Che cosa ha mandato il portale (docs/24-debug-potenziato.md).</summary>
public enum DebugEventKind
{
    Request,
    Log,
    Error,
    Sql,
    Custom,
}

/// <summary>
/// Un evento del portale: richiesta, riga di log, eccezione, query o evento a piacere. <see cref="Seq"/> è il numero
/// progressivo dato dal portale (cresce sempre, riparte quando il portale si riavvia); <see cref="RequestId"/> lega fra
/// loro gli eventi della stessa richiesta.
/// </summary>
public abstract record DebugEvent(long Seq, DateTimeOffset Time, string? RequestId)
{
    public abstract DebugEventKind Kind { get; }

    /// <summary>Riga breve per gli elenchi e la ricerca.</summary>
    public abstract string Summary { get; }

    /// <summary>Da guardare: errori, risposte 5xx, log da WARNING in su.</summary>
    public virtual bool IsProblem => false;
}

/// <summary>Una richiesta HTTP servita dal portale, con le query fatte per servirla.</summary>
public sealed record DebugRequest(
    long Seq,
    DateTimeOffset Time,
    string? RequestId,
    string Method,
    string Path,
    int Status,
    double DurationMs,
    long? Size = null,
    string? QueryString = null,
    string? View = null,
    string? User = null,
    string? ClientIp = null,
    int SqlCount = 0,
    double SqlMs = 0,
    IReadOnlyDictionary<string, string>? RequestHeaders = null,
    IReadOnlyDictionary<string, string>? ResponseHeaders = null,
    string? Body = null) : DebugEvent(Seq, Time, RequestId)
{
    public override DebugEventKind Kind => DebugEventKind.Request;

    public override string Summary => $"{Method} {Path} {Status}";

    public override bool IsProblem => Status >= 500;

    public bool IsClientError => Status is >= 400 and < 500;
}

/// <summary>Una riga di log (logging di Python, livelli come i suoi).</summary>
public sealed record DebugLogLine(long Seq, DateTimeOffset Time, string? RequestId, string Level, string Logger, string Message)
    : DebugEvent(Seq, Time, RequestId)
{
    public override DebugEventKind Kind => DebugEventKind.Log;

    public override string Summary => $"{Level} {Logger}: {Message}";

    public override bool IsProblem => Severity >= 30;

    /// <summary>Come i numeri di logging: DEBUG 10, INFO 20, WARNING 30, ERROR 40, CRITICAL 50.</summary>
    public int Severity => Level.ToUpperInvariant() switch
    {
        "DEBUG" or "TRACE" => 10,
        "INFO" => 20,
        "WARNING" or "WARN" => 30,
        "ERROR" => 40,
        "CRITICAL" or "FATAL" => 50,
        _ => 20,
    };
}

/// <summary>Una riga dello stack: <see cref="InApp"/> se è codice del portale (non di Django o delle librerie).</summary>
public sealed record DebugFrame(string File, int Line, string Function, string? Code, bool InApp);

/// <summary>Un'eccezione non gestita (o mandata a mano dal portale), con lo stack dalla chiamata più esterna alla più interna.</summary>
public sealed record DebugCrash(
    long Seq,
    DateTimeOffset Time,
    string? RequestId,
    string Type,
    string Message,
    IReadOnlyList<DebugFrame> Frames,
    string? Method = null,
    string? Path = null) : DebugEvent(Seq, Time, RequestId)
{
    public override DebugEventKind Kind => DebugEventKind.Error;

    public override string Summary => Message.Length == 0 ? Type : $"{Type}: {Message}";

    public override bool IsProblem => true;

    /// <summary>La riga del portale più vicina all'errore (o l'ultima dello stack se nessuna è del portale).</summary>
    public DebugFrame? Culprit => Frames.LastOrDefault(f => f.InApp) ?? (Frames.Count > 0 ? Frames[^1] : null);

    /// <summary>
    /// Lo stesso errore nello stesso punto ha la stessa impronta, anche con messaggi diversi (<c>KeyError: 'id'</c> e
    /// <c>KeyError: 'nome'</c> sulla stessa riga sono un errore solo): è quella che decide se un errore è nuovo.
    /// </summary>
    public string Fingerprint => Culprit is { } c ? $"{Type}@{c.File}:{c.Function}" : $"{Type}@{Path}";
}

/// <summary>Una query al database (il portale manda quelle lente, o tutte se gli si chiede).</summary>
public sealed record DebugSql(long Seq, DateTimeOffset Time, string? RequestId, string Sql, double DurationMs, string? Database = null)
    : DebugEvent(Seq, Time, RequestId)
{
    public override DebugEventKind Kind => DebugEventKind.Sql;

    public override string Summary => Sql;
}

/// <summary>Evento a piacere del portale (<c>nothing_debug.emit("ordine creato", id=12)</c>): nome e valori.</summary>
public sealed record DebugCustom(long Seq, DateTimeOffset Time, string? RequestId, string Name, IReadOnlyDictionary<string, string> Data)
    : DebugEvent(Seq, Time, RequestId)
{
    public override DebugEventKind Kind => DebugEventKind.Custom;

    public override string Summary => Data.Count == 0 ? Name : $"{Name} · {string.Join(" · ", Data.Select(d => $"{d.Key}={d.Value}"))}";
}

/// <summary>Stato di un controllo del portale (i suoi connettori): ok, da guardare, rotto.</summary>
public enum CheckState
{
    Ok,
    Warning,
    Failing,
}

/// <summary>Un controllo dichiarato dal portale (<c>@nothing_debug.check</c>): coda, cache, servizio esterno…</summary>
public sealed record PortalCheck(string Name, string Value, CheckState State, string? Detail = null);

/// <summary>Cosa il portale dice di sé quando si collega e a ogni controllo di stato.</summary>
public sealed record PortalInfo(
    string Name,
    string? Framework = null,
    string? Version = null,
    IReadOnlyList<string>? Connectors = null,
    bool? DebugMode = null,
    double? UptimeSeconds = null,
    int? Pid = null,
    string? Root = null);

/// <summary>Una pagina di eventi: <see cref="Boot"/> cambia quando il portale si riavvia (si riparte da zero).</summary>
public sealed record DebugPage(string Boot, long Seq, IReadOnlyList<DebugEvent> Events, bool More = false);

/// <summary>Risposta di <c>/_nothing/status</c>.</summary>
public sealed record PortalStatus(PortalInfo Info, IReadOnlyList<PortalCheck> Checks);
