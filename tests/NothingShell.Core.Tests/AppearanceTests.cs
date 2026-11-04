using NothingShell.Core.Design;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Notch;
using NothingShell.Core.Settings;
using Xunit;

namespace NothingShell.Core.Tests;

public class AppearanceTests
{
    private static readonly Argb WindowsBlue = Argb.FromRgb(0x0078D4);

    public static TheoryData<string> Accents()
    {
        var data = new TheoryData<string>();
        foreach (var (_, hex) in AppearanceChoices.Accents)
            data.Add(hex);
        // Scelte a mano difficili: quasi nero, quasi bianco, giallo puro, grigio medio.
        foreach (var hex in new[] { "#000000", "#FFFFFF", "#FFFF00", "#808080", "#010203" })
            data.Add(hex);
        return data;
    }

    [Theory]
    [MemberData(nameof(Accents))]
    public void AccentIsReadableOnEveryThemeAndTone(string hex)
    {
        foreach (var dark in new[] { true, false })
            foreach (var tone in Enum.GetValues<SurfaceTone>())
            {
                var palette = Palette.Create(dark, Argb.TryParse(hex)!.Value, tone);
                Assert.True(palette.Accent.ContrastWith(palette.BaseSolid) >= 4.5, $"accento {hex} dark={dark} {tone}");
                Assert.True(palette.OnAccent.ContrastWith(palette.Accent) >= 4.5, $"testo su accento {hex} dark={dark} {tone}");
            }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TokensKeepTextReadable(bool dark)
    {
        foreach (var tone in Enum.GetValues<SurfaceTone>())
        {
            var palette = Palette.Create(dark, WindowsBlue, tone);
            Assert.True(palette.Ink.ContrastWith(palette.BaseSolid) >= 7);
            Assert.True(palette.Muted.ContrastWith(palette.BaseSolid) >= 4.5);
            Assert.True(palette.Critical.ContrastWith(palette.BaseSolid) >= 4.5);
        }
    }

    [Fact]
    public void GlassMakesOnlyTheSurfaceTranslucent()
    {
        var solid = Palette.Create(true, WindowsBlue);
        var glass = Palette.Create(true, WindowsBlue, glass: 0.5);
        Assert.Equal(255, solid.Base.A);
        Assert.Equal(solid.Base, solid.SheenTop);
        Assert.Equal(solid.Outline, solid.RimTop);
        Assert.False(solid.IsGlass);
        Assert.True(glass.IsGlass);
        Assert.InRange((int)glass.Base.A, 80, 240);
        Assert.Equal(255, glass.BaseSolid.A);
        Assert.Equal(solid.Ink, glass.Ink);
        Assert.Equal(solid.Accent, glass.Accent);
    }

    [Fact]
    public void GlassStrengthClearerIsMoreTransparentAndClamped()
    {
        Assert.True(Palette.Create(true, WindowsBlue, glass: 1).Base.A < Palette.Create(true, WindowsBlue, glass: 0).Base.A);
        Assert.Equal(Palette.Create(false, WindowsBlue, glass: 1).Base, Palette.Create(false, WindowsBlue, glass: 7).Base);
    }

    [Theory]
    [InlineData(AppTheme.Light, true, false)]
    [InlineData(AppTheme.Dark, false, true)]
    [InlineData(AppTheme.System, true, true)]
    [InlineData(AppTheme.System, false, false)]
    public void ThemeFollowsChoiceOrSystem(AppTheme theme, bool systemDark, bool expected) =>
        Assert.Equal(expected, new AppearanceSettings { Theme = theme }.ToPalette(systemDark, WindowsBlue).IsDark);

    [Fact]
    public void AccentNullOrInvalidFollowsWindows()
    {
        Assert.Equal(WindowsBlue, AppearanceSettings.Default.ResolveAccent(WindowsBlue));
        Assert.Equal(WindowsBlue, new AppearanceSettings { Accent = "viola" }.ResolveAccent(WindowsBlue));
        Assert.Equal(Argb.FromRgb(0xE3008C), new AppearanceSettings { Accent = "#E3008C" }.ResolveAccent(WindowsBlue));
    }

    [Fact]
    public void GlassOffWhenWindowsTransparencyIsOff()
    {
        var look = new AppearanceSettings { Glass = true };
        Assert.True(look.ToPalette(true, WindowsBlue).IsGlass);
        Assert.False(look.ToPalette(true, WindowsBlue, systemTransparency: false).IsGlass);
        Assert.False((look with { Glass = false }).ToPalette(true, WindowsBlue).IsGlass);
    }

    [Theory]
    [InlineData("#3A7BD5", 0x3A7BD5u)]
    [InlineData("3a7bd5", 0x3A7BD5u)]
    [InlineData(" #000000 ", 0x000000u)]
    public void ParseAcceptsHex(string text, uint expected) => Assert.Equal(Argb.FromRgb(expected), Argb.TryParse(text));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("#11223344")]
    public void ParseRejectsAnythingElse(string? text) => Assert.Null(Argb.TryParse(text));

    [Fact]
    public void HexRoundTrips() => Assert.Equal("#0078D4", WindowsBlue.ToHex());

    [Fact]
    public void ContrastMatchesWcagReferences()
    {
        Assert.Equal(21, Argb.FromRgb(0).ContrastWith(Argb.FromRgb(0xFFFFFF)), 3);
        Assert.Equal(1, WindowsBlue.ContrastWith(WindowsBlue), 3);
    }

