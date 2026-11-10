using NothingShell.Core.Leisure;
using NothingShell.Core.Notch;
using NothingShell.Core.Settings;

namespace NothingShell.Core.Tests;

public class LeisureTests
{
    private const double Frame = 1.0 / 60;

    private static RooftopRun Started(int seed = 1)
    {
        var run = new RooftopRun(seed);
        run.Press();
        run.TakeEvents();
        return run;
    }

    /// <summary>Come la vista: un fotogramma alla volta (Update non va avanti più di un decimo per volta).</summary>
    private static void Advance(RooftopRun run, double seconds)
    {
        for (var t = 0.0; t < seconds - 1e-9; t += Frame)
            run.Update(Math.Min(Frame, seconds - t));
    }

    private static Roof? Under(RooftopRun run) =>
        run.Roofs.Where(r => run.CatX + RooftopRun.FootHalf > r.Left && run.CatX - RooftopRun.FootHalf < r.Right && Math.Abs(r.Top - run.CatY) < 0.5)
            .Cast<Roof?>().FirstOrDefault();

    /// <summary>
    /// Un giocatore attento: salta all'ultimo momento, col salto pieno, e lascia il tasto in discesa. Gioca per
    /// <paramref name="seconds"/> secondi o finché perde; ritorna gli eventi.
    /// </summary>
    private static List<RunEvent> Bot(RooftopRun run, double seconds, RunEvent? until = null)
    {
        var events = new List<RunEvent>();
        for (var t = 0.0; t < seconds && run.Phase == RunPhase.Running && (until is null || !events.Contains(until.Value)); t += Frame)
        {
            if (run.Grounded && Under(run) is { } roof && run.CatX - RooftopRun.FootHalf + (run.Speed * Frame * 1.5) >= roof.Right)
                run.Press();
            else if (!run.Grounded && run.VelocityY > 0)
                run.Release();
            run.Update(Frame);
            events.AddRange(run.TakeEvents());
        }
        return events;
    }

    [Fact]
    public void ItWaitsSittingAndTheFirstClickMakesItRun()
    {
        var run = new RooftopRun();
        Advance(run, 1);
        Assert.Equal(RunPhase.Title, run.Phase);
        Assert.Equal(0, run.Distance);
        Assert.Equal(CatMotion.Sit, CatPose.Of(run).Motion);

        run.Press();
        Assert.Equal(RunPhase.Running, run.Phase);
        Assert.True(run.Grounded); // il primo clic fa partire, non salta
        Advance(run, 0.5);
        Assert.True(run.Distance > 50);
        Assert.Equal(CatMotion.Run, CatPose.Of(run).Motion);
        Assert.Equal([RunEvent.Started], run.TakeEvents());
    }

    [Fact]
    public void TheHeavierTheCatTheLowerItJumps()
    {
        Assert.True(RooftopRun.JumpHeight(0) > 55);
        Assert.True(RooftopRun.JumpHeight(1) < 7); // 飛べなくなる: quasi non salta più
        for (var w = 0.0; w < 1; w += 0.1)
            Assert.True(RooftopRun.JumpHeight(w) > RooftopRun.JumpHeight(w + 0.1));
        Assert.True(RooftopRun.Reach(0, 0, 118) > 2 * RooftopRun.Reach(1, 0, 118) - 10);
    }

    [Fact]
    public void HoldingJumpsHigherThanATap()
    {
        double Peak(bool hold)
        {
            var run = Started();
            run.Press();
            var peak = run.CatY;
            for (var i = 0; i < 60; i++)
            {
                if (!hold && i == 3)
                    run.Release();
                run.Update(Frame);
                peak = Math.Min(peak, run.CatY);
            }
            return 118 - peak;
        }

        var full = Peak(hold: true);
        var tap = Peak(hold: false);
        Assert.InRange(full, RooftopRun.JumpHeight(RooftopRun.StartWeight) - 2, RooftopRun.JumpHeight(RooftopRun.StartWeight) + 1);
        Assert.True(tap < full * 0.6, $"saltino {tap:0.0}, salto pieno {full:0.0}");
    }

    [Fact]
    public void APressJustBeforeLandingJumpsOnLanding()
    {
        var run = Started();
        run.Press();
        while (run.VelocityY < 0 || run.CatY < 118 - 3)
            run.Update(Frame / 4);
        run.TakeEvents();
        run.Press(); // ancora in aria, a un soffio dal tetto
        var jumps = 0;
        for (var i = 0; i < 12; i++)
        {
            run.Update(Frame);
            jumps += run.TakeEvents().Count(e => e == RunEvent.Jumped);
        }
        Assert.Equal(1, jumps);
    }

    [Fact]
    public void RunningSlimsTheCatDown()
    {
        var run = Started();
        Advance(run, 2);
        Assert.Equal(RooftopRun.StartWeight - (2 * RooftopRun.BurnPerSecond), run.Weight, 3);
    }

