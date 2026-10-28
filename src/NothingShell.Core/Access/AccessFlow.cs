namespace NothingShell.Core.Access;

public enum AccessState
{
    Hidden,
    Locking,
    Locked,
    Welcoming,
}

/// <summary>Cosa deve fare l'host dopo un evento.</summary>
public enum AccessCommand
{
    None,
    Show,
    LockWorkstation,
    Hide,
}

/// <summary>
/// Stati dell'overlay di accesso (docs/09-access.md). Il blocco vero è di Windows: qui si decide
/// solo quando mostrare, animare e nascondere, con scadenze che impediscono all'overlay di
/// restare sopra un desktop sbloccato.
/// </summary>
public sealed class AccessFlow
{
    public static readonly TimeSpan MaxAnimation = TimeSpan.FromSeconds(4);

    private DateTimeOffset _since;

    public AccessState State { get; private set; } = AccessState.Hidden;

    public bool IsAnimating => State is AccessState.Locking or AccessState.Welcoming;

    public TimeSpan Elapsed(DateTimeOffset now) => now - _since;

    /// <summary>Fotogramma da disegnare adesso.</summary>
    public AccessFrame Frame(DateTimeOffset now) => State switch
    {
        AccessState.Locking => AccessTimeline.Lock(Elapsed(now)),
        AccessState.Welcoming => AccessTimeline.Welcome(Elapsed(now)),
        _ => AccessTimeline.Locked,
    };

    /// <summary>Blocca dalla shell: prima l'animazione di chiusura, poi il blocco di Windows.</summary>
    public AccessCommand RequestLock(DateTimeOffset now)
    {
        if (State is AccessState.Locking or AccessState.Locked)
            return AccessCommand.None;
        Enter(AccessState.Locking, now);
        return AccessCommand.Show;
    }

    /// <summary>La sessione si è bloccata (Win+L, blocco automatico o il nostro LockWorkStation).</summary>
    public AccessCommand OnSessionLocked(DateTimeOffset now)
    {
        var wasHidden = State is AccessState.Hidden or AccessState.Welcoming;
        Enter(AccessState.Locked, now);
        return wasHidden ? AccessCommand.Show : AccessCommand.None;
    }

    /// <summary>La sessione è stata sbloccata: parte il bentornato.</summary>
    public AccessCommand OnSessionUnlocked(DateTimeOffset now)
    {
        if (State is not (AccessState.Locked or AccessState.Locking))
            return AccessCommand.None;
        Enter(AccessState.Welcoming, now);
        return AccessCommand.None;
    }

    /// <summary>LockWorkStation non è riuscito: nessun overlay sopra il desktop.</summary>
    public AccessCommand OnLockFailed() => Hide();

    /// <summary>Clic sull'overlay: chi lo vede ha il desktop davanti, quindi si va avanti.</summary>
    public AccessCommand Skip(DateTimeOffset now) => State switch
    {
        AccessState.Locking => Finish(AccessState.Locked, now, AccessCommand.LockWorkstation),
        AccessState.Locked => Finish(AccessState.Welcoming, now, AccessCommand.None),
        AccessState.Welcoming => Hide(),
        _ => AccessCommand.None,
    };

    /// <summary>Avanza il tempo: chiude le sequenze finite o scadute.</summary>
    public AccessCommand OnTick(DateTimeOffset now)
    {
        var elapsed = Elapsed(now);
        return State switch
        {
            AccessState.Locking when elapsed >= AccessTimeline.LockDuration || elapsed >= MaxAnimation =>
                Finish(AccessState.Locked, now, AccessCommand.LockWorkstation),
            AccessState.Welcoming when elapsed >= AccessTimeline.WelcomeDuration || elapsed >= MaxAnimation => Hide(),
            _ => AccessCommand.None,
        };
    }

    private AccessCommand Finish(AccessState state, DateTimeOffset now, AccessCommand command)
    {
        Enter(state, now);
        return command;
    }

    private AccessCommand Hide()
    {
        if (State == AccessState.Hidden)
            return AccessCommand.None;
        State = AccessState.Hidden;
        return AccessCommand.Hide;
    }

    private void Enter(AccessState state, DateTimeOffset now)
    {
        State = state;
        _since = now;
    }
}
