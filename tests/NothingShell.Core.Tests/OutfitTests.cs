using NothingShell.Core.Design;
using NothingShell.Core.Settings;
using Xunit;

namespace NothingShell.Core.Tests;

public class OutfitTests
{
    private static readonly Argb WindowsBlue = Argb.FromRgb(0x0078D4);
    private static readonly Argb DarkInk = Argb.FromRgb(0x1B1B1B);
    private static readonly Argb LightInk = Argb.FromRgb(0xFFFFFF);
    private static readonly uint[] HardColors = [0x010203u, 0xFFFF00u, 0xFF00FFu, 0x3A7BD5u, 0x7F7F80u];

    [Theory]
    [InlineData(0xFF0000u, 0, 1, 1)]
    [InlineData(0x00FF00u, 120, 1, 1)]
    [InlineData(0x0000FFu, 240, 1, 1)]
    [InlineData(0xFFFFFFu, 0, 0, 1)]
    [InlineData(0x000000u, 0, 0, 0)]
    [InlineData(0x808080u, 0, 0, 0.502)]
    public void HsvReadsKnownColors(uint rgb, double h, double s, double v)
    {
        var hsv = Hsv.From(Argb.FromRgb(rgb));
        Assert.Equal(h, hsv.H, 1);
        Assert.Equal(s, hsv.S, 2);
        Assert.Equal(v, hsv.V, 2);
    }

    [Fact]
    public void HsvRoundTripsEveryAccentAndSomeHardOnes()
    {
        var colors = AppearanceChoices.Accents.Select(a => Argb.TryParse(a.Hex)!.Value).Concat(HardColors.Select(c => Argb.FromRgb(c)));
        foreach (var color in colors)
            Assert.Equal(color, Hsv.From(color).ToArgb());
    }

    [Fact]
    public void HsvWrapsHueAndClampsTheRest()
    {
        Assert.Equal(Argb.FromRgb(0xFF0000), new Hsv(360, 1, 1).ToArgb());
        Assert.Equal(Argb.FromRgb(0x0000FF), new Hsv(-120, 1, 1).ToArgb());
        Assert.Equal(Argb.FromRgb(0xFFFFFF), new Hsv(10, -3, 9).ToArgb());
        Assert.Equal(Argb.FromRgb(0x000000), new Hsv(double.NaN, double.NaN, double.NaN).ToArgb());
    }

    public static TheoryData<string> Outfits()
    {
        var data = new TheoryData<string>();
        foreach (var outfit in OutfitCatalog.BuiltIn)
            data.Add(outfit.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(Outfits))]
    public void BuiltInOutfitsKeepTheNotchReadableInBothThemes(string id)
    {
        var outfit = OutfitCatalog.BuiltIn.Single(o => o.Id == id);
        foreach (var ink in new[] { LightInk, DarkInk })
        {
            var colors = outfit.Resolve(ink);
            Assert.True(colors.Fill.ContrastWith(ink) >= NotchOutfit.FillMinContrast, $"{id} fondo");
            Assert.True(colors.Fill2.ContrastWith(ink) >= NotchOutfit.FillMinContrast, $"{id} sfumatura");
            Assert.True(colors.Pattern.ContrastWith(ink) >= NotchOutfit.PatternMinContrast, $"{id} motivo");
        }
    }

    [Theory]
    [InlineData("#FFFFFF")]
    [InlineData("#FFFF00")]
    [InlineData("#000000")]
    [InlineData("#808080")]
    [InlineData("non è un colore")]
    public void HandMadeOutfitsAreReadableWhateverTheColor(string fill)
    {
        var outfit = new NotchOutfit { Fill = fill, Fill2 = fill, Gradient = OutfitGradient.Diagonal, PatternColor = fill, Pattern = OutfitPattern.Dots };
        foreach (var dark in new[] { true, false })
        {
            var palette = Palette.Create(dark, WindowsBlue);
            var ink = palette.Ink;
            var colors = outfit.Resolve(ink, palette.Muted);
            Assert.True(colors.Fill.ContrastWith(palette.Muted) >= NotchOutfit.MutedMinContrast);
            Assert.True(colors.Fill2.ContrastWith(palette.Muted) >= NotchOutfit.MutedMinContrast);
            Assert.True(colors.Fill.ContrastWith(ink) >= NotchOutfit.FillMinContrast);
            Assert.True(colors.Fill2.ContrastWith(ink) >= NotchOutfit.FillMinContrast);
            Assert.True(colors.Pattern.ContrastWith(ink) >= NotchOutfit.PatternMinContrast);
        }
    }

    [Fact]
    public void SolidOutfitIgnoresTheSecondColor()
    {
        var colors = new NotchOutfit { Fill = "#102030", Fill2 = "#FF0000" }.Resolve(LightInk);
        Assert.Equal(colors.Fill, colors.Fill2);
    }

    [Fact]
    public void GradientWithoutSecondColorGoesDarker()
    {
        var colors = new NotchOutfit { Fill = "#2060A0", Gradient = OutfitGradient.Vertical }.Resolve(LightInk);
        Assert.True(colors.Fill2.Luminance < colors.Fill.Luminance);
    }

