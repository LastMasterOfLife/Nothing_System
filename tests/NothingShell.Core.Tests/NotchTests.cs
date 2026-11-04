using NothingShell.Core.Animation;
using NothingShell.Core.Design;
using NothingShell.Core.Notch;

namespace NothingShell.Core.Tests;

public class NotchStateMachineTests
{
    private static readonly TimeSpan T0 = TimeSpan.FromSeconds(10);

    [Fact]
    public void StartsIdle() => Assert.Equal(NotchState.Idle, new NotchStateMachine().State);

    [Fact]
    public void MediaPlayingShowsActivityAndStoppingReturnsIdle()
    {
        var sm = new NotchStateMachine();
        Assert.True(sm.OnActivityChanged(true));
        Assert.Equal(NotchState.Activity, sm.State);
        Assert.True(sm.OnActivityChanged(false));
        Assert.Equal(NotchState.Idle, sm.State);
    }

    [Fact]
    public void PointerEnterExpands()
    {
        var sm = new NotchStateMachine();
        Assert.True(sm.OnPointerEnter());
        Assert.Equal(NotchState.Expanded, sm.State);
    }

    [Fact]
    public void LeaveCollapsesRightAway()
    {
        var sm = new NotchStateMachine();
        sm.OnPointerEnter();
        sm.OnPointerLeave(T0);

        Assert.Equal(T0, sm.NextDeadline);
        Assert.True(sm.Tick(T0));
        Assert.Equal(NotchState.Idle, sm.State);
        Assert.Null(sm.CollapseDeadline);
    }

    [Fact]
    public void HoverOpensOnlyAfterTheEnterDelay()
    {
        var sm = new NotchStateMachine();
        Assert.True(sm.OnPointerHover(T0));
        Assert.True(sm.Peeking);
        Assert.Equal(NotchState.Idle, sm.State);

        Assert.False(sm.Tick(T0 + NotchMotion.EnterDelay - TimeSpan.FromMilliseconds(1)));
        Assert.True(sm.Tick(T0 + NotchMotion.EnterDelay));
        Assert.Equal(NotchState.Expanded, sm.State);
        Assert.False(sm.Peeking);
    }

    [Fact]
    public void LeavingWhilePeekingCancelsTheOpening()
    {
        var sm = new NotchStateMachine();
        sm.OnPointerHover(T0);
        Assert.True(sm.OnPointerLeave(T0 + TimeSpan.FromMilliseconds(100)));
        Assert.False(sm.Peeking);
        Assert.False(sm.Tick(T0 + TimeSpan.FromSeconds(1)));
        Assert.Equal(NotchState.Idle, sm.State);
    }

    [Fact]
    public void PeekingKeepsAnAlertUntilItOpensOrTheMouseLeaves()
    {
        var sm = new NotchStateMachine();
        sm.OnAlert(T0, TimeSpan.FromMilliseconds(100));
        sm.OnPointerHover(T0);
        Assert.Null(sm.AlertDeadline);

        var left = T0 + TimeSpan.FromMilliseconds(200);
        sm.OnPointerLeave(left);
        Assert.Equal(NotchState.Alert, sm.State);
        Assert.True(sm.Tick(left + NotchStateMachine.CardLeaveDelay));
        Assert.Equal(NotchState.Idle, sm.State);
    }

    [Fact]
    public void NoOpenDelayOpensAtOnceWithoutPeeking()
    {
        var sm = new NotchStateMachine { OpenDelay = TimeSpan.Zero };
        Assert.True(sm.OnPointerHover(T0));
        Assert.False(sm.Peeking);
        Assert.Equal(NotchState.Expanded, sm.State);
    }

    [Fact]
    public void OpenDelayIsTheChosenOne()
    {
        var delay = TimeSpan.FromMilliseconds(600);
        var sm = new NotchStateMachine { OpenDelay = delay };
        sm.OnPointerHover(T0);
        Assert.False(sm.Tick(T0 + NotchMotion.EnterDelay));
        Assert.True(sm.Tick(T0 + delay));
        Assert.Equal(NotchState.Expanded, sm.State);
    }

    [Fact]
    public void CloseDelayKeepsItOpenAndComingBackCancelsIt()
    {
        var delay = TimeSpan.FromSeconds(1);
        var sm = new NotchStateMachine { CloseDelay = delay };
        sm.OnPointerEnter();
        sm.OnPointerLeave(T0);
        Assert.False(sm.Tick(T0 + delay - TimeSpan.FromMilliseconds(1)));
        Assert.Equal(NotchState.Expanded, sm.State);

        sm.OnPointerHover(T0 + TimeSpan.FromMilliseconds(500));
        Assert.Null(sm.CollapseDeadline);
        sm.OnPointerLeave(T0 + TimeSpan.FromSeconds(2));
        Assert.True(sm.Tick(T0 + TimeSpan.FromSeconds(3)));
        Assert.Equal(NotchState.Idle, sm.State);
    }

