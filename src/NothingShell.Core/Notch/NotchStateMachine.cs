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
    /// <summary>Come Nusa: uscendo col mouse il notch si richiude subito (docs/20-stile-nusa.md). Di serie per <see cref="CloseDelay"/>.</summary>
    public static readonly TimeSpan LeaveDelay = TimeSpan.Zero;
    public static readonly TimeSpan AlertDuration = TimeSpan.FromSeconds(6);

    /// <summary>Dopo che il mouse lascia una scheda, quanto resta ancora visibile.</summary>
    public static readonly TimeSpan CardLeaveDelay = TimeSpan.FromMilliseconds(1500);

    private bool _activity;

    /// <summary>Col mouse sopra, quanto si aspetta prima di aprire (pagina Aspetto). Zero apre subito, senza gonfiarsi.</summary>
    public TimeSpan OpenDelay { get; set; } = NotchMotion.EnterDelay;

    /// <summary>Uscito il mouse, quanto resta aperto (pagina Aspetto): tornandoci sopra intanto non si chiude.</summary>
    public TimeSpan CloseDelay { get; set; } = LeaveDelay;

    public NotchState State { get; private set; } = NotchState.Idle;

    /// <summary>Si sta scrivendo nel campo della Home: il notch resta aperto anche col mouse altrove.</summary>
    public bool Held { get; private set; }

    /// <summary>Istante in cui il notch espanso si richiuderà se non arrivano altri eventi.</summary>
    public TimeSpan? CollapseDeadline { get; private set; }

    /// <summary>Istante in cui l'avviso in corso finirà.</summary>
    public TimeSpan? AlertDeadline { get; private set; }

    /// <summary>Istante in cui il notch col mouse sopra si aprirà (<see cref="OpenDelay"/>).</summary>
    public TimeSpan? ExpandDeadline { get; private set; }

    /// <summary>Il mouse è sopra e si aspetta di aprire: la pillola si gonfia un poco.</summary>
    public bool Peeking => ExpandDeadline is not null;

    /// <summary>La prossima scadenza da controllare con <see cref="Tick"/>.</summary>
    public TimeSpan? NextDeadline => new[] { CollapseDeadline, AlertDeadline, ExpandDeadline }.Where(d => d is not null).Min();

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

    /// <summary>Apre subito (pulsanti delle schede, e la fine dell'attesa di <see cref="OnPointerHover"/>).</summary>
    public bool OnPointerEnter()
    {
        AlertDeadline = null;
        ExpandDeadline = null;
        if (State == NotchState.Card)
            return false; // si resta sulla scheda finché il mouse è sopra
        CollapseDeadline = null;
        return SetState(NotchState.Expanded);
    }

    /// <summary>
    /// Il mouse arriva sul notch: si apre dopo <see cref="OpenDelay"/>, se resta. Restituisce true se comincia l'attesa (la
    /// pillola va gonfiata) o se si apre subito. Un avviso intanto non scade.
    /// </summary>
    public bool OnPointerHover(TimeSpan now)
    {
        if (State is NotchState.Expanded or NotchState.Card || OpenDelay <= TimeSpan.Zero)
            return OnPointerEnter();
        if (Peeking)
            return false;
        AlertDeadline = null;
        ExpandDeadline = now + OpenDelay;
        return true;
    }

    /// <summary>Restituisce true se il mouse è uscito durante l'attesa: la pillola torna alla sua misura.</summary>
    public bool OnPointerLeave(TimeSpan now)
    {
        if (Peeking)
        {
            ExpandDeadline = null;
            if (State == NotchState.Alert)
                AlertDeadline = now + CardLeaveDelay; // l'avviso resta ancora un attimo, poi se ne va
            return true;
        }
        if (State == NotchState.Expanded)
        {
            if (!Held)
                CollapseDeadline = now + CloseDelay;
        }
        else if (State == NotchState.Card)
        {
            AlertDeadline = now + CardLeaveDelay;
        }
        return false;
    }

    /// <summary>Tiene aperto il notch mentre si scrive. Lasciandolo col mouse altrove si richiude come al solito.</summary>
    public void Hold(bool held, bool pointerOver, TimeSpan now)
    {
        Held = held;
        if (held)
            CollapseDeadline = null;
        else if (State == NotchState.Expanded && !pointerOver)
            CollapseDeadline = now + CloseDelay;
    }

    /// <summary>Richiude subito il notch, anche col mouse sopra: la richiesta passa alla finestra dell'assistente.</summary>
    public bool Collapse()
    {
        Held = false;
        CollapseDeadline = null;
        ExpandDeadline = null;
        return State == NotchState.Expanded && SetState(CollapsedState);
    }

    public bool Tick(TimeSpan now)
    {
        var changed = false;
        if (ExpandDeadline is { } expand && now >= expand)
            changed |= OnPointerEnter();
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
