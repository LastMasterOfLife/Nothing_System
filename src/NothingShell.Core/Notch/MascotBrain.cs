using NothingShell.Core.Design;

namespace NothingShell.Core.Notch;

/// <summary>
/// Dove vogliono andare gli occhi della mascotte: <see cref="Open"/> 0 chiusi, 1 aperti, oltre 1 sgranati; <see cref="LookX"/> e
/// <see cref="LookY"/> da −1 a 1 (sinistra/su → destra/giù); <see cref="Smile"/> 0..1 strizza gli occhi in un sorriso;
/// <see cref="Lift"/> da −1 a 0 solleva tutto il viso (i saltelli della contentezza); <see cref="Wink"/> chiude un occhio solo,
/// verso −1 il sinistro e verso 1 il destro.
/// </summary>
public readonly record struct MascotPose(double Open, double LookX, double LookY, double Smile, double Lift = 0, double Wink = 0);

/// <summary>
/// Espressioni della mascotte (docs/21-mascotte.md): le reazioni a ciò che succede e, con l'iniziativa accesa, i gesti che fa
/// da sé e quelli con cui ti chiama prima di dirti qualcosa.
/// </summary>
public enum MascotMood
{
    /// <summary>Arriva un avviso: occhi sgranati verso l'alto.</summary>
    Surprised,

    /// <summary>Un agente o un comando ha finito: sorride e saltella.</summary>
    Happy,

    /// <summary>Un comando è fallito: occhi socchiusi verso il basso.</summary>
    Sad,

    /// <summary>Strizza un occhio sorridendo.</summary>
    Wink,

    /// <summary>Fa girare lo sguardo tutt'intorno, in un cerchio.</summary>
    LookAround,

    /// <summary>Un saltello solo, contenta di niente.</summary>
    Hop,

    /// <summary>Sbadiglia: occhi sgranati, poi strizzati all'insù, poi riaperti a metà.</summary>
    Yawn,

    /// <summary>Ha qualcosa da dirti: ti guarda con gli occhi grandi e fa due saltelli.</summary>
    Calling,

    /// <summary>Le gira la testa (il PC fatica): due giri veloci degli occhi, poi barcolla.</summary>
    Dizzy,

    /// <summary>Parla nell'avviso: guarda il suo testo e sobbalza a ogni parola.</summary>
    Talking,

    /// <summary>L'accarezzi: occhi socchiusi e felici che dondolano, con un fremito.</summary>
    Purr,

    /// <summary>Le fai il solletico: ridacchia a saltelli, strizzando un occhio e poi l'altro.</summary>
    Giggle,

    /// <summary>La saluti: ricambia muovendo lo sguardo di qua e di là, poi un occhiolino e un saltello.</summary>
    WaveBack,

    /// <summary>Muovi il mouse su e giù: annuisce due volte.</summary>
    Nod,

    /// <summary>Un colpetto sulla pillola: strizza gli occhi e sobbalza, poi ti fissa.</summary>
    Poked,

    /// <summary>Al terzo colpetto di fila si offende: occhi a fessura, guarda dall'altra parte.</summary>
    Huffy,
}

/// <summary>
/// La mascotte del notch (docs/20-stile-nusa.md, docs/21-mascotte.md): sbatte le palpebre a intervalli irregolari, segue il
/// mouse, si guarda intorno quando il mouse è fermo, sorride quando ci si passa sopra, si addormenta se il PC resta inattivo e al
/// risveglio sgrana gli occhi. Con <see cref="Initiative"/> fa anche gesti suoi. Decide solo le pose; le molle dell'interfaccia
/// le rendono fluide. Deterministica: tempo e caso arrivano da fuori.
/// </summary>
public sealed class MascotBrain(Random random)
{
    public static readonly TimeSpan SleepAfter = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan BlinkLength = TimeSpan.FromMilliseconds(110);
    public static readonly TimeSpan WakeLength = TimeSpan.FromMilliseconds(600);