    [Fact]
    public void CloseDelayAlsoAppliesWhenTypingStops()
    {
        var delay = TimeSpan.FromMilliseconds(700);
        var sm = new NotchStateMachine { CloseDelay = delay };
        sm.OnPointerEnter();
        sm.Hold(true, pointerOver: true, T0);
        sm.Hold(false, pointerOver: false, T0);
        Assert.Equal(T0 + delay, sm.CollapseDeadline);
    }

    [Fact]
    public void HoverOnAnOpenNotchCancelsItsCollapse()
    {
        var sm = new NotchStateMachine();
        sm.OnPointerEnter();
        sm.OnPointerLeave(T0);
        sm.OnPointerHover(T0);
        Assert.False(sm.Peeking);
        Assert.Null(sm.CollapseDeadline);
        Assert.Equal(NotchState.Expanded, sm.State);
    }

    [Fact]
    public void ReenteringCancelsCollapse()
    {
        var sm = new NotchStateMachine();
        sm.OnPointerEnter();
        sm.OnPointerLeave(T0);
        sm.OnPointerEnter();
        Assert.False(sm.Tick(T0 + TimeSpan.FromSeconds(5)));
        Assert.Equal(NotchState.Expanded, sm.State);
    }

    [Fact]
    public void MediaChangeWhileExpandedIsAppliedOnCollapse()
    {
        var sm = new NotchStateMachine();
        sm.OnPointerEnter();
        Assert.False(sm.OnActivityChanged(true));
        Assert.Equal(NotchState.Expanded, sm.State);

        sm.OnPointerLeave(T0);
        sm.Tick(T0 + NotchStateMachine.LeaveDelay);
        Assert.Equal(NotchState.Activity, sm.State);
    }

    [Fact]
    public void LeaveWhenNotExpandedSchedulesNothing()
    {
        var sm = new NotchStateMachine();
        sm.OnPointerLeave(T0);
        Assert.Null(sm.CollapseDeadline);
    }
}

public class NotchLayoutTests
{
    [Fact]
    public void HostFitsEveryState()
    {
        var host = NotchLayout.Host;
        foreach (var state in Enum.GetValues<NotchState>())
        {
            var s = NotchLayout.For(state);
            Assert.True(s.Width <= host.Width && s.Height + NotchLayout.TopMargin <= host.Height);
        }
    }

    [Fact]
    public void CompactStatesArePills()
    {
        var idle = NotchLayout.For(NotchState.Idle);
        Assert.Equal(idle.Height / 2, NotchLayout.CornerRadius(idle));
    }

    [Fact]
    public void HostLeavesRoomForPeekEarsAndShadow()
    {
        var host = NotchLayout.Host;
        foreach (var state in Enum.GetValues<NotchState>())
        {
            var s = NotchLayout.For(state);
            Assert.True((s.Width * NotchMotion.PeekScale) + (2 * NotchLayout.Ear(s)) <= host.Width);
        }
        Assert.True(host.Height >= NotchLayout.TallHeight + NotchLayout.TopMargin + NotchLayout.ShadowReach);
    }

    [Fact]
    public void LargeIsNusaWideAndKeepsOurHeights()
    {
        Assert.Equal(new Size2(144, 28), NotchLayout.For(NotchState.Idle, NotchSize.Small));
        Assert.Equal(new Size2(420, 172), NotchLayout.For(NotchState.Expanded, NotchSize.Small));
        foreach (var state in Enum.GetValues<NotchState>())
        {
            var small = NotchLayout.For(state, NotchSize.Small);
            var large = NotchLayout.For(state, NotchSize.Large);
            Assert.True(large.Width > small.Width && large.Height >= small.Height);
        }
        Assert.Equal(480, NotchLayout.For(NotchState.Expanded, NotchSize.Large).Width);
    }

    [Fact]
    public void AttachedNotchGrowsEarsAndRoundsTheBottom()
    {
        var idle = new Size2(174, 33);
        var open = new Size2(480, 172);
        Assert.InRange(NotchLayout.Ear(idle), 6, 9);
        Assert.Equal(NotchLayout.MaxEar, NotchLayout.Ear(open));
        Assert.Equal(NotchLayout.MaxAttachedRadius, NotchLayout.CornerRadius(open, NotchStyle.Attached));
        Assert.Equal(NotchLayout.MaxCornerRadius, NotchLayout.CornerRadius(open, NotchStyle.Floating));
        Assert.Equal(3, NotchLayout.Ear(NotchLayout.Quiet)); // mai oltre metà altezza
    }

