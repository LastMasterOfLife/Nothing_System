using System.Windows.Media;
using NothingShell.Core.Design;

namespace NothingShell.App.Rendering;

/// <summary>Token del design system convertiti in tipi WPF.</summary>
public static class Theme
{
    public static readonly Color Black = ToColor(Palette.Black);
    public static readonly Color Surface = ToColor(Palette.Surface);
    public static readonly Color White = ToColor(Palette.White);
    public static readonly Color Grey = ToColor(Palette.Grey);
    public static readonly Color Red = ToColor(Palette.Red);
    public static readonly Color DotOff = ToColor(Palette.DotOff);
    public static readonly Color Outline = ToColor(Palette.Outline);

    public static readonly Brush BlackBrush = Frozen(Black);
    public static readonly Brush SurfaceBrush = Frozen(Surface);
    public static readonly Brush GreyBrush = Frozen(Grey);
    public static readonly Brush RedBrush = Frozen(Red);
    public static readonly Brush WhiteBrush = Frozen(White);
    public static readonly Brush OutlineBrush = Frozen(Outline);

    public static readonly FontFamily BodyFont = new("Segoe UI Variable Text, Segoe UI");

    public static Color ToColor(Argb c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
