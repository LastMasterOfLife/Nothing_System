namespace NothingShell.Core.Leisure;

/// <summary>A che punto è la partita.</summary>
public enum RunPhase
{
    /// <summary>Il gatto aspetta seduto sul primo tetto: il primo clic lo fa partire.</summary>
    Title,
    Running,
    Paused,

    /// <summary>È caduto o ha sbattuto: vola giù, poi la partita finisce.</summary>
    Lost,
    Over,
}

public enum TreatKind
{
    /// <summary>Crocchette (カリカリ): 1 punto, +3,5% di peso.</summary>
    Kibble,

    /// <summary>Scatoletta (ねこ缶): 5 punti, +18% di peso.</summary>
    Can,
}

/// <summary>Quello che è appena successo, per le animazioni della vista.</summary>
public enum RunEvent
{
    Started,
    Jumped,
    Landed,
    Ate,
    AteCan,
    Goal,
    Fell,
    Bumped,
    Over,
}

/// <summary>Un tetto: dal bordo sinistro per <see cref="Width"/>, con il piano a <see cref="Top"/> (y verso il basso).</summary>
public readonly record struct Roof(double Left, double Width, double Top, int Look)
{
    public double Right => Left + Width;
}

public sealed class Treat(TreatKind kind, double x, double y)
{
    public TreatKind Kind { get; } = kind;

    public double X { get; } = x;

    public double Y { get; } = y;

    /// <summary>Secondi di partita in cui il gatto l'ha mangiata; null finché è lì.</summary>
    public double? EatenAt { get; internal set; }
}

/// <summary>Il traguardo di una tappa: una bandiera sul tetto.</summary>
public readonly record struct Goal(int Stage, double X, double Top);

/// <summary>
/// むちねこ (docs/25-svago.md): il gatto corre da solo sui tetti, salta i vuoti e mangia. Ogni crocchetta pesa, e più pesa meno
/// salta; correndo dimagrisce piano. Mondo in unità: la vista è alta <see cref="ViewHeight"/>, y verso il basso, la camera parte
/// da x 0 e il gatto le sta sempre a <see cref="CatScreenX"/>. Tutto deterministico dal seme, a passi fissi.
/// </summary>
public sealed class RooftopRun
{
    public const double ViewHeight = 170;
    public const double CatScreenX = 72;

    /// <summary>Mezza larghezza delle zampe: il gatto sta su un tetto finché le zampe lo toccano.</summary>
    public const double FootHalf = 7;

    public const double StartWeight = 0.2;
    public const double KibbleWeight = 0.035;
    public const double CanWeight = 0.18;

    /// <summary>Peso perso al secondo correndo sul tetto.</summary>
    public const double BurnPerSecond = 0.015;

    public const int KibblePoints = 1;
    public const int CanPoints = 5;
    public const int GoalPoints = 10;

    /// <summary>Ogni tanto il traguardo di una tappa: dopo, si corre un po' più svelti.</summary>
    public const double StageLength = 2600;

    public const double LightJump = 345;
    public const double HeavyJump = 120;
    public const double BaseGravity = 1000;

    /// <summary>Ogni salto si può fare fino a questo peso; più pesanti, i vuoti larghi e i gradini diventano troppo.</summary>
    public const double DesignWeight = 0.6;

    public const double MinTop = 96;
    public const double MaxTop = 140;
    public const double MinGap = 18;

    private const double FixedStep = 1.0 / 240;
    private const double CoyoteTime = 0.08;
    private const double JumpBuffer = 0.12;

    /// <summary>Lasciando presto il tasto il salto si accorcia: la salita si taglia a questa parte della spinta.</summary>
    private const double JumpCut = 0.45;

    /// <summary>Arrivando poco sotto il bordo di un tetto ci si sale lo stesso.</summary>
    private const double StepAssist = 5;

    private const double OverDepth = 40;
    private const double GenerateAhead = 720;
    private const double KeepBehind = 160;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(0.5);

    private readonly List<Roof> _roofs = [];
    private readonly List<Treat> _treats = [];
    private readonly List<Goal> _goals = [];
    private readonly List<RunEvent> _events = [];
    private Random _random = new(0);
    private double _catX;
    private bool _held;
    private double? _jumpBufferedAt;
    private double _leftGroundAt;
    private bool _jumpedSinceGround;
    private bool _bumped;
    private double _lostSpeed;