    [Fact]
    public void MotionPicksTheSpringByDirection()
    {
        var small = new Size2(144, 28);
        var big = new Size2(420, 172);
        Assert.Equal(NotchMotion.Open, NotchMotion.For(small, big, peeking: false));
        Assert.Equal(NotchMotion.Close, NotchMotion.For(big, small, peeking: false));
        Assert.Equal(NotchMotion.Peek, NotchMotion.For(small, small, peeking: true));
    }
}

/// <summary>Le molle di Nusa, misurate dallo schermo (docs/20-stile-nusa.md): stesso rimbalzo e stessi tempi.</summary>
public class NusaMotionTests
{
    private static (double Overshoot, double PeakTime, double SettleTime) Run(SpringTune tune, double from, double to)
    {
        var spring = new Spring(from, tune.Stiffness, tune.Damping) { Target = to };
        var direction = Math.Sign(to - from);
        double peak = from, peakTime = 0, settle = 0, t = 0;
        while (spring.Step(1.0 / 240) && t < 3)
        {
            t += 1.0 / 240;
            if ((spring.Value - peak) * direction > 0)
                (peak, peakTime) = (spring.Value, t);
            if (Math.Abs(spring.Value - to) > Math.Abs(to - from) * 0.01)
                settle = t;
        }
        return ((peak - to) / (to - from), peakTime, settle);
    }

    [Fact]
    public void OpeningOvershootsLikeNusa()
    {
        var (overshoot, peakTime, settle) = Run(NotchMotion.Open, 291, 765);
        Assert.InRange(overshoot, 0.05, 0.09);
        Assert.InRange(peakTime, 0.23, 0.29);
        Assert.InRange(settle, 0.35, 0.55);
    }

    [Fact]
    public void ClosingDipsBelowRestLikeNusa()
    {
        var (overshoot, peakTime, settle) = Run(NotchMotion.Close, 767, 271);
        Assert.InRange(overshoot, 0.05, 0.09); // ~35 px sotto la misura di riposo, come Nusa
        Assert.InRange(peakTime, 0.2, 0.3);
        Assert.InRange(settle, 0.3, 0.55);
    }

    [Theory]
    [InlineData(NotchFeel.Soft, -0.001, 0.01, 0.5, 0.8)]
    [InlineData(NotchFeel.Nusa, 0.05, 0.09, 0.35, 0.55)]
    [InlineData(NotchFeel.Snappy, 0.1, 0.16, 0.25, 0.45)]
    public void FeelsGoFromCalmToLively(NotchFeel feel, double minOvershoot, double maxOvershoot, double minSettle, double maxSettle)
    {
        var (overshoot, _, settle) = Run(NotchMotion.Of(feel).Open, 291, 765);
        Assert.InRange(overshoot, minOvershoot, maxOvershoot);
        Assert.InRange(settle, minSettle, maxSettle);
        var (closing, _, _) = Run(NotchMotion.Of(feel).Close, 767, 271);
        Assert.InRange(closing, minOvershoot, maxOvershoot + 0.02);
    }

    [Fact]
    public void FeelPicksItsOwnSprings()
    {
        var small = new Size2(144, 28);
        var big = new Size2(420, 172);
        Assert.Equal(NotchMotion.Snappy.Open, NotchMotion.For(small, big, peeking: false, NotchFeel.Snappy));
        Assert.Equal(NotchMotion.Soft.Close, NotchMotion.For(big, small, peeking: false, NotchFeel.Soft));
        Assert.Equal(NotchMotion.Open, NotchMotion.For(small, big, peeking: false));
    }

    [Fact]
    public void TuningMidFlightKeepsVelocity()
    {
        var spring = new Spring(0, NotchMotion.Peek.Stiffness, NotchMotion.Peek.Damping) { Target = 10 };
        spring.Step(0.05);
        var velocity = spring.Velocity;
        spring.Tune(NotchMotion.Open.Stiffness, NotchMotion.Open.Damping);
        Assert.Equal(velocity, spring.Velocity);
    }
}

public class SpringTests
{
    [Fact]
    public void ConvergesToTarget()
    {
        var spring = new Spring(0) { Target = 100 };
        var frames = 0;
        while (spring.Step(1.0 / 60) && frames < 600)
            frames++;

        Assert.True(spring.IsSettled);
        Assert.Equal(100, spring.Value);
        Assert.True(frames < 120, $"troppo lenta: {frames} frame");
    }

    [Fact]
    public void OvershootIsSmall()
    {
        var spring = new Spring(0) { Target = 100 };
        var max = 0.0;
        for (var i = 0; i < 300; i++)
        {
            spring.Step(1.0 / 60);
            max = Math.Max(max, spring.Value);
        }
        Assert.InRange(max, 100, 110);
    }

    [Fact]
    public void LongPauseDoesNotExplode()
    {
        var spring = new Spring(0) { Target = 100 };
        spring.Step(10);
        Assert.InRange(spring.Value, -50, 200);
    }
}
