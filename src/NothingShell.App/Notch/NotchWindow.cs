using System.Diagnostics;
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
using NothingShell.Core.Sensors;

namespace NothingShell.App.Notch;

/// <summary>
/// Notch in alto al centro (docs/03-notch.md, docs/08-notch-mail-pages.md). La finestra ha la
/// dimensione massima ed è trasparente: la pillola nera è animata al suo interno, e i pixel
/// trasparenti lasciano passare i click. Espanso, mostra pagine che si cambiano con uno swipe.
/// </summary>
public sealed class NotchWindow : Window
{
    private const int WmMouseHWheel = 0x020E;
    private const double AlertTextPitch = 2.5;
    private static readonly Duration Fade = new(TimeSpan.FromMilliseconds(120));
    private static readonly Duration Slide = new(TimeSpan.FromMilliseconds(150));
    private static readonly int AlertViewportColumns =
        (int)((NotchLayout.For(NotchState.Alert).Width - 80) / AlertTextPitch);

    private readonly NotchStateMachine _machine = new();
    private readonly NotchPager _pager = new();
    private readonly SwipeDetector _swipe = new();
    private readonly Countdown _countdown = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Spring _width;
    private readonly Spring _height;
    private readonly DotEqualizer _equalizer = new();

    private readonly Border _pill = new() { Background = Theme.BlackBrush, ClipToBounds = true };

    // Compatto: [punto rosso]  HH:MM o timer  [equalizzatore]
    private readonly Grid _compact = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly DotMatrixView _compactClock = new() { Pitch = 3 };
    private readonly Ellipse _liveDot = new() { Width = 8, Height = 8, Fill = Theme.RedBrush };
    private readonly DotMatrixView _equalizerView = new() { Pitch = 3 };

    // Avviso: [busta] oggetto che scorre
    private readonly Grid _alert = new() { VerticalAlignment = VerticalAlignment.Top, Opacity = 0 };
    private readonly DotMatrixView _alertIcon = new() { Pitch = 2.5, Dots = DotGrid.From(DotIcons.Mail), DotColor = Theme.Red };
    private readonly DotMatrixView _alertText = new() { Pitch = AlertTextPitch };

    // Espanso: pagine + indicatori
    private readonly Grid _expanded = new() { VerticalAlignment = VerticalAlignment.Top, Opacity = 0 };
    private readonly Grid _pageHost = new() { ClipToBounds = true };
    private readonly StackPanel _indicators = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Dictionary<NotchPage, NotchPageView> _pages = [];
    private readonly HomePage _home;
    private readonly MailPage _mail = new();
    private readonly TimerPage _timerPage;
    private readonly SystemPage _system = new();

    private readonly DispatcherTimer _deadlineTimer = new();
    private readonly DispatcherTimer _equalizerTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _alertMarquee = new() { Interval = TimeSpan.FromMilliseconds(45) };
    private readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private MediaSnapshot? _media;
    private DateTime _now = DateTime.Now;
    private string _alertMessage = string.Empty;
    private NotchPage? _alertPage;
    private int _alertOffset;
    private TimeSpan _lastFrame;
    private bool _animating;

