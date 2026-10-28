using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using NothingShell.App.Interop;
using NothingShell.App.Rendering;
using NothingShell.Core.Access;
using NothingShell.Core.DotMatrix;

namespace NothingShell.App.Access;

/// <summary>
/// Overlay a tutto schermo sul monitor principale: ora a punti, card rotonda con immagine e nome,
/// anello di punti, iride che si apre e chiude (docs/09-access.md). Solo grafica: nessun input.
/// </summary>
public sealed class AccessWindow : Window
{
    public const int RingDots = 40;
    private const double AvatarSize = 176;
    private const double RingRadius = 118;
    private const double RingDotSize = 7;
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    private readonly double _width;
    private readonly double _height;
    private readonly Point _center;
    private readonly double _maxIris;
    private readonly RectangleGeometry _screen;

    private readonly Grid _root = new();
    private readonly System.Windows.Shapes.Path _backdrop = new() { Fill = Theme.BlackBrush };
    private readonly Canvas _chrome = new();
    private readonly DotMatrixView _clock = new() { Pitch = 10, DotColor = Theme.White };
    private readonly DotMatrixView _date = new() { Pitch = 3, DotColor = Theme.Grey };
    private readonly Canvas _card = new();
    private readonly ScaleTransform _cardScale = new();
    private readonly Grid _avatar = new() { Width = AvatarSize, Height = AvatarSize };
    private readonly TextBlock _name = CenteredText(28, Theme.WhiteBrush);
    private readonly TextBlock _userName = CenteredText(14, Theme.GreyBrush);
    private readonly Canvas _ring = new();
    private readonly System.Windows.Shapes.Ellipse[] _ringDots = new System.Windows.Shapes.Ellipse[RingDots];
    private readonly DotMatrixView _caption = new() { Pitch = 3.5, DotColor = Theme.White };
    private readonly Brush _dotOff = Frozen(Theme.DotOff);

    private UserProfile _profile = UserProfile.Fallback;
    private string _clockText = string.Empty;
    private string _captionText = string.Empty;
    private int _litDots = -1;
    private bool _head;

    public AccessWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "Nothing Shell — Accesso";

        _width = SystemParameters.PrimaryScreenWidth;
        _height = SystemParameters.PrimaryScreenHeight;
        Left = 0;
        Top = 0;
        Width = _width;
        Height = _height;
        _center = new Point(_width / 2, _height / 2);
        _maxIris = Math.Sqrt(_center.X * _center.X + _center.Y * _center.Y) + RingDotSize;
        _screen = new RectangleGeometry(new Rect(0, 0, _width, _height));
        _screen.Freeze();

        BuildChrome();
        BuildCard();
        BuildRing();

