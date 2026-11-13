using NothingShell.Core.Design;

namespace NothingShell.Core.Notch;

public readonly record struct Size2(double Width, double Height);

/// <summary>
/// Dimensioni della pillola per ogni stato, in pixel logici (DIP). Due misure (docs/20-stile-nusa.md): Piccola, quella di
/// sempre, e Grande, quella di Nusa. Si sceglie una volta all'avvio con <see cref="Use"/>, prima di creare il notch.
/// </summary>
public static class NotchLayout
{
    public const double TopMargin = 4;
    public const double MaxCornerRadius = 14;

    /// <summary>Unito al bordo, gli angoli in basso arrivano fin qui (come Nusa da aperto).</summary>
    public const double MaxAttachedRadius = 26;

    /// <summary>Orecchie del notch unito al bordo: raggio del raccordo concavo, da riposo ad aperto.</summary>
    public const double MinEar = 6;
    public const double MaxEar = 20;

    /// <summary>Quanto può allargarsi l'ombra più ampia oltre la forma: la finestra lascia questo spazio intorno.</summary>
    public const double ShadowReach = 48;

    /// <summary>Altezza della Home con l'ora: subito sotto la data c'è il campo per chiedere, con la sua riga.</summary>
    public const double HomeHeight = 190;

    /// <summary>Altezza della Home con la musica, che ha i controlli sopra il campo.</summary>
    public const double HomeMediaHeight = 232;

    /// <summary>Altezza della pagina Svago: lo schermo del gioco viene circa 2 : 1, come il telefono in orizzontale del concept.</summary>
    public const double LeisureHeight = 232;

    /// <summary>Altezza del notch espanso sulle pagine con un elenco che scorre (Posta, Telefono, Agenti, Dispositivi, DNS, Server).</summary>
    public const double TallHeight = 340;

    public static NotchSize Size { get; private set; } = NotchSize.Small;

    /// <summary>La misura per tutta la sessione: le pagine si impaginano sulla larghezza del notch quando nascono.</summary>
    public static void Use(NotchSize size) => Size = size;

    public static bool IsTall(NotchPage page) => page is NotchPage.Mail or NotchPage.Phone or NotchPage.Agents or NotchPage.Devices or NotchPage.Database or NotchPage.Ftp or NotchPage.Diagnosi or NotchPage.Hours or NotchPage.Servers or NotchPage.Debug or NotchPage.Security;

    /// <summary>Notch espanso su <paramref name="page"/>: le pagine con un elenco si allungano in verticale.</summary>
    public static Size2 Expanded(NotchPage page, bool media = false) =>
        IsTall(page) ? new(For(NotchState.Expanded).Width, TallHeight)
        : page == NotchPage.Home ? new(For(NotchState.Expanded).Width, media ? HomeMediaHeight : HomeHeight)
        : page == NotchPage.Leisure ? new(For(NotchState.Expanded).Width, LeisureHeight)
        : For(NotchState.Expanded);

    /// <summary>A riposo col notch discreto: una lineetta contro il bordo alto, dove il mouse la trova comunque.</summary>
    public static Size2 Quiet { get; } = new(96, 6);

    /// <summary>
    /// Grande prende la larghezza di Nusa (riposo ~173 × 33, aperto ~481). Le altezze da aperto restano le nostre: Nusa è alto
    /// 160 ma le nostre pagine ne vogliono 172, e tagliarle sarebbe peggio.
    /// </summary>
    public static Size2 For(NotchState state) => For(state, Size);

    public static Size2 For(NotchState state, NotchSize size) => (size, state) switch
    {
        (NotchSize.Small, NotchState.Idle) => new(144, 28),
        (NotchSize.Small, NotchState.Activity) => new(256, 28),
        (NotchSize.Small, NotchState.Alert) => new(360, 36),
        (NotchSize.Small, NotchState.Expanded) => new(420, 172),
        (NotchSize.Small, NotchState.Card) => new(420, 112),
        (NotchSize.Large, NotchState.Idle) => new(174, 33),
        (NotchSize.Large, NotchState.Activity) => new(292, 33),
        (NotchSize.Large, NotchState.Alert) => new(410, 40),
        (NotchSize.Large, NotchState.Expanded) => new(480, 172),
        (NotchSize.Large, NotchState.Card) => new(480, 120),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    /// <summary>
    /// Dimensione della finestra che ospita la pillola: la più grande fra tutti gli stati, più lo spazio per orecchie e ombra
    /// ai lati e sotto.
    /// </summary>
    public static Size2 Host
    {
        get
        {
            var sizes = Enum.GetValues<NotchState>().Select(For).ToArray();
            var width = sizes.Max(s => s.Width) * NotchMotion.PeekScale;
            var height = Math.Max(sizes.Max(s => s.Height), TallHeight) + TopMargin;
            return new(width + (2 * ShadowReach), height + ShadowReach);
        }
    }

    public static double CornerRadius(Size2 size) => Math.Min(size.Height / 2, MaxCornerRadius);

    /// <summary>Raggio degli angoli in basso: unito al bordo cresce con l'altezza, fino a quello di Nusa da aperto.</summary>
    public static double CornerRadius(Size2 size, NotchStyle style) =>
        style == NotchStyle.Attached ? Math.Min(size.Height / 2, MaxAttachedRadius) : CornerRadius(size);

    /// <summary>
    /// Raggio delle orecchie del notch unito al bordo: ~8 DIP a riposo, ~20 da aperto (misurato su Nusa), mai più di metà
    /// altezza così non si toccano con gli angoli in basso.
    /// </summary>
    public static double Ear(Size2 size) =>
        Math.Min(size.Height / 2, Math.Clamp(MinEar + ((size.Height - 28) * 0.105), MinEar, MaxEar));
}
