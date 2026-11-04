namespace NothingShell.Core.Notch;

/// <summary>
/// Dove vogliono andare gli occhi della mascotte: <see cref="Open"/> 0 chiusi, 1 aperti, oltre 1 sgranati; <see cref="LookX"/> e
/// <see cref="LookY"/> da −1 a 1 (sinistra/su → destra/giù); <see cref="Smile"/> 0..1 strizza gli occhi in un sorriso.
/// </summary>
public readonly record struct MascotPose(double Open, double LookX, double LookY, double Smile);

/// <summary>
/// La mascotte del notch (docs/20-stile-nusa.md): sbatte le palpebre a intervalli irregolari, segue il mouse, si guarda intorno
/// quando il mouse è fermo, sorride quando ci si passa sopra, si addormenta se il PC resta inattivo e al risveglio sgrana gli
/// occhi. Decide solo le pose; le molle dell'interfaccia le rendono fluide. Deterministica: tempo e caso arrivano da fuori.
/// </summary>
public sealed class MascotBrain(Random random)
{
    public static readonly TimeSpan SleepAfter = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan BlinkLength = TimeSpan.FromMilliseconds(110);
    public static readonly TimeSpan WakeLength = TimeSpan.FromMilliseconds(600);

    /// <summary>Dopo quanto un mouse fermo smette di essere seguito e la mascotte si guarda intorno.</summary>
    public static readonly TimeSpan CursorRest = TimeSpan.FromSeconds(4);

    private const double SleepingOpen = 0.08;
    private const double DrowsyOpen = 0.55;

    private TimeSpan? _nextBlink;
    private TimeSpan? _blinkEnd;
    private TimeSpan? _secondBlink;
    private TimeSpan? _nextGlance;
    private TimeSpan? _glanceEnd;
    private double _glanceX;
    private double _glanceY;
    private TimeSpan? _wakeEnd;
    private bool _asleep;

    public MascotBrain() : this(Random.Shared)
    {
    }

    public bool Asleep => _asleep;

    /// <summary>
    /// La posa di adesso. <paramref name="cursor"/> è il mouse rispetto al centro del notch, già in −1..1 (null se lontano o
    /// fermo da <see cref="CursorRest"/>); <paramref name="idle"/> è da quanto non si tocca mouse o tastiera.
    /// </summary>
    public MascotPose Pose(TimeSpan now, (double X, double Y)? cursor, TimeSpan idle, bool greeting)
    {
        var sleeping = idle >= SleepAfter && !greeting;
        if (_asleep && !sleeping)
        {
            _wakeEnd = now + WakeLength;
            _nextBlink = now + WakeLength + TimeSpan.FromMilliseconds(250); // appena sveglio sbatte gli occhi
        }
        _asleep = sleeping;
        if (sleeping)
        {
            _blinkEnd = _secondBlink = _nextGlance = _glanceEnd = null;
            return new(SleepingOpen, 0, 0.6, 0);
        }

        var open = Blink(now) ? 0.06 : 1.0;
        if (_wakeEnd is { } wake && now < wake)
            open = Math.Max(open, 1.25);
        else if (idle >= SleepAfter * 0.75)
            open = Math.Min(open, DrowsyOpen); // gli si chiudono gli occhi poco prima di dormire

        var (x, y) = cursor is { } c
            ? (Math.Clamp(c.X, -1, 1), Math.Clamp(c.Y, -1, 1))
            : Glance(now);
        if (cursor is not null)
            _nextGlance = _glanceEnd = null;
        return new(open, x, y, greeting ? 1 : 0);
    }

    private bool Blink(TimeSpan now)
    {
        _nextBlink ??= now + Between(2.2, 5.5);
        if (_blinkEnd is { } end)
        {
            if (now < end)
                return true;
            _blinkEnd = null;
            // una volta su cinque, un secondo battito subito dopo
            _secondBlink = _secondBlink is null && random.NextDouble() < 0.2 ? now + TimeSpan.FromMilliseconds(140) : null;
            _nextBlink = _secondBlink ?? now + Between(2.2, 5.5);
        }
        if (now < _nextBlink)
            return false;
        _blinkEnd = now + BlinkLength;
        return true;
    }

    private (double X, double Y) Glance(TimeSpan now)
    {
        _nextGlance ??= now + Between(1.5, 4);
        if (_glanceEnd is { } end && now >= end)
        {
            _glanceEnd = null;
            _nextGlance = now + Between(2.5, 7);
        }
        if (_glanceEnd is null && now >= _nextGlance)
        {
            _glanceEnd = now + Between(0.7, 1.8);
            _glanceX = (random.NextDouble() * 2) - 1;
            _glanceY = (random.NextDouble() - 0.6) * 0.8;
        }
        return _glanceEnd is null ? (0, 0) : (_glanceX, _glanceY);
    }

    private TimeSpan Between(double minSeconds, double maxSeconds) =>
        TimeSpan.FromSeconds(minSeconds + (random.NextDouble() * (maxSeconds - minSeconds)));
}