    public NotchWindow(IMediaService media)
    {
        ArgumentNullException.ThrowIfNull(media);
        _home = new HomePage(media);
        _timerPage = new TimerPage(StartTimer, StopTimer);

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
        ShowPage(NotchPage.Home, animate: false);

        _deadlineTimer.Tick += (_, _) => OnDeadline();
        _equalizerTimer.Tick += (_, _) => _equalizerView.Dots = _equalizer.NextFrame();
        _alertMarquee.Tick += (_, _) => RenderAlertText(++_alertOffset);
        _countdownTimer.Tick += (_, _) => OnCountdownTick();
        media.Changed += snapshot => Dispatcher.BeginInvoke(() => OnMedia(snapshot));

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.MakeNonActivatingToolWindow(hwnd);
            HwndSource.FromHwnd(hwnd)?.AddHook(OnWindowMessage);
        };
        Loaded += (_, _) => PositionOnPrimaryScreen();
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea))
                PositionOnPrimaryScreen();
        };
    }

    public event Action? SettingsRequested;

    public void SetTime(DateTime now)
    {
        _now = now;
        _home.SetTime(now);
        UpdateCompactClock();
    }

    /// <summary>Mostra l'oggetto della mail per qualche secondo; passandoci sopra si apre la pagina Posta.</summary>
    public void ShowMailAlert(MailItem mail)
    {
        ArgumentNullException.ThrowIfNull(mail);
        ShowAlert(mail.Subject, isMail: true, NotchPage.Mail);
    }

    public void SetMail(IReadOnlyList<MailItem> mails, string emptyMessage) => _mail.Update(mails, emptyMessage);

    public void SetSystem(SystemStats stats) => _system.Update(stats);

    public void SetBattery(BatteryStatus battery) => _system.Update(battery);

    /// <summary>Prepara la pillola in uno stato e una pagina senza animazioni (per <c>--snapshot</c>).</summary>
    public FrameworkElement PrepareSnapshot(NotchState state, NotchPage page)
    {
        var size = NotchLayout.For(state);
        _width.SnapTo(size.Width);
        _height.SnapTo(size.Height);
        ApplyPillSize();
        ShowPage(page, animate: false);
        foreach (var (layer, visible) in new (UIElement, bool)[]
                 {
                     (_compact, state is NotchState.Idle or NotchState.Activity),
                     (_alert, state == NotchState.Alert),
                     (_expanded, state == NotchState.Expanded),
                 })
        {
            layer.BeginAnimation(OpacityProperty, null);
            layer.Opacity = visible ? 1 : 0;
        }
        SetActivityVisible(state == NotchState.Activity);
        return _pill;
    }

    /// <summary>Avvia il timer come dal pulsante della pagina (per <c>--snapshot</c>).</summary>
    public void StartTimer(TimeSpan duration)
    {
        _countdown.Start(duration, _clock.Elapsed);
        _countdownTimer.Start();
        OnCountdownTick();
        Apply(_machine.OnActivityChanged(HasActivity));
    }

    private bool HasActivity => _countdown.IsRunning || _media?.IsPlaying == true;

    private Grid BuildVisualTree()
    {
        _compact.Height = NotchLayout.For(NotchState.Idle).Height;
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

        var alertSize = NotchLayout.For(NotchState.Alert);
        _alert.Width = alertSize.Width;
        _alert.Height = alertSize.Height;
        _alert.HorizontalAlignment = HorizontalAlignment.Center;
        _alertIcon.HorizontalAlignment = HorizontalAlignment.Left;
        _alertIcon.VerticalAlignment = VerticalAlignment.Center;
        _alertIcon.Margin = new Thickness(20, 0, 0, 0);
        _alertText.HorizontalAlignment = HorizontalAlignment.Left;
        _alertText.VerticalAlignment = VerticalAlignment.Center;
        _alertText.Margin = new Thickness(60, 0, 20, 0);
        _alert.Children.Add(_alertIcon);
        _alert.Children.Add(_alertText);

        foreach (var page in new NotchPageView[] { _home, _mail, _timerPage, _system })
        {
            _pages[page.Page] = page;
            page.Visibility = Visibility.Collapsed;
            page.RenderTransform = new TranslateTransform();
            _pageHost.Children.Add(page);
            _indicators.Children.Add(new Ellipse { Width = 5, Height = 5, Margin = new Thickness(4, 0, 4, 0) });
        }

        var expandedSize = NotchLayout.For(NotchState.Expanded);
        _expanded.Width = expandedSize.Width;
        _expanded.Height = expandedSize.Height;
        _expanded.HorizontalAlignment = HorizontalAlignment.Center;
        _expanded.RowDefinitions.Add(new RowDefinition());
        _expanded.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _pageHost.Margin = new Thickness(NotchUi.Padding, 16, NotchUi.Padding, 4);
        _indicators.Margin = new Thickness(0, 0, 0, 12);
        Grid.SetRow(_indicators, 1);
        _expanded.Children.Add(_pageHost);
        _expanded.Children.Add(_indicators);
        _expanded.IsHitTestVisible = false;

        var content = new Grid();
        content.Children.Add(_compact);
        content.Children.Add(_alert);
        content.Children.Add(_expanded);
        _pill.Child = content;
        _pill.HorizontalAlignment = HorizontalAlignment.Center;
        _pill.VerticalAlignment = VerticalAlignment.Top;
        _pill.Margin = new Thickness(0, NotchLayout.TopMargin, 0, 0);
        _pill.MouseEnter += (_, _) => OnPointerEnter();
        _pill.MouseLeave += (_, _) => OnPointerLeave();
        _pill.MouseWheel += (_, e) =>
        {
            e.Handled = true;
            ApplySwipe(_swipe.OnVerticalWheel(e.Delta, _clock.Elapsed));
        };
        _pill.PreviewMouseLeftButtonDown += (_, e) => _swipe.OnDragStart(e.GetPosition(_pill).X);
        _pill.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                ApplySwipe(_swipe.OnDragMove(e.GetPosition(_pill).X));
        };
        _pill.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_swipe.OnDragEnd())
                e.Handled = true; // il trascinamento ha cambiato pagina: niente clic sui pulsanti
        };
        _pill.ContextMenu = BuildContextMenu();

        var root = new Grid(); // senza Background: le zone vuote non intercettano il mouse
        root.Children.Add(_pill);
        return root;
    }

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

    /// <summary>Scorrimento orizzontale del touchpad: WPF non lo espone, arriva come WM_MOUSEHWHEEL.</summary>
    private nint OnWindowMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmMouseHWheel && _machine.State == NotchState.Expanded && _pill.IsMouseOver)
        {
            var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            ApplySwipe(_swipe.OnHorizontalWheel(delta, _clock.Elapsed));
            handled = true;
        }
        return 0;
    }

    private void ApplySwipe(SwipeDirection direction)
    {
        if (_machine.State != NotchState.Expanded || direction == SwipeDirection.None)
            return;
        if (_pager.Apply(direction))
            ShowPage(_pager.Current, animate: true, fromRight: direction == SwipeDirection.Next);
    }

    private void ShowPage(NotchPage page, bool animate, bool fromRight = true)
    {
        _pager.Show(page);
        foreach (var (kind, view) in _pages)
        {
            var visible = kind == page;
            view.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible)
                view.OnHidden();
            else if (_machine.State == NotchState.Expanded)
                view.OnShown();
        }

        var index = _pager.Index;
        for (var i = 0; i < _indicators.Children.Count; i++)
        {
            var dot = (Ellipse)_indicators.Children[i];
            dot.Fill = i == index ? Theme.WhiteBrush : Theme.GreyBrush;
            dot.Width = dot.Height = i == index ? 6 : 5;
        }

        if (!animate)
            return;
        var shown = _pages[page];
        var transform = (TranslateTransform)shown.RenderTransform;
        transform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(fromRight ? 40 : -40, 0, Slide) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        shown.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Slide));
    }

    private void OnPointerEnter()
    {
        var alertPage = _machine.State == NotchState.Alert ? _alertPage : null;
        Apply(_machine.OnPointerEnter());
        if (alertPage is { } page)
            ShowPage(page, animate: false);
    }

    private void OnPointerLeave()
    {
        _machine.OnPointerLeave(_clock.Elapsed);
        ScheduleDeadline();
    }

    private void OnDeadline()
    {
        _deadlineTimer.Stop();
        Apply(_machine.Tick(_clock.Elapsed));
        ScheduleDeadline();
    }

    private void ScheduleDeadline()
    {
        if (_machine.NextDeadline is not { } deadline)
            return;
        _deadlineTimer.Interval = deadline > _clock.Elapsed ? deadline - _clock.Elapsed : TimeSpan.Zero;
        _deadlineTimer.Start();
    }

    private void ShowAlert(string message, bool isMail, NotchPage? page)
    {
        _alertMessage = message.Length > 0 ? message : "(senza oggetto)";
        _alertPage = page;
        _alertIcon.Visibility = isMail ? Visibility.Visible : Visibility.Collapsed;
        _alertText.Margin = new Thickness(isMail ? 60 : 20, 0, 20, 0);
        _alertText.DotColor = isMail ? Theme.White : Theme.Red;
        _alertOffset = 0;
        RenderAlertText(0);
        if (_machine.State == NotchState.Expanded)
        {
            if (page is { } p) // già aperto: si va direttamente alla pagina
                ShowPage(p, animate: true);
            return;
        }
        // un secondo avviso durante il primo: lo sostituisce e ne rinnova la durata
        var changed = _machine.OnAlert(_clock.Elapsed);
        Apply(changed || _machine.State == NotchState.Alert);
        ScheduleDeadline();
    }

    private void RenderAlertText(int offset)
    {
        var columns = Math.Min(AlertViewportColumns, DotText.Measure(_alertMessage));
        _alertText.Dots = DotText.RenderMarquee(_alertMessage, columns, offset);
    }

    private void OnMedia(MediaSnapshot? snapshot)
    {
        _media = snapshot;
        _home.ShowMedia(snapshot);
        Apply(_machine.OnActivityChanged(HasActivity));
        SetActivityVisible(_machine.State == NotchState.Activity); // musica in pausa con timer attivo: niente equalizzatore
    }

    private void StopTimer()
    {
        _countdown.Stop();
        _countdownTimer.Stop();
        OnCountdownTick();
        Apply(_machine.OnActivityChanged(HasActivity));
    }

    private void OnCountdownTick()
    {
        var now = _clock.Elapsed;
        if (_countdown.CheckFinished(now))
        {
            _countdownTimer.Stop();
            Apply(_machine.OnActivityChanged(HasActivity));
            ShowAlert("TEMPO SCADUTO", isMail: false, NotchPage.Timer);
        }
        _timerPage.Update(_countdown.IsRunning, _countdown.Remaining(now));
        UpdateCompactClock();
    }

    /// <summary>La pillola compatta mostra il tempo rimanente se un timer è in corso, altrimenti l'ora.</summary>
    private void UpdateCompactClock()
    {
        var text = _countdown.IsRunning
            ? Countdown.Format(_countdown.Remaining(_clock.Elapsed))
            : _now.ToString("HH:mm", NotchUi.Italian);
        _compactClock.Dots = DotText.Render(text);
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
        var alert = state == NotchState.Alert;
        FadeTo(_expanded, expanded ? 1 : 0);
        FadeTo(_alert, alert ? 1 : 0);
        FadeTo(_compact, expanded || alert ? 0 : 1);
        _expanded.IsHitTestVisible = expanded;
        SetActivityVisible(state == NotchState.Activity);

        if (alert && DotText.NeedsMarquee(_alertMessage, AlertViewportColumns))
            _alertMarquee.Start();
        else
            _alertMarquee.Stop();

        var page = _pages[_pager.Current];
        if (expanded)
            page.OnShown();
        else
            page.OnHidden();
    }

    private void SetActivityVisible(bool visible)
    {
        _liveDot.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        var music = visible && _media?.IsPlaying == true;
        _equalizerView.Visibility = music ? Visibility.Visible : Visibility.Hidden;
        if (music)
            _equalizerTimer.Start();
        else
            _equalizerTimer.Stop();

        if (visible)
        {
            var pulse = new DoubleAnimation(1, 0.25, new Duration(TimeSpan.FromMilliseconds(900)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            _liveDot.BeginAnimation(OpacityProperty, pulse);
        }
        else
        {
            _liveDot.BeginAnimation(OpacityProperty, null);
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
}
