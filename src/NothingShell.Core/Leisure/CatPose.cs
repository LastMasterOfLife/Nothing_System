namespace NothingShell.Core.Leisure;

/// <summary>Cosa sta facendo il gatto (SYSTEM del concept: 走る, 跳ぶ, 落ちる).</summary>
public enum CatMotion
{
    /// <summary>Seduto di fronte, in attesa.</summary>
    Sit,
    Run,

    /// <summary>In salita: allungato, zampe davanti e dietro.</summary>
    Jump,

    /// <summary>In discesa: raccolto a palla, zampe sotto.</summary>
    Fall,

    /// <summary>Ha sbattuto o è caduto nel vuoto: rotola giù.</summary>
    Tumble,
}

/// <summary>
/// La posa del gatto in un istante: la vista la disegna così com'è. <see cref="Stride"/> 0..1 è il passo (le zampe), Squash
/// 1 normale, sotto 1 schiacciato (atterraggio), sopra 1 allungato (stacco); Chew 0..1 mastica; Tail −1..1 la coda; Spin i
/// gradi del ruzzolone.
/// </summary>
public readonly record struct CatPose(CatMotion Motion, double Stride, double Squash, double Chew, bool Blink, double Tail, double Spin, bool Happy)
{
    public static CatPose Sitting { get; } = new(CatMotion.Sit, 0, 1, 0, false, 0, 0, false);

    private const double LandSquash = 0.14;
    private const double JumpStretch = 0.12;
    private const double ChewLength = 0.3;
    private const double HappyLength = 1.2;
    private const double BlinkEvery = 3.1;
    private const double BlinkLength = 0.12;

    /// <summary>Lunghezza di un passo intero: più pesante, passi più lunghi e lenti (ciondola).</summary>
    public static double StrideLength(double weight) => 30 + (10 * Math.Clamp(weight, 0, 1));

    /// <summary>La posa della partita in questo momento.</summary>
    public static CatPose Of(RooftopRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var blink = (run.Clock + (BlinkEvery / 2)) % BlinkEvery < BlinkLength; // appena partito ha gli occhi aperti
        if (run.Phase is RunPhase.Title or RunPhase.Over)
            return Sitting with { Blink = blink, Tail = Math.Sin(run.Clock * Math.PI * 2 / 1.6) };

        var stride = Fraction(run.Distance / StrideLength(run.Weight));
        var since = (double? at) => at is { } t ? run.Time - t : double.MaxValue;
        var ate = since(run.AteAt);
        var chew = ate < ChewLength ? Math.Sin(ate / ChewLength * Math.PI) : 0;
        var happy = since(run.GoalAt) < HappyLength || ate < ChewLength;

        if (run.Phase == RunPhase.Lost || (run.Phase == RunPhase.Paused && run.LostAt is not null))
        {
            var lost = since(run.LostAt);
            return new(run.Bumped ? CatMotion.Tumble : CatMotion.Fall, stride, 1, 0, false, 1, run.Bumped ? lost * 540 : 0, false);
        }

        if (run.Grounded)
        {
            var landed = since(run.LandedAt);
            var squash = landed < LandSquash ? 1 - (0.22 * (1 - (landed / LandSquash))) : 1;
            return new(CatMotion.Run, stride, squash, chew, blink, Math.Sin(stride * Math.PI * 2) * 0.4, 0, happy);
        }

        var jumped = since(run.JumpedAt);
        var stretch = jumped < JumpStretch ? 1 + (0.15 * (1 - (jumped / JumpStretch))) : 1;
        var motion = run.VelocityY < 60 ? CatMotion.Jump : CatMotion.Fall;
        return new(motion, stride, stretch, chew, false, motion == CatMotion.Jump ? -0.6 : 0.8, 0, happy);
    }

    private static double Fraction(double value) => value - Math.Floor(value);
}
