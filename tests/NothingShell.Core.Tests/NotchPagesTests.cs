using NothingShell.Core.Notch;
using NothingShell.Core.Settings;

namespace NothingShell.Core.Tests;

public class NotchAlertTests
{
    private static readonly TimeSpan T0 = TimeSpan.FromSeconds(100);

    [Fact]
    public void AlertShowsThenReturnsToPreviousState()
    {
        var sm = new NotchStateMachine();
        sm.OnActivityChanged(true);
        Assert.True(sm.OnAlert(T0));
        Assert.Equal(NotchState.Alert, sm.State);
        Assert.Equal(T0 + NotchStateMachine.AlertDuration, sm.NextDeadline);

        Assert.False(sm.Tick(T0 + TimeSpan.FromSeconds(5)));
        Assert.True(sm.Tick(T0 + NotchStateMachine.AlertDuration));
        Assert.Equal(NotchState.Activity, sm.State);
    }

    [Fact]
    public void ActivityChangeDuringAlertDoesNotCutIt()
    {
        var sm = new NotchStateMachine();
        sm.OnAlert(T0);
        Assert.False(sm.OnActivityChanged(true));
        Assert.Equal(NotchState.Alert, sm.State);
        sm.Tick(T0 + NotchStateMachine.AlertDuration);
        Assert.Equal(NotchState.Activity, sm.State);
    }

    [Fact]
    public void HoverDuringAlertExpandsAndCancelsAlert()
    {
        var sm = new NotchStateMachine();
        sm.OnAlert(T0);
        Assert.True(sm.OnPointerEnter());
        Assert.Null(sm.AlertDeadline);
        Assert.False(sm.Tick(T0 + TimeSpan.FromMinutes(1)));
        Assert.Equal(NotchState.Expanded, sm.State);
    }

    [Fact]
    public void AlertDoesNotInterruptExpanded()
    {
        var sm = new NotchStateMachine();
        sm.OnPointerEnter();
        Assert.False(sm.OnAlert(T0));
        Assert.Equal(NotchState.Expanded, sm.State);
    }

    [Fact]
    public void NextDeadlineIsTheEarliest()
    {
        var sm = new NotchStateMachine();
        sm.OnPointerEnter();
        sm.OnPointerLeave(T0);
        Assert.Equal(T0 + NotchStateMachine.LeaveDelay, sm.NextDeadline);
    }
}

public class NotchPagerTests
{
    [Fact]
    public void SwipesMoveOnePageAndStopAtEdges()
    {
        var pager = new NotchPager();
        Assert.False(pager.Apply(SwipeDirection.Previous)); // già alla prima
        Assert.True(pager.Apply(SwipeDirection.Next));
        Assert.Equal(NotchPage.Mail, pager.Current);
        pager.Show(NotchPage.System);
        Assert.False(pager.Apply(SwipeDirection.Next)); // già all'ultima
        Assert.Equal(3, pager.Index);
    }
}

public class SwipeDetectorTests
{
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void OneWheelNotchIsOneSwipe()
    {
        var s = new SwipeDetector();
        Assert.Equal(SwipeDirection.Next, s.OnVerticalWheel(-120, Ms(1000)));
        Assert.Equal(SwipeDirection.Previous, s.OnVerticalWheel(120, Ms(2000)));
    }

    [Fact]
    public void TouchpadDeltasAccumulate()
    {
        var s = new SwipeDetector();
        Assert.Equal(SwipeDirection.None, s.OnHorizontalWheel(40, Ms(1000)));
        Assert.Equal(SwipeDirection.None, s.OnHorizontalWheel(40, Ms(1020)));
        Assert.Equal(SwipeDirection.Next, s.OnHorizontalWheel(40, Ms(1040)));
    }

    [Fact]
    public void OneLongSwipeMovesOnlyOnePage()
    {
        var s = new SwipeDetector();
        var fired = 0;
        for (var t = 0; t < 300; t += 10) // 30 eventi da 40 = un gesto lungo e veloce
            if (s.OnHorizontalWheel(40, Ms(1000 + t)) != SwipeDirection.None)
                fired++;
        Assert.Equal(1, fired);
    }

