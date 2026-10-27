using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using NothingShell.App.Interop;
using NothingShell.App.Rendering;
using NothingShell.Core.Animation;
using NothingShell.Core.Design;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Media;
using NothingShell.Core.Notch;

namespace NothingShell.App.Notch;

/// <summary>
/// Notch in alto al centro (docs/03-notch.md). La finestra ha la dimensione massima ed è trasparente:
/// la pillola nera è animata al suo interno, e i pixel trasparenti lasciano passare i click.
/// </summary>
public sealed class NotchWindow : Window
{
    private const double ExpandedPadding = 20;
    private const double TitlePitch = 3;
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");
    private static readonly Duration Fade = new(TimeSpan.FromMilliseconds(120));

    private readonly IMediaService _media;
    private readonly NotchStateMachine _machine = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Spring _width;
    private readonly Spring _height;
    private readonly DotEqualizer _equalizer = new();

    private readonly Border _pill = new() { Background = Theme.BlackBrush, ClipToBounds = true };
    private readonly Grid _compact = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly Grid _expanded = new() { VerticalAlignment = VerticalAlignment.Top, Opacity = 0 };
    private readonly DotMatrixView _compactClock = new() { Pitch = 3 };
    private readonly Ellipse _liveDot = new() { Width = 8, Height = 8, Fill = Theme.RedBrush };
    private readonly DotMatrixView _equalizerView = new() { Pitch = 3 };

    private readonly StackPanel _mediaPanel = new();
    private readonly TextBlock _source = Text(11, Theme.GreyBrush);
    private readonly DotMatrixView _headerClock = new() { Pitch = 2, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly DotMatrixView _title = new() { Pitch = TitlePitch };
    private readonly TextBlock _artist = Text(13, Theme.GreyBrush);
    private readonly DotMatrixView _playPauseIcon = new() { Pitch = 3 };

    private readonly StackPanel _idlePanel = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly DotMatrixView _bigClock = new() { Pitch = 6, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly DotMatrixView _date = new() { Pitch = 2, DotColor = Theme.Grey, HorizontalAlignment = HorizontalAlignment.Center };

    private readonly DispatcherTimer _collapseTimer = new();
    private readonly DispatcherTimer _equalizerTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _marqueeTimer = new() { Interval = TimeSpan.FromMilliseconds(45) };

    private MediaSnapshot? _snapshot;
    private int _marqueeOffset;
    private TimeSpan _lastFrame;
    private bool _animating;

    public NotchWindow(IMediaService media)
    {
        ArgumentNullException.ThrowIfNull(media);
        _media = media;

        var idle = NotchLayout.For(NotchState.Idle);
        _width = new Spring(idle.Width);
        _height = new Spring(idle.Height);

        var host = NotchLayout.Host;
        Width = host.Width;
        Height = host.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "Nothing Shell — Notch";

        Content = BuildVisualTree();
        ApplyPillSize();

        _collapseTimer.Tick += (_, _) => Collapse();
        _equalizerTimer.Tick += (_, _) => _equalizerView.Dots = _equalizer.NextFrame();
        _marqueeTimer.Tick += (_, _) => RenderTitle(++_marqueeOffset);
        _media.Changed += snapshot => Dispatcher.BeginInvoke(() => OnMedia(snapshot));

        SourceInitialized += (_, _) =>
            NativeMethods.MakeNonActivatingToolWindow(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => PositionOnPrimaryScreen();
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea))
                PositionOnPrimaryScreen();
        };
    }

    public void SetTime(DateTime now)
    {
        var hhmm = now.ToString("HH:mm", Italian);
        _compactClock.Dots = DotText.Render(hhmm);
        _headerClock.Dots = DotText.Render(hhmm);
        _bigClock.Dots = DotText.Render(hhmm);
        _date.Dots = DotText.Render(now.ToString("dddd d MMMM", Italian));
    }