    public RooftopRun(int seed = 1)
    {
        Seed = seed;
        Reset();
    }

    public int Seed { get; private set; }

    public RunPhase Phase { get; private set; } = RunPhase.Title;

    /// <summary>Secondi da quando la partita esiste, in ogni fase (battiti e coda da seduto).</summary>
    public double Clock { get; private set; }

    /// <summary>Secondi di corsa (fermi in pausa e da seduto).</summary>
    public double Time { get; private set; }

    /// <summary>Bordo sinistro della camera.</summary>
    public double Distance { get; private set; }

    public double CatX => _catX;

    /// <summary>Dove poggiano le zampe.</summary>
    public double CatY { get; private set; }

    /// <summary>Velocità verticale: negativa in salita.</summary>
    public double VelocityY { get; private set; }

    public bool Grounded { get; private set; }

    /// <summary>0 leggero … 1 così pesante che quasi non salta più.</summary>
    public double Weight { get; private set; }

    public int Score { get; private set; }

    public int Stage { get; private set; }

    public double Speed { get; private set; }

    public double? JumpedAt { get; private set; }

    public double? LandedAt { get; private set; }

    public double? AteAt { get; private set; }

    public double? GoalAt { get; private set; }

    public double? LostAt { get; private set; }

    /// <summary>Ha sbattuto contro il muro di un tetto, invece di cadere in un vuoto.</summary>
    public bool Bumped => _bumped;

    public double? OverAt { get; private set; }

    public IReadOnlyList<Roof> Roofs => _roofs;

    public IReadOnlyList<Treat> Treats => _treats;

    public IReadOnlyList<Goal> Goals => _goals;

    /// <summary>Il traguardo davanti, se è già stato costruito.</summary>
    public Goal? NextGoal => _goals.Where(g => g.Stage == Stage).Cast<Goal?>().FirstOrDefault();

    public static CatBuild BuildOf(double weight) => weight < 0.34 ? CatBuild.Light : weight < 0.67 ? CatBuild.Normal : CatBuild.Heavy;

    public static double JumpSpeed(double weight) => LightJump + ((HeavyJump - LightJump) * Math.Clamp(weight, 0, 1));

    /// <summary>Più pesante, cade anche più in fretta.</summary>
    public static double Gravity(double weight) => BaseGravity * (1 + (0.25 * Math.Clamp(weight, 0, 1)));

    /// <summary>Quanto sale il salto pieno.</summary>
    public static double JumpHeight(double weight) => JumpSpeed(weight) * JumpSpeed(weight) / (2 * Gravity(weight));

    /// <summary>
    /// Secondi in aria del salto pieno fino a tornare all'altezza <paramref name="rise"/> sopra lo stacco (negativo: più in basso);
    /// 0 se lassù non ci arriva.
    /// </summary>
    public static double AirTime(double weight, double rise)
    {
        var v = JumpSpeed(weight);
        var g = Gravity(weight);
        var d = (v * v) - (2 * g * rise);
        return d < 0 ? 0 : (v + Math.Sqrt(d)) / g;
    }

    /// <summary>Il vuoto più largo che il salto pieno passa a questa velocità (le zampe toccano dai due bordi).</summary>
    public static double Reach(double weight, double rise, double speed) =>
        AirTime(weight, rise) is var t and > 0 ? (speed * t) + (2 * FootHalf) : 0;

    /// <summary>La città di un seme fino a <paramref name="length"/>, uguale a quella che si corre, senza giocarla.</summary>
    public static (IReadOnlyList<Roof> Roofs, IReadOnlyList<Goal> Goals, IReadOnlyList<Treat> Treats) City(int seed, double length)
    {
        var run = new RooftopRun(seed);
        while (run._roofs[^1].Right < length)
            run.AddRoof();
        return ([.. run._roofs], [.. run._goals], [.. run._treats]);
    }

    public static double SpeedFor(int stage) => Math.Min(118 + (10 * (stage - 1)), 178);