    [Fact]
    public void PausesResetTheSum()
    {
        var s = new SwipeDetector();
        s.OnHorizontalWheel(100, Ms(1000));
        Assert.Equal(SwipeDirection.None, s.OnHorizontalWheel(100, Ms(1000 + 400)));
    }

    [Fact]
    public void DragFiresOnceAndSwallowsTheClick()
    {
        var s = new SwipeDetector();
        s.OnDragStart(200);
        Assert.Equal(SwipeDirection.None, s.OnDragMove(180));
        Assert.Equal(SwipeDirection.Next, s.OnDragMove(150));
        Assert.Equal(SwipeDirection.None, s.OnDragMove(50));
        Assert.True(s.OnDragEnd());

        s.OnDragStart(100);
        Assert.Equal(SwipeDirection.Previous, s.OnDragMove(150));
        s.OnDragEnd();

        s.OnDragStart(100);
        Assert.False(s.OnDragEnd()); // semplice clic
    }
}

public class CountdownTests
{
    private static readonly TimeSpan T0 = TimeSpan.FromMinutes(10);

    [Fact]
    public void CountsDownAndFinishesOnce()
    {
        var c = new Countdown();
        c.Start(TimeSpan.FromMinutes(5), T0);
        Assert.Equal(TimeSpan.FromMinutes(4), c.Remaining(T0 + TimeSpan.FromMinutes(1)));
        Assert.False(c.CheckFinished(T0 + TimeSpan.FromMinutes(4)));
        Assert.True(c.CheckFinished(T0 + TimeSpan.FromMinutes(5)));
        Assert.False(c.CheckFinished(T0 + TimeSpan.FromMinutes(6)));
        Assert.False(c.IsRunning);
    }

    [Fact]
    public void StoppedTimerHasNothingLeft()
    {
        var c = new Countdown();
        c.Start(TimeSpan.FromMinutes(5), T0);
        c.Stop();
        Assert.Equal(TimeSpan.Zero, c.Remaining(T0));
        Assert.False(c.CheckFinished(T0 + TimeSpan.FromHours(1)));
    }

    [Theory]
    [InlineData(1500, "25:00")]
    [InlineData(59.2, "01:00")]
    [InlineData(0, "00:00")]
    [InlineData(-3, "00:00")]
    [InlineData(3725, "1:02:05")]
    public void Format(double seconds, string expected) =>
        Assert.Equal(expected, Countdown.Format(TimeSpan.FromSeconds(seconds)));
}

public class MailDetectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 23, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Outlook", true)]
    [InlineData("Posta", true)]
    [InlineData("Mozilla Thunderbird", true)]
    [InlineData("Spotify", false)]
    [InlineData("Brave", false)] // browser senza sito di posta
    public void KnownApps(string app, bool expected) =>
        Assert.Equal(expected, MailDetector.IsMail(app, ["Mario Rossi", "Ciao"]));

    [Fact]
    public void BrowserWithMailSiteIsMail() =>
        Assert.True(MailDetector.IsMail("Brave", ["Mario Rossi", "Riunione\nci vediamo alle 10", "mail.google.com"]));

    [Fact]
    public void ExtraAppsFromSettings() =>
        Assert.True(MailDetector.IsMail("Windows PowerShell", ["x"], ["PowerShell"]));

    [Fact]
    public void ExtractsSenderAndFirstLineOfSubject()
    {
        var mail = MailDetector.Extract("Brave", ["Mario Rossi", "Riunione di domani\nanteprima del testo", "mail.google.com"], Now);
        Assert.Equal(new MailItem("Mario Rossi", "Riunione di domani", "Brave", Now), mail);
    }

    [Fact]
    public void SingleLineIsTheSubject() =>
        Assert.Equal(new MailItem("", "3 nuovi messaggi", "Thunderbird", Now),
            MailDetector.Extract("Thunderbird", ["3 nuovi messaggi"], Now));

    [Fact]
    public void EmptyNotificationIsNull() => Assert.Null(MailDetector.Extract("Outlook", ["  ", ""], Now));
}

public class MailSettingsTests
{
    [Fact]
    public void OldSettingsFileGetsMailDefaults()
    {
        var s = ShellSettingsStore.Deserialize("""{ "notch": true, "bar": false, "disabledWidgets": [] }""");
        Assert.True(s.MailInNotch);
        Assert.False(s.Bar);
    }
}
