namespace NothingShell.Core.Design;

/// <summary>Come si stende il colore del vestito: pieno o sfumato verso un secondo colore (docs/22-vestiti-notch.md).</summary>
public enum OutfitGradient
{
    Solid,
    Horizontal,
    Vertical,
    Diagonal,
}

/// <summary>Il motivo ripetuto sopra il colore del vestito.</summary>
public enum OutfitPattern
{
    None,
    Stripes,
    Dots,
    Checks,
    Grid,
    Waves,
    Stars,
    Hearts,
}

/// <summary>I colori di un vestito come si disegnano davvero: regolati perché il testo del notch resti leggibile.</summary>
public readonly record struct OutfitColors(Argb Fill, Argb Fill2, Argb Pattern, Argb? Rim);

/// <summary>
/// Vestito del notch (docs/22-vestiti-notch.md): colore o sfumatura, un motivo ripetuto e un bordo, stesi sopra il fondo del
/// notch. Solo valori semplici, così due vestiti uguali sono uguali e le impostazioni si confrontano come sempre.
/// </summary>
public sealed record NotchOutfit
{
    public const double MinCoverage = 0.2;
    public const double MaxPatternOpacity = 0.6;

    /// <summary>Contrasto minimo fra il fondo del vestito e il testo del notch.</summary>
    public const double FillMinContrast = 4.5;

    /// <summary>Contrasto minimo fra il motivo e il testo: il motivo sta sotto il testo, non deve confondersi con lui.</summary>
    public const double PatternMinContrast = 3;

    /// <summary>Contrasto minimo fra il fondo e il testo secondario del notch.</summary>
    public const double MutedMinContrast = 3;

    private static readonly Argb FallbackFill = Argb.FromRgb(0x202020);

    /// <summary>Quelli di serie hanno un nome parlante (<c>natale</c>); quelli fatti a mano cominciano con <see cref="CustomPrefix"/>.</summary>
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>Colore del fondo, <c>#RRGGBB</c>.</summary>
    public string Fill { get; init; } = "#202020";

    /// <summary>Secondo colore della sfumatura; senza, la sfumatura va verso una versione più scura del primo.</summary>
    public string? Fill2 { get; init; }

    public OutfitGradient Gradient { get; init; } = OutfitGradient.Solid;

    public OutfitPattern Pattern { get; init; } = OutfitPattern.None;

    public string PatternColor { get; init; } = "#FFFFFF";

    /// <summary>Quanto si vede il motivo, da 0 a <see cref="MaxPatternOpacity"/>.</summary>
    public double PatternOpacity { get; init; } = 0.25;

    /// <summary>Quanto il vestito copre il vetro: 1 è pieno, più in basso il vetro si intravede.</summary>
    public double Coverage { get; init; } = 1;

    /// <summary>Bordo colorato, <c>#RRGGBB</c>; null tiene quello del tema.</summary>
    public string? Rim { get; init; }

    public const string CustomPrefix = "mio-";

    /// <summary>Fatto a mano: si modifica e si elimina; quelli di serie si personalizzano facendone una copia.</summary>
    public bool IsCustom => Id.StartsWith(CustomPrefix, StringComparison.Ordinal);

    public double PatternStrength => double.IsFinite(PatternOpacity) ? Math.Clamp(PatternOpacity, 0, MaxPatternOpacity) : 0;

    public double CoverageAmount => double.IsFinite(Coverage) ? Math.Clamp(Coverage, MinCoverage, 1) : 1;

    /// <summary>Un nuovo vestito fatto a mano con un id che non si ripete.</summary>
    public static string NewId() => CustomPrefix + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// I colori da disegnare sopra un notch il cui testo è <paramref name="ink"/> (e quello secondario <paramref name="muted"/>):
    /// fondo e motivo si scuriscono (testo chiaro) o si schiariscono (testo scuro) quanto basta, come l'accento. Un vestito
    /// chiaro nel tema scuro diventa la sua versione scura.
    /// </summary>
    public OutfitColors Resolve(Argb ink, Argb? muted = null)
    {
        var fill = Argb.TryParse(Fill) ?? FallbackFill;
        var fill2 = Gradient == OutfitGradient.Solid ? fill
            : Argb.TryParse(Fill2) ?? fill.Mix(Argb.FromRgb(0), 0.35);
        var pattern = Argb.TryParse(PatternColor) ?? Argb.FromRgb(0xFFFFFF);
        return new OutfitColors(Background(fill), Background(fill2), Palette.Readable(pattern, ink, PatternMinContrast), Argb.TryParse(Rim));

        Argb Background(Argb color)
        {
            var readable = Palette.Readable(color, ink, FillMinContrast);
            return muted is { } secondary ? Palette.Readable(readable, secondary, MutedMinContrast) : readable;
        }
    }
}

