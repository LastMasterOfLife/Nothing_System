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
using NothingShell.App.Services;
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
    private const double LevelPitch = 5;
    private static readonly TimeSpan LevelDuration = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(4);
    private static readonly int LevelColumns = (int)((NotchLayout.For(NotchState.Alert).Width - 56 - 100) / LevelPitch);
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

    // Compatto: [punto rosso | microfono e webcam]  HH:MM o timer  [equalizzatore | download]
    private readonly Grid _compact = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly DotMatrixView _compactClock = new() { Pitch = 3 };
    private readonly Ellipse _liveDot = new() { Width = 8, Height = 8, Fill = Theme.RedBrush };
    private readonly DotMatrixView _equalizerView = new() { Pitch = 3 };
    private readonly StackPanel _privacyIcons = new() { Orientation = Orientation.Horizontal };
    private readonly DotMatrixView _micIcon = new() { Pitch = 2.5, Dots = DotGrid.From(DotIcons.Microphone), DotColor = Theme.Red };
    private readonly DotMatrixView _camIcon = new() { Pitch = 2.5, Dots = DotGrid.From(DotIcons.Camera), DotColor = Theme.Red };
    private readonly StackPanel _downloadView = new() { Orientation = Orientation.Horizontal };
    private readonly DotMatrixView _downloadIcon = new() { Pitch = 2.5, Dots = DotGrid.From(DotIcons.Download) };
    private readonly DotMatrixView _downloadText = new() { Pitch = 2 };

    // Avviso: [icona] testo che scorre, oppure [icona] livello a punti e valore (volume, luminosità)
    private readonly Grid _alert = new() { VerticalAlignment = VerticalAlignment.Top, Opacity = 0 };
    private readonly DotMatrixView _alertIcon = new() { Pitch = 2.5, Dots = DotGrid.From(DotIcons.Mail), DotColor = Theme.Red };
    private readonly DotMatrixView _alertText = new() { Pitch = AlertTextPitch };
    private readonly DotMatrixView _alertLevel = new() { Pitch = LevelPitch, OffColor = Theme.DotOff };
    private readonly DotMatrixView _alertValue = new() { Pitch = AlertTextPitch };

    // Scheda: contenuto con pulsanti (screenshot, download completato)
    private readonly Border _card = new() { VerticalAlignment = VerticalAlignment.Top, Opacity = 0, IsHitTestVisible = false };

    // Espanso: pagine + indicatori
    private readonly Grid _expanded = new() { VerticalAlignment = VerticalAlignment.Top, Opacity = 0 };
    private readonly Grid _pageHost = new() { ClipToBounds = true };
    private readonly StackPanel _indicators = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Dictionary<NotchPage, NotchPageView> _pages = [];
    private readonly HomePage _home;
    private readonly AgendaPage _agenda;
    private readonly MailPage _mail = new();
    private readonly TimerPage _timerPage;
    private readonly SystemPage _system = new();

    private readonly DispatcherTimer _deadlineTimer = new();
    private readonly DispatcherTimer _equalizerTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _alertMarquee = new() { Interval = TimeSpan.FromMilliseconds(45) };
    private readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private MediaSnapshot? _media;
    private PrivacyState _privacy = PrivacyState.None;
    private IReadOnlyList<ActiveDownload> _downloads = [];
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
        _agenda = new AgendaPage(e => AppointmentOpened?.Invoke(e));
        _timerPage = new TimerPage(StartTimer, StopTimer);
        _mail.Opened += mail => MailOpened?.Invoke(mail);

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

    /// <summary>Clic su una mail della pagina Posta.</summary>
    public event Action<MailItem>? MailOpened;

    /// <summary>Clic su un appuntamento della pagina Agenda.</summary>
    public event Action<CalendarEvent>? AppointmentOpened;

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
        ShowBanner(mail.Subject, DotIcons.Mail, Theme.Red, Theme.White, NotchPage.Mail, duration: null);
    }

    /// <summary>Volume o luminosità: icona, barra a punti e valore per 1,5 s; ogni tasto rinnova la durata.</summary>
    public void ShowLevel(DotGlyph icon, double level, bool muted)
    {
        ArgumentNullException.ThrowIfNull(icon);
        _alertMessage = string.Empty;
        _alertPage = null;
        SetAlertIcon(icon, muted ? Theme.Red : Theme.White);
        _alertText.Visibility = Visibility.Collapsed;
        _alertLevel.Visibility = Visibility.Visible;
        _alertValue.Visibility = Visibility.Visible;
        _alertLevel.Dots = DotBar.Render(muted ? 0 : level, LevelColumns);
        _alertValue.Dots = DotText.Render(muted ? "MUTO" : $"{Math.Round(level * 100):0}");
        _alertValue.DotColor = muted ? Theme.Red : Theme.White;
        RaiseAlert(LevelDuration);
    }

    /// <summary>Avviso breve con icona (cuffie collegate…).</summary>
    public void ShowNotice(DotGlyph icon, Color iconColor, string text)
    {
        ArgumentNullException.ThrowIfNull(icon);
        ShowBanner(text, icon, iconColor, Theme.White, page: null, NoticeDuration);
    }

    /// <summary>Scheda con contenuto e pulsanti; col mouse sopra resta aperta.</summary>
    public void ShowCard(UIElement content, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(content);
        _card.Child = content;
        Apply(_machine.OnCard(_clock.Elapsed, duration) || _machine.State == NotchState.Card);
        ScheduleDeadline();
    }

    public void DismissCard()
    {
        Apply(_machine.DismissCard());
        ScheduleDeadline();
    }

    /// <summary>Microfono e webcam in uso: icone rosse a sinistra nella pillola, dettaglio nella pagina Sistema.</summary>
    public void SetPrivacy(PrivacyState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _privacy = state;
        _micIcon.Visibility = state.Microphone.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _camIcon.Visibility = state.Camera.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _camIcon.Margin = new Thickness(state.Microphone.Count > 0 ? 6 : 0, 0, 0, 0);
        _system.SetActivities(state, _downloads);
        OnActivitiesChanged();
    }

    /// <summary>Download in corso: freccia e dimensione a destra nella pillola.</summary>
    public void SetDownloads(IReadOnlyList<ActiveDownload> downloads)
    {
        ArgumentNullException.ThrowIfNull(downloads);
        _downloads = downloads;
        if (downloads.Count > 0)
            _downloadText.Dots = DotText.Render(DownloadTracker.FormatShort(downloads.Sum(d => d.Bytes)));
        _system.SetActivities(_privacy, downloads);
        OnActivitiesChanged();
    }

    public void SetMail(IReadOnlyList<MailItem> mails, string emptyMessage) => _mail.Update(mails, emptyMessage);

    /// <summary>Appuntamenti: pagina Agenda e, sotto l'ora della Home, il prossimo.</summary>
    public void SetAgenda(IReadOnlyList<CalendarEvent> events, DateTimeOffset now, string emptyMessage)
    {
        ArgumentNullException.ThrowIfNull(events);
        _agenda.Update(events, now, emptyMessage);
        _home.SetNext(Agenda.Next(events, now), now);
    }

    /// <summary>Apre il notch su una pagina (pulsanti delle schede): si richiude quando il mouse esce.</summary>
    public void OpenPage(NotchPage page)
    {
        var changed = _machine.DismissCard();
        changed |= _machine.OnPointerEnter();
        ShowPage(page, animate: false);
        Apply(changed);
        if (!_pill.IsMouseOver)
            OnPointerLeave();
    }

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
                     (_card, state == NotchState.Card),
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

    private bool HasActivity => _countdown.IsRunning || _media?.IsPlaying == true || _privacy.Any || _downloads.Count > 0;

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
        _privacyIcons.HorizontalAlignment = HorizontalAlignment.Left;
        _privacyIcons.VerticalAlignment = VerticalAlignment.Center;
        _privacyIcons.Margin = new Thickness(18, 0, 0, 0);
        _micIcon.VerticalAlignment = VerticalAlignment.Center;
        _camIcon.VerticalAlignment = VerticalAlignment.Center;
        _privacyIcons.Children.Add(_micIcon);
        _privacyIcons.Children.Add(_camIcon);
        _downloadView.HorizontalAlignment = HorizontalAlignment.Right;
        _downloadView.VerticalAlignment = VerticalAlignment.Center;
        _downloadView.Margin = new Thickness(0, 0, 18, 0);
        _downloadIcon.VerticalAlignment = VerticalAlignment.Center;
        _downloadText.VerticalAlignment = VerticalAlignment.Center;
        _downloadText.Margin = new Thickness(6, 0, 0, 0);
        _downloadView.Children.Add(_downloadIcon);
        _downloadView.Children.Add(_downloadText);
        _compact.Children.Add(_compactClock);
        _compact.Children.Add(_liveDot);
        _compact.Children.Add(_equalizerView);
        _compact.Children.Add(_privacyIcons);
        _compact.Children.Add(_downloadView);
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
        _alertLevel.HorizontalAlignment = HorizontalAlignment.Left;
        _alertLevel.VerticalAlignment = VerticalAlignment.Center;
        _alertLevel.Margin = new Thickness(56, 0, 0, 0);
        _alertValue.HorizontalAlignment = HorizontalAlignment.Right;
        _alertValue.VerticalAlignment = VerticalAlignment.Center;
        _alertValue.Margin = new Thickness(0, 0, 22, 0);
        _alert.Children.Add(_alertIcon);
        _alert.Children.Add(_alertText);
        _alert.Children.Add(_alertLevel);
        _alert.Children.Add(_alertValue);

        var cardSize = NotchLayout.For(NotchState.Card);
        _card.Width = cardSize.Width;
        _card.Height = cardSize.Height;
        _card.HorizontalAlignment = HorizontalAlignment.Center;
        _card.Padding = new Thickness(NotchUi.Padding, 14, NotchUi.Padding, 14);

        foreach (var page in new NotchPageView[] { _home, _agenda, _mail, _timerPage, _system })
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
        content.Children.Add(_card);
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

    /// <summary>Avviso con testo che scorre: icona facoltativa a sinistra, durata standard se non indicata.</summary>
    private void ShowBanner(string message, DotGlyph? icon, Color iconColor, Color textColor, NotchPage? page, TimeSpan? duration)
    {
        _alertMessage = message.Length > 0 ? message : "(senza oggetto)";
        _alertPage = page;
        if (icon is not null)
            SetAlertIcon(icon, iconColor);
        else
            _alertIcon.Visibility = Visibility.Collapsed;
        _alertText.Visibility = Visibility.Visible;
        _alertLevel.Visibility = Visibility.Collapsed;
        _alertValue.Visibility = Visibility.Collapsed;
        _alertText.Margin = new Thickness(icon is null ? 20 : 20 + (icon.Width * _alertIcon.Pitch) + 16, 0, 20, 0);
        _alertText.DotColor = textColor;
        _alertOffset = 0;
        RenderAlertText(0);
        if (_machine.State == NotchState.Expanded)
        {
            if (page is { } p) // già aperto: si va direttamente alla pagina
                ShowPage(p, animate: true);
            return;
        }
        RaiseAlert(duration);
    }

    private void SetAlertIcon(DotGlyph icon, Color color)
    {
        _alertIcon.Dots = DotGrid.From(icon);
        _alertIcon.DotColor = color;
        _alertIcon.Visibility = Visibility.Visible;
    }

    /// <summary>Un secondo avviso durante il primo lo sostituisce e ne rinnova la durata.</summary>
    private void RaiseAlert(TimeSpan? duration)
    {
        var changed = _machine.OnAlert(_clock.Elapsed, duration);
        Apply(changed || _machine.State == NotchState.Alert);
        ScheduleDeadline();
    }

    private void OnActivitiesChanged()
    {
        Apply(_machine.OnActivityChanged(HasActivity));
        SetActivityVisible(_machine.State == NotchState.Activity);
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
            ShowBanner("TEMPO SCADUTO", icon: null, Theme.Red, Theme.Red, NotchPage.Timer, duration: null);
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
        var card = state == NotchState.Card;
        FadeTo(_expanded, expanded ? 1 : 0);
        FadeTo(_alert, alert ? 1 : 0);
        FadeTo(_card, card ? 1 : 0);
        FadeTo(_compact, expanded || alert || card ? 0 : 1);
        _expanded.IsHitTestVisible = expanded;
        _card.IsHitTestVisible = card;
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

    /// <summary>
    /// A sinistra microfono e webcam in uso (hanno la precedenza sul punto rosso), a destra il download in corso
    /// (ha la precedenza sull'equalizzatore della musica).
    /// </summary>
    private void SetActivityVisible(bool visible)
    {
        var privacy = visible && _privacy.Any;
        _privacyIcons.Visibility = privacy ? Visibility.Visible : Visibility.Hidden;
        _liveDot.Visibility = visible && !privacy ? Visibility.Visible : Visibility.Hidden;
        var downloading = visible && _downloads.Count > 0;
        _downloadView.Visibility = downloading ? Visibility.Visible : Visibility.Hidden;
        var music = visible && !downloading && _media?.IsPlaying == true;
        _equalizerView.Visibility = music ? Visibility.Visible : Visibility.Hidden;
        if (music)
            _equalizerTimer.Start();
        else
            _equalizerTimer.Stop();

        if (visible && !privacy)
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
