using System.Globalization;
using System.Text;

namespace NothingShell.Core.Design;

/// <summary>Glifo a matrice di punti: righe di '#' (acceso) e '.' (spento), larghezza variabile.</summary>
public sealed class DotGlyph
{
    private readonly bool[,] _dots;

    public DotGlyph(params string[] rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Length == 0 || rows.Any(r => r.Length != rows[0].Length))
            throw new ArgumentException("Tutte le righe del glifo devono avere la stessa larghezza.", nameof(rows));

        Height = rows.Length;
        Width = rows[0].Length;
        _dots = new bool[Width, Height];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                _dots[x, y] = rows[y][x] == '#';
    }

    public int Width { get; }
    public int Height { get; }
    public bool this[int x, int y] => _dots[x, y];
}

/// <summary>Font 5x7 a punti (alfabeto maiuscolo, cifre, punteggiatura base).</summary>
public static class DotFont
{
    public const int Height = 7;

    private static readonly Dictionary<char, DotGlyph> Glyphs = new()
    {
        ['0'] = new(".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###."),
        ['1'] = new("..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###."),
        ['2'] = new(".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####"),
        ['3'] = new("#####", "...#.", "..#..", "...#.", "....#", "#...#", ".###."),
        ['4'] = new("...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#."),
        ['5'] = new("#####", "#....", "####.", "....#", "....#", "#...#", ".###."),
        ['6'] = new("..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###."),
        ['7'] = new("#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..."),
        ['8'] = new(".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###."),
        ['9'] = new(".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.."),
        ['A'] = new(".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#"),
        ['B'] = new("####.", "#...#", "#...#", "####.", "#...#", "#...#", "####."),
        ['C'] = new(".###.", "#...#", "#....", "#....", "#....", "#...#", ".###."),
        ['D'] = new("###..", "#..#.", "#...#", "#...#", "#...#", "#..#.", "###.."),
        ['E'] = new("#####", "#....", "#....", "####.", "#....", "#....", "#####"),
        ['F'] = new("#####", "#....", "#....", "####.", "#....", "#....", "#...."),
        ['G'] = new(".###.", "#...#", "#....", "#.###", "#...#", "#...#", ".####"),
        ['H'] = new("#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#"),
        ['I'] = new(".###.", "..#..", "..#..", "..#..", "..#..", "..#..", ".###."),
        ['J'] = new("..###", "...#.", "...#.", "...#.", "...#.", "#..#.", ".##.."),
        ['K'] = new("#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#"),
        ['L'] = new("#....", "#....", "#....", "#....", "#....", "#....", "#####"),
        ['M'] = new("#...#", "##.##", "#.#.#", "#.#.#", "#...#", "#...#", "#...#"),
        ['N'] = new("#...#", "#...#", "##..#", "#.#.#", "#..##", "#...#", "#...#"),
        ['O'] = new(".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."),
        ['P'] = new("####.", "#...#", "#...#", "####.", "#....", "#....", "#...."),
        ['Q'] = new(".###.", "#...#", "#...#", "#...#", "#.#.#", "#..#.", ".##.#"),
        ['R'] = new("####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#"),
        ['S'] = new(".####", "#....", "#....", ".###.", "....#", "....#", "####."),
        ['T'] = new("#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.."),
        ['U'] = new("#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."),
        ['V'] = new("#...#", "#...#", "#...#", "#...#", "#...#", ".#.#.", "..#.."),
        ['W'] = new("#...#", "#...#", "#...#", "#.#.#", "#.#.#", "#.#.#", ".#.#."),
        ['X'] = new("#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#"),
        ['Y'] = new("#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#.."),
        ['Z'] = new("#####", "....#", "...#.", "..#..", ".#...", "#....", "#####"),
        [' '] = new("...", "...", "...", "...", "...", "...", "..."),
        [':'] = new(".", ".", "#", ".", "#", ".", "."),
        ['.'] = new(".", ".", ".", ".", ".", ".", "#"),
        [','] = new("..", "..", "..", "..", "..", ".#", "#."),
        ['!'] = new("#", "#", "#", "#", "#", ".", "#"),
        ['\''] = new("#", "#", ".", ".", ".", ".", "."),
        ['"'] = new("#.#", "#.#", "...", "...", "...", "...", "..."),
        ['-'] = new("...", "...", "...", "###", "...", "...", "..."),
        ['_'] = new(".....", ".....", ".....", ".....", ".....", ".....", "#####"),
        ['+'] = new(".....", "..#..", "..#..", "#####", "..#..", "..#..", "....."),
        ['('] = new(".#", "#.", "#.", "#.", "#.", "#.", ".#"),
        [')'] = new("#.", ".#", ".#", ".#", ".#", ".#", "#."),
        ['/'] = new("....#", "...#.", "...#.", "..#..", ".#...", ".#...", "#...."),
        ['&'] = new(".#...", "#.#..", "#.#..", ".#...", "#.#.#", "#..#.", ".##.#"),
        ['?'] = new(".###.", "#...#", "....#", "...#.", "..#..", ".....", "..#.."),
        ['°'] = new(".#.", "#.#", ".#.", "...", "...", "...", "..."),
        ['%'] = new("##..#", "##.#.", "...#.", "..#..", ".#...", ".#.##", "#..##"),
    };

