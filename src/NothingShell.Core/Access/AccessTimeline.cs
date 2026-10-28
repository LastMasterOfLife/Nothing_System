using NothingShell.Core.Animation;

namespace NothingShell.Core.Access;

public enum AccessCaption
{
    Goodbye,
    Locked,
    Welcome,
}

/// <summary>
/// Un fotogramma dell'overlay di accesso (docs/09-access.md).
/// Iris: 0 = schermo tutto nero, 1 = foro che scopre l'intero schermo.
/// RingLit: frazione dei punti dell'anello accesi (in senso orario). RingSpread: distanza extra dei
/// punti dall'anello, in raggi dell'anello (0 = al loro posto).
/// </summary>
public sealed record AccessFrame(
    double Iris,
    double CardScale,
    double CardOpacity,
    double RingLit,
    bool RingHead,
    double RingSpread,
    double RingOpacity,
    AccessCaption Caption,
    double CaptionOpacity,
    double ClockOpacity,
    bool CoversVisible);

/// <summary>Le due sequenze animate come funzioni pure del tempo trascorso.</summary>
public static class AccessTimeline
{
    public static readonly TimeSpan LockDuration = TimeSpan.FromMilliseconds(1100);
    public static readonly TimeSpan WelcomeDuration = TimeSpan.FromMilliseconds(1600);

    /// <summary>Lo stato fermo dietro la schermata di Windows.</summary>
    public static AccessFrame Locked { get; } = new(
        Iris: 0, CardScale: 1, CardOpacity: 1, RingLit: 0, RingHead: false, RingSpread: 0, RingOpacity: 1,
        Caption: AccessCaption.Locked, CaptionOpacity: 1, ClockOpacity: 1, CoversVisible: true);

    /// <summary>Chiusura: iride che si chiude, card che compare, anello che arriva e si spegne.</summary>
    public static AccessFrame Lock(TimeSpan elapsed)
    {
        var t = elapsed.TotalMilliseconds;
        if (t >= LockDuration.TotalMilliseconds)
            return Locked with { Caption = AccessCaption.Goodbye };

        var cardIn = Easing.Progress(t, 250, 700);
        return new AccessFrame(
            Iris: 1 - Easing.InOutCubic(Easing.Progress(t, 0, 450)),
            CardScale: Easing.Lerp(0.6, 1, Easing.OutBack(cardIn)),
            CardOpacity: Easing.Progress(t, 250, 450),
            RingLit: 1 - Easing.InOutCubic(Easing.Progress(t, 750, 1050)),
            RingHead: false,
            RingSpread: Easing.Lerp(1.5, 0, Easing.OutCubic(Easing.Progress(t, 250, 750))),
            RingOpacity: Easing.Progress(t, 250, 500),
            Caption: AccessCaption.Goodbye,
            CaptionOpacity: Easing.Progress(t, 400, 600),
            ClockOpacity: Easing.Progress(t, 250, 450),
            CoversVisible: t >= 450);
    }

    /// <summary>Bentornato: anello che si accende, pulsazione, iride che si apre dalla card.</summary>
    public static AccessFrame Welcome(TimeSpan elapsed)
    {
        var t = elapsed.TotalMilliseconds;
        var pulse = Easing.Progress(t, 550, 850);
        var exit = Easing.Progress(t, 850, 1600);
        var scale = (1 + 0.06 * Math.Sin(Math.PI * pulse)) * (1 - Easing.InBack(exit) * 0.9);
        return new AccessFrame(
            Iris: Easing.InOutCubic(exit),
            CardScale: Math.Max(0, scale),
            CardOpacity: 1 - Easing.Progress(t, 1100, 1500),
            RingLit: Easing.InOutCubic(Easing.Progress(t, 0, 550)),
            RingHead: t < 550,
            RingSpread: 2.5 * Easing.OutCubic(exit),
            RingOpacity: 1 - Easing.Progress(t, 900, 1450),
            Caption: AccessCaption.Welcome,
            CaptionOpacity: Math.Min(Easing.Progress(t, 0, 250), 1 - Easing.Progress(t, 850, 1050)),
            ClockOpacity: 1 - Easing.Progress(t, 850, 1050),
            CoversVisible: t < 850);
    }

    /// <summary>Quanti dei <paramref name="dots"/> punti dell'anello sono accesi.</summary>
    public static int LitDots(double ringLit, int dots) => (int)Math.Round(Easing.Clamp01(ringLit) * dots);
}
