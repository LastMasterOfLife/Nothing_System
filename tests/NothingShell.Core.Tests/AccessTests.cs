using NothingShell.Core.Access;
using NothingShell.Core.Animation;

namespace NothingShell.Core.Tests;

public class AccessFlowTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RequestLockShowsThenLocksWhenAnimationEnds()
    {
        var flow = new AccessFlow();

        Assert.Equal(AccessCommand.Show, flow.RequestLock(T0));
        Assert.Equal(AccessState.Locking, flow.State);
        Assert.Equal(AccessCommand.None, flow.OnTick(T0.AddMilliseconds(500)));
        Assert.Equal(AccessCommand.LockWorkstation, flow.OnTick(T0 + AccessTimeline.LockDuration));
        Assert.Equal(AccessState.Locked, flow.State);
    }

    [Fact]
    public void RequestLockIgnoredWhileLockingOrLocked()
    {
        var flow = new AccessFlow();
        flow.RequestLock(T0);

        Assert.Equal(AccessCommand.None, flow.RequestLock(T0.AddMilliseconds(100)));
        flow.OnSessionLocked(T0.AddMilliseconds(200));
        Assert.Equal(AccessCommand.None, flow.RequestLock(T0.AddMilliseconds(300)));
    }

    [Fact]
    public void ExternalLockShowsStaticOverlay()
    {
        var flow = new AccessFlow();

        Assert.Equal(AccessCommand.Show, flow.OnSessionLocked(T0));
        Assert.Equal(AccessState.Locked, flow.State);
        Assert.Equal(AccessTimeline.Locked, flow.Frame(T0.AddMinutes(5)));
    }

    [Fact]
    public void SessionLockedDuringLockAnimationDoesNotLockAgain()
    {
        var flow = new AccessFlow();
        flow.RequestLock(T0);

        Assert.Equal(AccessCommand.None, flow.OnSessionLocked(T0.AddMilliseconds(300)));
        Assert.Equal(AccessCommand.None, flow.OnTick(T0.AddSeconds(2)));
    }

    [Fact]
    public void UnlockPlaysWelcomeThenHides()
    {
        var flow = new AccessFlow();
        flow.OnSessionLocked(T0);
        var unlock = T0.AddMinutes(10);

        flow.OnSessionUnlocked(unlock);
        Assert.Equal(AccessState.Welcoming, flow.State);
        Assert.Equal(AccessCommand.None, flow.OnTick(unlock.AddMilliseconds(800)));
        Assert.Equal(AccessCommand.Hide, flow.OnTick(unlock + AccessTimeline.WelcomeDuration));
        Assert.Equal(AccessState.Hidden, flow.State);
    }

    [Fact]
    public void UnlockWhenHiddenDoesNothing()
    {
        var flow = new AccessFlow();

        Assert.Equal(AccessCommand.None, flow.OnSessionUnlocked(T0));
        Assert.Equal(AccessState.Hidden, flow.State);
    }

    [Fact]
    public void LockFailedHidesOverlay()
    {
        var flow = new AccessFlow();
        flow.RequestLock(T0);
        flow.OnTick(T0.AddSeconds(2));

        Assert.Equal(AccessCommand.Hide, flow.OnLockFailed());
        Assert.Equal(AccessState.Hidden, flow.State);
    }

    [Fact]
    public void SkipAdvancesEveryState()
    {
        var flow = new AccessFlow();
        flow.RequestLock(T0);

        Assert.Equal(AccessCommand.LockWorkstation, flow.Skip(T0.AddMilliseconds(100)));
        Assert.Equal(AccessCommand.None, flow.Skip(T0.AddMilliseconds(200)));
        Assert.Equal(AccessState.Welcoming, flow.State);
        Assert.Equal(AccessCommand.Hide, flow.Skip(T0.AddMilliseconds(300)));
        Assert.Equal(AccessCommand.None, flow.Skip(T0.AddMilliseconds(400)));
    }

    [Fact]
    public void LockDuringWelcomeReturnsToLocked()
    {
        var flow = new AccessFlow();
        flow.OnSessionLocked(T0);
        flow.OnSessionUnlocked(T0.AddSeconds(1));

        Assert.Equal(AccessCommand.Show, flow.OnSessionLocked(T0.AddSeconds(1.5)));
        Assert.Equal(AccessState.Locked, flow.State);
    }
}

public class AccessTimelineTests
{
    private static AccessFrame Lock(double ms) => AccessTimeline.Lock(TimeSpan.FromMilliseconds(ms));

    private static AccessFrame Welcome(double ms) => AccessTimeline.Welcome(TimeSpan.FromMilliseconds(ms));

