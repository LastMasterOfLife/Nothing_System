namespace NothingShell.Core.Notch;

public readonly record struct Size2(double Width, double Height);

/// <summary>Dimensioni della pillola per ogni stato, in pixel logici (DIP).</summary>
public static class NotchLayout
{
    public const double TopMargin = 4;
    public const double MaxCornerRadius = 14;

    /// <summary>Altezza del notch espanso sulle pagine con un elenco che scorre (Posta, Telefono, Agenti, Dispositivi, DNS).</summary>
    public const double TallHeight = 340;

    /// <summary>Altezza della Home con l'ora: subito sotto la data c'è il campo per chiedere, con la sua riga.</summary>
    public const double HomeHeight = 190;

    /// <summary>Altezza della Home con la musica, che ha i controlli sopra il campo.</summary>
    public const double HomeMediaHeight = 232;

    public static bool IsTall(NotchPage page) => page is NotchPage.Mail or NotchPage.Phone or NotchPage.Agents or NotchPage.Devices or NotchPage.Database or NotchPage.Ftp or NotchPage.Dns;

    /// <summary>Notch espanso su <paramref name="page"/>: le pagine con un elenco si allungano in verticale.</summary>
    public static Size2 Expanded(NotchPage page, bool media = false) =>
        IsTall(page) ? new(For(NotchState.Expanded).Width, TallHeight)
        : page == NotchPage.Home ? new(For(NotchState.Expanded).Width, media ? HomeMediaHeight : HomeHeight)
        : For(NotchState.Expanded);

    /// <summary>A riposo col notch discreto: una lineetta contro il bordo alto, dove il mouse la trova comunque.</summary>
    public static Size2 Quiet { get; } = new(96, 6);

    public static Size2 For(NotchState state) => state switch
    {
        NotchState.Idle => new(144, 28),
        NotchState.Activity => new(256, 28),
        NotchState.Alert => new(360, 36),
        NotchState.Expanded => new(420, 172),
        NotchState.Card => new(420, 112),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    /// <summary>Dimensione della finestra che ospita la pillola: la più grande fra tutti gli stati.</summary>
    public static Size2 Host
    {
        get
        {
            var sizes = Enum.GetValues<NotchState>().Select(For).ToArray();
            return new(sizes.Max(s => s.Width), Math.Max(sizes.Max(s => s.Height), TallHeight) + TopMargin);
        }
    }

    public static double CornerRadius(Size2 size) => Math.Min(size.Height / 2, MaxCornerRadius);
}
