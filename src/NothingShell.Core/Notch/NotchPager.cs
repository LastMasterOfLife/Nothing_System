namespace NothingShell.Core.Notch;

public enum NotchPage
{
    Home,
    Agenda,
    Mail,
    Phone,
    Work,
    Agents,
    Servers,
    Debug,
    Devices,
    Database,
    Ftp,
    Diagnosi,
    Hours,
    Clipboard,
    Timer,
    System,
}

public enum SwipeDirection
{
    None,
    Next,
    Previous,
}

/// <summary>
/// Pagina corrente del notch espanso; ai bordi lo swipe si ferma (docs/08-notch-mail-pages.md). Le pagine delle
/// funzioni spente non ci sono (docs/12-sviluppo.md): la Home c'è sempre.
/// </summary>
public sealed class NotchPager
{
    public static readonly NotchPage[] Pages = Enum.GetValues<NotchPage>();

    private NotchPage[] _available = Pages;

    public NotchPage Current { get; private set; } = NotchPage.Home;

    public IReadOnlyList<NotchPage> Available => _available;

    public int Index => Array.IndexOf(_available, Current);

    /// <summary>
    /// Le pagine visibili, nell'ordine dato (la Home è sempre la prima); true se la corrente è sparita (si torna alla Home).
    /// </summary>
    public bool SetAvailable(IEnumerable<NotchPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        _available = [NotchPage.Home, .. pages.Where(p => p != NotchPage.Home).Distinct()];
        if (_available.Contains(Current))
            return false;
        Current = NotchPage.Home;
        return true;
    }

    public bool IsAvailable(NotchPage page) => _available.Contains(page);

    /// <summary>Applica lo swipe; restituisce true se la pagina è cambiata.</summary>
    public bool Apply(SwipeDirection direction)
    {
        var target = direction switch
        {
            SwipeDirection.Next => Math.Min(Index + 1, _available.Length - 1),
            SwipeDirection.Previous => Math.Max(Index - 1, 0),
            _ => Index,
        };
        return Show(_available[target]);
    }

    public bool Show(NotchPage page)
    {
        if (page == Current || !_available.Contains(page))
            return false;
        Current = page;
        return true;
    }
}

/// <summary>
/// Ordine delle pagine scelto in Impostazioni › Notch: la Home resta la prima, le pagine mai ordinate (nuove o
/// assenti dal file) seguono nell'ordine di serie.
/// </summary>
public static class NotchPageOrder
{
    /// <summary>Tutte le pagine, una volta sola, nell'ordine salvato.</summary>
    public static IReadOnlyList<NotchPage> Resolve(IEnumerable<NotchPage>? saved)
    {
        var order = new List<NotchPage> { NotchPage.Home };
        foreach (var page in (saved ?? []).Concat(NotchPager.Pages))
        {
            if (Enum.IsDefined(page) && !order.Contains(page))
                order.Add(page);
        }
        return order;
    }

    /// <summary>
    /// Sposta una pagina di un posto (<paramref name="delta"/> −1 su, +1 giù) scavalcando quelle che ora non si vedono
    /// (<paramref name="movable"/>: le funzioni spente restano dove sono). La Home non si sposta e non si scavalca.
    /// </summary>
    public static IReadOnlyList<NotchPage> Move(IEnumerable<NotchPage>? saved, NotchPage page, int delta, IEnumerable<NotchPage> movable)
    {
        ArgumentNullException.ThrowIfNull(movable);
        var order = Resolve(saved).ToList();
        var candidates = movable.ToHashSet();
        var from = order.IndexOf(page);
        if (page == NotchPage.Home || delta == 0 || from < 0)
            return order;
        var step = Math.Sign(delta);
        for (var to = from + step; to > 0 && to < order.Count; to += step)
        {
            if (!candidates.Contains(order[to]))
                continue;
            (order[from], order[to]) = (order[to], order[from]);
            break;
        }
        return order;
    }
}

