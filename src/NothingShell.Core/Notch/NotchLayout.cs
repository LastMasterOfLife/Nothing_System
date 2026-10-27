namespace NothingShell.Core.Notch;

public readonly record struct Size2(double Width, double Height);

/// <summary>Dimensioni della pillola per ogni stato, in pixel logici (DIP).</summary>
public static class NotchLayout
{
    public const double TopMargin = 6;
    public const double MaxCornerRadius = 28;

    public static Size2 For(NotchState state) => state switch
    {
        NotchState.Idle => new(200, 34),
        NotchState.Activity => new(300, 34),
        NotchState.Expanded => new(420, 150),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    /// <summary>Dimensione della finestra che ospita la pillola: la più grande fra tutti gli stati.</summary>
    public static Size2 Host
    {
        get
        {
            var sizes = Enum.GetValues<NotchState>().Select(For).ToArray();
            return new(sizes.Max(s => s.Width), sizes.Max(s => s.Height) + TopMargin);
        }
    }

    public static double CornerRadius(Size2 size) => Math.Min(size.Height / 2, MaxCornerRadius);
}
