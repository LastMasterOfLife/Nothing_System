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
            Cursor = Cursors.Hand,
            Child = content,
        };
        Motion.StateLayer(button, Colors.Transparent, Theme.Surface);
        Motion.Press(button);
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

/// <summary>
/// Elenco delle pagine alte del notch: la rotellina (o due dita sul touchpad, in verticale) lo fa scorrere invece di
/// cambiare pagina; se il contenuto ci sta tutto la rotellina torna a cambiare pagina. A destra una sottile barra
/// grigia dice dove si è. Le pagine si cambiano sempre con lo scorrimento orizzontale o trascinando.
/// </summary>
public sealed class NotchScrollList : Grid
{
    private readonly WheelScrollViewer _scroll = new();
    private readonly Border _thumb = new()
    {
        Width = 3,
        CornerRadius = new CornerRadius(1.5),
        Background = Theme.GreyBrush,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, 0, -10, 0),
        Opacity = 0.6,
        IsHitTestVisible = false,
    };

    public NotchScrollList(UIElement content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _scroll.Content = content;
        _scroll.ScrollChanged += (_, _) => UpdateThumb();
        Children.Add(_scroll);
        Children.Add(_thumb);
        _thumb.Visibility = Visibility.Collapsed;
    }

    /// <summary>Torna in cima (nuove mail, pagina riaperta).</summary>
    public void ScrollToTop() => _scroll.ScrollToTop();

    private void UpdateThumb()
    {
        var total = _scroll.ExtentHeight;
        var visible = _scroll.ViewportHeight;
        if (total <= visible + 0.5 || visible <= 0)
        {
            _thumb.Visibility = Visibility.Collapsed;
            return;
        }
        _thumb.Visibility = Visibility.Visible;
        _thumb.Height = Math.Max(16, visible * visible / total);
        _thumb.RenderTransform = new TranslateTransform(0, (visible - _thumb.Height) * (_scroll.VerticalOffset / _scroll.ScrollableHeight));
    }

    /// <summary>Con niente da scorrere la rotellina non si ferma qui: arriva alla pillola e cambia pagina.</summary>
    private sealed class WheelScrollViewer : ScrollViewer
    {
        public WheelScrollViewer()
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            CanContentScroll = false; // a pixel, non a righe intere
            PanningMode = PanningMode.VerticalOnly;
            Focusable = false;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            if (ScrollableHeight <= 0)
                return;
            base.OnMouseWheel(e);
        }
    }
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