    [Fact]
    public void AlreadyReadableColorsStayAsChosen()
    {
        var colors = new NotchOutfit { Fill = "#0B1033", Rim = "#2E8B57" }.Resolve(LightInk);
        Assert.Equal(Argb.FromRgb(0x0B1033), colors.Fill);
        Assert.Equal(Argb.FromRgb(0x2E8B57), colors.Rim);
        Assert.Null(new NotchOutfit().Resolve(LightInk).Rim);
    }

    [Theory]
    [InlineData(-1, 0, NotchOutfit.MinCoverage)]
    [InlineData(5, 5, 1)]
    [InlineData(double.NaN, double.NaN, 1)]
    public void StrengthsStayInRange(double pattern, double coverage, double expectedCoverage)
    {
        var outfit = new NotchOutfit { PatternOpacity = pattern, Coverage = coverage };
        Assert.InRange(outfit.PatternStrength, 0, NotchOutfit.MaxPatternOpacity);
        Assert.Equal(expectedCoverage, outfit.CoverageAmount);
    }

    [Fact]
    public void BuiltInIdsAreUniqueAndNotCustom()
    {
        Assert.Equal(OutfitCatalog.BuiltIn.Count, OutfitCatalog.BuiltIn.Select(o => o.Id).Distinct().Count());
        Assert.All(OutfitCatalog.BuiltIn, o => Assert.False(o.IsCustom));
        Assert.True(OutfitCatalog.Copy(OutfitCatalog.BuiltIn[0]).IsCustom);
        Assert.NotEqual(NotchOutfit.NewId(), NotchOutfit.NewId());
    }

    [Fact]
    public void WardrobeSavesReplacesAndRemoves()
    {
        var mine = OutfitCatalog.Copy(OutfitCatalog.BuiltIn[1]);
        Assert.EndsWith("(mio)", mine.Name, StringComparison.Ordinal);
        var wardrobe = OutfitCatalog.Save(null, mine);
        Assert.Single(wardrobe);
        wardrobe = OutfitCatalog.Save(wardrobe, mine with { Name = "Il mio Natale" });
        Assert.Equal("Il mio Natale", Assert.Single(wardrobe).Name);
        Assert.Equal(OutfitCatalog.BuiltIn.Count + 1, OutfitCatalog.Wardrobe(wardrobe).Count);
        Assert.Empty(OutfitCatalog.Remove(wardrobe, mine.Id));
    }

    [Fact]
    public void OutfitAndWardrobeRoundTripThroughTheStore()
    {
        var mine = OutfitCatalog.Copy(OutfitCatalog.BuiltIn[0]) with { Coverage = 0.7, Pattern = OutfitPattern.Hearts };
        var look = new AppearanceSettings { Tone = SurfaceTone.Custom, Surface = "#14283C", NotchOutfit = mine };
        var json = ShellSettingsStore.Serialize(ShellSettings.Default with { Appearance = look, Wardrobe = [mine] });
        var back = ShellSettingsStore.Deserialize(json);
        Assert.Equal(look, back.Appearance);
        Assert.Equal(mine, Assert.Single(back.Wardrobe!));
    }

    [Fact]
    public void PresetsKeepTheOutfit()
    {
        var mine = new AppearanceSettings { NotchOutfit = OutfitCatalog.BuiltIn[2] };
        Assert.Equal(OutfitCatalog.BuiltIn[2], AppearanceChoices.Presets[2].Settings.WithNotchOf(mine).NotchOutfit);
    }

    [Theory]
    [InlineData("#FFFFFF")]
    [InlineData("#FFFF00")]
    [InlineData("#000000")]
    [InlineData("#808080")]
    [InlineData("#14283C")]
    [InlineData("#F5E6C8")]
    public void CustomSurfaceKeepsTextReadable(string hex)
    {
        foreach (var dark in new[] { true, false })
        {
            var palette = new AppearanceSettings { Theme = dark ? AppTheme.Dark : AppTheme.Light, Tone = SurfaceTone.Custom, Surface = hex }
                .ToPalette(dark, WindowsBlue);
            Assert.True(palette.Ink.ContrastWith(palette.BaseSolid) >= 7, $"{hex} dark={dark} testo");
            Assert.True(palette.Muted.ContrastWith(palette.BaseSolid) >= 4.5, $"{hex} dark={dark} secondario");
            Assert.True(palette.Accent.ContrastWith(palette.BaseSolid) >= 4.5, $"{hex} dark={dark} accento");
            Assert.True(palette.Critical.ContrastWith(palette.BaseSolid) >= 4.5, $"{hex} dark={dark} critico");
        }
    }

    [Fact]
    public void CustomSurfaceKeepsAReadableChoiceAsIs()
    {
        var palette = new AppearanceSettings { Tone = SurfaceTone.Custom, Surface = "#14283C" }.ToPalette(true, WindowsBlue);
        Assert.Equal(Argb.FromRgb(0x14283C), palette.BaseSolid);
    }

    [Fact]
    public void CustomSurfaceWithoutColorIsNeutral() =>
        Assert.Equal(Palette.Create(true, WindowsBlue).BaseSolid, new AppearanceSettings { Tone = SurfaceTone.Custom }.ToPalette(true, WindowsBlue).BaseSolid);
}
