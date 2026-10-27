using NothingShell.Core.Design;
using NothingShell.Core.Launcher;

namespace NothingShell.Core.Tests;

public class AppSearchTests
{
    private static readonly AppEntry Code = new("code", "Visual Studio Code");
    private static readonly AppEntry Calc = new("calc", "Calcolatrice");
    private static readonly AppEntry Calendar = new("cal", "Calendario");
    private static readonly AppEntry Spotify = new("spotify", "Spotify");
    private static readonly AppEntry Edge = new("edge", "Microsoft Edge");
    private static readonly AppEntry[] All = [Code, Calc, Calendar, Spotify, Edge];
    private static readonly Dictionary<string, UsageEntry> NoUsage = [];

    [Theory]
    [InlineData("Spotify", "spo", 993)]          // prefisso: 1000 − lunghezza
    [InlineData("Microsoft Edge", "edge", 800)]  // inizio di parola
    [InlineData("Visual Studio Code", "vsc", 700)] // iniziali
    [InlineData("Calcolatrice", "olat", 500)]    // contiene
    [InlineData("Calcolatrice", "cltc", 293)]    // sottosequenza: c·a·l·c·o·l·a·t·r·i·c → 7 lettere saltate
    public void ScoreTiers(string name, string query, int expected) => Assert.Equal(expected, AppSearch.Score(name, query));

    [Fact]
    public void NoMatchIsNull() => Assert.Null(AppSearch.Score("Spotify", "xyz"));

    [Theory]
    [InlineData("x64 Native Tools Command Prompt for VS")] // la 'v' non inizia una parola utile
    [InlineData("VLC media player - reset preferences and cache files")] // troppe lettere saltate
    public void LooseSubsequencesAreRejected(string name) => Assert.Null(AppSearch.Score(name, "vsc"));

    [Fact]
    public void EmptyQueryIsNull() => Assert.Null(AppSearch.Score("Spotify", "  "));

    [Fact]
    public void AccentsAndCaseAreIgnored() => Assert.NotNull(AppSearch.Score("Città Ideale", "CITTA"));

    [Fact]
    public void VscFindsVisualStudioCodeFirst() =>
        Assert.Equal(Code, AppSearch.Search(All, "vsc", NoUsage)[0]);

    [Fact]
    public void ShorterPrefixMatchWins() =>
        Assert.Equal([Calendar, Calc], AppSearch.Search(All, "cal", NoUsage));

    [Fact]
    public void UsageBreaksTies()
    {
        var usage = new Dictionary<string, UsageEntry> { ["calc"] = new(5, DateTimeOffset.UnixEpoch) };
        Assert.Equal(Calc, AppSearch.Search(All, "cal", usage)[0]);
    }

    [Fact]
    public void ResultsAreCapped()
    {
        var many = Enumerable.Range(0, 30).Select(i => new AppEntry($"a{i}", $"App {i}"));
        Assert.Equal(AppSearch.MaxResults, AppSearch.Search(many, "app", NoUsage).Count);
    }

    [Theory]
    [InlineData("Uninstall Foo", "foo", true)]
    [InlineData("Disinstalla Bar", "bar", true)]
    [InlineData("Foo Website", "foo", true)]
    [InlineData("Manuale", @"C:\docs\manual.pdf", true)]
    [InlineData("Spotify", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", false)]
    public void Noise(string name, string id, bool noise) => Assert.Equal(noise, AppSearch.IsNoise(new AppEntry(id, name)));

    [Fact]
    public void SectionsAreAlphabeticalWithHashLast()
    {
        var sections = AppSearch.Sections([Spotify, new AppEntry("7z", "7-Zip"), Calc, Code, Calendar, new AppEntry("e", "Éclair")]);
        Assert.Equal(['C', 'E', 'S', 'V', '#'], sections.Select(s => s.Letter));
        Assert.Equal([Calc, Calendar], sections[0].Apps);
    }

    [Fact]
    public void RecentsAreByLastUseAndOnlyKnownApps()
    {
        var usage = new Dictionary<string, UsageEntry>
        {
            ["calc"] = new(1, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)),
            ["spotify"] = new(9, new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero)),
            ["uninstalled"] = new(3, new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero)),
        };
        Assert.Equal([Spotify, Calc], AppSearch.Recents(All, usage));
    }
}

