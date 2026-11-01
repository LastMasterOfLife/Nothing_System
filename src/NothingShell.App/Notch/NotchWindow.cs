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
using NothingShell.Core.Agents;
using NothingShell.Core.Animation;
using NothingShell.Core.Connectors;
using NothingShell.Core.Design;
using NothingShell.Core.Dev;
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

    private readonly Border _pill = Theme.Panel(new Border { ClipToBounds = true });
    private readonly GlassBackdrop _glass;
    private bool _quiet;

    // Compatto: [punto rosso | microfono e webcam]  HH:MM o timer  [equalizzatore | download]
    private readonly Grid _compact = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly DotMatrixView _compactClock = new() { Pitch = 3, TextSize = 13 };
    private readonly Ellipse _liveDot = new() { Width = 8, Height = 8, Fill = Theme.AccentBrush };
    private readonly DotMatrixView _equalizerView = new() { Pitch = 3 };
    private readonly StackPanel _privacyIcons = new() { Orientation = Orientation.Horizontal };
    private readonly DotMatrixView _micIcon = new() { Pitch = 2.5, Dots = DotGrid.From(DotIcons.Microphone), DotColor = Theme.Critical };
    private readonly DotMatrixView _camIcon = new() { Pitch = 2.5, Dots = DotGrid.From(DotIcons.Camera), DotColor = Theme.Critical };
    private readonly StackPanel _downloadView = new() { Orientation = Orientation.Horizontal };
    private readonly DotMatrixView _downloadIcon = new() { Pitch = 2.5, Dots = DotGrid.From(DotIcons.Download) };
    private readonly DotMatrixView _downloadText = new() { Pitch = 2 };
    private readonly StackPanel _taskView = new() { Orientation = Orientation.Horizontal };
    private readonly DotMatrixView _taskIcon = new() { Pitch = 2.5, Dots = DotGrid.From(DotIcons.Terminal) };
    private readonly DotMatrixView _taskText = new() { Pitch = 2 };
    private readonly StackPanel _agentView = new() { Orientation = Orientation.Horizontal };
    private readonly DotMatrixView _agentIcon = new() { Pitch = 2.5, Dots = DotGrid.From(DotIcons.Agent) };
    private readonly DotMatrixView _agentText = new() { Pitch = 2 };

    // Avviso: [icona] testo che scorre, oppure [icona] livello a punti e valore (volume, luminosità)
    private readonly Grid _alert = new() { VerticalAlignment = VerticalAlignment.Top, Opacity = 0 };
    private readonly DotMatrixView _alertIcon = new() { Pitch = 2.5, Dots = DotGrid.From(DotIcons.Mail), DotColor = Theme.Accent };
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
    private readonly Dictionary<NotchPage, Ellipse> _indicatorDots = [];
    private readonly HomePage _home;
    private readonly AgendaPage _agenda;
    private readonly MailPage _mail = new();
    private readonly TimerPage _timerPage;
    private readonly SystemPage _system = new();
    private readonly WorkPage _work;
    private readonly ServersPage _servers;
    private readonly ClipboardPage _clipboard;
    private readonly PhonePage _phone;
    private readonly AgentsPage _agents = new();
    private readonly DevicesPage _devices;

    private readonly DispatcherTimer _deadlineTimer = new();
    private readonly DispatcherTimer _equalizerTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _alertMarquee = new() { Interval = TimeSpan.FromMilliseconds(45) };
    private readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _taskTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private MediaSnapshot? _media;
    private PrivacyState _privacy = PrivacyState.None;
    private IReadOnlyList<ActiveDownload> _downloads = [];
    private IReadOnlyList<RunningTask> _tasks = [];
    private bool _ciRunning;
    private AgentSnapshot[] _working = [];
    private DateTime _now = DateTime.Now;
    private string _alertMessage = string.Empty;
    private NotchPage? _alertPage;
    private int _alertOffset;
    private TimeSpan _lastFrame;
    private bool _animating;
    private nint _beforeAsk;

    public NotchWindow(IMediaService media, DevPageActions dev, ConnectedPageActions connected)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(dev);
        ArgumentNullException.ThrowIfNull(connected);
        _phone = new PhonePage(connected);
        _devices = new DevicesPage(connected);
        _home = new HomePage(media);
        _work = new WorkPage(dev.OpenTask);
        _servers = new ServersPage(dev.OpenServer, dev.KillServer);
        _clipboard = new ClipboardPage(dev.CopyClip, dev.ClipAction);
        _agenda = new AgendaPage(e => AppointmentOpened?.Invoke(e));
        _timerPage = new TimerPage(StartTimer, StopTimer);
        _mail.Opened += mail => MailOpened?.Invoke(mail);
        _home.AskClicked += BeginAsk;
        _home.AskSubmitted += OnAskSubmitted;
        _home.AskCancelled += CancelAsk;

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
        _glass = GlassBackdrop.Attach(this, _pill);
        _quiet = Theme.Settings.QuietNotch;
        Theme.Changed += (_, _) => OnAppearanceChanged();
        ApplyQuiet();
        ApplyPillSize();
        ShowPage(NotchPage.Home, animate: false);

        _deadlineTimer.Tick += (_, _) => OnDeadline();
        _equalizerTimer.Tick += (_, _) => _equalizerView.Dots = _equalizer.NextFrame();
        _alertMarquee.Tick += (_, _) => RenderAlertText(++_alertOffset);
        _countdownTimer.Tick += (_, _) => OnCountdownTick();
        _taskTimer.Tick += (_, _) => RenderTaskElapsed();
        media.Changed += snapshot => Dispatcher.BeginInvoke(() => OnMedia(snapshot));

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.MakeNonActivatingToolWindow(hwnd);
            HwndSource.FromHwnd(hwnd)?.AddHook(OnWindowMessage);
        };
        Deactivated += (_, _) => ReleaseAsk();
        Loaded += (_, _) =>
        {
            PositionOnPrimaryScreen();
            ApplyPillSize();
        };
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea))
                PositionOnPrimaryScreen();
        };
    }

    public event Action? SettingsRequested;

    /// <summary>Invio nel campo della Home: la richiesta e se si è premuto Ctrl per scegliere (docs/16-assistente-notch.md).</summary>
    public event Action<string, bool>? AskSubmitted;

    /// <summary>La riga sotto il campo della Home: quale scorciatoia userebbe Invio.</summary>
    public Func<string, string>? AskPreview
    {
        get => _home.AskPreview;
        set => _home.AskPreview = value;
    }

    /// <summary>Il pulsante del terminale nel notch espanso: si apre il pannello dell'assistente.</summary>
    public event Action? AssistantRequested;

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
        ShowBanner(mail.Subject, DotIcons.Mail, Theme.Accent, Theme.Ink, NotchPage.Mail, duration: null);
    }

    /// <summary>Volume o luminosità: icona, barra a punti e valore per 1,5 s; ogni tasto rinnova la durata.</summary>
    public void ShowLevel(DotGlyph icon, double level, bool muted)
    {
        ArgumentNullException.ThrowIfNull(icon);
        _alertMessage = string.Empty;
        _alertPage = null;
        SetAlertIcon(icon, muted ? Theme.Critical : Theme.Ink);
        _alertText.Visibility = Visibility.Collapsed;
        _alertLevel.Visibility = Visibility.Visible;
        _alertValue.Visibility = Visibility.Visible;
        _alertLevel.Dots = DotBar.Render(muted ? 0 : level, LevelColumns);
        _alertValue.Dots = DotText.Render(muted ? "MUTO" : $"{Math.Round(level * 100):0}");
        _alertValue.DotColor = muted ? Theme.Critical : Theme.Ink;
        RaiseAlert(LevelDuration);
    }

    /// <summary>Avviso breve con icona (cuffie collegate…).</summary>
    public void ShowNotice(DotGlyph icon, Color iconColor, string text)
    {
        ArgumentNullException.ThrowIfNull(icon);
        ShowBanner(text, icon, iconColor, Theme.Ink, page: null, NoticeDuration);
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

    /// <summary>Comandi dal terminale (docs/12-sviluppo.md): tempo dell'ultimo avviato a destra nella pillola, pagina Lavoro.</summary>
    public void SetTasks(IReadOnlyList<RunningTask> running, IReadOnlyList<FinishedTask> recent)
    {
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(recent);
        _tasks = running;
        _work.Update(running, recent);
        RenderTaskElapsed();
        if (running.Count > 0)
            _taskTimer.Start();
        else
            _taskTimer.Stop();
        OnActivitiesChanged();
    }

    /// <summary>CI seguita dopo un push: in corso conta come attività (punto che pulsa), riga nella pagina Lavoro.</summary>
    public void SetCi(CiStatus? status, string? problem)
    {
        _ciRunning = status?.State == CiState.Pending;
        _work.SetCi(status, problem);
        OnActivitiesChanged();
    }

    public void SetServers(IReadOnlyList<DevServer> servers) => _servers.Update(servers);

    public void SetPhone(PhonePageData data) => _phone.Update(data);

    /// <summary>
    /// Agenti (docs/14-agenti-connettori.md): pagina Agenti e, con un agente al lavoro, scintilla e costo del turno a destra
    /// nella pillola, aggiornati a ogni risposta.
    /// </summary>
    public void SetAgents(IReadOnlyList<AgentSnapshot> sessions, double? today)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _agents.Update(sessions, today);
        _working = sessions.Where(s => s.State == AgentState.Working).ToArray();
        if (_working.Length > 0)
            _agentText.Dots = DotText.Render(_working.Any(s => s.TurnCost is null)
                ? AgentFormat.Tokens(_working.Sum(s => s.TurnUsage.Output))
                : AgentFormat.Cost(_working.Sum(s => s.TurnCost ?? 0)));
        OnActivitiesChanged();
    }

    public void SetConnectors(IReadOnlyList<ConnectorState> states) => _devices.Update(states);

    public void SetClipboard(IReadOnlyList<ClipEntry> entries) => _clipboard.Update(entries);

    /// <summary>Avviso breve con icona; passandoci sopra col mouse si apre <paramref name="page"/>.</summary>
    public void ShowHint(DotGlyph icon, Color iconColor, string text, NotchPage page)
    {
        ArgumentNullException.ThrowIfNull(icon);
        ShowBanner(text, icon, iconColor, Theme.Ink, page, NoticeDuration);
    }

    /// <summary>Le pagine delle funzioni accese; le altre spariscono dallo swipe e dagli indicatori.</summary>
    public void SetAvailablePages(IEnumerable<NotchPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var current = _pager.Current;
        _pager.SetAvailable(pages);
        foreach (var (page, dot) in _indicatorDots)
            dot.Visibility = _pager.IsAvailable(page) ? Visibility.Visible : Visibility.Collapsed;
        if (current != _pager.Current)
            ShowPage(_pager.Current, animate: false);
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

    /// <summary>Come il clic sul campo (per <c>--ask-demo</c>): true se ora la tastiera arriva davvero al campo.</summary>
    public bool BeginAskForDemo()
    {
        BeginAsk();
        return WindowNative.GetForegroundWindow() == new WindowInteropHelper(this).Handle && _home.IsAskFocused;
    }

    /// <summary>La Home con una richiesta scritta nel campo (per <c>--snapshot</c>).</summary>
    public void SetAskForSnapshot(string text) => _home.SetAskText(text);

    /// <summary>Prepara la pillola in uno stato e una pagina senza animazioni (per <c>--snapshot</c>).</summary>
    public FrameworkElement PrepareSnapshot(NotchState state, NotchPage page)
    {
        var size = SizeFor(state, page);
        _expanded.Height = ExpandedSize(page).Height;
        _width.SnapTo(size.Width);
        _height.SnapTo(size.Height);
        ApplyPillSize();
        ShowPage(page, animate: false);
        foreach (var (layer, visible) in new (UIElement, bool)[]
                 {
                     (_compact, CompactOpacity(state) > 0),
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

    private bool HasActivity =>
        _countdown.IsRunning || _media?.IsPlaying == true || _privacy.Any || _downloads.Count > 0 || _tasks.Count > 0 || _ciRunning
        || _working.Length > 0;

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
        _taskView.HorizontalAlignment = HorizontalAlignment.Right;
        _taskView.VerticalAlignment = VerticalAlignment.Center;
        _taskView.Margin = new Thickness(0, 0, 18, 0);
        _taskIcon.VerticalAlignment = VerticalAlignment.Center;
        _taskText.VerticalAlignment = VerticalAlignment.Center;
        _taskText.Margin = new Thickness(6, 0, 0, 0);
        _taskView.Children.Add(_taskIcon);
        _taskView.Children.Add(_taskText);
        _agentView.HorizontalAlignment = HorizontalAlignment.Right;
        _agentView.VerticalAlignment = VerticalAlignment.Center;
        _agentView.Margin = new Thickness(0, 0, 18, 0);
        _agentIcon.VerticalAlignment = VerticalAlignment.Center;
        _agentText.VerticalAlignment = VerticalAlignment.Center;
        _agentText.Margin = new Thickness(6, 0, 0, 0);
        _agentView.Children.Add(_agentIcon);
        _agentView.Children.Add(_agentText);
        _compact.Children.Add(_compactClock);
        _compact.Children.Add(_liveDot);
        _compact.Children.Add(_equalizerView);
        _compact.Children.Add(_privacyIcons);
        _compact.Children.Add(_downloadView);
        _compact.Children.Add(_taskView);
        _compact.Children.Add(_agentView);
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

        foreach (var page in new NotchPageView[] { _home, _agenda, _mail, _phone, _work, _agents, _servers, _devices, _clipboard, _timerPage, _system }.OrderBy(p => p.Page))
        {
            _pages[page.Page] = page;
            page.Visibility = Visibility.Collapsed;
            page.RenderTransform = new TranslateTransform();
            _pageHost.Children.Add(page);
            var dot = new Ellipse { Width = 5, Height = 5, Margin = new Thickness(4, 0, 4, 0) };
            _indicatorDots[page.Page] = dot;
            _indicators.Children.Add(dot);
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
        _expanded.Children.Add(BuildAssistantButton());
        _expanded.IsHitTestVisible = false;

        var content = new Grid();
        content.Children.Add(_compact);
        content.Children.Add(_alert);
        content.Children.Add(_expanded);
        content.Children.Add(_card);
        _pill.Child = content;
        _pill.HorizontalAlignment = HorizontalAlignment.Center;
        _pill.VerticalAlignment = VerticalAlignment.Top;
        _pill.MouseEnter += (_, _) => OnPointerEnter();
        _pill.MouseLeave += (_, _) => OnPointerLeave();
        _pill.MouseWheel += (_, e) =>
        {
            e.Handled = true;
            ApplySwipe(_swipe.OnVerticalWheel(e.Delta, _clock.Elapsed));
        };
        _pill.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!_home.IsMouseOverAsk) // lì si seleziona il testo
                _swipe.OnDragStart(e.GetPosition(_pill).X);
        };
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

    /// <summary>In basso a destra su ogni pagina, sulla riga degli indicatori: abbastanza dentro da non finire nell'angolo tondo.</summary>
    private Border BuildAssistantButton()
    {
        var button = NotchUi.Button(new DotMatrixView { Pitch = 2, Dots = DotGrid.From(DotIcons.Terminal) }, () => AssistantRequested?.Invoke(), width: 24);
        button.Height = 22;
        button.CornerRadius = new CornerRadius(4);
        button.Margin = new Thickness(0, 0, 20, 3);
        button.HorizontalAlignment = HorizontalAlignment.Right;
        button.VerticalAlignment = VerticalAlignment.Bottom;
        button.ToolTip = "Chiedi o scrivi un comando";
        Grid.SetRowSpan(button, 2);
        return button;
    }

    private ContextMenu BuildContextMenu()
    {
        var assistant = new MenuItem { Header = "Chiedi o scrivi un comando…" };
        assistant.Click += (_, _) => AssistantRequested?.Invoke();
        var settings = new MenuItem { Header = "Impostazioni…" };
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        var exit = new MenuItem { Header = "Esci da Nothing Shell" };
        exit.Click += (_, _) => Application.Current.Shutdown();
        var menu = new ContextMenu();
        menu.Items.Add(assistant);
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
        if (_machine.State != NotchState.Expanded || direction == SwipeDirection.None || _machine.Held)
            return;
        if (_pager.Apply(direction))
            ShowPage(_pager.Current, animate: true, fromRight: direction == SwipeDirection.Next);
    }

    private void ShowPage(NotchPage page, bool animate, bool fromRight = true)
    {
        _pager.Show(page);
        page = _pager.Current; // una pagina spenta non si apre
        foreach (var (kind, view) in _pages)
        {
            var visible = kind == page;
            view.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible)
                view.OnHidden();
            else if (_machine.State == NotchState.Expanded)
                view.OnShown();
        }

        foreach (var (kind, dot) in _indicatorDots)
        {
            dot.Fill = kind == page ? Theme.AccentBrush : Theme.DotOffBrush;
            dot.Width = dot.Height = kind == page ? 6 : 5;
        }
        if (_machine.State == NotchState.Expanded)
            ResizeExpanded(page);

        if (!animate)
            return;
        var shown = _pages[page];
        var transform = (TranslateTransform)shown.RenderTransform;
        transform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(fromRight ? 40 : -40, 0, Slide) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        shown.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Slide));
    }

    private Size2 ExpandedSize(NotchPage page) => NotchLayout.Expanded(page, _media is not null);

    private Size2 SizeFor(NotchState state, NotchPage page) =>
        state == NotchState.Expanded ? ExpandedSize(page)
        : state == NotchState.Idle && _quiet ? NotchLayout.Quiet
        : NotchLayout.For(state);

    /// <summary>Notch discreto acceso o spento dalla pagina Aspetto: la pillola cambia subito misura.</summary>
    private void OnAppearanceChanged()
    {
        if (_quiet == Theme.Settings.QuietNotch)
            return;
        _quiet = Theme.Settings.QuietNotch;
        ApplyQuiet();
        var size = SizeFor(_machine.State, _pager.Current);
        _width.Target = size.Width;
        _height.Target = size.Height;
        StartAnimation();
        FadeTo(_compact, CompactOpacity(_machine.State));
    }

    /// <summary>Contro il bordo alto, così basta spingere il mouse in su per trovarla.</summary>
    private void ApplyQuiet() => _pill.Margin = new Thickness(0, _quiet ? 0 : NotchLayout.TopMargin, 0, 0);

    private double CompactOpacity(NotchState state) =>
        state is NotchState.Expanded or NotchState.Alert or NotchState.Card || (state == NotchState.Idle && _quiet) ? 0 : 1;

    /// <summary>Il notch espanso si allunga o si accorcia con la pagina: le pagine con un elenco sono più alte.</summary>
    private void ResizeExpanded(NotchPage page)
    {
        var size = ExpandedSize(page);
        if (Math.Abs(_height.Target - size.Height) < 0.5)
            return;
        _expanded.Height = size.Height; // la pillola taglia mentre la sua altezza anima
        _width.Target = size.Width;
        _height.Target = size.Height;
        StartAnimation();
    }

    /// <summary>
    /// Il notch non prende mai il focus da solo, così passarci sopra col mouse non toglie la tastiera a chi sta scrivendo
    /// altrove. Lo chiede il clic sul campo, e finché si scrive il notch resta aperto.
    /// </summary>
    private void BeginAsk()
    {
        if (_machine.Held)
            return;
        _beforeAsk = WindowNative.GetForegroundWindow();
        WindowNative.BringToFront(new WindowInteropHelper(this).Handle);
        Activate();
        _home.FocusAsk();
        _machine.Hold(true, _pill.IsMouseOver, _clock.Elapsed);
    }

    private void ReleaseAsk()
    {
        if (!_machine.Held)
            return;
        Keyboard.ClearFocus();
        _machine.Hold(false, _pill.IsMouseOver, _clock.Elapsed);
        ScheduleDeadline();
    }

    /// <summary>Esc: si butta il testo e la tastiera torna alla finestra di prima.</summary>
    private void CancelAsk()
    {
        _home.ClearAsk();
        var before = _beforeAsk;
        ReleaseAsk();
        if (before != 0)
            WindowNative.SetForegroundWindow(before);
    }

    private void OnAskSubmitted(string text, bool choose)
    {
        _home.ClearAsk();
        Keyboard.ClearFocus();
        Apply(_machine.Collapse()); // la finestra che si apre sta sotto la pillola compatta
        AskSubmitted?.Invoke(text, choose);
    }

    private void OnPointerEnter()
    {
        var alertPage = _machine.State == NotchState.Alert ? _alertPage : null;
        Apply(_machine.OnPointerEnter());
        if (alertPage is { } page && _pager.IsAvailable(page))
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
            if (page is { } p && _pager.IsAvailable(p) && !_machine.Held) // già aperto: si va alla pagina, ma non mentre si scrive
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
        if (_machine.State == NotchState.Expanded)
            ResizeExpanded(_pager.Current); // la Home con la musica è più alta
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
            ShowBanner("TEMPO SCADUTO", icon: null, Theme.Critical, Theme.Critical, NotchPage.Timer, duration: null);
        }
        _timerPage.Update(_countdown.IsRunning, _countdown.Remaining(now));
        UpdateCompactClock();
    }

    private void RenderTaskElapsed()
    {
        if (_tasks is [var latest, ..])
            _taskText.Dots = DotText.Render(TaskTracker.FormatElapsed(DateTimeOffset.Now - latest.StartedAt));
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
        var size = SizeFor(state, _pager.Current);
        if (state == NotchState.Expanded)
            _expanded.Height = size.Height;
        _width.Target = size.Width;
        _height.Target = size.Height;
        StartAnimation();

        var expanded = state == NotchState.Expanded;
        var alert = state == NotchState.Alert;
        var card = state == NotchState.Card;
        FadeTo(_expanded, expanded ? 1 : 0);
        FadeTo(_alert, alert ? 1 : 0);
        FadeTo(_card, card ? 1 : 0);
        FadeTo(_compact, CompactOpacity(state));
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
    /// A sinistra microfono e webcam in uso (hanno la precedenza sul punto rosso), a destra il comando in corso,
    /// poi l'agente al lavoro con il costo del turno, poi il download, poi l'equalizzatore della musica.
    /// </summary>
    private void SetActivityVisible(bool visible)
    {
        var privacy = visible && _privacy.Any;
        _privacyIcons.Visibility = privacy ? Visibility.Visible : Visibility.Hidden;
        _liveDot.Visibility = visible && !privacy ? Visibility.Visible : Visibility.Hidden;
        var task = visible && _tasks.Count > 0;
        _taskView.Visibility = task ? Visibility.Visible : Visibility.Hidden;
        var agent = visible && !task && _working.Length > 0;
        _agentView.Visibility = agent ? Visibility.Visible : Visibility.Hidden;
        var downloading = visible && !task && !agent && _downloads.Count > 0;
        _downloadView.Visibility = downloading ? Visibility.Visible : Visibility.Hidden;
        var music = visible && !task && !agent && !downloading && _media?.IsPlaying == true;
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
        var radius = NotchLayout.CornerRadius(size);
        _pill.CornerRadius = new CornerRadius(radius);
        _glass.SetShape(new Rect((Width - size.Width) / 2, _pill.Margin.Top, size.Width, size.Height), radius);
    }

    private void PositionOnPrimaryScreen()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }

    private static void FadeTo(UIElement element, double opacity) =>
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, Fade));
}
