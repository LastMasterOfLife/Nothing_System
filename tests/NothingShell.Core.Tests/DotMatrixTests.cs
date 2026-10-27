using NothingShell.Core.Design;
using NothingShell.Core.DotMatrix;

namespace NothingShell.Core.Tests;

public class DotFontTests
{
    [Theory]
    [InlineData("0123456789")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ")]
    [InlineData(" :.,!'\"-_+()/&?")]
    public void EveryGlyphHasFontHeight(string chars)
    {
        foreach (var c in chars)
        {
            Assert.True(DotFont.Supports(c), $"'{c}' non supportato");
            Assert.Equal(DotFont.Height, DotFont.Get(c).Height);
        }
    }

    [Fact]
    public void LowercaseMapsToUppercase() => Assert.Same(DotFont.Get('A'), DotFont.Get('a'));

    [Fact]
    public void AccentsAreStripped() => Assert.Same(DotFont.Get('E'), DotFont.Get('è'));

    [Fact]
    public void UnknownCharFallsBackToQuestionMark() => Assert.Same(DotFont.Get('?'), DotFont.Get('€'));

    [Fact]
    public void GlyphRejectsRaggedRows() =>
        Assert.Throws<ArgumentException>(() => new DotGlyph("##", "#"));
}

public class DotTextTests
{
    [Fact]
    public void MeasureSumsGlyphWidthsAndSpacing()
    {
        // 4 cifre da 5 + ':' da 1 + 4 spazi fra glifi
        Assert.Equal(4 * 5 + 1 + 4, DotText.Measure("12:34"));
    }

    [Fact]
    public void MeasureEmptyIsZero() => Assert.Equal(0, DotText.Measure(""));

    [Fact]
    public void RenderDrawsGlyphPixels()
    {
        var grid = DotText.Render("1");
        Assert.Equal(
            "..#..\n.##..\n..#..\n..#..\n..#..\n..#..\n.###.",
            grid.ToString());
    }

    [Fact]
    public void RenderLeavesSpacingColumnEmpty()
    {
        var grid = DotText.Render("HH");
        for (var y = 0; y < grid.Height; y++)
            Assert.Equal(0f, grid[5, y]);
    }

    [Fact]
    public void ShortTextDoesNotScroll()
    {
        var a = DotText.RenderMarquee("HI", 40, 0).ToString();
        var b = DotText.RenderMarquee("HI", 40, 17).ToString();
        Assert.Equal(a, b);
    }

    [Fact]
    public void MarqueeIsPeriodic()
    {
        const string text = "UNA CANZONE CON UN TITOLO LUNGO";
        var period = DotText.Measure(text) + DotText.MarqueeGap;
        Assert.Equal(
            DotText.RenderMarquee(text, 30, 3).ToString(),
            DotText.RenderMarquee(text, 30, 3 + period).ToString());
        Assert.NotEqual(
            DotText.RenderMarquee(text, 30, 0).ToString(),
            DotText.RenderMarquee(text, 30, 1).ToString());
    }

    [Fact]
    public void MarqueeHandlesNegativeOffset()
    {
        const string text = "UNA CANZONE CON UN TITOLO LUNGO";
        var period = DotText.Measure(text) + DotText.MarqueeGap;
        Assert.Equal(
            DotText.RenderMarquee(text, 30, period - 1).ToString(),
            DotText.RenderMarquee(text, 30, -1).ToString());
    }
}

public class DotGridTests
{
    [Fact]
    public void OutOfBoundsWritesAreIgnored()
    {
        var grid = new DotGrid(2, 2);
        grid[5, 5] = 1f;
        grid[-1, 0] = 1f;
        Assert.Equal(0, grid.LitCount);
        Assert.Equal(0f, grid[9, 9]);
    }

    [Fact]
    public void IntensityIsClamped()
    {
        var grid = new DotGrid(1, 1) { [0, 0] = 3f };
        Assert.Equal(1f, grid[0, 0]);
    }

    [Fact]
    public void DrawGridKeepsMaxIntensity()
    {
        var target = new DotGrid(2, 1) { [0, 0] = 0.8f };
        var source = new DotGrid(2, 1) { [0, 0] = 0.3f, [1, 0] = 0.5f };
        target.Draw(source, 0, 0);
        Assert.Equal(0.8f, target[0, 0]);
        Assert.Equal(0.5f, target[1, 0]);
    }
}