    private Grid BuildVisualTree()
    {
        // Pillola compatta: [punto rosso]  HH:MM  [equalizzatore]
        var compactHeight = NotchLayout.For(NotchState.Idle).Height;
        _compact.Height = compactHeight;
        _compactClock.HorizontalAlignment = HorizontalAlignment.Center;
        _compactClock.VerticalAlignment = VerticalAlignment.Center;
        _liveDot.HorizontalAlignment = HorizontalAlignment.Left;
        _liveDot.VerticalAlignment = VerticalAlignment.Center;
        _liveDot.Margin = new Thickness(18, 0, 0, 0);
        _equalizerView.HorizontalAlignment = HorizontalAlignment.Right;
        _equalizerView.VerticalAlignment = VerticalAlignment.Center;
        _equalizerView.Margin = new Thickness(0, 0, 18, 0);
        _compact.Children.Add(_compactClock);
        _compact.Children.Add(_liveDot);
        _compact.Children.Add(_equalizerView);
        SetActivityVisible(false);

        // Pannello espanso con media
        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(_source);
        header.Children.Add(_headerClock);
        _source.VerticalAlignment = VerticalAlignment.Center;
        _title.HorizontalAlignment = HorizontalAlignment.Left;
        _artist.Margin = new Thickness(0, 6, 0, 0);
        _artist.TextTrimming = TextTrimming.CharacterEllipsis;

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
        };
        controls.Children.Add(DotButton(Glyph(DotIcons.Previous), () => _media.PreviousAsync()));
        _playPauseIcon.Dots = Glyph(DotIcons.Play);
        controls.Children.Add(DotButton(_playPauseIcon, () => _media.PlayPauseAsync()));
        controls.Children.Add(DotButton(Glyph(DotIcons.Next), () => _media.NextAsync()));

        _mediaPanel.Children.Add(header);
        _mediaPanel.Children.Add(_title);
        _mediaPanel.Children.Add(_artist);
        _mediaPanel.Children.Add(controls);

        // Pannello espanso senza media: orologio grande + data
        _idlePanel.Margin = new Thickness(0, 22, 0, 0);
        _date.Margin = new Thickness(0, 14, 0, 0);
        _idlePanel.Children.Add(_bigClock);
        _idlePanel.Children.Add(_date);

        var expandedSize = NotchLayout.For(NotchState.Expanded);
        _expanded.Width = expandedSize.Width;
        _expanded.Height = expandedSize.Height;
        _expanded.HorizontalAlignment = HorizontalAlignment.Center;
        _expanded.Margin = new Thickness(0);
        var inner = new Grid { Margin = new Thickness(ExpandedPadding, 16, ExpandedPadding, 12) };
        inner.Children.Add(_mediaPanel);
        inner.Children.Add(_idlePanel);
        _expanded.Children.Add(inner);
        _expanded.IsHitTestVisible = false;
        ShowMediaPanel(false);

        var content = new Grid();
        content.Children.Add(_compact);
        content.Children.Add(_expanded);
        _pill.Child = content;
        _pill.HorizontalAlignment = HorizontalAlignment.Center;
        _pill.VerticalAlignment = VerticalAlignment.Top;
        _pill.Margin = new Thickness(0, NotchLayout.TopMargin, 0, 0);
        _pill.MouseEnter += (_, _) => Apply(_machine.OnPointerEnter());
        _pill.MouseLeave += (_, _) => OnPointerLeave();
        _pill.ContextMenu = BuildContextMenu();

