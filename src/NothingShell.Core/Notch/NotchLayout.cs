namespace NothingShell.Core.Notch;

public readonly record struct Size2(double Width, double Height);

/// <summary>Dimensioni della pillola per ogni stato, in pixel logici (DIP).</summary>
public static class NotchLayout
{
    public const double TopMargin = 6;
    public const double MaxCornerRadius = 28;

    /// <summary>Altezza del notch espanso sulle pagine con un elenco che scorre (Posta, Telefono, Agenti, Dispositivi).</summary>
    public const double TallHeight = 340;

    public static bool IsTall(NotchPage page) => page is NotchPage.Mail or NotchPage.Phone or NotchPage.Agents or NotchPage.Devices;

    /// <summary>Notch espanso su <paramref name="page"/>: le pagine con un elenco si allungano in verticale.</summary>
    public static Size2 Expanded(NotchPage page) =>
        IsTall(page) ? new(For(NotchState.Expanded).Width, TallHeight) : For(NotchState.Expanded);

    public static Size2 For(NotchState state) => state switch
    {
        NotchState.Idle => new(200, 34),
        NotchState.Activity => new(300, 34),
        NotchState.Alert => new(400, 40),
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