    /// <summary>Dopo quanto un mouse fermo smette di essere seguito e la mascotte si guarda intorno.</summary>
    public static readonly TimeSpan CursorRest = TimeSpan.FromSeconds(4);

    public static readonly TimeSpan SurpriseLength = TimeSpan.FromMilliseconds(900);
    public static readonly TimeSpan HappyLength = TimeSpan.FromMilliseconds(1600);
    public static readonly TimeSpan SadLength = TimeSpan.FromMilliseconds(2000);

    private const double SleepingOpen = 0.08;
    private const double DrowsyOpen = 0.55;
    private const double TiredOpen = 0.7;

    private TimeSpan? _nextBlink;
    private TimeSpan? _blinkEnd;
    private TimeSpan? _secondBlink;
    private TimeSpan? _nextGlance;
    private TimeSpan? _glanceEnd;
    private double _glanceX;
    private double _glanceY;
    private TimeSpan? _wakeEnd;
    private TimeSpan? _nextGesture;
    private bool _asleep;
    private int _side = 1;
    private (MascotMood Mood, TimeSpan Start, TimeSpan End)? _mood;

    public MascotBrain() : this(Random.Shared)
    {
    }

    public bool Asleep => _asleep;

    /// <summary>
    /// Acceso, col mouse fermo fa gesti suoi: ogni 2–5 minuti con <see cref="MascotInitiative.Sometimes"/>, ogni 40–100 secondi
    /// con <see cref="MascotInitiative.Often"/>.
    /// </summary>
    public MascotInitiative Initiative { get; set; }

    /// <summary>Batteria quasi scarica: gli occhi restano a mezz'asta e i gesti diventano sbadigli.</summary>
    public bool Tired { get; set; }

    /// <summary>Quanto dura ogni espressione.</summary>
    public static TimeSpan LengthOf(MascotMood mood) => mood switch
    {
        MascotMood.Surprised => SurpriseLength,
        MascotMood.Happy => HappyLength,
        MascotMood.Sad => SadLength,
        MascotMood.Wink => TimeSpan.FromMilliseconds(650),
        MascotMood.LookAround => TimeSpan.FromMilliseconds(1400),
        MascotMood.Hop => TimeSpan.FromMilliseconds(600),
        MascotMood.Yawn => TimeSpan.FromMilliseconds(1800),
        MascotMood.Calling => TimeSpan.FromMilliseconds(700),
        MascotMood.Dizzy => TimeSpan.FromMilliseconds(2000),
        MascotMood.Talking => TimeSpan.FromMilliseconds(1600),
        MascotMood.Purr => TimeSpan.FromMilliseconds(1800),
        MascotMood.Giggle => TimeSpan.FromMilliseconds(1000),
        MascotMood.WaveBack => TimeSpan.FromMilliseconds(1500),
        MascotMood.Nod => TimeSpan.FromMilliseconds(1150),
        MascotMood.Poked => TimeSpan.FromMilliseconds(900),
        _ => TimeSpan.FromMilliseconds(2000),
    };

    /// <summary>La reazione in corso a <paramref name="now"/>, se c'è.</summary>
    public MascotMood? MoodAt(TimeSpan now) => _mood is { } m && now >= m.Start && now < m.End ? m.Mood : null;

    /// <summary>Reagisce a qualcosa che succede: per poco la posa è quella della reazione, e se dormiva si sveglia.</summary>
    public void React(MascotMood mood, TimeSpan now)
    {
        _mood = (mood, now, now + LengthOf(mood));
        if (mood is MascotMood.Wink or MascotMood.Giggle or MascotMood.WaveBack or MascotMood.Huffy)
            _side = random.NextDouble() < 0.5 ? -1 : 1;
    }