    public static int StageAt(double x) => (int)Math.Floor(Math.Max(0, x) / StageLength) + 1;

    /// <summary>Clic, spazio: parte, salta, riprende, ricomincia.</summary>
    public void Press()
    {
        switch (Phase)
        {
            case RunPhase.Title:
                Phase = RunPhase.Running;
                _events.Add(RunEvent.Started);
                break;
            case RunPhase.Paused:
                Phase = RunPhase.Running;
                break;
            case RunPhase.Running:
                _held = true;
                _jumpBufferedAt = Time;
                TryJump();
                break;
            case RunPhase.Over when OverAt is { } at && Clock - at >= RetryDelay.TotalSeconds:
                Seed++;
                Reset();
                Phase = RunPhase.Running;
                _events.Add(RunEvent.Started);
                break;
        }
    }

    /// <summary>Lasciato presto, il salto si accorcia: un saltino passa sotto le crocchette in alto.</summary>
    public void Release()
    {
        _held = false;
        if (Phase == RunPhase.Running && !Grounded && VelocityY < 0)
            VelocityY = Math.Max(VelocityY, -JumpSpeed(Weight) * JumpCut);
    }

    public void Pause()
    {
        if (Phase != RunPhase.Running)
            return;
        Phase = RunPhase.Paused;
        _held = false;
    }

    /// <summary>Gli eventi successi dall'ultima volta, in ordine.</summary>
    public IReadOnlyList<RunEvent> TakeEvents()
    {
        if (_events.Count == 0)
            return [];
        var events = _events.ToArray();
        _events.Clear();
        return events;
    }

    /// <summary>Avanza di <paramref name="seconds"/> (al massimo un decimo: dopo un intoppo non si salta avanti).</summary>
    public void Update(double seconds)
    {
        var remaining = Math.Clamp(seconds, 0, 0.1);
        while (remaining > 1e-9)
        {
            var dt = Math.Min(FixedStep, remaining);
            remaining -= dt;
            Clock += dt;
            if (Phase == RunPhase.Running)
                StepRunning(dt);
            else if (Phase == RunPhase.Lost)
                StepLost(dt);
        }
    }

    private void Reset()
    {
        _random = new Random(Seed);
        _roofs.Clear();
        _treats.Clear();
        _goals.Clear();
        _events.Clear();
        Distance = 0;
        _catX = CatScreenX;
        CatY = 118;
        VelocityY = 0;
        Grounded = true;
        Weight = StartWeight;
        Score = 0;
        Stage = 1;
        Speed = SpeedFor(1);
        Time = 0;
        JumpedAt = LandedAt = AteAt = GoalAt = LostAt = OverAt = null;
        _held = false;
        _jumpBufferedAt = null;
        _jumpedSinceGround = false;
        _bumped = false;
        Generate();
    }

    private void StepRunning(double dt)
    {
        Time += dt;
        var before = _catX;
        Distance += Speed * dt;
        _catX = Distance + CatScreenX;

        if (HitWall(before))
            return;

        if (Grounded && RoofUnder(CatY, exact: true) is null)
        {
            Grounded = false;
            _leftGroundAt = Time;
        }
        if (_jumpBufferedAt is { } pressed && Time - pressed > JumpBuffer)
            _jumpBufferedAt = null;
        TryJump();

        if (!Grounded)
            Fly(dt);
        if (Phase != RunPhase.Running)
            return;

        if (Grounded)
            Weight = Math.Max(0, Weight - (BurnPerSecond * dt));
        Eat();
        CrossGoal();
        Generate();
    }

    /// <summary>Il fronte delle zampe ha passato il muro di un tetto più alto: o ci sale (di poco sotto), o sbatte.</summary>
    private bool HitWall(double before)
    {
        foreach (var roof in _roofs)
        {
            if (roof.Left < before + FootHalf || roof.Left >= _catX + FootHalf || CatY <= roof.Top + 0.5)
                continue;
            if (CatY - roof.Top <= StepAssist)
            {
                Land(roof);
                return false;
            }
            _catX = roof.Left - FootHalf;
            VelocityY = Math.Max(VelocityY, 0);
            Grounded = false;
            _bumped = true;
            Lose(RunEvent.Bumped);
            return true;
        }
        return false;
    }

