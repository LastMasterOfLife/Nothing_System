namespace NothingShell.Core.Design;

/// <summary>Colore ARGB indipendente dal framework grafico.</summary>
public readonly record struct Argb(byte A, byte R, byte G, byte B)
{
    public static Argb FromRgb(uint rgb, byte alpha = 0xFF) =>
        new(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public Argb WithAlpha(double opacity) => this with { A = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255) };
}

/// <summary>Token colore del design system (docs/02-design-system.md).</summary>
public static class Palette
{
    public static readonly Argb Black = Argb.FromRgb(0x000000);
    public static readonly Argb Surface = Argb.FromRgb(0x111111);
    public static readonly Argb White = Argb.FromRgb(0xFFFFFF);
    public static readonly Argb DotOff = White.WithAlpha(0.12);
    public static readonly Argb Grey = Argb.FromRgb(0x8A8A8A);
    public static readonly Argb Red = Argb.FromRgb(0xD71921);
    public static readonly Argb Outline = Argb.FromRgb(0x262626);
}
