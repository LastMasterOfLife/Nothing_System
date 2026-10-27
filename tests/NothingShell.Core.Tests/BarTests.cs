using NothingShell.Core.Bar;
using NothingShell.Core.Widgets;

namespace NothingShell.Core.Tests;

public class TaskbarModelTests
{
    private static AppWindow W(nint h, string app, int z, bool min = false, string? title = null) =>
        new(h, app, title ?? $"{app}-{h}", min, z);

    [Fact]
    public void GroupsByAppInOrderOfAppearance()
    {
        var model = new TaskbarModel();
        Assert.True(model.Update([W(1, "code", 0), W(2, "edge", 1), W(3, "code", 2)]));

        Assert.Equal(["code", "edge"], model.Groups.Select(g => g.AppKey));
        Assert.Equal([(nint)1, 3], model.Groups[0].Windows.Select(w => w.Handle));
    }

    [Fact]
    public void OrderStaysStableWhenZOrderChanges()
    {
        var model = new TaskbarModel();
        model.Update([W(1, "code", 0), W(2, "edge", 1)]);
        Assert.False(model.Update([W(2, "edge", 0), W(1, "code", 1)])); // solo cambio di primo piano

        Assert.Equal(["code", "edge"], model.Groups.Select(g => g.AppKey));
    }

    [Fact]
    public void NewAppsGoLastAndClosedAppsDisappear()
    {
        var model = new TaskbarModel();
        model.Update([W(1, "code", 0), W(2, "edge", 1)]);
        Assert.True(model.Update([W(3, "spotify", 0), W(2, "edge", 1)]));

        Assert.Equal(["edge", "spotify"], model.Groups.Select(g => g.AppKey));
    }

    [Fact]
    public void TitleOrMinimizeChangeIsAChange()
    {
        var model = new TaskbarModel();
        model.Update([W(1, "code", 0, title: "a")]);
        Assert.True(model.Update([W(1, "code", 0, title: "b")]));
        Assert.True(model.Update([W(1, "code", 0, min: true, title: "b")]));
    }

    [Fact]
    public void SingleWindowTogglesMinimize()
    {
        var group = new AppGroup("code", [W(1, "code", 0)]);
        Assert.Equal(new ClickAction(ClickKind.Minimize, 1), TaskbarModel.Click(group, foreground: 1));
        Assert.Equal(new ClickAction(ClickKind.Activate, 1), TaskbarModel.Click(group, foreground: 99));
    }

    [Fact]
    public void MinimizedSingleWindowIsRestoredEvenIfForeground()
    {
        var group = new AppGroup("code", [W(1, "code", 0, min: true)]);
        Assert.Equal(ClickKind.Activate, TaskbarModel.Click(group, foreground: 1).Kind);
    }

    [Fact]
    public void ManyWindowsActivateMostRecentThenCycle()
    {
        var group = new AppGroup("code", [W(1, "code", 5), W(2, "code", 3), W(3, "code", 9)]);
        Assert.Equal(new ClickAction(ClickKind.Activate, 2), TaskbarModel.Click(group, foreground: 99));
        Assert.Equal(new ClickAction(ClickKind.Activate, 3), TaskbarModel.Click(group, foreground: 2));
        Assert.Equal(new ClickAction(ClickKind.Activate, 1), TaskbarModel.Click(group, foreground: 3));
    }

    [Fact]
    public void EmptyGroupDoesNothing() =>
        Assert.Equal(ClickAction.None, TaskbarModel.Click(new AppGroup("x", []), 1));
}

public class MonochromeTests
{
    [Fact]
    public void PixelsBecomeGreyAndKeepAlpha()
    {
        byte[] px = [0, 0, 255, 128, 255, 255, 255, 255, 0, 0, 0, 10]; // rosso, bianco, nero
        Design.Monochrome.ApplyBgra(px);

        Assert.Equal(px[0], px[1]);
        Assert.Equal(px[1], px[2]);
        Assert.Equal(128, px[3]);
        Assert.Equal(255, px[4]);
        Assert.Equal(Design.Monochrome.Floor, px[8]);
        Assert.Equal(10, px[11]);
    }
}

public class FullscreenTests
{
    private static readonly Rect2 Monitor = new(0, 0, 1920, 1200);

    [Fact]
    public void CoveringWindowIsFullscreen() =>
        Assert.True(Fullscreen.IsFullscreen(new Rect2(0, 0, 1920, 1200), Monitor, "Chrome_WidgetWin_1"));

    [Fact]
    public void MaximizedWindowIsNot() => // massimizzata: lascia libera la fascia della barra
        Assert.False(Fullscreen.IsFullscreen(new Rect2(-8, -8, 1936, 1156), Monitor, "Chrome_WidgetWin_1"));

    [Fact]
    public void DesktopIsNot() =>
        Assert.False(Fullscreen.IsFullscreen(new Rect2(0, 0, 1920, 1200), Monitor, "Progman"));
}
