using System.Globalization;

namespace NothingShell.Core.Design;

/// <summary>Colore ARGB indipendente dal framework grafico.</summary>
public readonly record struct Argb(byte A, byte R, byte G, byte B)
{
    public static Argb FromRgb(uint rgb, byte alpha = 0xFF) =>
        new(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>Legge <c>#RRGGBB</c> o <c>RRGGBB</c>; null se non è un colore (l'alfa non si sceglie).</summary>
    public static Argb? TryParse(string? text)
    {
        var hex = text?.Trim().TrimStart('#');
        return hex is { Length: 6 } && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
            ? FromRgb(rgb)
            : null;
    }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public Argb WithAlpha(double opacity) => this with { A = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255) };

    /// <summary>Verso <paramref name="other"/> di una frazione <paramref name="amount"/>; l'alfa resta.</summary>
    public Argb Mix(Argb other, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return this with { R = Lerp(R, other.R, t), G = Lerp(G, other.G, t), B = Lerp(B, other.B, t) };
    }

    /// <summary>Luminanza relativa WCAG 2.x, ignorando l'alfa.</summary>
    public double Luminance => (0.2126 * Linear(R)) + (0.7152 * Linear(G)) + (0.0722 * Linear(B));

    /// <summary>Rapporto di contrasto WCAG (da 1 a 21).</summary>
    public double ContrastWith(Argb other)
    {
        var (a, b) = (Luminance, other.Luminance);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static byte Lerp(byte from, byte to, double t) => (byte)Math.Round(from + ((to - from) * t));

    private static double Linear(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}

/// <summary>
/// Token colore del design system (docs/02-design-system.md), calcolati dalle scelte della pagina Aspetto: tema, accento,
/// tono dello sfondo e vetro. Accento e critico sono regolati sul tema così restano leggibili anche con colori scelti a mano.
/// </summary>
public sealed record Palette
{
    private const double AccentMinContrast = 4.5;
    private static readonly Argb White = Argb.FromRgb(0xFFFFFF);
    private static readonly Argb Black = Argb.FromRgb(0x000000);

    private Palette()
    {
    }

    public bool IsDark { get; private init; }

    /// <summary>Vetro acceso: le superfici lasciano passare ciò che sta dietro.</summary>
    public bool IsGlass { get; private init; }

    /// <summary>Fondo pieno delle superfici principali (finestre che non possono avere il vetro).</summary>
    public Argb BaseSolid { get; private init; }

    /// <summary>Fondo delle superfici principali: col vetro è traslucido.</summary>
    public Argb Base { get; private init; }

    /// <summary>Riempimento tenue: passaggio del mouse, campi, elementi secondari.</summary>
    public Argb Surface { get; private init; }

    /// <summary>Riempimento al passaggio del mouse su un elemento già riempito.</summary>
    public Argb SurfaceHover { get; private init; }

    /// <summary>Scheda appoggiata su una superficie.</summary>
    public Argb Card { get; private init; }

    public Argb Ink { get; private init; }

    public Argb Muted { get; private init; }

    public Argb Accent { get; private init; }

    /// <summary>Testo e icone sopra un riempimento <see cref="Accent"/>.</summary>
    public Argb OnAccent { get; private init; }

    /// <summary>Errori, pericoli, avvisi: resta rosso qualunque accento si scelga.</summary>
    public Argb Critical { get; private init; }

    public Argb DotOff { get; private init; }

    public Argb Outline { get; private init; }

    /// <summary>Bordo alto delle superfici: col vetro cattura la luce, senza vetro coincide con <see cref="Outline"/>.</summary>
    public Argb RimTop { get; private init; }

    public Argb RimBottom { get; private init; }

    /// <summary>Velo in alto sulle superfici di vetro; senza vetro è uguale a <see cref="Base"/>.</summary>
    public Argb SheenTop { get; private init; }

    public static Palette Create(bool dark, Argb accent, SurfaceTone tone = SurfaceTone.Neutral, double? glass = null)
    {
        var neutral = Argb.FromRgb(dark ? 0x202020u : 0xF3F3F3u);
        var solid = tone switch
        {
            SurfaceTone.Tinted => neutral.Mix(accent, dark ? 0.12 : 0.10),
            SurfaceTone.Deep => dark ? Black : White,
            _ => neutral,
        };
        var ink = Argb.FromRgb(dark ? 0xFFFFFFu : 0x1B1B1Bu);
        var outline = dark ? White.WithAlpha(0.10) : Black.WithAlpha(0.10);
        var strength = glass is { } g ? Math.Clamp(g, 0, 1) : 0;
        // Più trasparenza, meno velo: oltre questi limiti il testo non si legge più su uno sfondo qualunque.
        var alpha = glass is null ? 1 : dark ? 0.88 - (0.28 * strength) : 0.92 - (0.26 * strength);
        var based = solid.WithAlpha(alpha);
        var adjusted = Readable(accent.WithAlpha(1), solid, AccentMinContrast);
        return new Palette
        {
            IsDark = dark,
            IsGlass = glass is not null,
            BaseSolid = solid,
            Base = based,
            Surface = dark ? White.WithAlpha(0.07) : Black.WithAlpha(0.05),
            SurfaceHover = dark ? White.WithAlpha(0.12) : Black.WithAlpha(0.09),
            Card = dark ? White.WithAlpha(0.055) : White.WithAlpha(0.70),
            Ink = ink,
            Muted = Argb.FromRgb(dark ? 0xC8C8C8u : 0x505050u),
            Accent = adjusted,
            OnAccent = adjusted.ContrastWith(White) >= adjusted.ContrastWith(Black) ? White : Black,
            Critical = Readable(Argb.FromRgb(dark ? 0xFF6B6Bu : 0xC42B1Cu), solid, AccentMinContrast),
            DotOff = dark ? White.WithAlpha(0.14) : Black.WithAlpha(0.14),
            Outline = outline,
            RimTop = glass is null ? outline : dark ? White.WithAlpha(0.30) : White.WithAlpha(0.95),
            RimBottom = glass is null ? outline : dark ? White.WithAlpha(0.07) : Black.WithAlpha(0.10),
            SheenTop = glass is null ? based : solid.Mix(White, dark ? 0.08 : 0.35).WithAlpha(alpha * 0.82),
        };
    }

    /// <summary>
    /// Il colore più vicino a <paramref name="color"/> con contrasto almeno <paramref name="minimum"/> su
    /// <paramref name="background"/>: si schiarisce su fondi scuri, si scurisce su fondi chiari.
    /// </summary>
    public static Argb Readable(Argb color, Argb background, double minimum)
    {
        var target = background.Luminance < 0.18 ? White : Black;
        for (var step = 0; step <= 40; step++)
        {
            var candidate = color.Mix(target, step / 40.0);
            if (candidate.ContrastWith(background) >= minimum)
                return candidate;
        }
        return target;
    }
}