    [Fact]
    public void LockStartsOpenAndEndsClosedWithRingOff()
    {
        Assert.Equal(1, Lock(0).Iris, 3);
        Assert.Equal(0, Lock(0).CardOpacity, 3);

        var end = Lock(AccessTimeline.LockDuration.TotalMilliseconds);
        Assert.Equal(0, end.Iris, 3);
        Assert.Equal(1, end.CardScale, 3);
        Assert.Equal(0, end.RingLit, 3);
        Assert.True(end.CoversVisible);
    }

    [Fact]
    public void LockCardOvershootsBeforeSettling()
    {
        var max = Enumerable.Range(250, 450).Max(t => Lock(t).CardScale);

        Assert.True(max > 1.0);
        Assert.Equal(1, Lock(1000).CardScale, 3);
    }

    [Fact]
    public void LockIrisNeverIncreases()
    {
        var values = Enumerable.Range(0, 1100).Select(t => Lock(t).Iris).ToList();

        Assert.All(values.Zip(values.Skip(1)), p => Assert.True(p.Second <= p.First + 1e-9));
    }

    [Fact]
    public void WelcomeLightsRingThenOpensIris()
    {
        Assert.Equal(0, Welcome(0).RingLit, 3);
        Assert.True(Welcome(0).RingHead);
        Assert.Equal(1, Welcome(550).RingLit, 3);
        Assert.False(Welcome(600).RingHead);
        Assert.Equal(0, Welcome(800).Iris, 3);
        Assert.Equal(1, Welcome(1600).Iris, 3);
    }

    [Fact]
    public void WelcomeEndsInvisible()
    {
        var end = Welcome(AccessTimeline.WelcomeDuration.TotalMilliseconds);

        Assert.Equal(0, end.CardOpacity, 3);
        Assert.Equal(0, end.RingOpacity, 3);
        Assert.Equal(0, end.CaptionOpacity, 3);
        Assert.False(end.CoversVisible);
        Assert.True(end.RingSpread > 2);
    }

    [Fact]
    public void WelcomeCardScaleNeverNegative()
    {
        Assert.All(Enumerable.Range(0, 1700), t => Assert.True(Welcome(t).CardScale >= 0));
    }

    [Theory]
    [InlineData(0, 40, 0)]
    [InlineData(0.5, 40, 20)]
    [InlineData(1, 40, 40)]
    [InlineData(1.4, 40, 40)]
    public void LitDotsIsClamped(double lit, int dots, int expected) =>
        Assert.Equal(expected, AccessTimeline.LitDots(lit, dots));
}

public class AccessIdentityTests
{
    [Theory]
    [InlineData("Andrea Conrado", "AC")]
    [InlineData("conra", "C")]
    [InlineData("anna maria rossi", "AM")]
    [InlineData("  ", "?")]
    [InlineData("42 Studio", "S")]
    public void Initials(string name, string expected) => Assert.Equal(expected, AccessIdentity.Initials(name));

    [Theory]
    [InlineData(4, "Buonanotte")]
    [InlineData(5, "Buongiorno")]
    [InlineData(13, "Buon pomeriggio")]
    [InlineData(18, "Buonasera")]
    [InlineData(23, "Buonanotte")]
    public void Greeting(int hour, string expected) => Assert.Equal(expected, AccessIdentity.Greeting(hour));

    [Fact]
    public void CaptionUsesFirstName()
    {
        Assert.Equal("Buonasera, Andrea", AccessIdentity.Caption(AccessCaption.Welcome, "Andrea Conrado", 20));
        Assert.Equal("Buongiorno", AccessIdentity.Caption(AccessCaption.Welcome, "", 8));
        Assert.Equal("Bloccato", AccessIdentity.Caption(AccessCaption.Locked, "Andrea", 8));
        Assert.Equal("A presto", AccessIdentity.Caption(AccessCaption.Goodbye, "Andrea", 8));
    }
}

public class EasingTests
{
    [Fact]
    public void CurvesHitEndpoints()
    {
        foreach (var f in new Func<double, double>[] { Easing.OutCubic, Easing.InCubic, Easing.InOutCubic, Easing.OutBack, Easing.InBack })
        {
            Assert.Equal(0, f(0), 6);
            Assert.Equal(1, f(1), 6);
        }
    }

    [Theory]
    [InlineData(5, 0, 10, 0.5)]
    [InlineData(-1, 0, 10, 0)]
    [InlineData(20, 0, 10, 1)]
    [InlineData(3, 3, 3, 1)]
    public void Progress(double t, double start, double end, double expected) =>
        Assert.Equal(expected, Easing.Progress(t, start, end), 6);
}
