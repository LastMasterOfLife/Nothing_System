namespace NothingShell.Core.Notch;

public enum NotchState
{
    Idle,
    Activity,
    Alert,
    Expanded,
}

/// <summary>
/// Macchina a stati del notch (docs/03-notch.md, docs/08-notch-mail-pages.md). Deterministica:
/// il tempo arriva dall'esterno, così si testa senza orologi reali. Ogni metodo restituisce true
/// se lo stato è cambiato.
/// </summary>
public sealed class NotchStateMachine
{
    public static readonly TimeSpan LeaveDelay = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan AlertDuration = TimeSpan.FromSeconds(6);

    private bool _activity;

    public NotchState State { get; private set; } = NotchState.Idle;

    /// <summary>Istante in cui il notch espanso si richiuderà se non arrivano altri eventi.</summary>
    public TimeSpan? CollapseDeadline { get; private set; }

    /// <summary>Istante in cui l'avviso in corso finirà.</summary>
    public TimeSpan? AlertDeadline { get; private set; }

    /// <summary>La prossima scadenza da controllare con <see cref="Tick"/>.</summary>
    public TimeSpan? NextDeadline => (CollapseDeadline, AlertDeadline) switch
    {
        ({ } c, { } a) => c < a ? c : a,
        ({ } c, null) => c,
        (null, { } a) => a,
        _ => null,
    };

    private NotchState CollapsedState => _activity ? NotchState.Activity : NotchState.Idle;

    /// <summary>Musica in riproduzione o timer in corso.</summary>
    public bool OnActivityChanged(bool active)
    {
        _activity = active;
        return State is NotchState.Idle or NotchState.Activity && SetState(CollapsedState);
    }

    /// <summary>Un avviso (mail, timer scaduto): visibile per <see cref="AlertDuration"/>, ma non interrompe il notch espanso.</summary>
    public bool OnAlert(TimeSpan now)
    {
        if (State == NotchState.Expanded)
            return false;
        AlertDeadline = now + AlertDuration;
        return SetState(NotchState.Alert);
    }

    public bool OnPointerEnter()
    {
        CollapseDeadline = null;
        AlertDeadline = null;
        return SetState(NotchState.Expanded);
    }

    public bool OnPointerLeave(TimeSpan now)
    {
        if (State == NotchState.Expanded)
            CollapseDeadline = now + LeaveDelay;
        return false;
    }

    public bool Tick(TimeSpan now)
    {
        var changed = false;
        if (CollapseDeadline is { } collapse && now >= collapse)
        {
            CollapseDeadline = null;
            changed |= SetState(CollapsedState);
        }
        if (AlertDeadline is { } alert && now >= alert)
        {
            AlertDeadline = null;
            if (State == NotchState.Alert)
                changed |= SetState(CollapsedState);
        }
        return changed;
    }

    private bool SetState(NotchState next)
    {
        if (next == State)
            return false;
        State = next;
        return true;
    }
}
