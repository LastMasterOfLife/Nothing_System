using NothingShell.Core.Notch;

namespace NothingShell.Core.Tests;

public class MascotPlayTests
{
    // la pillola piccola a riposo, 144 × 28, con gli occhi al centro
    private const double HalfWidth = 72;
    private const double Bottom = 14;
    private static readonly TimeSpan Start = TimeSpan.FromSeconds(10);

    private static MascotPlay Play() => new(HalfWidth, Bottom);

    /// <summary>
    /// Il mouse lungo <paramref name="path"/> (posizione rispetto agli occhi in funzione dei secondi) per <paramref name="seconds"/>
    /// secondi, una posizione ogni 30 ms come quando è vicino; ritorna le risposte della mascotte, in ordine.
    /// </summary>
    private static List<MascotMood> Trace(MascotPlay play, double seconds, Func<double, (double X, double Y)> path)
    {
        var answers = new List<MascotMood>();
        for (var i = 0; i * 0.03 < seconds; i++)
        {
            var t = i * 0.03;
            var (x, y) = path(t);
            if (play.Observe(Start + TimeSpan.FromSeconds(t), x, y) is { } answer)
                answers.Add(answer);
        }
        return answers;
    }

    private static string Answers(List<MascotMood> answers) => string.Join(' ', answers);

    [Fact]
    public void PettingUnderThePillMakesItPurr() =>
        Assert.Equal(MascotMood.Purr, Assert.Single(Trace(Play(), 2, t => (50 * Math.Sin(t * Math.PI * 2 / 0.6), 35))));

    [Fact]
    public void TicklingMakesItGiggle()
    {
        Assert.Equal(MascotMood.Giggle, Trace(Play(), 1, t => (6 * Math.Sin(t * Math.PI * 2 / 0.16), 30)).First());
        Assert.Equal(MascotMood.Giggle, Trace(Play(), 1, t => (10, 30 + (6 * Math.Sin(t * Math.PI * 2 / 0.16)))).First()); // anche su e giù
    }

    [Fact]
    public void WavingFurtherDownMakesItWaveBack() =>
        Assert.Equal(MascotMood.WaveBack, Assert.Single(Trace(Play(), 2, t => (120 * Math.Sin(t * Math.PI * 2 / 0.7), 200))));

    [Fact]
    public void UpAndDownMakesItNod() =>
        Assert.Equal(MascotMood.Nod, Assert.Single(Trace(Play(), 1.2, t => (0, 150 + (60 * Math.Sin(t * Math.PI * 2 / 0.6))))));

    [Fact]
    public void GoingRoundMakesItDizzy()
    {
        var clockwise = Trace(Play(), 1.5, t => (60 * Math.Cos(t * Math.PI * 2 / 1.2), 150 + (60 * Math.Sin(t * Math.PI * 2 / 1.2))));
        Assert.Equal(MascotMood.Dizzy, Assert.Single(clockwise));
        var other = Trace(Play(), 1.5, t => (80 * Math.Cos(t * Math.PI * 2 / 1.2), 150 - (50 * Math.Sin(t * Math.PI * 2 / 1.2))));
        Assert.Equal(MascotMood.Dizzy, Assert.Single(other)); // anche al contrario, e un po' schiacciato
    }

    [Fact]
    public void HalfACircleIsNotARound() =>
        Assert.Empty(Trace(Play(), 0.6, t => (60 * Math.Cos(t * Math.PI * 2 / 1.2), 150 + (60 * Math.Sin(t * Math.PI * 2 / 1.2)))));

    [Fact]
    public void APokeOnThePillMakesItJump() =>
        Assert.Equal(MascotMood.Poked, Assert.Single(Trace(Play(), 1, t => (0, t is >= 0.3 and < 0.45 ? 0 : 60))));

    [Fact]
    public void AtTheThirdPokeInARowItTakesOffence()
    {
        var play = Play();
        Assert.Equal("Poked Poked Huffy", Answers(Trace(play, 2.4, t => (0, (t % 0.8) is >= 0.3 and < 0.45 ? 0 : 60))));
        var again = Trace(play, 1, t => (0, t is >= 0.3 and < 0.45 ? 0 : 60)); // dopo l'offesa si ricomincia a contare
        Assert.Equal(MascotMood.Poked, Assert.Single(again));
    }

    [Fact]
    public void StayingOnThePillIsNotAPoke() =>
        Assert.Empty(Trace(Play(), 1.5, t => (0, t is >= 0.3 and < 1 ? 0 : 60))); // ci si ferma: il notch si apre

    [Fact]
    public void LeavingThePillFromTheSideIsNotAPoke() =>
        Assert.Empty(Trace(Play(), 1, t => t < 0.3 ? (0, 60) : t < 0.45 ? (0, 0) : (120, 0)));

    [Fact]
    public void OrdinaryMovesAreNotGestures()
    {
        // attraverso lo schermo, appena sotto il notch
        Assert.Empty(Trace(Play(), 1, t => (-400 + (800 * t), 40)));
        // verso il notch, e lì fermi
        Assert.Empty(Trace(Play(), 1.5, t => t < 0.5 ? (200 - (400 * t), 400 - (740 * t)) : (0, 30)));
        // da una scheda del browser all'altra: ogni volta ci si ferma a cliccare
        Assert.Empty(Trace(Play(), 4, t => (Tab(t), 40)));
        // una curva larga verso il basso
        Assert.Empty(Trace(Play(), 1, t => (300 * Math.Cos(t * Math.PI), 50 + (300 * Math.Sin(t * Math.PI)))));

        // 150 ms per passare da una parte all'altra, poi 550 ms fermi
        static double Tab(double t)
        {
            var into = t % 0.7;
            var x = into < 0.15 ? -100 + (200 * into / 0.15) : 100;
            return (int)(t / 0.7) % 2 == 0 ? x : -x;
        }
    }

    [Fact]
    public void FarAwayItDoesNotPlay()
    {
        var play = Play();
        Assert.Empty(Trace(play, 2, t => (120 * Math.Sin(t * Math.PI * 2 / 0.7), 700)));
        Assert.False(play.Near);
        Assert.Empty(Trace(play, 2, t => (600 + (120 * Math.Sin(t * Math.PI * 2 / 0.7)), 200)));
        Trace(play, 0.1, _ => (0, 100));
        Assert.True(play.Near);
    }

    [Fact]
    public void AGapInTheTraceStartsAgain()
    {
        var play = Play();
        // due mezze carezze, separate da mezzo secondo in cui la mascotte non guardava (notch aperto): insieme farebbero le fusa
        for (var i = 0; i < 20; i++)
            Assert.Null(play.Observe(Start + TimeSpan.FromMilliseconds(30 * i), 50 * Math.Sin(i * 0.03 * Math.PI * 2 / 0.6), 35));
        for (var i = 0; i < 20; i++)
            Assert.Null(play.Observe(Start + TimeSpan.FromMilliseconds(1100 + (30 * i)), 50 * Math.Sin(i * 0.03 * Math.PI * 2 / 0.6), 35));
    }
}