public class LauncherSettingsTests
{
    [Fact]
    public void RecordLaunchCountsAndTimestamps()
    {
        var t1 = new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);
        var s = LauncherSettings.Default.RecordLaunch("code", t1).RecordLaunch("code", t1.AddHours(1));
        Assert.Equal(new UsageEntry(2, t1.AddHours(1)), s.Usage["code"]);
        Assert.Empty(LauncherSettings.Default.Usage); // immutabile
    }

    [Fact]
    public void RoundTrip()
    {
        var s = new LauncherSettings(false, new Dictionary<string, UsageEntry> { ["x"] = new(3, DateTimeOffset.UnixEpoch) });
        var back = LauncherSettingsStore.Deserialize(LauncherSettingsStore.Serialize(s));
        Assert.False(back.UseWinKey);
        Assert.Equal(3, back.Usage["x"].Count);
    }

    [Theory]
    [InlineData("rotto")]
    [InlineData("null")]
    [InlineData("{\"useWinKey\": false}")]
    public void InvalidJsonFallsBackToDefault(string json) =>
        Assert.Same(LauncherSettings.Default, LauncherSettingsStore.Deserialize(json));
}

public class WinKeyGestureTests
{
    private const int Win = WinKeyGesture.LeftWin;
    private const int E = 0x45;

    [Fact]
    public void WinAloneOpensLauncherOnRelease()
    {
        var g = new WinKeyGesture();
        Assert.Equal(KeyDecision.Pass, g.OnKey(Win, down: true, injected: false));
        Assert.Equal(KeyDecision.OpenLauncher, g.OnKey(Win, down: false, injected: false));
    }

    [Fact]
    public void WinChordPassesThrough()
    {
        var g = new WinKeyGesture();
        g.OnKey(Win, true, false);
        g.OnKey(E, true, false);
        g.OnKey(E, false, false);
        Assert.Equal(KeyDecision.Pass, g.OnKey(Win, false, false));
    }

    [Fact]
    public void AutorepeatDoesNotResetChord()
    {
        var g = new WinKeyGesture();
        g.OnKey(Win, true, false);
        g.OnKey(E, true, false);
        g.OnKey(Win, true, false); // autorepeat
        Assert.Equal(KeyDecision.Pass, g.OnKey(Win, false, false));
    }

    [Fact]
    public void WinClickIsAChord()
    {
        var g = new WinKeyGesture();
        g.OnKey(Win, true, false);
        g.OnMouseButton();
        Assert.Equal(KeyDecision.Pass, g.OnKey(Win, false, false));
    }

    [Fact]
    public void InjectedAndDisabledArePassed()
    {
        var g = new WinKeyGesture();
        g.OnKey(Win, true, injected: true);
        Assert.Equal(KeyDecision.Pass, g.OnKey(Win, false, injected: true));

        g.Enabled = false;
        g.OnKey(Win, true, false);
        Assert.Equal(KeyDecision.Pass, g.OnKey(Win, false, false));
    }

    [Fact]
    public void OtherKeysAloneAreIgnored()
    {
        var g = new WinKeyGesture();
        Assert.Equal(KeyDecision.Pass, g.OnKey(E, false, false));
        Assert.Equal(KeyDecision.Pass, g.OnKey(Win, false, false)); // rilascio senza pressione vista
    }
}

public class UnpremultiplyTests
{
    [Fact]
    public void RestoresColorFromPremultiplied()
    {
        byte[] px = [50, 100, 0, 128, 10, 20, 30, 255];
        Monochrome.Unpremultiply(px);
        Assert.Equal([99, 199, 0, 128, 10, 20, 30, 255], px);
    }

    [Fact]
    public void BitmapWithoutAlphaBecomesOpaque()
    {
        byte[] px = [10, 20, 30, 0, 40, 50, 60, 0];
        Monochrome.Unpremultiply(px);
        Assert.Equal(255, px[3]);
        Assert.Equal(255, px[7]);
        Assert.Equal(10, px[0]);
    }
}