        var root = new Grid(); // senza Background: le zone vuote non intercettano il mouse
        root.Children.Add(_pill);
        return root;
    }

    public event Action? SettingsRequested;

    private ContextMenu BuildContextMenu()
    {
        var settings = new MenuItem { Header = "Impostazioni…" };
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        var exit = new MenuItem { Header = "Esci da Nothing Shell" };
        exit.Click += (_, _) => Application.Current.Shutdown();
        var menu = new ContextMenu();
        menu.Items.Add(settings);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        return menu;
    }

    private void OnPointerLeave()
    {
        _machine.OnPointerLeave(_clock.Elapsed);
        if (_machine.CollapseDeadline is { } deadline)
        {
            _collapseTimer.Interval = deadline - _clock.Elapsed;
            _collapseTimer.Start();
        }
    }

    private void Collapse()
    {
        _collapseTimer.Stop();
        Apply(_machine.Tick(_clock.Elapsed));
    }

    private void OnMedia(MediaSnapshot? snapshot)
    {
        var titleChanged = snapshot?.Title != _snapshot?.Title;
        _snapshot = snapshot;

        ShowMediaPanel(snapshot is not null);
        if (snapshot is not null)
        {
            _source.Text = snapshot.SourceApp.ToUpperInvariant();
            _artist.Text = snapshot.Artist;
            _playPauseIcon.Dots = Glyph(snapshot.IsPlaying ? DotIcons.Pause : DotIcons.Play);
            if (titleChanged)
                _marqueeOffset = 0;
            RenderTitle(_marqueeOffset);
        }

        Apply(_machine.OnMediaPlayingChanged(snapshot?.IsPlaying == true));
        UpdateMarquee();
    }

    /// <summary>Allinea la UI allo stato corrente della macchina a stati.</summary>
    private void Apply(bool changed)
    {
        if (!changed)
            return;

        var state = _machine.State;
        var size = NotchLayout.For(state);
        _width.Target = size.Width;
        _height.Target = size.Height;
        StartAnimation();

        var expanded = state == NotchState.Expanded;
        FadeTo(_expanded, expanded ? 1 : 0);
        FadeTo(_compact, expanded ? 0 : 1);
        _expanded.IsHitTestVisible = expanded;
        SetActivityVisible(state == NotchState.Activity);
        UpdateMarquee();
    }

    private void SetActivityVisible(bool visible)
    {
        _liveDot.Visibility = _equalizerView.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        if (visible)
        {
            _equalizerTimer.Start();
            var pulse = new DoubleAnimation(1, 0.25, new Duration(TimeSpan.FromMilliseconds(900)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            _liveDot.BeginAnimation(OpacityProperty, pulse);
        }
        else
        {
            _equalizerTimer.Stop();
            _liveDot.BeginAnimation(OpacityProperty, null);
        }
    }

    private void ShowMediaPanel(bool hasMedia)
    {
        _mediaPanel.Visibility = hasMedia ? Visibility.Visible : Visibility.Collapsed;
        _idlePanel.Visibility = hasMedia ? Visibility.Collapsed : Visibility.Visible;
    }

    private static readonly int TitleViewportColumns =
        (int)((NotchLayout.For(NotchState.Expanded).Width - 2 * ExpandedPadding) / TitlePitch);

    private void RenderTitle(int offset)
    {
        var title = _snapshot?.Title ?? string.Empty;
        _title.Dots = DotText.RenderMarquee(title, Math.Min(TitleViewportColumns, DotText.Measure(title)), offset);
    }

    private void UpdateMarquee()
    {
        var scroll = _machine.State == NotchState.Expanded
                     && _snapshot is { } s
                     && DotText.NeedsMarquee(s.Title, TitleViewportColumns);
        if (scroll)
        {
            RenderTitle(_marqueeOffset);
            _marqueeTimer.Start();
        }
        else
        {
            _marqueeTimer.Stop();
            _marqueeOffset = 0;
            if (_snapshot is not null)
                RenderTitle(0);
        }
    }

    private void StartAnimation()
    {
        if (_animating)
            return;
        _animating = true;
        _lastFrame = _clock.Elapsed;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        var dt = (now - _lastFrame).TotalSeconds;
        _lastFrame = now;

        var moving = _width.Step(dt) | _height.Step(dt);
        ApplyPillSize();
        if (moving)
            return;
        CompositionTarget.Rendering -= OnFrame;
        _animating = false;
    }

    private void ApplyPillSize()
    {
        var size = new Size2(Math.Max(1, _width.Value), Math.Max(1, _height.Value));
        _pill.Width = size.Width;
        _pill.Height = size.Height;
        _pill.CornerRadius = new CornerRadius(NotchLayout.CornerRadius(size));
    }

    private void PositionOnPrimaryScreen()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }

    private static void FadeTo(UIElement element, double opacity) =>
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, Fade));

    private static DotGrid Glyph(DotGlyph glyph) => DotGrid.From(glyph);

    private static Border DotButton(DotGrid icon, Func<Task> action) =>
        DotButton(new DotMatrixView { Pitch = 3, Dots = icon }, action);

    private static Border DotButton(DotMatrixView icon, Func<Task> action)
    {
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var button = new Border
        {
            Width = 56,
            Height = 36,
            CornerRadius = new CornerRadius(18),
            Margin = new Thickness(6, 0, 6, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = icon,
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

    private static TextBlock Text(double size, Brush foreground) => new()
    {
        FontFamily = Theme.BodyFont,
        FontSize = size,
        Foreground = foreground,
    };
}
