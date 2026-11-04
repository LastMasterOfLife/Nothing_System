namespace NothingShell.Core.Design;

/// <summary>Tema della shell: quello di Windows, oppure sempre chiaro o scuro.</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>Tono dello sfondo delle superfici.</summary>
public enum SurfaceTone
{
    /// <summary>Grigio di Windows 11.</summary>
    Neutral,

    /// <summary>Grigio con una punta del colore d'accento.</summary>
    Tinted,

    /// <summary>Nero o bianco puri.</summary>
    Deep,
}

/// <summary>Come si disegnano testo, icone e barre di notch, widget e barra.</summary>
public enum DisplayStyle
{
    /// <summary>Segoe UI Variable, icone Fluent, barre piene: si confonde con Windows 11.</summary>
    Windows,

    /// <summary>Matrice a punti di Nothing.</summary>
    Dots,
}

/// <summary>Forma del notch (docs/20-stile-nusa.md).</summary>
public enum NotchStyle
{
    /// <summary>Pillola staccata di poco dal bordo alto, angoli tondi tutt'intorno.</summary>
    Floating,

    /// <summary>Unito al bordo alto: in alto si allarga con due raccordi concavi, le "orecchie".</summary>
    Attached,
}

/// <summary>Misura del notch: Piccola è quella di sempre, Grande quella di Nusa.</summary>
public enum NotchSize
{
    Small,
    Large,
}

/// <summary>Le scelte della pagina Aspetto (docs/02-design-system.md), salvate con le impostazioni della shell.</summary>
public sealed record AppearanceSettings
{
    public const double DefaultGlassStrength = 0.5;
    public const double DefaultNotchShadow = 0.5;

    public static AppearanceSettings Default { get; } = new();

    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>Accento come <c>#RRGGBB</c>; null segue quello di Windows.</summary>
    public string? Accent { get; init; }

    public SurfaceTone Tone { get; init; } = SurfaceTone.Neutral;

    /// <summary>Stile Liquid Glass: superfici traslucide che sfocano ciò che sta dietro.</summary>
    public bool Glass { get; init; } = true;

    /// <summary>Trasparenza del vetro, da 0 (velato) a 1 (limpido).</summary>
    public double GlassStrength { get; init; } = DefaultGlassStrength;

    public DisplayStyle Display { get; init; } = DisplayStyle.Windows;

    /// <summary>Notch ridotto a una lineetta finché non serve: niente orologio sullo schermo.</summary>
    public bool QuietNotch { get; init; }

    public NotchStyle NotchStyle { get; init; } = NotchStyle.Floating;

    /// <summary>Si applica al riavvio della shell: le pagine del notch si impaginano sulla sua larghezza.</summary>
    public NotchSize NotchSize { get; init; } = NotchSize.Small;

    /// <summary>A riposo, al posto dell'ora, due occhi che sbattono le palpebre, seguono il mouse e si addormentano.</summary>
    public bool NotchMascot { get; init; }

    /// <summary>Ampiezza dell'ombra sotto il notch aperto, da 0 (nessuna) a 1 (larga e morbida).</summary>
    public double NotchShadow { get; init; } = DefaultNotchShadow;

    /// <summary>Le scelte del notch di <paramref name="from"/> su questo aspetto: uno stile rapido non le tocca.</summary>
    public AppearanceSettings WithNotchOf(AppearanceSettings from)
    {
        ArgumentNullException.ThrowIfNull(from);
        return this with
        {
            QuietNotch = from.QuietNotch,
            NotchStyle = from.NotchStyle,
            NotchSize = from.NotchSize,
            NotchMascot = from.NotchMascot,
            NotchShadow = from.NotchShadow,
        };
    }

    /// <summary>Accento effettivo: quello scelto se valido, altrimenti <paramref name="system"/>.</summary>
    public Argb ResolveAccent(Argb system) => Argb.TryParse(Accent) ?? system;

    /// <summary>
    /// Le preferenze di Windows che contano: tema chiaro o scuro, colore d'accento, e se la trasparenza è accesa
    /// (Personalizzazione › Colori): spenta lì, niente vetro anche qui.
    /// </summary>
    public Palette ToPalette(bool systemDark, Argb systemAccent, bool systemTransparency = true)
    {
        var dark = Theme switch
        {
            AppTheme.Light => false,
            AppTheme.Dark => true,
            _ => systemDark,
        };
        return Palette.Create(dark, ResolveAccent(systemAccent), Tone, Glass && systemTransparency ? GlassStrength : null);
    }
}

/// <summary>Scelte già fatte per la pagina Aspetto: colori e stili che stanno bene insieme.</summary>
public static class AppearanceChoices
{
    /// <summary>Colori d'accento proposti; l'azzurro è quello di serie di Windows 11, il rosso quello di Nothing.</summary>
    public static IReadOnlyList<(string Name, string Hex)> Accents { get; } =
    [
        ("Azzurro", "#0078D4"),
        ("Indaco", "#5B5FC7"),
        ("Viola", "#8764B8"),
        ("Rosa", "#E3008C"),
        ("Rosso Nothing", "#D71921"),
        ("Arancio", "#F7630C"),
        ("Ambra", "#FFB900"),
        ("Verde", "#10893E"),
        ("Menta", "#00B294"),
        ("Grafite", "#68768A"),
    ];

    /// <summary>Stili rapidi: un clic cambia tutto insieme, poi si può ritoccare ogni voce.</summary>
    public static IReadOnlyList<(string Name, string Description, AppearanceSettings Settings)> Presets { get; } =
    [
        ("Windows 11", "Tema e colore di Windows, vetro", AppearanceSettings.Default),
        ("Windows solido", "Come Windows, senza trasparenze", new AppearanceSettings { Glass = false }),
        ("Nothing", "Nero, rosso e matrice a punti",
            new AppearanceSettings
            {
                Theme = AppTheme.Dark,
                Accent = "#D71921",
                Tone = SurfaceTone.Deep,
                Glass = false,
                Display = DisplayStyle.Dots,
            }),
    ];
}
