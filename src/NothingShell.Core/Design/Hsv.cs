namespace NothingShell.Core.Design;

/// <summary>
/// Colore come lo sceglie il selettore della pagina Aspetto (alla Figma): tinta <see cref="H"/> da 0 a 360 sulla banda,
/// saturazione <see cref="S"/> e luminosità <see cref="V"/> da 0 a 1 nel quadrato.
/// </summary>
public readonly record struct Hsv(double H, double S, double V)
{
    public static Hsv From(Argb color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        double hue = 0;
        if (delta > 0)
        {
            hue = max == r ? 60 * (((g - b) / delta) % 6)
                : max == g ? 60 * (((b - r) / delta) + 2)
                : 60 * (((r - g) / delta) + 4);
            if (hue < 0)
                hue += 360;
        }
        return new Hsv(hue, max == 0 ? 0 : delta / max, max);
    }

    /// <summary>Il colore pieno; la tinta gira (360 è di nuovo il rosso), saturazione e luminosità stanno nei limiti.</summary>
    public Argb ToArgb()
    {
        var h = double.IsFinite(H) ? ((H % 360) + 360) % 360 : 0;
        var s = double.IsFinite(S) ? Math.Clamp(S, 0, 1) : 0;
        var v = double.IsFinite(V) ? Math.Clamp(V, 0, 1) : 0;
        var c = v * s;
        var x = c * (1 - Math.Abs(((h / 60) % 2) - 1));
        var m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return new Argb(0xFF, Channel(r + m), Channel(g + m), Channel(b + m));
    }

    private static byte Channel(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
}
