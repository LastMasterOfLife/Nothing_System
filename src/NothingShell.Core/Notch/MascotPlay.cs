namespace NothingShell.Core.Notch;

/// <summary>
/// I giochi col mouse (docs/21-mascotte.md): riconosce i gesti che fai vicino al notch a riposo e dice come risponde la
/// mascotte. Carezza avanti e indietro appena sotto la pillola → fusa; solletico (scatti piccoli e svelti) → ridacchia; saluto
/// largo più in basso → ricambia; su e giù → annuisce; un giro intorno → le gira la testa; un colpetto sulla pillola → sobbalza,
/// e al terzo di fila si offende. Le posizioni sono in DIP rispetto al centro degli occhi, con y verso il basso: la pillola va
/// da −<c>halfWidth</c> a <c>halfWidth</c> e finisce in basso a <c>bottom</c>. Deterministica: il tempo arriva da fuori.
/// </summary>
public sealed class MascotPlay(double halfWidth, double bottom)
{
    /// <summary>Oltre questa distanza dagli occhi, in orizzontale o in basso, il mouse non gioca con lei.</summary>
    public const double Reach = 450;

    /// <summary>La fascia sotto la pillola dove si accarezza e si fa il solletico.</summary>
    public const double Close = 60;

    /// <summary>Un colpetto: dentro la pillola dal basso e di nuovo fuori in basso entro questo tempo (prima che si apra).</summary>
    public static readonly TimeSpan PokeLength = TimeSpan.FromMilliseconds(450);

    /// <summary>Tre colpetti entro questo tempo e si offende.</summary>
    public static readonly TimeSpan HuffWindow = TimeSpan.FromSeconds(6);

    private static readonly TimeSpan TrailLength = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(250); // fra due posizioni: di più, l'interfaccia non guardava
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(300); // fermo così a lungo (un clic, una lettura): non è un gesto
    private const double Still = 2;
    private const double Jitter = 3; // inversioni più piccole sono tremolio della mano

    private readonly List<Sample> _trail = [];
    private readonly List<TimeSpan> _pokes = [];
    private Sample? _last;
    private Sample? _rest;
    private TimeSpan? _entered;
    private bool _fromBelow;

    /// <summary>Se il mouse è abbastanza vicino da giocare: l'interfaccia allora lo guarda più spesso.</summary>
    public bool Near { get; private set; }

    /// <summary>Il mouse a <paramref name="now"/>: se ha appena finito un gesto, la risposta della mascotte.</summary>
    public MascotMood? Observe(TimeSpan now, double x, double y)
    {
        if (_last is { } last && (now - last.Time > Gap || now < last.Time))
            Forget();
        var sample = new Sample(now, x, y);
        var before = _last;
        _last = sample;
        Near = Math.Abs(x) <= Reach && y <= Reach;
        if (!Near)
        {
            Forget();
            return null;
        }

        if (Inside(sample))
        {
            if (before is not { } b || !Inside(b))
            {
                _entered = now;
                _fromBelow = before is { } previous && previous.Y > bottom;
            }
            _trail.Clear(); // i gesti si fanno fuori: sopra la pillola il notch si prepara ad aprirsi
            _rest = null;
            return null;
        }
        if (_entered is { } entered)
        {
            _entered = null;
            if (_fromBelow && y > bottom && now - entered <= PokeLength)
                return Poked(now);
        }

        if (_rest is not { } rest || Distance(rest, sample) > Still)
            _rest = sample;
        else if (now - rest.Time > Pause)
            _trail.Clear();
        _trail.Add(sample);
        _trail.RemoveAll(s => now - s.Time > TrailLength);
        if (Recognize(now) is not { } answer)
            return null;
        _trail.Clear(); // lo stesso movimento non risponde due volte
        return answer;
    }

    private bool Inside(Sample s) => Math.Abs(s.X) <= halfWidth && s.Y <= bottom;

    private void Forget()
    {
        _trail.Clear();
        _last = _rest = null;
        _entered = null;
    }

    private MascotMood Poked(TimeSpan now)
    {
        _pokes.RemoveAll(t => now - t > HuffWindow);
        _pokes.Add(now);
        if (_pokes.Count < 3)
            return MascotMood.Poked;
        _pokes.Clear();
        return MascotMood.Huffy;
    }

    private MascotMood? Recognize(TimeSpan now)
    {
        if (_trail.Count < 4)
            return null;
        var xs = Turns(s => s.X);
        var ys = Turns(s => s.Y);
        if (Tickled(xs, now) || Tickled(ys, now))
            return MascotMood.Giggle;
        // carezza: avanti e indietro due volte, tratti da 20 DIP in su, appena sotto la pillola
        if (Shake(xs, 4, TimeSpan.FromSeconds(1.8), 20, (halfWidth * 2) + 80, now) is { } pet
            && All(pet.Since, CloseBy) && Flat(pet, s => s.Y))
            return MascotMood.Purr;
        // saluto: avanti e indietro largo, più in basso
        if (Shake(xs, 4, TimeSpan.FromSeconds(2), 50, Reach * 2, now) is { } wave
            && Mean(wave.Since, s => s.Y) > bottom + Close && All(wave.Since, s => s.Y > bottom) && Flat(wave, s => s.Y))
            return MascotMood.WaveBack;
        // sì: su e giù due volte, sotto la pillola
        if (Shake(ys, 3, TimeSpan.FromSeconds(1.6), 40, Reach, now) is { } nod
            && All(nod.Since, s => s.Y > bottom) && Flat(nod, s => s.X))
            return MascotMood.Nod;
        return Loop(now) ? MascotMood.Dizzy : null;
    }

