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

/// <summary>Carattere delle molle del notch: quanto rimbalza e quanto è svelto ad aprirsi e chiudersi.</summary>
public enum NotchFeel
{
    /// <summary>Lenta e senza rimbalzo.</summary>
    Soft,

    /// <summary>Quella di Nusa: un piccolo rimbalzo.</summary>
    Nusa,

    /// <summary>Svelta e con un rimbalzo più vivace.</summary>
    Snappy,
}

/// <summary>Su quale schermo sta il notch.</summary>
public enum NotchScreen
{
    /// <summary>Lo schermo principale di Windows.</summary>
    Primary,

    /// <summary>Quello dove sta il mouse: il notch ci si sposta quando è chiuso, e ci porta gli avvisi.</summary>
    Mouse,

    /// <summary>Uno schermo scelto (<see cref="AppearanceSettings.NotchMonitor"/>); scollegato, si torna al principale.</summary>
    Chosen,
}

/// <summary>Quanto la mascotte prende l'iniziativa (docs/21-mascotte.md).</summary>
public enum MascotInitiative
{
    /// <summary>Reagisce soltanto: avvisi, lavori finiti, sonno.</summary>
    Off,

    /// <summary>Ogni pochi minuti un gesto suo; ti ricorda la pausa e al ritorno ti dice cosa è successo mentre eri via.</summary>
    Sometimes,

    /// <summary>Gesti più frequenti, e ti dice anche quando si è fatto tardi o il PC fatica.</summary>
    Often,
}

/// <summary>Le scelte della pagina Aspetto (docs/02-design-system.md), salvate con le impostazioni della shell.</summary>
public sealed record AppearanceSettings
{
    public const double DefaultGlassStrength = 0.5;
    public const double DefaultNotchShadow = 0.5;
    public const int DefaultNotchOpenDelayMs = 290;
    public const int MaxNotchDelayMs = 3000;
    public const double MinAnimationSpeed = 0.5;
    public const double MaxAnimationSpeed = 1.5;

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

    /// <summary>Se la mascotte fa qualcosa da sé: gesti suoi, la pausa, cosa è successo mentre eri via.</summary>
    public MascotInitiative NotchMascotInitiative { get; init; }

    /// <summary>Se la mascotte risponde ai gesti col mouse vicino al notch: carezze, solletico, saluti, giri, colpetti.</summary>
    public bool NotchMascotPlay { get; init; } = true;

    /// <summary>Ampiezza dell'ombra sotto il notch aperto, da 0 (nessuna) a 1 (larga e morbida).</summary>
    public double NotchShadow { get; init; } = DefaultNotchShadow;

    /// <summary>Col mouse sopra, millisecondi prima di aprire: 0 apre subito, di serie l'attesa di Nusa.</summary>
    public int NotchOpenDelayMs { get; init; } = DefaultNotchOpenDelayMs;

    /// <summary>Uscito il mouse, millisecondi prima di richiudere: di serie subito, come Nusa.</summary>
    public int NotchCloseDelayMs { get; init; }

    public NotchScreen NotchScreen { get; init; } = NotchScreen.Primary;

    public NotchFeel NotchFeel { get; init; } = NotchFeel.Nusa;

    /// <summary>Velocità di tutte le animazioni della shell: 1 è quella di serie, 0,5 la metà, 1,5 una volta e mezzo.</summary>
    public double AnimationSpeed { get; init; } = 1;

    /// <summary><see cref="AnimationSpeed"/> nei limiti, e 1 se non è un numero.</summary>
    public double MotionSpeed => double.IsFinite(AnimationSpeed) ? Math.Clamp(AnimationSpeed, MinAnimationSpeed, MaxAnimationSpeed) : 1;

    /// <summary>Nome di Windows dello schermo scelto (es. <c>\\.\DISPLAY2</c>), con <see cref="NotchScreen.Chosen"/>.</summary>
    public string? NotchMonitor { get; init; }

    public TimeSpan NotchOpenDelay => TimeSpan.FromMilliseconds(Math.Clamp(NotchOpenDelayMs, 0, MaxNotchDelayMs));

    public TimeSpan NotchCloseDelay => TimeSpan.FromMilliseconds(Math.Clamp(NotchCloseDelayMs, 0, MaxNotchDelayMs));

    /// <summary>Le scelte del notch e del movimento di <paramref name="from"/> su questo aspetto: uno stile rapido non le tocca.</summary>
    public AppearanceSettings WithNotchOf(AppearanceSettings from)
    {
        ArgumentNullException.ThrowIfNull(from);
        return this with
        {
            QuietNotch = from.QuietNotch,
            NotchStyle = from.NotchStyle,
            NotchSize = from.NotchSize,
            NotchMascot = from.NotchMascot,
            NotchMascotInitiative = from.NotchMascotInitiative,
            NotchMascotPlay = from.NotchMascotPlay,
            NotchShadow = from.NotchShadow,
            NotchOpenDelayMs = from.NotchOpenDelayMs,
            NotchCloseDelayMs = from.NotchCloseDelayMs,
            NotchScreen = from.NotchScreen,
            NotchMonitor = from.NotchMonitor,
            NotchFeel = from.NotchFeel,
            AnimationSpeed = from.AnimationSpeed,
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