    /// <summary>Glifo per il carattere: minuscole → maiuscole, accenti rimossi, sconosciuti → '?'.</summary>
    public static DotGlyph Get(char c)
    {
        if (Glyphs.TryGetValue(char.ToUpperInvariant(c), out var glyph))
            return glyph;

        var baseChar = StripDiacritics(c);
        return Glyphs.TryGetValue(char.ToUpperInvariant(baseChar), out glyph) ? glyph : Glyphs['?'];
    }

    public static bool Supports(char c) => Glyphs.ContainsKey(char.ToUpperInvariant(c));

    private static char StripDiacritics(char c)
    {
        foreach (var d in c.ToString().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(d) != UnicodeCategory.NonSpacingMark)
                return d;
        return c;
    }
}

/// <summary>Icone alte 7 punti per i controlli media.</summary>
public static class DotIcons
{
    public static readonly DotGlyph Play = new(
        "#......", "###....", "#####..", "#######", "#####..", "###....", "#......");

    public static readonly DotGlyph Pause = new(
        "##.##", "##.##", "##.##", "##.##", "##.##", "##.##", "##.##");

    public static readonly DotGlyph Next = new(
        "#...#..#", "##..##.#", "###.####", "########", "###.####", "##..##.#", "#...#..#");

    public static readonly DotGlyph Previous = new(
        "#..#...#", "#.##..##", "####.###", "########", "####.###", "#.##..##", "#..#...#");

    /// <summary>Griglia 3×3: pulsante Start della barra.</summary>
    public static readonly DotGlyph Start = new(
        "#.#.#", ".....", "#.#.#", ".....", "#.#.#");

    public static readonly DotGlyph Wifi = new(
        ".#####.", "#.....#", "..###..", ".#...#.", ".......", "...#...");

    public static readonly DotGlyph Mail = new(
        "#########", "##.....##", "#.#...#.#", "#..#.#..#", "#...#...#", "#.......#", "#########");

    public static readonly DotGlyph Search = new(
        ".###...", "#...#..", "#...#..", "#...#..", ".###...", ".....#.", "......#");

    public static readonly DotGlyph Lock = new(
        "..###..", ".#...#.", ".#...#.", "#######", "###.###", "###.###", "#######");

    public static readonly DotGlyph Sleep = new(
        "..###..", ".##....", "##.....", "##.....", "##.....", ".##...#", "..####.");

    public static readonly DotGlyph Restart = new(
        "..###.#", ".#...##", "#...###", "#......", "#.....#", ".#...#.", "..###..");

    public static readonly DotGlyph Power = new(
        "...#...", ".#.#.#.", "#..#..#", "#.....#", "#.....#", ".#...#.", "..###..");

    public static readonly DotGlyph Ethernet = new(
        "..###..", "..#.#..", "#######", "#.....#", "#.#.#.#", "#######");

    public static readonly DotGlyph Close = new(
        "#.....#", ".#...#.", "..#.#..", "...#...", "..#.#..", ".#...#.", "#.....#");

    public static readonly DotGlyph Microphone = new(
        "..###..", "..###..", "#.###.#", "#.###.#", ".#...#.", "..###..", "...#...");

    public static readonly DotGlyph Camera = new(
        "######..#", "######.##", "#########", "######.##", "######..#");

    public static readonly DotGlyph Speaker = new(
        "...#....", "..##..#.", "####...#", "####.#.#", "####...#", "..##..#.", "...#....");

    public static readonly DotGlyph SpeakerMuted = new(
        "...#....", "..##....", "####.#.#", "####..#.", "####.#.#", "..##....", "...#....");

    public static readonly DotGlyph Sun = new(
        "#..#..#", ".#...#.", "..###..", "#.###.#", "..###..", ".#...#.", "#..#..#");

    public static readonly DotGlyph Download = new(
        "..#..", "..#..", "..#..", "#.#.#", ".###.", "..#..", "#####");

    public static readonly DotGlyph Headphones = new(
        ".#####.", "#.....#", "#.....#", "#.....#", "##...##", "##...##", "##...##");

    public static readonly DotGlyph Screenshot = new(
        "##...##", "#.....#", "..###..", "..###..", "..###..", "#.....#", "##...##");

    public static readonly DotGlyph Bluetooth = new(
        "..#..", "..##.", "#.#.#", ".###.", "#.#.#", "..##.", "..#..");

    public static readonly DotGlyph Calendar = new(
        ".#...#.", "#######", "#.....#", "#.#.#.#", "#.....#", "#.#.#.#", "#######");

    /// <summary>Schermo: Proietta (Win+P).</summary>
    public static readonly DotGlyph Display = new(
        "#######", "#.....#", "#.....#", "#.....#", "#######", "...#...", "..###..");

    public static readonly DotGlyph Gear = new(
        "..#.#..", ".#####.", "##...##", ".#...#.", "##...##", ".#####.", "..#.#..");
}