    [Fact]
    public void EveryTreatAddsPointsAndWeight()
    {
        var run = Started(3);
        var events = Bot(run, 40);
        var kibble = events.Count(e => e == RunEvent.Ate);
        var cans = events.Count(e => e == RunEvent.AteCan);
        var goals = events.Count(e => e == RunEvent.Goal);
        Assert.True(kibble > 0, "nessuna crocchetta in 40 secondi");
        Assert.Equal((kibble * RooftopRun.KibblePoints) + (cans * RooftopRun.CanPoints) + (goals * RooftopRun.GoalPoints), run.Score);
        Assert.InRange(run.Weight, 0, 1);
        Assert.All(run.Treats.Where(t => t.EatenAt is not null), t => Assert.True(t.EatenAt <= run.Time));
    }

    [Fact]
    public void WithoutJumpingItFallsAndThenItIsOver()
    {
        var run = Started();
        var events = new List<RunEvent>();
        for (var i = 0; i < 60 * 10 && run.Phase != RunPhase.Over; i++)
        {
            run.Update(Frame);
            events.AddRange(run.TakeEvents());
        }
        Assert.Equal(RunPhase.Over, run.Phase);
        Assert.Contains(events, e => e is RunEvent.Bumped or RunEvent.Fell);
        Assert.Equal(RunEvent.Over, events[^1]);

        run.Press(); // subito no: era un clic per saltare
        Assert.Equal(RunPhase.Over, run.Phase);
        Advance(run, 0.6);
        run.Press();
        Assert.Equal(RunPhase.Running, run.Phase);
        Assert.Equal(0, run.Score);
        Assert.Equal(2, run.Seed); // un'altra città
        Assert.Equal(RooftopRun.StartWeight, run.Weight);
    }

    [Fact]
    public void BumpingIntoAWallTumblesDown()
    {
        var run = Started();
        while (run.Phase == RunPhase.Running)
            run.Update(Frame);
        Assert.Equal(RunPhase.Lost, run.Phase);
        Assert.True(run.Bumped); // dal primo tetto senza saltare si sbatte contro il muro del secondo (o si cade)
        var x = run.CatX;
        Advance(run, 0.2);
        Assert.Equal(RunPhase.Lost, run.Phase);
        Assert.Equal(x, run.CatX); // resta contro il muro e scivola giù
        var pose = CatPose.Of(run);
        Assert.Equal(CatMotion.Tumble, pose.Motion);
        Assert.True(pose.Spin > 90);
    }

    [Fact]
    public void PauseStopsEverything()
    {
        var run = Started();
        Advance(run, 1);
        run.Pause();
        var (distance, time, weight) = (run.Distance, run.Time, run.Weight);
        Advance(run, 3);
        Assert.Equal(RunPhase.Paused, run.Phase);
        Assert.Equal((distance, time, weight), (run.Distance, run.Time, run.Weight));
        run.Press();
        Assert.Equal(RunPhase.Running, run.Phase);
        Assert.True(run.Grounded); // riprendere non fa saltare
    }

