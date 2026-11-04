using NothingShell.Core.Design;
using NothingShell.Core.Notch;

namespace NothingShell.Core.Tests;

public class MascotNudgesTests
{
    private static readonly DateTime Morning = new(2026, 11, 4, 9, 0, 0);
    private static readonly DateTime Night = new(2026, 11, 4, 23, 40, 0);
    private static readonly TimeSpan Here = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);

    private static MascotNudges Nudges(MascotInitiative level = MascotInitiative.Sometimes) => new() { Level = level };

    /// <summary>Come il notch: ogni due secondi guarda l'inattività e chiede se c'è qualcosa da dire.</summary>
    private static List<(DateTime At, MascotNudge Nudge)> Run(MascotNudges nudges, DateTime from, TimeSpan length, Func<DateTime, TimeSpan>? idle = null)
    {
        var said = new List<(DateTime, MascotNudge)>();
        for (var now = from; now < from + length; now += Tick)
        {
            nudges.Observe(now, idle?.Invoke(now) ?? Here);
            if (nudges.Next(now) is { } nudge)
                said.Add((now, nudge));
        }
        return said;
    }

    /// <summary>Via dieci minuti con <paramref name="events"/> nel frattempo, poi di ritorno: cosa dice.</summary>
    private static MascotNudge? AfterAway(params (MascotEvent What, int Count)[] events)
    {
        var nudges = Nudges();
        nudges.Observe(Morning, Here);
        nudges.Observe(Morning.AddMinutes(3), TimeSpan.FromMinutes(3));
        foreach (var (what, count) in events)
            nudges.Noticed(what, count);
        var back = Morning.AddMinutes(10);
        nudges.Observe(back, TimeSpan.Zero);
        Assert.Null(nudges.Next(back)); // prima finisce di svegliarsi
        var welcome = nudges.Next(back + MascotNudges.WelcomeDelay);
        Assert.Null(nudges.Next(back + TimeSpan.FromSeconds(10))); // lo dice una volta sola
        return welcome;
    }

    [Fact]
    public void SuggestsABreakAfterFiftyMinutesOfWork()
    {
        var said = Run(Nudges(), Morning, TimeSpan.FromHours(2));
        Assert.Equal(2, said.Count);
        Assert.All(said, s => Assert.Equal((MascotNudgeKind.Break, MascotMood.Calling, (NotchPage?)null), (s.Nudge.Kind, s.Nudge.Gesture, s.Nudge.Page)));
        Assert.InRange(said[0].At, Morning.AddMinutes(49), Morning.AddMinutes(50));
        Assert.Equal("SEI AL COMPUTER DA 50 MINUTI: UNA PAUSA?", said[0].Nudge.Text);
        Assert.Equal("SEI AL COMPUTER DA 1 ORA E 40: UNA PAUSA?", said[1].Nudge.Text);
    }

    [Fact]
    public void ARealBreakStartsTheCountAgain()
    {
        var pause = Morning.AddMinutes(30);
        var said = Run(Nudges(), Morning, TimeSpan.FromMinutes(90), now => now >= pause && now < pause.AddMinutes(6) ? now - pause : Here);
        var (at, nudge) = Assert.Single(said);
        Assert.Equal(MascotNudgeKind.Break, nudge.Kind);
        Assert.True(at >= pause.AddMinutes(5) + MascotNudges.BreakAfter);
    }

    [Fact]
    public void AShortStepAwayIsNotABreak()
    {
        var step = Morning.AddMinutes(20);
        var said = Run(Nudges(), Morning, TimeSpan.FromHours(1), now => now >= step && now < step.AddMinutes(3) ? now - step : Here);
        Assert.Equal(MascotNudgeKind.Break, Assert.Single(said).Nudge.Kind);
        Assert.True(said[0].At < Morning.AddMinutes(51));
    }

    [Fact]
    public void TellsWhatHappenedWhileYouWereAway()
    {
        var welcome = AfterAway((MascotEvent.Mail, 2), (MascotEvent.AgentDone, 1), (MascotEvent.TaskFailed, 1));
        Assert.NotNull(welcome);
        Assert.Equal((MascotNudgeKind.WelcomeBack, MascotMood.Calling), (welcome.Kind, welcome.Gesture));
        Assert.Equal("MENTRE ERI VIA: 2 MAIL, 1 AGENTE HA FINITO, 1 COMANDO FALLITO", welcome.Text);
        Assert.Equal(NotchPage.Mail, welcome.Page);
    }

    [Fact]
    public void TheWelcomeOpensThePageOfWhatHappened()
    {
        var agents = AfterAway((MascotEvent.AgentDone, 2), (MascotEvent.TaskDone, 3));
        Assert.Equal("MENTRE ERI VIA: 2 AGENTI HANNO FINITO, 3 COMANDI FINITI", agents?.Text);
        Assert.Equal(NotchPage.Agents, agents?.Page);
        var tasks = AfterAway((MascotEvent.TaskDone, 1));
        Assert.Equal("MENTRE ERI VIA: 1 COMANDO FINITO", tasks?.Text);
        Assert.Equal(NotchPage.Work, tasks?.Page);
    }

    [Fact]
    public void NothingMissedNothingToSay() => Assert.Null(AfterAway());

    [Fact]
    public void WhatHappensWhileYouAreHereIsNotRepeated()
    {
        var nudges = Nudges();
        nudges.Observe(Morning, Here);
        nudges.Noticed(MascotEvent.Mail);
        nudges.Observe(Morning.AddMinutes(3), TimeSpan.FromMinutes(3));
        nudges.Observe(Morning.AddMinutes(10), TimeSpan.Zero);
        Assert.Null(nudges.Next(Morning.AddMinutes(10) + MascotNudges.WelcomeDelay));
    }

    [Fact]
    public void TheWelcomeWaitsForTheMascotButNotForever()
    {
        var nudges = Nudges();
        nudges.Observe(Morning, Here);
        nudges.Observe(Morning.AddMinutes(3), TimeSpan.FromMinutes(3));
        nudges.Noticed(MascotEvent.Mail);
        nudges.Observe(Morning.AddMinutes(10), TimeSpan.Zero);
        // il notch era nascosto (schermo intero): quando la mascotte ricompare è passato troppo
        Assert.Null(nudges.Next(Morning.AddMinutes(10) + MascotNudges.WelcomeWindow));
    }

    [Fact]
    public void LateOnlyWhenOftenAndOncePerNight()
    {
        Assert.DoesNotContain(Run(Nudges(), Night, TimeSpan.FromMinutes(10)), s => s.Nudge.Kind == MascotNudgeKind.Late);

        var nudges = Nudges(MascotInitiative.Often);
        var late = Assert.Single(Run(nudges, Night, TimeSpan.FromMinutes(10))).Nudge;
        Assert.Equal((MascotNudgeKind.Late, MascotMood.Yawn, "SI È FATTO TARDI"), (late.Kind, late.Gesture, late.Text));
        Assert.DoesNotContain(Run(nudges, Night.AddHours(1), TimeSpan.FromMinutes(20)), s => s.Nudge.Kind == MascotNudgeKind.Late); // stessa notte
        nudges.Observe(Night.AddHours(12), TimeSpan.FromHours(10)); // a dormire, davvero
        Assert.Contains(Run(nudges, Night.AddDays(1), TimeSpan.FromMinutes(10)), s => s.Nudge.Kind == MascotNudgeKind.Late); // la notte dopo
        nudges.Observe(Morning.AddDays(2).AddMinutes(-1), TimeSpan.FromHours(8));
        Assert.Empty(Run(nudges, Morning.AddDays(2), TimeSpan.FromMinutes(30))); // di mattina niente
    }

    [Fact]
    public void DizzyWhenTheCpuStaysAtTheTop()
    {
        var nudges = Nudges(MascotInitiative.Often);
        nudges.Observe(Morning, Here);
        nudges.ObserveCpu(Morning, 0.97);
        Assert.Null(nudges.Next(Morning.AddSeconds(10)));
        nudges.ObserveCpu(Morning.AddSeconds(20), 0.95);
        var busy = nudges.Next(Morning.AddSeconds(21));
        Assert.Equal((MascotNudgeKind.Busy, MascotMood.Dizzy, (string?)null), (busy?.Kind, busy?.Gesture, busy?.Text));
        Assert.Null(nudges.Next(Morning.AddSeconds(30))); // non barcolla di continuo

        var calm = Nudges();
        calm.Observe(Morning, Here);
        calm.ObserveCpu(Morning, 0.97);
        calm.ObserveCpu(Morning.AddSeconds(30), 0.97);
        Assert.Null(calm.Next(Morning.AddSeconds(31))); // solo con Spesso
    }

    [Fact]
    public void ACpuThatCalmsDownIsForgiven()
    {
        var nudges = Nudges(MascotInitiative.Often);
        nudges.Observe(Morning, Here);
        nudges.ObserveCpu(Morning, 0.97);
        nudges.ObserveCpu(Morning.AddSeconds(10), 0.4);
        nudges.ObserveCpu(Morning.AddSeconds(15), 0.97);
        Assert.Null(nudges.Next(Morning.AddSeconds(25)));
    }

    [Fact]
    public void LeavesSilenceBetweenTwoThings()
    {
        var nudges = Nudges(MascotInitiative.Often);
        nudges.Observe(Night.AddMinutes(-55), Here); // al lavoro da prima: pausa e ora tarda insieme
        var said = Run(nudges, Night, TimeSpan.FromMinutes(20));
        Assert.Equal(2, said.Count);
        Assert.Equal(MascotNudgeKind.Break, said[0].Nudge.Kind);
        Assert.Equal(MascotNudgeKind.Late, said[1].Nudge.Kind);
        Assert.True(said[1].At - said[0].At >= MascotNudges.QuietGap);
    }

    [Fact]
    public void OffSaysNothing()
    {
        var nudges = Nudges(MascotInitiative.Off);
        Assert.Empty(Run(nudges, Night, TimeSpan.FromHours(2)));
        nudges.Observe(Morning, TimeSpan.FromMinutes(3));
        nudges.Noticed(MascotEvent.Mail);
        nudges.ObserveCpu(Morning, 1);
        Assert.Empty(Run(nudges, Morning.AddMinutes(1), TimeSpan.FromMinutes(5)));
    }
}
