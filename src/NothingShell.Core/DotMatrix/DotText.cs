using NothingShell.Core.Design;

namespace NothingShell.Core.DotMatrix;

/// <summary>Layout di testo nel font a punti.</summary>
public static class DotText
{
    public const int LetterSpacing = 1;

    /// <summary>Colonne vuote fra la fine del testo che scorre e il suo nuovo inizio.</summary>
    public const int MarqueeGap = 12;

    public static int Measure(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
            return 0;
        return text.Sum(c => DotFont.Get(c).Width) + LetterSpacing * (text.Length - 1);
    }

    public static DotGrid Render(string text)
    {
        var grid = new DotGrid(Measure(text), DotFont.Height);
        var x = 0;
        foreach (var c in text)
        {
            var glyph = DotFont.Get(c);
            grid.Draw(glyph, x, 0);
            x += glyph.Width + LetterSpacing;
        }
        return grid;
    }

    /// <summary>
    /// Finestra larga <paramref name="viewportWidth"/> colonne sul testo che scorre a ciclo continuo.
    /// Se il testo entra nella finestra non scorre e resta allineato a sinistra.
    /// </summary>
    public static DotGrid RenderMarquee(string text, int viewportWidth, int offset)
    {
        var source = Render(text);
        var view = new DotGrid(viewportWidth, DotFont.Height);
        if (source.Width <= viewportWidth)
        {
            view.Draw(source, 0, 0);
            return view;
        }

        var period = source.Width + MarqueeGap;
        var start = -(((offset % period) + period) % period);
        view.Draw(source, start, 0);
        view.Draw(source, start + period, 0);
        return view;
    }

    public static bool NeedsMarquee(string text, int viewportWidth) => Measure(text) > viewportWidth;
}
