namespace NothingShell.Core.Animation;

/// <summary>Curve di andamento su [0, 1] e utilità per le sequenze a tempo.</summary>
public static class Easing
{
    private const double Back = 1.70158;

    public static double Clamp01(double t) => Math.Clamp(t, 0, 1);

    /// <summary>Avanzamento (0–1) di <paramref name="t"/> nell'intervallo [start, end].</summary>
    public static double Progress(double t, double start, double end) =>
        end <= start ? (t >= end ? 1 : 0) : Clamp01((t - start) / (end - start));

    public static double Lerp(double from, double to, double p) => from + (to - from) * p;

    public static double OutCubic(double t)
    {
        var u = 1 - Clamp01(t);
        return 1 - u * u * u;
    }

    public static double InCubic(double t)
    {
        var c = Clamp01(t);
        return c * c * c;
    }

    public static double InOutCubic(double t)
    {
        var c = Clamp01(t);
        return c < 0.5 ? 4 * c * c * c : 1 - Math.Pow(-2 * c + 2, 3) / 2;
    }

    /// <summary>Arriva oltre il valore finale e torna indietro: il "rimbalzo" della card.</summary>
    public static double OutBack(double t)
    {
        var c = Clamp01(t) - 1;
        return 1 + (Back + 1) * c * c * c + Back * c * c;
    }

    /// <summary>Prende la rincorsa all'indietro prima di partire.</summary>
    public static double InBack(double t)
    {
        var c = Clamp01(t);
        return (Back + 1) * c * c * c - Back * c * c;
    }
}
