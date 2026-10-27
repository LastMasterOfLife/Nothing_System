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
}
