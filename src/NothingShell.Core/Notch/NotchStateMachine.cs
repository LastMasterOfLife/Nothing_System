namespace NothingShell.Core.Notch;

public enum NotchState
{
    Idle,
    Activity,
    Alert,
    Expanded,

    /// <summary>Scheda con contenuto e pulsanti (screenshot, download completato): resta aperta col mouse sopra.</summary>
    Card,
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

    /// <summary>Dopo che il mouse lascia una scheda, quanto resta ancora visibile.</summary>
    public static readonly TimeSpan CardLeaveDelay = TimeSpan.FromMilliseconds(1500);

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

    /// <summary>
    /// Un avviso (mail, timer scaduto, volume): visibile per <paramref name="duration"/> (di norma
    /// <see cref="AlertDuration"/>), ma non interrompe il notch espanso.
    /// </summary>
    public bool OnAlert(TimeSpan now, TimeSpan? duration = null)
    {
        if (State == NotchState.Expanded)
            return false;
        AlertDeadline = now + (duration ?? AlertDuration);
        return SetState(NotchState.Alert);
    }

    /// <summary>Una scheda con pulsanti: come un avviso, ma il mouse sopra la tiene aperta invece di espandere il notch.</summary>
    public bool OnCard(TimeSpan now, TimeSpan duration)
    {
        if (State == NotchState.Expanded)
            return false;
        AlertDeadline = now + duration;
        return SetState(NotchState.Card);
    }

    /// <summary>Chiude subito la scheda (dopo un'azione sui suoi pulsanti).</summary>
    public bool DismissCard()
    {
        if (State != NotchState.Card)
            return false;
        AlertDeadline = null;
        return SetState(CollapsedState);
    }

    public bool OnPointerEnter()
    {
        AlertDeadline = null;
        if (State == NotchState.Card)
            return false; // si resta sulla scheda finché il mouse è sopra
        CollapseDeadline = null;
        return SetState(NotchState.Expanded);
    }

    public bool OnPointerLeave(TimeSpan now)
    {
        if (State == NotchState.Expanded)
            CollapseDeadline = now + LeaveDelay;
        else if (State == NotchState.Card)
            AlertDeadline = now + CardLeaveDelay;
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
            if (State is NotchState.Alert or NotchState.Card)
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