        _root.Width = _width;
        _root.Height = _height;
        _root.Children.Add(_backdrop);
        _root.Children.Add(_chrome);
        _root.Children.Add(_ring);
        _root.Children.Add(_card);
        Content = _root;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.MakeNonActivatingToolWindow(hwnd);
        };
        Render(AccessTimeline.Locked, DateTime.Now);
    }

    /// <summary>Il contenuto da disegnare in PNG per gli snapshot.</summary>
    public FrameworkElement Surface => _root;

    public void SetProfile(UserProfile profile)
    {
        _profile = profile;
        _name.Text = profile.DisplayName;
        _userName.Text = profile.UserName;
        _avatar.Children.Clear();
        _avatar.Children.Add(new System.Windows.Shapes.Ellipse { Fill = Theme.SurfaceBrush });
        if (profile.Picture is { } picture)
        {
            _avatar.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Fill = new ImageBrush(picture) { Stretch = Stretch.UniformToFill },
            });
        }
        else
        {
            _avatar.Children.Add(new DotMatrixView
            {
                Pitch = 8,
                DotColor = Theme.White,
                Dots = DotText.Render(AccessIdentity.Initials(profile.DisplayName)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        _avatar.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = Theme.OutlineBrush, StrokeThickness = 1 });
        _captionText = string.Empty; // il saluto contiene il nome
    }

    /// <summary>Porta l'overlay sopra notch e barra.</summary>
    public void BringToTop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0)
            AccessNative.BringToTopmost(hwnd);
    }

    public void Render(AccessFrame frame, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(frame);
        RenderBackdrop(frame.Iris);
        RenderClock(now);
        _chrome.Opacity = frame.ClockOpacity;

        _cardScale.ScaleX = _cardScale.ScaleY = frame.CardScale;
        _card.Opacity = frame.CardOpacity;

        RenderRing(frame);
        RenderCaption(AccessIdentity.Caption(frame.Caption, _profile.DisplayName, now.Hour), frame.Caption);
        _caption.Opacity = frame.CaptionOpacity;
    }

    private void RenderBackdrop(double iris)
    {
        if (iris >= 0.999)
        {
            _backdrop.Visibility = Visibility.Hidden;
            return;
        }
        _backdrop.Visibility = Visibility.Visible;
        if (iris <= 0.001)
        {
            _backdrop.Data = _screen;
            return;
        }
        var radius = iris * _maxIris;
        var hole = new EllipseGeometry(_center, radius, radius);
        var data = new CombinedGeometry(GeometryCombineMode.Exclude, _screen, hole);
        data.Freeze();
        _backdrop.Data = data;
    }

    private void RenderClock(DateTime now)
    {
        var text = now.ToString("HH:mm", Italian);
        if (text == _clockText)
            return;
        _clockText = text;
        _clock.Dots = DotText.Render(text);
        _date.Dots = DotText.Render(now.ToString("dddd d MMMM", Italian));
        Place(_clock, _clock.Dots.Width * _clock.Pitch, _height * 0.13);
        Place(_date, _date.Dots.Width * _date.Pitch, _height * 0.13 + 7 * _clock.Pitch + 18);
    }

    private void RenderRing(AccessFrame frame)
    {
        _ring.Opacity = frame.RingOpacity;
        var radius = RingRadius * (1 + frame.RingSpread);
        var size = RingDotSize * (1 + 0.35 * frame.RingSpread);
        for (var i = 0; i < RingDots; i++)
        {
            var angle = -Math.PI / 2 + i * 2 * Math.PI / RingDots;
            var dot = _ringDots[i];
            dot.Width = dot.Height = size;
            Canvas.SetLeft(dot, _center.X + radius * Math.Cos(angle) - size / 2);
            Canvas.SetTop(dot, _center.Y + radius * Math.Sin(angle) - size / 2);
        }

        var lit = AccessTimeline.LitDots(frame.RingLit, RingDots);
        var head = frame.RingHead && lit is > 0 and < RingDots;
        if (lit == _litDots && head == _head)
            return;
        _litDots = lit;
        _head = head;
        for (var i = 0; i < RingDots; i++)
            _ringDots[i].Fill = head && i == lit - 1 ? Theme.RedBrush : i < lit ? Theme.WhiteBrush : _dotOff;
    }

    private void RenderCaption(string text, AccessCaption caption)
    {
        text = text.ToUpper(Italian);
        if (text == _captionText)
            return;
        _captionText = text;
        _caption.DotColor = caption == AccessCaption.Locked ? Theme.Grey : Theme.White;
        _caption.Dots = DotText.Render(text);
        Place(_caption, _caption.Dots.Width * _caption.Pitch, _center.Y + RingRadius + 128);
    }

    private void BuildChrome()
    {
        _chrome.Children.Add(_clock);
        _chrome.Children.Add(_date);
    }

    private void BuildCard()
    {
        _cardScale.CenterX = _center.X;
        _cardScale.CenterY = _center.Y;
        _card.RenderTransform = _cardScale;

        Canvas.SetLeft(_avatar, _center.X - AvatarSize / 2);
        Canvas.SetTop(_avatar, _center.Y - AvatarSize / 2);
        _card.Children.Add(_avatar);

        _name.Width = _width;
        Canvas.SetTop(_name, _center.Y + RingRadius + 26);
        _card.Children.Add(_name);

        _userName.Width = _width;
        Canvas.SetTop(_userName, _center.Y + RingRadius + 68);
        _card.Children.Add(_userName);

        _card.Children.Add(_caption);
        SetProfile(_profile);
    }

    private void BuildRing()
    {
        for (var i = 0; i < RingDots; i++)
        {
            _ringDots[i] = new System.Windows.Shapes.Ellipse { Fill = _dotOff };
            _ring.Children.Add(_ringDots[i]);
        }
    }

    private void Place(FrameworkElement element, double width, double top)
    {
        Canvas.SetLeft(element, Math.Round((_width - width) / 2));
        Canvas.SetTop(element, Math.Round(top));
    }

    private static TextBlock CenteredText(double size, Brush foreground) => new()
    {
        FontFamily = Theme.BodyFont,
        FontSize = size,
        Foreground = foreground,
        TextAlignment = TextAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>Pannello nero semplice per un monitor secondario (non trasparente: costa poco).</summary>
public sealed class AccessCover : Window
{
    private readonly AccessNative.Rect _bounds;

    internal AccessCover(AccessNative.Rect bounds)
    {
        _bounds = bounds;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Theme.BlackBrush;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 100;
        Height = 100;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.MakeNonActivatingToolWindow(hwnd);
            AccessNative.PlaceTopmost(hwnd, _bounds);
        };
    }

    public void BringToTop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0)
            AccessNative.PlaceTopmost(hwnd, _bounds);
    }
}
