using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NothingShell.App.Rendering;
using NothingShell.Core.Notch;

namespace NothingShell.App.Notch;

/// <summary>Elementi grafici condivisi dalle parti del notch.</summary>
internal static class NotchUi
{
    public const double Padding = 20;
    public static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>Larghezza utile delle pagine del notch espanso.</summary>
    public static double PageWidth => NotchLayout.For(NotchState.Expanded).Width - 2 * Padding;

    public static TextBlock Text(double size, Brush foreground, string text = "") => new()
    {
        Text = text,
        FontFamily = Theme.BodyFont,
        FontSize = size,
        Foreground = foreground,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    /// <summary>Pulsante a pillola con contenuto a punti; l'azione può essere asincrona.</summary>
    public static Border Button(UIElement content, Func<Task> action, double width = 56)
    {
        if (content is FrameworkElement fe)
        {
            fe.HorizontalAlignment = HorizontalAlignment.Center;
            fe.VerticalAlignment = VerticalAlignment.Center;
        }
        var button = new Border
        {
            Width = width,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Margin = new Thickness(5, 0, 5, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = content,
        };
        button.MouseEnter += (_, _) => button.Background = Theme.SurfaceBrush;
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            await action();
        };
        return button;
    }

    public static Border Button(UIElement content, Action action, double width = 56) =>
        Button(content, () =>
        {
            action();
            return Task.CompletedTask;
        }, width);
}

/// <summary>Una pagina del notch espanso (docs/08-notch-mail-pages.md).</summary>
public abstract class NotchPageView : Grid
{
    public abstract NotchPage Page { get; }

    /// <summary>La pagina diventa visibile con il notch espanso: animazioni e timer possono partire.</summary>
    public virtual void OnShown()
    {
    }

    /// <summary>La pagina non è più visibile: fermare ciò che consuma CPU.</summary>
    public virtual void OnHidden()
    {
    }
}