    [Fact]
    public void SettingsRoundTripThroughTheStore()
    {
        var look = new AppearanceSettings
        {
            Theme = AppTheme.Light,
            Accent = "#E3008C",
            Tone = SurfaceTone.Tinted,
            Glass = false,
            GlassStrength = 0.25,
            Display = DisplayStyle.Dots,
            QuietNotch = true,
            NotchStyle = NotchStyle.Attached,
            NotchSize = NotchSize.Large,
            NotchMascot = true,
            NotchShadow = 0.8,
            NotchOpenDelayMs = 0,
            NotchCloseDelayMs = 700,
            NotchScreen = NotchScreen.Chosen,
            NotchMonitor = @"\\.\DISPLAY2",
            NotchFeel = NotchFeel.Snappy,
            AnimationSpeed = 1.25,
        };
        var json = ShellSettingsStore.Serialize(ShellSettings.Default with { Appearance = look });
        Assert.Equal(look, ShellSettingsStore.Deserialize(json).Appearance);
    }

    [Fact]
    public void PresetsKeepTheNotchChoices()
    {
        var mine = new AppearanceSettings
        {
            NotchStyle = NotchStyle.Attached,
            NotchMascot = true,
            NotchShadow = 0.2,
            NotchSize = NotchSize.Large,
            NotchOpenDelayMs = 600,
            NotchCloseDelayMs = 300,
            NotchScreen = NotchScreen.Mouse,
            NotchFeel = NotchFeel.Soft,
            AnimationSpeed = 0.75,
        };
        var nothing = AppearanceChoices.Presets[2].Settings.WithNotchOf(mine);
        Assert.Equal(mine.NotchOpenDelayMs, nothing.NotchOpenDelayMs);
        Assert.Equal(mine.NotchCloseDelayMs, nothing.NotchCloseDelayMs);
        Assert.Equal(mine.NotchScreen, nothing.NotchScreen);
        Assert.Equal(mine.NotchFeel, nothing.NotchFeel);
        Assert.Equal(0.75, nothing.AnimationSpeed);
        Assert.Equal(DisplayStyle.Dots, nothing.Display);
        Assert.Equal(mine.NotchStyle, nothing.NotchStyle);
        Assert.Equal(mine.NotchSize, nothing.NotchSize);
        Assert.True(nothing.NotchMascot);
        Assert.Equal(0.2, nothing.NotchShadow);
    }

    [Fact]
    public void NotchDelaysDefaultToNusaAndStayInRange()
    {
        Assert.Equal(NotchMotion.EnterDelay, AppearanceSettings.Default.NotchOpenDelay);
        Assert.Equal(NotchStateMachine.LeaveDelay, AppearanceSettings.Default.NotchCloseDelay);
        Assert.Equal(TimeSpan.Zero, new AppearanceSettings { NotchOpenDelayMs = -5 }.NotchOpenDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(AppearanceSettings.MaxNotchDelayMs), new AppearanceSettings { NotchCloseDelayMs = 99999 }.NotchCloseDelay);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(0.1, AppearanceSettings.MinAnimationSpeed)]
    [InlineData(9, AppearanceSettings.MaxAnimationSpeed)]
    [InlineData(double.NaN, 1)]
    public void AnimationSpeedStaysInRange(double speed, double expected) =>
        Assert.Equal(expected, new AppearanceSettings { AnimationSpeed = speed }.MotionSpeed);

    [Fact]
    public void OldSettingsFileGetsTheDefaultLook() =>
        Assert.Null(ShellSettingsStore.Deserialize("""{"notch":true,"bar":true,"disabledWidgets":[]}""").Appearance);

    [Fact]
    public void DefaultLooksLikeWindows()
    {
        var look = AppearanceSettings.Default;
        Assert.Equal(AppTheme.System, look.Theme);
        Assert.Null(look.Accent);
        Assert.True(look.Glass);
        Assert.Equal(DisplayStyle.Windows, look.Display);
    }

    [Fact]
    public void PresetsHaveDistinctSettings() =>
        Assert.Equal(AppearanceChoices.Presets.Count, AppearanceChoices.Presets.Select(p => p.Settings).Distinct().Count());

    [Theory]
    [InlineData("BATTERIA 82%", "Batteria 82%")]
    [InlineData("NON COLLEGATO", "Non collegato")]
    [InlineData("TEMPO SCADUTO", "Tempo scaduto")]
    [InlineData("domenica", "domenica")]
    [InlineData("Casa-5G", "Casa-5G")]
    [InlineData("22:52", "22:52")]
    [InlineData("CPU", "CPU")]
    [InlineData("CI", "CI")]
    [InlineData("42%", "42%")]
    [InlineData("", "")]
    public void FriendlyDropsShoutingOnly(string text, string expected) => Assert.Equal(expected, DotText.Friendly(text));

    [Fact]
    public void GridsRememberWhatTheyDraw()
    {
        Assert.Equal("CIAO", DotText.Render("CIAO").Text);
        Assert.Equal("CIAO", DotText.RenderMarquee("CIAO", 4, 0).Text);
        Assert.Same(DotIcons.Mail, DotGrid.From(DotIcons.Mail).Glyph);
        Assert.Equal(0.5, DotBar.Render(0.5, 16).Fraction);
        Assert.Equal(1, DotBar.Render(9, 16).Fraction);
        Assert.Equal(0, DotBar.Render(double.NaN, 16).Fraction);
        Assert.Null(DotText.Render("A").Glyph);
        Assert.Null(DotBar.Signal(2).Fraction);
    }

    [Fact]
    public void QuietNotchFitsAndStaysSmallerThanIdle()
    {
        var quiet = NotchLayout.Quiet;
        var idle = NotchLayout.For(NotchState.Idle);
        Assert.True(quiet.Width < idle.Width && quiet.Height < idle.Height);
        Assert.Equal(quiet.Height / 2, NotchLayout.CornerRadius(quiet));
    }
}
