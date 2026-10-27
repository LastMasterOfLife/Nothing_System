using NothingShell.Core.Design;

namespace NothingShell.Core.DotMatrix;

/// <summary>Bitmap di punti con intensità 0..1 per punto.</summary>
public sealed class DotGrid
{
    private readonly float[] _dots;

    public DotGrid(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        Width = width;
        Height = height;
        _dots = new float[width * height];
    }

    public static DotGrid Empty { get; } = new(0, 0);

    public static DotGrid From(DotGlyph glyph)
    {
        ArgumentNullException.ThrowIfNull(glyph);
        var grid = new DotGrid(glyph.Width, glyph.Height);
        grid.Draw(glyph, 0, 0);
        return grid;
    }

    public int Width { get; }
    public int Height { get; }

    public float this[int x, int y]
    {
        get => InBounds(x, y) ? _dots[y * Width + x] : 0f;
        set
        {
            if (InBounds(x, y))
                _dots[y * Width + x] = Math.Clamp(value, 0f, 1f);
        }
    }

    public int LitCount => _dots.Count(d => d > 0f);

    public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    /// <summary>Accende i punti del glifo a partire da (x, y); quelli fuori griglia sono scartati.</summary>
    public void Draw(DotGlyph glyph, int x, int y, float intensity = 1f)
    {
        ArgumentNullException.ThrowIfNull(glyph);
        for (var gy = 0; gy < glyph.Height; gy++)
            for (var gx = 0; gx < glyph.Width; gx++)
                if (glyph[gx, gy])
                    this[x + gx, y + gy] = intensity;
    }

    /// <summary>Copia un'altra griglia a partire da (x, y), tenendo il massimo delle intensità.</summary>
    public void Draw(DotGrid other, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(other);
        for (var oy = 0; oy < other.Height; oy++)
            for (var ox = 0; ox < other.Width; ox++)
            {
                var v = other[ox, oy];
                if (v > this[x + ox, y + oy])
                    this[x + ox, y + oy] = v;
            }
    }

    /// <summary>Rappresentazione testuale ('#' acceso, '.' spento), utile per test e debug.</summary>
    public override string ToString()
    {
        var rows = new string[Height];
        for (var y = 0; y < Height; y++)
        {
            var row = new char[Width];
            for (var x = 0; x < Width; x++)
                row[x] = this[x, y] > 0f ? '#' : '.';
            rows[y] = new string(row);
        }
        return string.Join('\n', rows);
    }
}
