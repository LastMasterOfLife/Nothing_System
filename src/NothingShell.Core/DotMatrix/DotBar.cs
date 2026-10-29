namespace NothingShell.Core.DotMatrix;

/// <summary>Barra orizzontale di punti: le colonne accese sono proporzionali alla frazione.</summary>
public static class DotBar
{
    public static DotGrid Render(double fraction, int columns, int rows = 1)
    {
        var grid = new DotGrid(columns, rows);
        var lit = LitColumns(fraction, columns);
        for (var x = 0; x < lit; x++)
            for (var y = 0; y < rows; y++)
                grid[x, y] = 1f;
        return grid;
    }

    /// <summary>Colonne accese; una frazione positiva accende sempre almeno una colonna.</summary>
    public static int LitColumns(double fraction, int columns)
    {
        if (double.IsNaN(fraction) || fraction <= 0)
            return 0;
        return Math.Clamp((int)Math.Round(Math.Min(fraction, 1) * columns), 1, columns);
    }

    /// <summary>Intensità delle tacche spente del segnale: si vedono, ma appena.</summary>
    public const float SignalOff = 0.2f;

    /// <summary>
    /// Tacche del segnale come sulla barra del telefono: <paramref name="bars"/> colonne doppie che crescono da sinistra,
    /// accese fino a <paramref name="strength"/>, le altre fioche. Alta quanto il font (7).
    /// </summary>
    public static DotGrid Signal(int strength, int bars = 4)
    {
        const int height = Design.DotFont.Height;
        var grid = new DotGrid(bars * 3 - 1, height);
        for (var i = 0; i < bars; i++)
        {
            var barHeight = (int)Math.Round(2 + (height - 2) * i / (double)Math.Max(1, bars - 1));
            var intensity = i < strength ? 1f : SignalOff;
            for (var y = height - barHeight; y < height; y++)
            {
                grid[i * 3, y] = intensity;
                grid[i * 3 + 1, y] = intensity;
            }
        }
        return grid;
    }
}
