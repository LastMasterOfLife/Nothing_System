using NothingShell.Core.Animation;
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
    public void LeaveCollapsesOnlyAfterDelay()
    {
        var sm = new NotchStateMachine();
        sm.OnPointerEnter();
        sm.OnPointerLeave(T0);

        Assert.False(sm.Tick(T0 + NotchStateMachine.LeaveDelay - TimeSpan.FromMilliseconds(1)));
        Assert.Equal(NotchState.Expanded, sm.State);

        Assert.True(sm.Tick(T0 + NotchStateMachine.LeaveDelay));
        Assert.Equal(NotchState.Idle, sm.State);
        Assert.Null(sm.CollapseDeadline);
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
