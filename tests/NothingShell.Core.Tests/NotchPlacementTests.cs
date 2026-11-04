using NothingShell.Core.Bar;
using NothingShell.Core.Design;
using NothingShell.Core.Notch;
using NothingShell.Core.Widgets;

namespace NothingShell.Core.Tests;

public class NotchPlacementTests
{
    private static readonly DisplayMonitor Main = new(1, @"\\.\DISPLAY1", new Rect2(0, 0, 3840, 2160), new Rect2(0, 0, 3840, 2088), true, 1.5);
    private static readonly DisplayMonitor Left = new(2, @"\\.\DISPLAY2", new Rect2(-1920, 200, 1920, 1080), new Rect2(-1920, 200, 1920, 1032), false, 1);
    private static readonly DisplayMonitor[] Both = [Main, Left];

    [Fact]
    public void PrimaryIsTheDefault() =>
        Assert.Same(Main, NotchPlacement.Pick(Both, NotchScreen.Primary, @"\\.\DISPLAY2", Left.Handle));

    [Fact]
    public void FollowsTheScreenUnderTheMouse()
    {
        Assert.Same(Left, NotchPlacement.Pick(Both, NotchScreen.Mouse, null, Left.Handle));
        Assert.Same(Main, NotchPlacement.Pick(Both, NotchScreen.Mouse, null, 0)); // mouse non trovato
    }

    [Fact]
    public void ChosenScreenByNameAndBackToPrimaryWhenUnplugged()
    {
        Assert.Same(Left, NotchPlacement.Pick(Both, NotchScreen.Chosen, @"\\.\display2", 0));
        Assert.Same(Main, NotchPlacement.Pick(Both, NotchScreen.Chosen, @"\\.\DISPLAY3", 0));
        Assert.Same(Main, NotchPlacement.Pick(Both, NotchScreen.Chosen, null, 0));
    }

    [Fact]
    public void WithoutPrimaryTakesTheFirstAndWithoutScreensNothing()
    {
        Assert.Same(Left, NotchPlacement.Pick([Left], NotchScreen.Primary, null, 0));
        Assert.Null(NotchPlacement.Pick([], NotchScreen.Mouse, null, 0));
    }

    [Fact]
    public void PlacesCenteredOnTheTopEdgeInPhysicalPixels()
    {
        var host = new Size2(600, 400);
        Assert.Equal(new Rect2(1470, 0, 900, 600), NotchPlacement.Place(Main, host));
        Assert.Equal(new Rect2(-1260, 200, 600, 400), NotchPlacement.Place(Left, host));
    }

    [Fact]
    public void ScreensAreNumberedLeftToRight() =>
        Assert.Equal([Left, Main], NotchPlacement.InOrder(Both));
}