/// <summary>
/// Trasforma rotellina, scorrimento del touchpad e trascinamento in swipe di una pagina alla volta:
/// gli spostamenti si sommano fino alla soglia, poi una pausa evita salti multipli.
/// </summary>
public sealed class SwipeDetector
{
    /// <summary>Una tacca di rotellina (WHEEL_DELTA).</summary>
    public const double WheelThreshold = 120;

    public const double DragThreshold = 40;
    public static readonly TimeSpan Cooldown = TimeSpan.FromMilliseconds(400);

    /// <summary>Senza nuovi movimenti per questo tempo, la somma riparte da zero.</summary>
    public static readonly TimeSpan IdleReset = TimeSpan.FromMilliseconds(300);

    private double _accumulated;
    private TimeSpan _lastInput; // zero: il primo evento parte sempre da una somma vuota
    private TimeSpan _cooldownUntil = TimeSpan.MinValue;
    private double? _dragStart;
    private bool _dragFired;

    /// <summary>Scorrimento orizzontale (touchpad): positivo = contenuto verso sinistra = pagina successiva.</summary>
    public SwipeDirection OnHorizontalWheel(double delta, TimeSpan now) => Accumulate(delta, now);

    /// <summary>Rotellina verticale: negativo (verso il basso) = pagina successiva.</summary>
    public SwipeDirection OnVerticalWheel(double delta, TimeSpan now) => Accumulate(-delta, now);

    public void OnDragStart(double x)
    {
        _dragStart = x;
        _dragFired = false;
    }

    /// <summary>Un trascinamento cambia al massimo una pagina, verso sinistra = successiva.</summary>
    public SwipeDirection OnDragMove(double x)
    {
        if (_dragStart is not { } start || _dragFired)
            return SwipeDirection.None;
        var dx = x - start;
        if (Math.Abs(dx) < DragThreshold)
            return SwipeDirection.None;
        _dragFired = true;
        return dx < 0 ? SwipeDirection.Next : SwipeDirection.Previous;
    }

    /// <summary>Fine del trascinamento; true se ha cambiato pagina (il clic va ignorato).</summary>
    public bool OnDragEnd()
    {
        var fired = _dragFired;
        _dragStart = null;
        _dragFired = false;
        return fired;
    }

    private SwipeDirection Accumulate(double delta, TimeSpan now)
    {
        if (now < _cooldownUntil)
            return SwipeDirection.None;
        if (now - _lastInput > IdleReset)
            _accumulated = 0;
        _lastInput = now;
        _accumulated += delta;
        if (Math.Abs(_accumulated) < WheelThreshold)
            return SwipeDirection.None;

        var direction = _accumulated > 0 ? SwipeDirection.Next : SwipeDirection.Previous;
        _accumulated = 0;
        _cooldownUntil = now + Cooldown;
        return direction;
    }
}

/// <summary>Conto alla rovescia del timer del notch.</summary>
public sealed class Countdown
{
    private TimeSpan _endsAt;

    public bool IsRunning { get; private set; }

    public TimeSpan Duration { get; private set; }

    public void Start(TimeSpan duration, TimeSpan now)
    {
        Duration = duration;
        _endsAt = now + duration;
        IsRunning = true;
    }

    public void Stop() => IsRunning = false;

    public TimeSpan Remaining(TimeSpan now) =>
        !IsRunning ? TimeSpan.Zero : _endsAt > now ? _endsAt - now : TimeSpan.Zero;

    /// <summary>True una sola volta, quando il tempo è finito: il timer si ferma.</summary>
    public bool CheckFinished(TimeSpan now)
    {
        if (!IsRunning || now < _endsAt)
            return false;
        IsRunning = false;
        return true;
    }

    /// <summary>mm:ss, oppure h:mm:ss oltre l'ora; i secondi parziali contano come interi (59.2 s → 01:00).</summary>
    public static string Format(TimeSpan remaining)
    {
        var total = (int)Math.Ceiling(Math.Max(0, remaining.TotalSeconds));
        var hours = total / 3600;
        var minutes = total / 60 % 60;
        var seconds = total % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{seconds:00}" : $"{minutes:00}:{seconds:00}";
    }
}
