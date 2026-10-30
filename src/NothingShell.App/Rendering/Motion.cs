using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NothingShell.App.Rendering;

/// <summary>
/// Movimento condiviso dei controlli (docs/02-design-system.md): lo stato si legge dal riempimento che sfuma
/// (niente bordi che compaiono), la pressione rimpicciolisce appena e al rilascio torna con un piccolo rimbalzo,
/// i contenuti nuovi entrano sfumando dal basso. Curva OutQuint: parte svelta e si posa piano.
/// Con le animazioni di Windows spente (Accessibilità › Effetti visivi) tutto salta subito allo stato finale.
/// </summary>
public static class Motion
{
    public static readonly Duration Fast = TimeSpan.FromMilliseconds(140);
    public static readonly Duration Normal = TimeSpan.FromMilliseconds(260);
    public static readonly Duration Slow = TimeSpan.FromMilliseconds(420);

    public static readonly IEasingFunction OutQuint = Frozen(new QuinticEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction Pop = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 });

    /// <summary>Spegne le animazioni (immagini di --snapshot).</summary>
    public static bool Suspended { get; set; }

    /// <summary>Preferenza di Windows: false con gli effetti di animazione disattivati.</summary>
    public static bool Enabled => !Suspended && SystemParameters.ClientAreaAnimation;

    private static Duration D(Duration d) => Enabled ? d : TimeSpan.Zero;

    /// <summary>Riempimento che sfuma verso <paramref name="color"/>; il pennello diventa modificabile se era condiviso.</summary>
    public static void FadeTo(Border border, Color color, Duration? duration = null)
    {
        ArgumentNullException.ThrowIfNull(border);
        if (border.Background is not SolidColorBrush { IsFrozen: false } brush)
        {
            var from = border.Background is SolidColorBrush b ? b.Color : Colors.Transparent;
            border.Background = brush = new SolidColorBrush(from);
        }
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, D(duration ?? Fast)) { EasingFunction = OutQuint });
    }

    /// <summary>
    /// Stato del controllo come riempimento: <paramref name="idle"/> a riposo, <paramref name="hover"/> col mouse sopra.
    /// <paramref name="isActive"/>, se dato, tiene il controllo nello stato acceso (scelta corrente) senza effetto hover.
    /// </summary>
    public static void StateLayer(Border border, Color idle, Color hover, Func<bool>? isActive = null)
    {
        ArgumentNullException.ThrowIfNull(border);
        border.Background = new SolidColorBrush(idle);
        border.MouseEnter += (_, _) =>
        {
            if (isActive?.Invoke() != true)
                FadeTo(border, hover);
        };
        border.MouseLeave += (_, _) =>
        {
            if (isActive?.Invoke() != true)
                FadeTo(border, idle, Normal);
        };
    }

    /// <summary>Premuto si stringe al 96%; rilasciato torna con un piccolo rimbalzo. Il centro resta fermo.</summary>
    public static void Press(UIElement element, double pressed = 0.96)
    {
        ArgumentNullException.ThrowIfNull(element);
        var scale = new ScaleTransform(1, 1);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        AddTransform(element, scale);
        void To(double value, Duration duration, IEasingFunction easing)
        {
            var animation = new DoubleAnimation(value, D(duration)) { EasingFunction = easing };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }
        element.PreviewMouseLeftButtonDown += (_, _) => To(pressed, Fast, OutQuint);
        element.PreviewMouseLeftButtonUp += (_, _) => To(1, Slow, Pop);
        element.MouseLeave += (_, _) => To(1, Normal, OutQuint);
    }

    /// <summary>Ingresso: da trasparente e spostato di <paramref name="offsetY"/> DIP alla posizione finale.</summary>
    public static void Enter(UIElement element, double offsetY = 10, double delayMs = 0, double offsetX = 0)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!Enabled)
        {
            element.Opacity = 1;
            return;
        }
        var shift = new TranslateTransform(offsetX, offsetY);
        AddTransform(element, shift);
        var begin = TimeSpan.FromMilliseconds(delayMs);
        element.Opacity = 0;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Normal) { BeginTime = begin, EasingFunction = OutQuint });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offsetY, 0, Slow) { BeginTime = begin, EasingFunction = OutQuint });
        if (offsetX != 0)
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(offsetX, 0, Slow) { BeginTime = begin, EasingFunction = OutQuint });
    }

    /// <summary>Apertura di una finestra o di un pannello: sfuma e cresce dal 96%.</summary>
    public static void Open(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!Enabled)
            return;
        var scale = new ScaleTransform(0.96, 0.96);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        AddTransform(element, scale);
        var grow = new DoubleAnimation(0.96, 1, Slow) { EasingFunction = OutQuint };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Normal) { EasingFunction = OutQuint });
    }

    /// <summary>Sposta un indicatore (la pillola di un controllo a segmenti) fino a <paramref name="x"/>, cambiandone la larghezza.</summary>
    public static void Slide(FrameworkElement indicator, TranslateTransform shift, double x, double width)
    {
        ArgumentNullException.ThrowIfNull(indicator);
        ArgumentNullException.ThrowIfNull(shift);
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, D(Normal)) { EasingFunction = OutQuint });
        indicator.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(width, D(Normal)) { EasingFunction = OutQuint });
    }

    /// <summary>Respiro continuo dell'opacità (caricamento): da 1 a <paramref name="low"/> e ritorno.</summary>
    public static void Breathe(UIElement element, double low = 0.25, double delayMs = 0)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!Enabled)
            return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, low, TimeSpan.FromMilliseconds(520))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    /// <summary>Cursore a mano e pressione animata per un elemento cliccabile.</summary>
    public static T Clickable<T>(T element)
        where T : FrameworkElement
    {
        element.Cursor = Cursors.Hand;
        Press(element);
        return element;
    }

    /// <summary>Aggiunge una trasformazione a quelle che l'elemento ha già (pressione e ingresso convivono).</summary>
    private static void AddTransform(UIElement element, Transform transform)
    {
        switch (element.RenderTransform)
        {
            case null:
            case MatrixTransform { Matrix.IsIdentity: true }:
                element.RenderTransform = transform;
                break;
            case TransformGroup { IsFrozen: false } group:
                group.Children.Add(transform);
                break;
            case var existing:
                element.RenderTransform = new TransformGroup { Children = [existing, transform] };
                break;
        }
    }

    private static EasingFunctionBase Frozen(EasingFunctionBase easing)
    {
        easing.Freeze();
        return easing;
    }
}