    private void TryJump()
    {
        if (_jumpBufferedAt is null || _jumpedSinceGround)
            return;
        var coyote = !Grounded && Time - _leftGroundAt <= CoyoteTime && VelocityY >= 0;
        if (!Grounded && !coyote)
            return;
        VelocityY = -JumpSpeed(Weight);
        Grounded = false;
        _jumpedSinceGround = true;
        _jumpBufferedAt = null;
        JumpedAt = Time;
        _events.Add(RunEvent.Jumped);
        if (!_held)
            Release(); // premuto e già lasciato prima di staccarsi (col buffer): saltino
    }

    private void Fly(double dt)
    {
        var y = CatY;
        VelocityY += Gravity(Weight) * dt;
        var next = y + (VelocityY * dt);
        if (VelocityY >= 0)
        {
            foreach (var roof in _roofs)
            {
                if (Overlaps(roof) && y <= roof.Top + 0.01 && next >= roof.Top)
                {
                    Land(roof);
                    return;
                }
            }
        }
        CatY = next;
        if (CatY > ViewHeight)
            Lose(RunEvent.Fell);
    }

    private void Land(Roof roof)
    {
        CatY = roof.Top;
        VelocityY = 0;
        Grounded = true;
        _jumpedSinceGround = false;
        LandedAt = Time;
        _events.Add(RunEvent.Landed);
        TryJump(); // premuto appena prima di toccare: salta subito
    }

    private void Lose(RunEvent why)
    {
        Phase = RunPhase.Lost;
        LostAt = Time;
        _lostSpeed = Speed;
        _held = false;
        _events.Add(why);
    }

    private void StepLost(double dt)
    {
        Time += dt;
        _lostSpeed *= Math.Exp(-3 * dt);
        Distance += _lostSpeed * dt;
        if (!_bumped)
        {
            var before = _catX;
            _catX += _lostSpeed * dt;
            foreach (var roof in _roofs.Where(r => r.Left >= before + FootHalf && r.Left < _catX + FootHalf && CatY > r.Top))
            {
                _catX = roof.Left - FootHalf; // cadendo nel vuoto si ferma contro il muro, non ci passa attraverso
                _bumped = true;
            }
        }
        VelocityY += Gravity(Weight) * dt;
        CatY += VelocityY * dt;
        if (CatY < ViewHeight + OverDepth)
            return;
        Phase = RunPhase.Over;
        OverAt = Clock;
        _events.Add(RunEvent.Over);
    }

    private bool Overlaps(Roof roof) => _catX + FootHalf > roof.Left && _catX - FootHalf < roof.Right;

    private Roof? RoofUnder(double y, bool exact)
    {
        foreach (var roof in _roofs)
        {
            if (Overlaps(roof) && (!exact || Math.Abs(roof.Top - y) < 0.5))
                return roof;
        }
        return null;
    }

    /// <summary>Il centro del corpo: più pesante, più alto e più largo da prendere.</summary>
    private (double X, double Y, double Reach) Body() => (_catX + 3, CatY - 10 - (3 * Weight), 3 * Weight);

    private void Eat()
    {
        var (x, y, grow) = Body();
        foreach (var treat in _treats)
        {
            if (treat.EatenAt is not null)
                continue;
            var reach = (treat.Kind == TreatKind.Can ? 15 : 12) + grow;
            if (Math.Abs(treat.X - x) > reach || Math.Abs(treat.Y - y) > reach)
                continue;
            treat.EatenAt = Time;
            AteAt = Time;
            var can = treat.Kind == TreatKind.Can;
            Weight = Math.Min(1, Weight + (can ? CanWeight : KibbleWeight));
            Score += can ? CanPoints : KibblePoints;
            _events.Add(can ? RunEvent.AteCan : RunEvent.Ate);
        }
    }

    private void CrossGoal()
    {
        foreach (var goal in _goals)
        {
            if (goal.Stage != Stage || _catX < goal.X)
                continue;
            Stage++;
            Score += GoalPoints;
            Speed = SpeedFor(Stage);
            GoalAt = Time;
            _events.Add(RunEvent.Goal);
            return;
        }
    }