    /// <summary>
    /// La posa di adesso. <paramref name="cursor"/> è il mouse rispetto al centro del notch, già in −1..1 (null se lontano o
    /// fermo da <see cref="CursorRest"/>); <paramref name="idle"/> è da quanto non si tocca mouse o tastiera.
    /// </summary>
    public MascotPose Pose(TimeSpan now, (double X, double Y)? cursor, TimeSpan idle, bool greeting)
    {
        var mood = MoodAt(now);
        var sleeping = idle >= SleepAfter && !greeting && mood is null;
        if (_asleep && !sleeping)
        {
            _wakeEnd = now + WakeLength;
            _nextBlink = now + WakeLength + TimeSpan.FromMilliseconds(250); // appena sveglio sbatte gli occhi
        }
        _asleep = sleeping;
        if (sleeping)
        {
            _blinkEnd = _secondBlink = _nextGlance = _glanceEnd = _nextGesture = null;
            return new(SleepingOpen, 0, 0.6, 0);
        }
        if (mood is null && cursor is null && !greeting)
            mood = Gesture(now, idle);
        if (mood is { } reaction)
            return Feel(reaction, now - _mood!.Value.Start, _side);

        var open = Blink(now) ? 0.06 : 1.0;
        if (_wakeEnd is { } wake && now < wake)
            open = Math.Max(open, 1.25);
        else if (idle >= SleepAfter * 0.75)
            open = Math.Min(open, DrowsyOpen); // gli si chiudono gli occhi poco prima di dormire
        else if (Tired)
            open = Math.Min(open, TiredOpen);

        var (x, y) = cursor is { } c
            ? (Math.Clamp(c.X, -1, 1), Math.Clamp(c.Y, -1, 1))
            : Glance(now);
        if (cursor is not null)
            _nextGlance = _glanceEnd = null;
        return new(open, x, y, greeting ? 1 : 0);
    }

    /// <summary>
    /// La posa di un'espressione <paramref name="elapsed"/> dopo il suo inizio (docs/21-mascotte.md); <paramref name="side"/> è
    /// l'occhio dell'occhiolino, −1 il sinistro e 1 il destro.
    /// </summary>
    public static MascotPose Feel(MascotMood mood, TimeSpan elapsed, int side = 1)
    {
        var t = elapsed.TotalSeconds;
        return mood switch
        {
            MascotMood.Surprised => new(1.4, 0, -0.7, 0),
            MascotMood.Happy => new(1, 0, 0, 1, Hops(t, 2, 0.8, 1)),
            MascotMood.Sad => new(0.5, 0, 0.8, 0),
            MascotMood.Wink => t < 0.45 ? new(1, 0.3 * side, -0.1, 0.6, 0, side) : new(1, 0.15 * side, 0, 0.3),
            MascotMood.LookAround => t < 1.2
                ? new(1.05, 0.9 * Math.Sin(t * Math.PI * 2 / 1.2), -0.9 * Math.Cos(t * Math.PI * 2 / 1.2), 0)
                : new(1, 0, 0, 0),
            MascotMood.Hop => new(1.1, 0, -0.2, 0.7, Hops(t, 1, 0.4, 1)),
            MascotMood.Yawn => t switch
            {
                < 0.25 => new(1.15, 0, -0.3, 0),
                < 0.65 => new(Lerp(1.15, 0.12, (t - 0.25) / 0.4), 0, Lerp(-0.3, -0.5, (t - 0.25) / 0.4), 0, Lerp(0, -0.35, (t - 0.25) / 0.4)),
                < 1.2 => new(0.12, 0, -0.5, 0, -0.35),
                _ => new(Lerp(0.12, 0.75, (t - 1.2) / 0.4), 0, 0.1, 0),
            },
            MascotMood.Calling => new(1.2, 0, 0.35, 0.2, Hops(t, 2, 0.5, 0.6)),
            MascotMood.Dizzy => t < 1.2
                ? new(0.9, Math.Sin(t * Math.PI * 2 / 0.6), -Math.Cos(t * Math.PI * 2 / 0.6), 0)
                : new(0.6, 0.4 * Math.Sin(t * Math.PI * 6) * (2 - t), 0.3, 0),
            MascotMood.Talking => new(1, 0.8, 0.1, 0.35, Hops(t, 4, 1.6, 0.5)),
            // si struscia contro la mano: dondola piano (0,9 s a passata) e freme 4 volte al secondo
            MascotMood.Purr => t < 1.5
                ? new(0.6, 0.3 * Math.Sin(t * Math.PI * 2 / 0.9), 0.1, 1, -0.15 * Math.Abs(Math.Sin(t * Math.PI * 4)))
                : new(Lerp(0.6, 1, (t - 1.5) / 0.3), 0, 0.1, Lerp(1, 0.4, (t - 1.5) / 0.3)),
            MascotMood.Giggle => t < 0.75
                ? new(0.9, 0.25 * Alternate(t, 0.25, side), 0, 0.6, Hops(t, 3, 0.75, 0.55), Alternate(t, 0.25, side))
                : new(0.85, 0, 0, 0.6),
            MascotMood.WaveBack => t switch
            {
                < 0.9 => new(1.05, 0.8 * Math.Sin(t * Math.PI * 2 / 0.6), 0.3, 0.7),
                < 1.35 => new(1, 0.2 * side, 0.3, 0.6, Hops(t - 0.9, 1, 0.35, 0.6), side),
                _ => new(1, 0, 0.2, 0.3),
            },
            // giù e su due volte: guardando in su tutto il viso si alza
            MascotMood.Nod => t < 1
                ? new(1, 0, 0.2 + (0.7 * Math.Cos(t * Math.PI * 2 / 0.5)), 0.4, -0.7 * (0.5 - (0.5 * Math.Cos(t * Math.PI * 2 / 0.5))))
                : new(1, 0, 0.1, 0.3),
            MascotMood.Poked => t switch
            {
                < 0.15 => new(0.1, 0, 0, 0.4, -0.4),
                < 0.75 => new(1.3, 0, 0.6, 0),
                _ => new(1, 0, 0.3, 0),
            },
            _ => t switch // offesa: sbuffa con un saltello e guarda dall'altra parte, poi torna a sbirciarti
            {
                < 0.2 => new(0.4, 0, 0.2, 0),
                < 1.6 => new(0.5, -0.9 * side, -0.3, 0, Hops(t - 0.2, 1, 0.3, 0.4)),
                _ => new(0.8, Lerp(-0.9 * side, 0, (t - 1.6) / 0.4), 0.2, 0),
            },
        };
    }

