namespace NothingShell.Core.DotMatrix;

/// <summary>Piccolo equalizzatore a barre verticali di punti, per lo stato Activity del notch.</summary>
public sealed class DotEqualizer(int bars = 3, int height = 7, int seed = 7)
{
    private readonly Random _random = new(seed);
    private readonly double[] _levels = new double[bars];

    public int Width => bars * 2 - 1;
    public int Height => height;

    /// <summary>Avanza di un fotogramma: ogni barra si muove verso un nuovo livello casuale.</summary>
    public DotGrid NextFrame()
    {
        var grid = new DotGrid(Width, height);
        for (var b = 0; b < bars; b++)
        {
            var target = 1 + _random.NextDouble() * (height - 1);
            _levels[b] += (target - _levels[b]) * 0.6;
            var lit = Math.Clamp((int)Math.Round(_levels[b]), 1, height);
            for (var y = height - lit; y < height; y++)
                grid[b * 2, y] = 1f;
        }
        return grid;
    }
}
