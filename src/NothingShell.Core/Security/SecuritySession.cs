namespace NothingShell.Core.Security;

/// <summary>
/// Lo stato di una sessione della Modalità Sicurezza (docs/26-sicurezza.md): il bersaglio, l'autorizzazione, il registro
/// azione-per-azione e le debolezze trovate. Deterministica: il tempo arriva da fuori (come <c>NotchStateMachine</c>), così
/// si testa senza orologi veri. La regola del cancello è assoluta: senza un bersaglio <b>autorizzato</b> non parte nulla.
/// </summary>
public sealed class SecuritySession
{
    private readonly List<ScanAction> _actions = [];
    private readonly List<Finding> _findings = [];
    private SecurityTarget? _target;
    private ScanAuthorization? _auth;
    private SessionStatus _status = SessionStatus.Idle;
    private string _activity = string.Empty;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _endedAt;
    private string? _error;
    private string? _running;

    public SessionStatus Status => _status;

    public SecurityTarget? Target => _target;

    public bool IsAuthorized =>
        _auth is { } a && _target is { } t && ScopeGuard.NormalizeHost(a.Host) == ScopeGuard.NormalizeHost(t.Host);

    /// <summary>Punta a un bersaglio: la sessione aspetta la conferma di autorizzazione e dimentica il giro precedente.</summary>
    public void SetTarget(SecurityTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
        _auth = null;
        _actions.Clear();
        _findings.Clear();
        _running = null;
        _startedAt = _endedAt = null;
        _error = null;
        _activity = string.Empty;
        _status = SessionStatus.NeedsAuthorization;
    }

    /// <summary>Registra la conferma dell'utente. Vale solo se l'host combacia col bersaglio; restituisce true se accettata.</summary>
    public bool Authorize(ScanAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (_target is not { } target || ScopeGuard.NormalizeHost(authorization.Host) != ScopeGuard.NormalizeHost(target.Host))
            return false;
        _auth = authorization;
        return true;
    }

    /// <summary>Avvia la sessione sulle fasi date. Serve un bersaglio autorizzato e nessuna sessione già in corso.</summary>
    public bool Begin(IReadOnlyList<ScanStep> plan, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!IsAuthorized || _status == SessionStatus.Running || plan.Count == 0)
            return false;
        _actions.Clear();
        _findings.Clear();
        foreach (var step in plan)
            _actions.Add(new ScanAction(step.Id, step.Tool, step.Label, StepStatus.Pending));
        _status = SessionStatus.Running;
        _startedAt = at;
        _endedAt = null;
        _error = null;
        _running = null;
        _activity = "Avvio…";
        return true;
    }

    /// <summary>Una fase comincia: diventa «in corso» e la riga dell'attività dice cosa sta facendo.</summary>
    public void StartStep(string stepId, TimeSpan elapsed, string? activity = null)
    {
        var index = _actions.FindIndex(a => a.StepId == stepId);
        if (index < 0)
            return;
        _running = stepId;
        _actions[index] = _actions[index] with { Status = StepStatus.Running, Started = elapsed };
        _activity = activity ?? _actions[index].Label;
    }

    /// <summary>Aggiorna solo la riga dell'attività (le righe di output di uno strumento che scorre).</summary>
    public void NoteActivity(string text) => _activity = text ?? string.Empty;

    /// <summary>Aggiunge una debolezza e la conta sulla fase in corso.</summary>
    public void AddFinding(Finding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        _findings.Add(finding);
        var index = _running is { } id ? _actions.FindIndex(a => a.StepId == id) : -1;
        if (index >= 0)
            _actions[index] = _actions[index] with { Findings = _actions[index].Findings + 1 };
    }

    /// <summary>Una fase finisce (fatta, saltata o in errore).</summary>
    public void CompleteStep(string stepId, StepStatus status, TimeSpan elapsed, string? detail = null)
    {
        var index = _actions.FindIndex(a => a.StepId == stepId);
        if (index < 0)
            return;
        _actions[index] = _actions[index] with { Status = status, Ended = elapsed, Detail = detail };
        if (_running == stepId)
            _running = null;
    }

    /// <summary>Fine regolare: le fasi non ancora partite restano saltate.</summary>
    public void Complete(DateTimeOffset at)
    {
        SkipPending();
        _running = null;
        _status = SessionStatus.Completed;
        _endedAt = at;
        _activity = "Completata";
    }

    /// <summary>Fermata dall'utente: le fasi in corso o in attesa diventano saltate.</summary>
    public void Abort(DateTimeOffset at)
    {
        for (var i = 0; i < _actions.Count; i++)
            if (_actions[i].Status is StepStatus.Pending or StepStatus.Running)
                _actions[i] = _actions[i] with { Status = StepStatus.Skipped, Ended = _actions[i].Ended };
        _running = null;
        _status = SessionStatus.Aborted;
        _endedAt = at;
        _activity = "Interrotta";
    }

    /// <summary>Interrotta da un errore grave (bersaglio irraggiungibile, ecc.).</summary>
    public void Fail(string reason, DateTimeOffset at)
    {
        SkipPending();
        _running = null;
        _status = SessionStatus.Failed;
        _error = reason;
        _endedAt = at;
        _activity = "Errore";
    }

    public SecuritySnapshot Snapshot() => new(
        _status, _target, _activity, _actions.ToArray(), _findings.ToArray(), _startedAt, _endedAt, _error);

    private void SkipPending()
    {
        for (var i = 0; i < _actions.Count; i++)
            if (_actions[i].Status == StepStatus.Pending)
                _actions[i] = _actions[i] with { Status = StepStatus.Skipped };
    }
}