    /// <summary>Solletico: almeno sei scatti piccoli in un secondo, in orizzontale o in verticale, appena sotto la pillola.</summary>
    private bool Tickled(List<(TimeSpan Time, double At)> turns, TimeSpan now) =>
        Shake(turns, 6, TimeSpan.FromSeconds(1), Jitter, 20, now) is { } tickle && All(tickle.Since, CloseBy);

    private bool CloseBy(Sample s) => s.Y > bottom && s.Y <= bottom + Close && Math.Abs(s.X) <= halfWidth + 40;

    /// <summary>Dove il mouse ha invertito la marcia su un asse, dalla più vecchia; le inversioni sotto <see cref="Jitter"/> no.</summary>
    private List<(TimeSpan Time, double At)> Turns(Func<Sample, double> axis)
    {
        var turns = new List<(TimeSpan, double)>();
        var direction = 0;
        var extreme = axis(_trail[0]);
        var at = _trail[0].Time;
        for (var i = 1; i < _trail.Count; i++)
        {
            var value = axis(_trail[i]);
            if (direction == 0)
            {
                if (Math.Abs(value - extreme) < Jitter)
                    continue;
                direction = Math.Sign(value - extreme);
            }
            if ((value - extreme) * direction >= 0)
            {
                (extreme, at) = (value, _trail[i].Time);
            }
            else if ((extreme - value) * direction >= Jitter)
            {
                turns.Add((at, extreme));
                direction = -direction;
                (extreme, at) = (value, _trail[i].Time);
            }
        }
        return turns;
    }

    /// <summary>
    /// Le ultime <paramref name="count"/> inversioni, se sono tutte negli ultimi <paramref name="within"/> e ogni tratto fra
    /// l'una e l'altra è lungo da <paramref name="min"/> a <paramref name="max"/> DIP.
    /// </summary>
    private static Stroke? Shake(List<(TimeSpan Time, double At)> turns, int count, TimeSpan within, double min, double max, TimeSpan now)
    {
        var first = turns.Count - count;
        if (first < 0 || now - turns[first].Time > within)
            return null;
        var total = 0.0;
        for (var i = first + 1; i < turns.Count; i++)
        {
            var length = Math.Abs(turns[i].At - turns[i - 1].At);
            if (length < min || length > max)
                return null;
            total += length;
        }
        return new(turns[first].Time, total / (count - 1));
    }

    /// <summary>Sull'altro asse il mouse si è mosso poco: avanti e indietro, non in diagonale.</summary>
    private bool Flat(Stroke stroke, Func<Sample, double> other)
    {
        var values = _trail.Where(s => s.Time >= stroke.Since).Select(other).ToList();
        return values.Max() - values.Min() <= Math.Max(12, stroke.Length * 0.5);
    }

    private bool All(TimeSpan since, Func<Sample, bool> where) => _trail.Where(s => s.Time >= since).All(where);

    private double Mean(TimeSpan since, Func<Sample, double> of) => _trail.Where(s => s.Time >= since).Average(of);

    /// <summary>
    /// Un giro: negli ultimi istanti il mouse ha girato tutt'intorno a un centro sotto la pillola, a distanza quasi costante
    /// (avanti e indietro passa dal centro e non vale).
    /// </summary>
    private bool Loop(TimeSpan now)
    {
        for (var start = _trail.Count - 8; start >= 0; start--)
        {
            if (now - _trail[start].Time < TimeSpan.FromSeconds(0.4))
                continue;
            var n = _trail.Count - start;
            double cx = 0, cy = 0;
            for (var i = start; i < _trail.Count; i++)
            {
                cx += _trail[i].X;
                cy += _trail[i].Y;
            }
            (cx, cy) = (cx / n, cy / n);
            if (cy <= bottom)
                continue;
            double radius = 0, nearest = double.MaxValue, swept = 0;
            for (var i = start; i < _trail.Count; i++)
            {
                var r = double.Hypot(_trail[i].X - cx, _trail[i].Y - cy);
                radius += r / n;
                nearest = Math.Min(nearest, r);
                if (i > start)
                {
                    var turn = Math.Atan2(_trail[i].Y - cy, _trail[i].X - cx) - Math.Atan2(_trail[i - 1].Y - cy, _trail[i - 1].X - cx);
                    swept += Math.IEEERemainder(turn, Math.PI * 2);
                }
            }
            if (radius >= 18 && nearest >= radius * 0.35 && Math.Abs(swept) >= Math.PI * 2 * 0.92)
                return true;
        }
        return false;
    }

    private static double Distance(Sample a, Sample b) => double.Hypot(a.X - b.X, a.Y - b.Y);

    private readonly record struct Sample(TimeSpan Time, double X, double Y);

    /// <summary>Da quando dura un avanti e indietro, e quanto è lungo in media ogni tratto.</summary>
    private readonly record struct Stroke(TimeSpan Since, double Length);
}
