namespace NothingShell.Core.Notch;

public enum NotchState
{
    Idle,
    Activity,
    Expanded,
}

/// <summary>
/// Macchina a stati del notch (docs/03-notch.md). Deterministica: il tempo arriva dall'esterno,
/// così si testa senza orologi reali. Ogni metodo restituisce true se lo stato è cambiato.
/// </summary>
public sealed class NotchStateMachine
{
    public static readonly TimeSpan LeaveDelay = TimeSpan.FromMilliseconds(400);

    private bool _mediaPlaying;

    public NotchState State { get; private set; } = NotchState.Idle;

    /// <summary>Istante in cui il notch si richiuderà se non arrivano altri eventi.</summary>
    public TimeSpan? CollapseDeadline { get; private set; }

    private NotchState CollapsedState => _mediaPlaying ? NotchState.Activity : NotchState.Idle;

    public bool OnMediaPlayingChanged(bool playing)
    {
        _mediaPlaying = playing;
        return State != NotchState.Expanded && SetState(CollapsedState);
    }

    public bool OnPointerEnter()
    {
        CollapseDeadline = null;
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
        if (CollapseDeadline is not { } deadline || now < deadline)
            return false;
        CollapseDeadline = null;
        return SetState(CollapsedState);
    }

    private bool SetState(NotchState next)
    {
        if (next == State)
            return false;
        State = next;
        return true;
    }
}