    /// <summary><paramref name="side"/> e il suo opposto, uno ogni <paramref name="seconds"/>: l'occhio che strizza ridacchiando.</summary>
    private static int Alternate(double t, double seconds, int side) => (int)(t / seconds) % 2 == 0 ? side : -side;

    /// <summary><paramref name="count"/> saltelli alti <paramref name="height"/> nei primi <paramref name="seconds"/>, poi fermo.</summary>
    private static double Hops(double t, int count, double seconds, double height) =>
        t < seconds ? -height * Math.Abs(Math.Sin(t * Math.PI * count / seconds)) : 0;

    private static double Lerp(double from, double to, double amount) => from + ((to - from) * Math.Clamp(amount, 0, 1));

    /// <summary>Con l'iniziativa accesa, ogni tanto un gesto suo: assonnata o stanca solo sbadigli.</summary>
    private MascotMood? Gesture(TimeSpan now, TimeSpan idle)
    {
        if (Initiative == MascotInitiative.Off || (_wakeEnd is { } wake && now < wake))
        {
            _nextGesture = null;
            return null;
        }
        _nextGesture ??= now + GestureGap();
        if (now < _nextGesture)
            return null;
        _nextGesture = now + GestureGap();
        var mood = Tired || idle >= SleepAfter * 0.75
            ? MascotMood.Yawn
            : random.NextDouble() switch
            {
                < 0.3 => MascotMood.Wink,
                < 0.6 => MascotMood.LookAround,
                < 0.8 => MascotMood.Hop,
                _ => MascotMood.Yawn,
            };
        React(mood, now);
        return mood;
    }

    private TimeSpan GestureGap() => Initiative == MascotInitiative.Often ? Between(40, 100) : Between(120, 300);

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
