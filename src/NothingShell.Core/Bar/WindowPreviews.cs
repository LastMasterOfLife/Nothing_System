using NothingShell.Core.Notch;

namespace NothingShell.Core.Bar;

/// <summary>Anteprime delle finestre di un'app al passaggio del mouse sulla barra (docs/05-bar.md).</summary>
public static class WindowPreviews
{
    /// <summary>Area massima di una miniatura (DIP).</summary>
    public static readonly Size2 MaxThumbnail = new(240, 150);

    /// <summary>Sotto questa larghezza le miniature non si leggono più: si rinuncia ad aggiungerne.</summary>
    public const double MinThumbnailWidth = 120;

    /// <summary>
    /// Dimensione della miniatura di ogni finestra: proporzioni della finestra, dentro <see cref="MaxThumbnail"/>;
    /// se in fila non stanno in <paramref name="available"/> (con <paramref name="gap"/> fra l'una e l'altra)
    /// si rimpiccioliscono tutte insieme, fino a <see cref="MinThumbnailWidth"/>.
    /// </summary>
    public static IReadOnlyList<Size2> Fit(IReadOnlyList<Size2> sources, double available, double gap)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var sizes = sources.Select(s => Contain(s, MaxThumbnail)).ToArray();
        var total = sizes.Sum(s => s.Width) + gap * Math.Max(0, sizes.Length - 1);
        if (sizes.Length == 0 || total <= available)
            return sizes;

        var widths = sizes.Sum(s => s.Width);
        var scale = Math.Max((available - gap * (sizes.Length - 1)) / widths, MinThumbnailWidth / sizes.Max(s => s.Width));
        return sizes.Select(s => new Size2(Math.Round(s.Width * scale), Math.Round(s.Height * scale))).ToArray();
    }

    /// <summary>Rettangolo con le proporzioni di <paramref name="source"/> dentro <paramref name="box"/>.</summary>
    public static Size2 Contain(Size2 source, Size2 box)
    {
        if (source.Width <= 0 || source.Height <= 0)
            return box;
        var scale = Math.Min(box.Width / source.Width, box.Height / source.Height);
        return new Size2(Math.Round(source.Width * scale), Math.Round(source.Height * scale));
    }
}