/// <summary>I vestiti di serie: un tema per stagione e qualcuno per tutti i giorni.</summary>
public static class OutfitCatalog
{
    public static IReadOnlyList<NotchOutfit> BuiltIn { get; } =
    [
        new()
        {
            Id = "notte-stellata", Name = "Notte stellata", Fill = "#0B1033", Fill2 = "#2A1B5C", Gradient = OutfitGradient.Vertical,
            Pattern = OutfitPattern.Stars, PatternColor = "#FFF4C2", PatternOpacity = 0.55,
        },
        new()
        {
            Id = "natale", Name = "Natale", Fill = "#B3121B", Fill2 = "#6E0B10", Gradient = OutfitGradient.Vertical,
            Pattern = OutfitPattern.Dots, PatternColor = "#FFFFFF", PatternOpacity = 0.35, Rim = "#2E8B57",
        },
        new()
        {
            Id = "halloween", Name = "Halloween", Fill = "#1A1410",
            Pattern = OutfitPattern.Stripes, PatternColor = "#F7630C", PatternOpacity = 0.4, Rim = "#F7630C",
        },
        new()
        {
            Id = "mare", Name = "Mare", Fill = "#064C78", Fill2 = "#0E8FA8", Gradient = OutfitGradient.Horizontal,
            Pattern = OutfitPattern.Waves, PatternColor = "#BDF4FF", PatternOpacity = 0.35,
        },
        new()
        {
            Id = "tramonto", Name = "Tramonto", Fill = "#E0603F", Fill2 = "#5B2A86", Gradient = OutfitGradient.Diagonal,
        },
        new()
        {
            Id = "bosco", Name = "Bosco", Fill = "#1F3B2C", Fill2 = "#2F5D3A", Gradient = OutfitGradient.Vertical,
            Pattern = OutfitPattern.Checks, PatternColor = "#A3C76D", PatternOpacity = 0.14,
        },
        new()
        {
            Id = "san-valentino", Name = "San Valentino", Fill = "#B0124F", Fill2 = "#6D0A33", Gradient = OutfitGradient.Diagonal,
            Pattern = OutfitPattern.Hearts, PatternColor = "#FFC1E3", PatternOpacity = 0.4,
        },
        new()
        {
            Id = "carbonio", Name = "Carbonio", Fill = "#161616",
            Pattern = OutfitPattern.Grid, PatternColor = "#FFFFFF", PatternOpacity = 0.08,
        },
        new()
        {
            Id = "nothing", Name = "Nothing", Fill = "#000000",
            Pattern = OutfitPattern.Dots, PatternColor = "#FFFFFF", PatternOpacity = 0.16, Rim = "#D71921",
        },
    ];

    /// <summary>I vestiti da mostrare nel guardaroba: quelli di serie e poi i tuoi.</summary>
    public static IReadOnlyList<NotchOutfit> Wardrobe(IReadOnlyList<NotchOutfit>? mine) => [.. BuiltIn, .. mine ?? []];

    /// <summary>Il guardaroba con <paramref name="outfit"/> aggiunto o, se c'era già (stesso id), rimpiazzato.</summary>
    public static IReadOnlyList<NotchOutfit> Save(IReadOnlyList<NotchOutfit>? mine, NotchOutfit outfit)
    {
        ArgumentNullException.ThrowIfNull(outfit);
        var list = (mine ?? []).ToList();
        var index = list.FindIndex(o => o.Id == outfit.Id);
        if (index >= 0)
            list[index] = outfit;
        else
            list.Add(outfit);
        return list;
    }

    public static IReadOnlyList<NotchOutfit> Remove(IReadOnlyList<NotchOutfit>? mine, string id) =>
        [.. (mine ?? []).Where(o => o.Id != id)];

    /// <summary>Copia da ritoccare di un vestito (anche di serie): id nuovo e "(mio)" nel nome.</summary>
    public static NotchOutfit Copy(NotchOutfit outfit)
    {
        ArgumentNullException.ThrowIfNull(outfit);
        return outfit with { Id = NotchOutfit.NewId(), Name = outfit.IsCustom ? $"{outfit.Name} (copia)" : $"{outfit.Name} (mio)" };
    }
}