    [Fact]
    public void TheSameSeedBuildsTheSameCity()
    {
        var a = new RooftopRun(7);
        var b = new RooftopRun(7);
        var c = new RooftopRun(8);
        Assert.Equal(a.Roofs, b.Roofs);
        Assert.Equal(a.Treats.Select(t => (t.Kind, t.X, t.Y)), b.Treats.Select(t => (t.Kind, t.X, t.Y)));
        Assert.NotEqual(a.Roofs, c.Roofs);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void EveryGapCanBeJumpedUpToTheDesignWeight(int seed)
    {
        var (roofs, goals, treats) = RooftopRun.City(seed, 4 * RooftopRun.StageLength);
        foreach (var (from, to) in roofs.Zip(roofs.Skip(1)))
        {
            var gap = to.Left - from.Right;
            var rise = from.Top - to.Top;
            var speed = RooftopRun.SpeedFor(RooftopRun.StageAt(from.Right));
            Assert.InRange(gap, RooftopRun.MinGap, RooftopRun.Reach(RooftopRun.DesignWeight, rise, speed));
            Assert.True(rise < RooftopRun.JumpHeight(RooftopRun.DesignWeight), $"gradino di {rise:0.0}");
            Assert.InRange(to.Top, RooftopRun.MinTop, RooftopRun.MaxTop);
        }
        Assert.Equal([1, 2, 3], goals.Select(g => g.Stage).Take(3));
        foreach (var goal in goals)
            Assert.Contains(roofs, r => goal.X >= r.Left + 40 && goal.X <= r.Right - 60 && r.Top == goal.Top);
        Assert.DoesNotContain(treats, t => t.X < roofs[0].Right); // la pista di partenza è sgombra
    }

    [Fact]
    public void TheCityIsTheSameWhetherPlayedOrNot()
    {
        var (roofs, _, _) = RooftopRun.City(5, 3000);
        var run = Started(5);
        var seen = new HashSet<Roof>(run.Roofs);
        while (run.Phase == RunPhase.Running)
        {
            Bot(run, 1);
            seen.UnionWith(run.Roofs);
        }
        Assert.All(seen.Where(r => r.Right < 3000), r => Assert.Contains(r, roofs));
    }

    [Fact]
    public void ACarefulCatReachesTheFirstGoal()
    {
        var reached = 0;
        for (var seed = 1; seed <= 10; seed++)
        {
            var run = Started(seed);
            if (Bot(run, 40).Contains(RunEvent.Goal))
                reached++;
        }
        Assert.True(reached >= 7, $"traguardo raggiunto in {reached} partite su 10");
    }

    [Fact]
    public void PassingTheGoalStartsAFasterStage()
    {
        var run = Started(1);
        var events = Bot(run, 40, until: RunEvent.Goal);
        Assert.Contains(RunEvent.Goal, events);
        Assert.Equal(2, run.Stage);
        Assert.Equal(RooftopRun.SpeedFor(2), run.Speed);
        Assert.True(CatPose.Of(run).Happy); // contento al traguardo
    }

    [Fact]
    public void ThePoseFollowsTheJump()
    {
        var run = Started();
        Advance(run, 0.3);
        Assert.Equal(CatMotion.Run, CatPose.Of(run).Motion);
        run.Press();
        run.Update(Frame);
        var takeoff = CatPose.Of(run);
        Assert.Equal(CatMotion.Jump, takeoff.Motion);
        Assert.True(takeoff.Squash > 1); // allungato allo stacco
        while (run.VelocityY < 100)
            run.Update(Frame);
        Assert.Equal(CatMotion.Fall, CatPose.Of(run).Motion);
        while (!run.Grounded)
            run.Update(Frame / 4);
        var landing = CatPose.Of(run);
        Assert.Equal(CatMotion.Run, landing.Motion);
        Assert.True(landing.Squash < 1); // schiacciato all'atterraggio
        Advance(run, 0.3);
        Assert.Equal(1, CatPose.Of(run).Squash);
    }

    [Fact]
    public void EveryPoseStaysInRange()
    {
        for (var seed = 1; seed <= 4; seed++)
        {
            var run = Started(seed);
            for (var i = 0; i < 60 * 30 && run.Phase != RunPhase.Over; i++)
            {
                if (run.Grounded && Under(run) is { } roof && run.CatX - RooftopRun.FootHalf + (run.Speed * Frame * 1.5) >= roof.Right)
                    run.Press();
                run.Update(Frame);
                var pose = CatPose.Of(run);
                Assert.InRange(pose.Stride, 0, 1);
                Assert.InRange(pose.Squash, 0.75, 1.2);
                Assert.InRange(pose.Chew, 0, 1);
                Assert.InRange(pose.Tail, -1, 1);
            }
        }
    }

    [Fact]
    public void TheBuildFollowsTheWeight()
    {
        Assert.Equal(CatBuild.Light, RooftopRun.BuildOf(0.2));
        Assert.Equal(CatBuild.Normal, RooftopRun.BuildOf(0.5));
        Assert.Equal(CatBuild.Heavy, RooftopRun.BuildOf(0.9));
    }

    [Fact]
    public void TheRecordOnlyGoesUp()
    {
        var settings = LeisureSettings.Default with { Enabled = true };
        settings = settings.WithResult(18, 2);
        Assert.Equal((18, 2), (settings.Best, settings.BestStage));
        Assert.Same(settings, settings.WithResult(12, 3));
        Assert.Equal(40, settings.WithResult(40, 1).Best);
    }

    [Fact]
    public void LeisureIsSavedWithTheSettings()
    {
        var settings = ShellSettings.Default with { Leisure = new LeisureSettings(true, 42, 3) };
        var back = ShellSettingsStore.Deserialize(ShellSettingsStore.Serialize(settings));
        Assert.Equal(new LeisureSettings(true, 42, 3), back.LeisureOrDefault());
        Assert.False(ShellSettings.Default.LeisureOrDefault().Enabled); // spento di serie
    }

    [Fact]
    public void ThePageHasRoomForTheGame()
    {
        Assert.Contains(NotchPage.Leisure, NotchPager.Pages);
        Assert.Equal(NotchPage.Leisure, NotchPageOrder.Resolve(null)[^1]); // in fondo allo swipe, finché non la si sposta
        Assert.Equal(NotchLayout.LeisureHeight, NotchLayout.Expanded(NotchPage.Leisure).Height);
        Assert.True(NotchLayout.Host.Height >= NotchLayout.LeisureHeight);
    }
}
