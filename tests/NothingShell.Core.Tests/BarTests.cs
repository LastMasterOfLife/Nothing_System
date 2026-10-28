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

public class PinnedAppsTests
{
    private static AppWindow W(nint h, string app, int z) => new(h, app, $"{app}-{h}", false, z);

    private static PinnedApp Pin(string id) => new(id, $"App {id}", $@"C:\pins\{id}.lnk", $@"C:\apps\{id}.exe", false);

    [Fact]
    public void PinnedAppsComeFirstInPinOrderEvenIfClosed()
    {
        var model = new TaskbarModel();
        model.Update([W(1, "EDGE", 0), W(2, "CODE", 1)]);
        Assert.True(model.SetPinned([Pin("code"), Pin("teams")]));

        Assert.Equal(["CODE", "TEAMS", "EDGE"], model.Groups.Select(g => g.AppKey));
        Assert.True(model.Groups[0].IsRunning);
        Assert.False(model.Groups[1].IsRunning);
        Assert.Equal("App teams", model.Groups[1].Title);
        Assert.Null(model.Groups[2].Pin);
    }

    [Fact]
    public void PinMatchesWindowsIgnoringCase()
    {
        var model = new TaskbarModel();
        model.SetPinned([Pin("Microsoft.VisualStudioCode")]);
        model.Update([W(1, AppIds.Key("microsoft.visualstudiocode"), 0)]);

        var group = Assert.Single(model.Groups);
        Assert.Equal([(nint)1], group.Windows.Select(w => w.Handle));
    }

    [Fact]
    public void ClosedPinnedAppLaunches()
    {
        var group = new AppGroup("TEAMS", [], Pin("teams"));
        Assert.Equal(ClickKind.Launch, TaskbarModel.Click(group, foreground: 1).Kind);
    }

    [Fact]
    public void SamePinsAreNotAChange()
    {
        var model = new TaskbarModel();
        model.SetPinned([Pin("a")]);
        Assert.False(model.SetPinned([Pin("a")]));
        Assert.True(model.SetPinned([Pin("a"), Pin("b")]));
    }

    [Fact]
    public void DefaultAppIdUsesMostSpecificKnownFolder()
    {
        KnownFolder[] folders =
        [
            new(@"C:\Program Files", "{6D809377-6AF0-444B-8957-A3773F02200E}"),
            new(@"C:\Users\me\AppData\Local", "{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}"),
            new(@"C:\Users\me\AppData\Local\Programs", "{5CD7AEE2-2219-4A67-B85D-6C9CE15660CB}"),
        ];
        Assert.Equal(@"{6D809377-6AF0-444B-8957-A3773F02200E}\KeePass Password Safe 2\KeePass.exe",
            AppIds.Default(@"C:\Program Files\KeePass Password Safe 2\KeePass.exe", folders));
        Assert.Equal(@"{5CD7AEE2-2219-4A67-B85D-6C9CE15660CB}\App\app.exe",
            AppIds.Default(@"c:\users\me\appdata\local\programs\App\app.exe", folders));
        Assert.Equal(@"D:\tools\x.exe", AppIds.Default(@"D:\tools\x.exe", folders));
        Assert.Equal(@"C:\Program Files Extra\x.exe", AppIds.Default(@"C:\Program Files Extra\x.exe", folders));
    }

    [Fact]
    public void FavoritesBlobSplitsIntoIdLists()
    {
        byte[] blob = [0, 2, 0, 0, 0, 0xAA, 0xBB, 0, 1, 0, 0, 0, 0xCC, 0, 9, 0];
        var lists = TaskbandFavorites.Split(blob);

        Assert.Equal(2, lists.Count);
        Assert.Equal([0xAA, 0xBB], lists[0]);
        Assert.Equal([0xCC], lists[1]);
    }

    [Fact]
    public void TruncatedFavoritesKeepValidEntries() =>
        Assert.Single(TaskbandFavorites.Split([0, 1, 0, 0, 0, 0xAA, 0, 50, 0, 0, 0, 1]));
}

public class JumpListTests
{
    [Fact]
    public void CleanDropsUrisAndDuplicatesAndNamesUntitledItems()
    {
        var items = JumpLists.Clean(
        [
            new("index.html", @"C:\site\index.html", null, false),
            new("mailto:me@example.com", "", null, false),
            new("tel:123", "tel:123", null, false),
            new("index.html", @"c:\SITE\index.html", null, false),
            new("", @"\\server\share\docs\", null, false),
        ]);

        Assert.Equal(["index.html", "docs"], items.Select(i => i.Title));
    }

    [Fact]
    public void CleanKeepsAtMostTen() =>
        Assert.Equal(JumpLists.MaxItems, JumpLists.Clean(
            Enumerable.Range(0, 30).Select(i => new JumpItem($"f{i}", $@"C:\f{i}.txt", null, false))).Count);

    [Fact]
    public void LinksWithSameExeButDifferentArgumentsAreDistinct() =>
        Assert.Equal(2, JumpLists.Clean(
        [
            new("plainest", @"C:\AS\studio64.exe", @"""C:\dev\plainest""", true),
            new("wp_app", @"C:\AS\studio64.exe", @"""C:\dev\wp_app""", true),
        ]).Count);

    [Fact]
    public void FileOpensWithItsApp() =>
        Assert.Equal(new LaunchCommand(@"C:\Chrome\chrome.exe", @"""C:\Downloads\a.pdf"""),
            JumpLists.Open(new("a.pdf", @"C:\Downloads\a.pdf", null, false), @"C:\Chrome\chrome.exe"));

    [Fact]
    public void LinkRunsItsTarget() =>
        Assert.Equal(new LaunchCommand(@"C:\Windows\System32\mstsc.exe", "/v:\"host\""),
            JumpLists.Open(new("host", @"C:\Windows\System32\mstsc.exe", "/v:\"host\"", true), @"C:\other.exe"));

    [Theory]
    [InlineData(null)]
    [InlineData(@"C:\Windows\explorer.exe")]
    public void WithoutAppExeThePathIsOpened(string? exe) =>
        Assert.Equal(new LaunchCommand(@"C:\dev", null), JumpLists.Open(new("dev", @"C:\dev", null, false), exe));

    [Fact]
    public void QuoteEscapesTrailingBackslash()
    {
        Assert.Equal(@"""C:\\""", JumpLists.Quote(@"C:\"));
        Assert.Equal(@"""C:\a b""", JumpLists.Quote(@"C:\a b"));
    }
}
