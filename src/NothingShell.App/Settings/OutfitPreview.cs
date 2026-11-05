using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NothingShell.App.Rendering;
using NothingShell.Core.Design;

namespace NothingShell.App.Settings;

/// <summary>
/// Un notch in miniatura con indosso un vestito, disegnato con gli stessi pennelli del notch vero (<see cref="OutfitPaint"/>):
/// piccolo per le tessere del guardaroba, grande e aperto nell'editor. <see cref="Show"/> lo ridipinge senza rifarlo, così
/// segue il selettore mentre si trascina.
/// </summary>
internal sealed class OutfitPreview : Grid
{
    private readonly Border _base;
    private readonly Border _dress;
    private readonly Border _pattern;
    private readonly Border _rim;

    public OutfitPreview(NotchOutfit? outfit, bool large)
    {
        var (width, height) = large ? (380.0, 112.0) : (112.0, 30.0);
        var radius = new CornerRadius(large ? 26 : height / 2);
        Width = width;
        Height = height;
        HorizontalAlignment = HorizontalAlignment.Left;
        _base = new Border { CornerRadius = radius, Background = Theme.BaseSolidBrush };
        _dress = new Border { CornerRadius = radius };
        _pattern = new Border { CornerRadius = radius };
        _rim = new Border { CornerRadius = radius, BorderThickness = new Thickness(1) };
        Children.Add(_base);
        Children.Add(_dress);
        Children.Add(_pattern);
        Children.Add(large ? OpenContent() : IdleContent());
        Children.Add(_rim);
        Show(outfit);
    }

    public void Show(NotchOutfit? outfit)
    {
        var palette = Theme.Current;
        _dress.Background = outfit is null ? null : OutfitPaint.Fill(outfit, palette);
        _dress.Opacity = outfit?.CoverageAmount ?? 0;
        _pattern.Background = outfit is null ? null : OutfitPaint.Pattern(outfit, palette);
        _pattern.Opacity = outfit is null ? 0 : outfit.PatternStrength * outfit.CoverageAmount;
        _rim.BorderBrush = (outfit is null ? null : OutfitPaint.Rim(outfit)) ?? Theme.OutlineBrush;
    }

    private static TextBlock IdleContent() => new()
    {
        Text = "12:34",
        FontFamily = Theme.DisplayFont,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = Theme.InkBrush,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Come la pagina Home del notch aperto: ora, una riga di dettaglio e una barra d'avanzamento.</summary>
    private static Grid OpenContent()
    {
        var content = new Grid { Margin = new Thickness(26, 16, 26, 16) };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        text.Children.Add(new TextBlock { Text = "12:34", FontFamily = Theme.DisplayFont, FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Theme.InkBrush });
        text.Children.Add(new TextBlock { Text = "Oggi · 2 appuntamenti", FontFamily = Theme.BodyFont, FontSize = 12, Foreground = Theme.MutedBrush, Margin = new Thickness(0, 2, 0, 0) });
        content.Children.Add(text);
        var track = new Grid { Height = 4, VerticalAlignment = VerticalAlignment.Bottom };
        track.Children.Add(new Rectangle { RadiusX = 2, RadiusY = 2, Fill = Theme.DotOffBrush });
        track.Children.Add(new Rectangle { RadiusX = 2, RadiusY = 2, Fill = Theme.AccentBrush, Width = 140, HorizontalAlignment = HorizontalAlignment.Left });
        content.Children.Add(track);
        return content;
    }
}