    // ── Città ─────────────────────────────────────────────────────────────────

    private void Generate()
    {
        while (_roofs.Count == 0 || _roofs[^1].Right < Distance + GenerateAhead)
            AddRoof();
        var behind = Distance - KeepBehind;
        _roofs.RemoveAll(r => r.Right < behind);
        _treats.RemoveAll(t => t.X < behind);
        _goals.RemoveAll(g => g.X < behind);
    }

    private double Range(double min, double max) => min + (_random.NextDouble() * (max - min));

    private void AddRoof()
    {
        if (_roofs.Count == 0)
        {
            _roofs.Add(new Roof(-40, 480, CatY, _random.Next(1000))); // la pista di partenza, senza niente
            return;
        }

        var last = _roofs[^1];
        var stage = StageAt(last.Right);
        var speed = SpeedFor(stage);
        var top = Math.Clamp(last.Top - Range(-34, 0.7 * JumpHeight(DesignWeight)), MinTop, MaxTop);
        var rise = last.Top - top;
        var widest = (0.8 * (Reach(DesignWeight, rise, speed) - (2 * FootHalf))) + (2 * FootHalf);
        var hardness = Math.Min(1, 0.6 + (0.13 * (stage - 1))); // vuoti più larghi tappa dopo tappa
        var gap = Range(MinGap, MinGap + ((widest - MinGap) * hardness));
        var left = last.Right + gap;
        var width = Range(stage == 1 ? 130 : 100, 280);

        // il traguardo sta sempre su un tetto, con spazio prima e dopo
        var boundary = stage * StageLength;
        Goal? goal = null;
        if (boundary > last.Right && boundary <= left + width)
        {
            var x = Math.Max(boundary, left + 50);
            width = Math.Max(width, x - left + 70);
            goal = new Goal(stage, x, top);
        }

        var roof = new Roof(left, width, top, _random.Next(1000));
        _roofs.Add(roof);
        if (goal is { } g)
            _goals.Add(g);
        PlaceTreats(last, roof, speed, goal);
    }

    /// <summary>
    /// Crocchette in arco sopra il vuoto, dove passa il salto pieno di un gatto leggero (un saltino ci passa sotto); una fila sul
    /// tetto, da scavalcare se non si vuole; ogni tanto una scatoletta.
    /// </summary>
    private void PlaceTreats(Roof from, Roof to, double speed, Goal? goal)
    {
        var gap = to.Left - from.Right;
        if (gap >= 26 && _random.NextDouble() < 0.55)
        {
            const double ArcWeight = 0.25;
            var v = JumpSpeed(ArcWeight);
            var g = Gravity(ArcWeight);
            var air = AirTime(ArcWeight, from.Top - to.Top);
            var takeoff = from.Right + FootHalf - 2;
            foreach (var part in new[] { 0.32, 0.5, 0.68 })
            {
                var t = air * part;
                var height = (v * t) - (g * t * t / 2);
                _treats.Add(new Treat(TreatKind.Kibble, takeoff + 3 + (speed * t), from.Top - height - 11));
            }
        }

        var rowEnd = to.Left + 30;
        if (to.Width >= 110 && _random.NextDouble() < 0.5)
        {
            var count = _random.Next(2, 5);
            var start = to.Left + Range(40, Math.Max(41, to.Width - 40 - (count * 16)));
            for (var i = 0; i < count; i++)
                _treats.Add(new Treat(TreatKind.Kibble, start + (i * 16), to.Top - 10));
            rowEnd = start + (count * 16);
        }

        if (to.Width >= 120 && _random.NextDouble() < 0.22)
        {
            var x = Math.Max(rowEnd + 26, to.Left + (to.Width * Range(0.45, 0.75)));
            if (x < to.Right - 30 && (goal is not { } flag || Math.Abs(flag.X - x) > 30))
                _treats.Add(new Treat(TreatKind.Can, x, to.Top - 11));
        }
    }
}

/// <summary>Le tre corporature del gatto (CAT STATE): 軽い, ふつう, 重い.</summary>
public enum CatBuild
{
    Light,
    Normal,
    Heavy,
}
