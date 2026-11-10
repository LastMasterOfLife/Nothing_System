using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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

    // Unito al bordo (docs/20-stile-nusa.md): la pillola non ha fondo suo, lo disegna _body con le orecchie.
    private readonly Path _body = new() { Fill = Theme.PanelBrush, Stroke = Theme.RimBrush, StrokeThickness = 1, Visibility = Visibility.Collapsed };
    private readonly Grid _layers = new();

    // Il vestito (docs/22-vestiti-notch.md): fondo e motivo stesi sopra il fondo del notch, con la sua stessa forma.
    private readonly Path _dress = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Path _dressPattern = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private NotchOutfit? _outfit;
    private (NotchOutfit? Outfit, Palette Palette)? _dressed;
    private readonly BlurEffect _shadowBlur = new() { RenderingBias = RenderingBias.Performance };
    private readonly Path _shadow = new() { Fill = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly Grid _shadowHost = new() { IsHitTestVisible = false }; // il ritaglio sta qui: su _shadow verrebbe prima della sfocatura
    private readonly MascotView _mascot = new(NotchLayout.For(NotchState.Idle).Height / 28) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
    private NotchStyle _style;
    private double _shadowAmount;
    private bool _mascotOn;
    private bool _peeking;
    private AppearanceSettings? _applied;

    // Su quale schermo (pagina Aspetto): seguendo il mouse ci si sposta solo a notch chiuso.
    private readonly DispatcherTimer _followTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private NotchScreen _screen;
    private string? _monitorName;
    private NotchFeel _feel;

    // La mascotte reagisce (docs/20-stile-nusa.md): prima di un avviso sgrana gli occhi, e l'avviso arriva un attimo dopo.
    private static readonly Duration SurpriseBeat = new(TimeSpan.FromMilliseconds(350));
    private readonly DispatcherTimer _surpriseTimer = new();
    private Action? _afterSurprise;
    private bool _mascotShown;

    // L'iniziativa della mascotte (docs/21-mascotte.md): ogni pochi secondi guarda se ha qualcosa da dirti, e lo dice nell'avviso
    // dove c'è lei al posto dell'icona.
    private static readonly TimeSpan SpeechDuration = TimeSpan.FromSeconds(6);
    private readonly MascotNudges _nudges = new();
    private readonly DispatcherTimer _nudgeTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly MascotView _speaker = new(NotchLayout.For(NotchState.Idle).Height / 28)
    {
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(16, 0, 0, 0),
        Gaze = (0.8, 0.1),
        Visibility = Visibility.Collapsed,
    };
    private bool _speaking;
    private bool _lowBattery;

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
    private readonly DebugNotchPage _debugPage = new();
    private readonly ClipboardPage _clipboard;
    private readonly PhonePage _phone;
    private readonly AgentsPage _agents = new();
    private readonly DevicesPage _devices;
    private readonly DevicesPage _database;
    private readonly DevicesPage _ftp;
    private readonly DiagnosiPage _diagnosi = new();
    private readonly HoursPage _hours = new();
    private readonly LeisurePage _leisure = new();

    // Svago (docs/25-svago.md): giocando il notch resta aperto e tiene la tastiera, come il campo della Home.
    private bool _gameHold;

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
    private List<NotchPage> _allowedPages = [.. NotchPager.Pages];
    private HashSet<NotchPage> _sectionsInUse = [];
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
        _database = new DevicesPage(connected, NotchPage.Database, "DATABASE", "Nessun database. Attiva il connettore Database in Impostazioni › CONNETTORI › LIBRERIA.");
        _ftp = new DevicesPage(connected, NotchPage.Ftp, "FTP", "Nessun server FTP. Attiva il connettore Server FTP in Impostazioni › CONNETTORI › LIBRERIA.");
        _home = new HomePage(media);
        _work = new WorkPage(dev.OpenTask);
        _servers = new ServersPage(dev);
        _clipboard = new ClipboardPage(dev.CopyClip, dev.ClipAction);
        _agenda = new AgendaPage(e => AppointmentOpened?.Invoke(e));
        _timerPage = new TimerPage(StartTimer, StopTimer);
        _mail.Opened += mail => MailOpened?.Invoke(mail);
        _home.AskClicked += () => BeginAsk(_home.FocusAsk);
        _home.AskSubmitted += OnAskSubmitted;
        _home.AskCancelled += CancelAsk;
        _diagnosi.InputClicked += () => BeginAsk(_diagnosi.FocusInput);
        _diagnosi.InputCancelled += () => CancelAsk(_diagnosi.ClearInput);
        _hours.GroupClicked += name => HoursGroupClicked?.Invoke(name);
        _hours.InputClicked += () => BeginAsk(_hours.FocusInput);
        _hours.InputCancelled += () => CancelAsk(_hours.ClearInput);
        _leisure.PlayRequested += HoldForGame;
        _leisure.LeaveRequested += EndGameHold;
        _leisure.Finished += (score, stage) => LeisureFinished?.Invoke(score, stage);

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
        ReadAppearance();
        Theme.Changed += (_, _) => OnAppearanceChanged();
        ApplyQuiet();
        ApplyStyle();
        ApplyOutfit();
        UpdateMascot();
        ApplyPillSize();
        ShowPage(NotchPage.Home, animate: false);

        _deadlineTimer.Tick += (_, _) => OnDeadline();
        _equalizerTimer.Tick += (_, _) => _equalizerView.Dots = _equalizer.NextFrame();
        _alertMarquee.Tick += (_, _) => RenderAlertText(++_alertOffset);
        _countdownTimer.Tick += (_, _) => OnCountdownTick();
        _taskTimer.Tick += (_, _) => RenderTaskElapsed();
        _followTimer.Tick += (_, _) => FollowMouse();
        _nudgeTimer.Tick += (_, _) => OnNudgeTick();
        _surpriseTimer.Tick += (_, _) =>
        {
            _surpriseTimer.Stop();
            var show = _afterSurprise;
            _afterSurprise = null;
            show?.Invoke();
        };
        media.Changed += snapshot => Dispatcher.BeginInvoke(() => OnMedia(snapshot));

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.MakeNonActivatingToolWindow(hwnd);
            HwndSource.FromHwnd(hwnd)?.AddHook(OnWindowMessage);
        };
        Deactivated += (_, _) =>
        {
            if (_gameHold)
            {
                _gameHold = false; // un clic su un'altra finestra: il gatto si ferma e il notch si richiude come al solito
                _leisure.Interrupt();
            }
            ReleaseAsk();
        };
        IsVisibleChanged += (_, _) => UpdateMascot();
        Loaded += (_, _) => PlaceOnScreen();
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea))
                PlaceOnScreen();
        };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(PlaceOnScreen);
    }

    public event Action? SettingsRequested;

    /// <summary>Il notch è passato su un altro schermo: chi nasconde il notch a schermo intero guarda questo.</summary>
    public event Action? MonitorChanged;

    /// <summary>HMONITOR dello schermo dove sta il notch; 0 finché la finestra non c'è.</summary>
    public nint MonitorHandle { get; private set; }

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
        _nudges.Noticed(MascotEvent.Mail);
        ShowBanner(mail.Subject, DotIcons.Mail, Theme.Accent, Theme.Ink, NotchPage.Mail, duration: null);
    }

    /// <summary>Volume o luminosità: icona, barra a punti e valore per 1,5 s; ogni tasto rinnova la durata.</summary>
    public void ShowLevel(DotGlyph icon, double level, bool muted)
    {
        ArgumentNullException.ThrowIfNull(icon);
        _alertMessage = string.Empty;
        _alertPage = null;
        _speaking = false;
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
        FollowMouse(); // la scheda arriva dove si sta guardando
        Surprise(() =>
        {
            Apply(_machine.OnCard(_clock.Elapsed, duration) || _machine.State == NotchState.Card);
            ScheduleDeadline();
        });
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
        var finished = _tasks.Any(t => running.All(r => r.Id != t.Id));
        _tasks = running;
        if (finished && recent.Where(t => !t.Quiet).MaxBy(t => t.FinishedAt) is { } last)
        {
            _mascot.React(last.Succeeded ? MascotMood.Happy : MascotMood.Sad); // il comando ha finito: la mascotte torna e lo dice
            _nudges.Noticed(last.Succeeded ? MascotEvent.TaskDone : MascotEvent.TaskFailed);
        }
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

    /// <summary>Pagina Debug: i portali collegati con le cose da guardare (docs/24-debug-potenziato.md).</summary>
    public void SetDebug(IReadOnlyList<NothingShell.Core.PortalDebug.DebugPortalView> portals) => _debugPage.Update(portals);

    /// <summary>Dettaglio o una riga della pagina Debug: la dashboard del portale, sull'evento se c'è.</summary>
    public event Action<string, NothingShell.Core.PortalDebug.DebugEvent?>? DebugOpened
    {
        add => _debugPage.Opened += value;
        remove => _debugPage.Opened -= value;
    }

    public event Action? DebugSettingsRequested
    {
        add => _debugPage.SettingsRequested += value;
        remove => _debugPage.SettingsRequested -= value;
    }

    public void SetPhone(PhonePageData data) => _phone.Update(data);

    /// <summary>
    /// Agenti (docs/14-agenti-connettori.md): pagina Agenti e, con un agente al lavoro, scintilla e costo del turno a destra
    /// nella pillola, aggiornati a ogni risposta.
    /// </summary>
    public void SetAgents(IReadOnlyList<AgentSnapshot> sessions, double? today)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _agents.Update(sessions, today);
        var wasWorking = _working;
        _working = sessions.Where(s => s.State == AgentState.Working).ToArray();
        var finished = sessions.Count(s => s.State != AgentState.Working && wasWorking.Any(w => w.Id == s.Id));
        if (finished > 0)
            _mascot.React(MascotMood.Happy); // un agente ha finito il turno (non: una sessione chiusa)
        _nudges.Noticed(MascotEvent.AgentDone, finished);
        if (_working.Length > 0)
            _agentText.Dots = DotText.Render(_working.Any(s => s.TurnCost is null)
                ? AgentFormat.Tokens(_working.Sum(s => s.TurnUsage.Output))
                : AgentFormat.Cost(_working.Sum(s => s.TurnCost ?? 0)));
        OnActivitiesChanged();
    }

    /// <summary>
    /// Ogni connettore sta nella sua pagina: database e FTP nelle loro, che compaiono finché ne esiste uno (e il
    /// ShellController le lascia accese), gli altri in Dispositivi.
    /// </summary>
    public void SetConnectors(IReadOnlyList<ConnectorState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        _devices.Update(states.Where(s => ConnectorSections.Of(s.Manifest) is null).ToList());
        _database.Update(states.Where(s => ConnectorSections.Of(s.Manifest) == NotchPage.Database).ToList());
        _ftp.Update(states.Where(s => ConnectorSections.Of(s.Manifest) == NotchPage.Ftp).ToList());
        _sectionsInUse = states.Select(s => ConnectorSections.Of(s.Manifest)).OfType<NotchPage>().ToHashSet();
        ApplyAvailablePages();
    }

    public void SetClipboard(IReadOnlyList<ClipEntry> entries) => _clipboard.Update(entries);

    /// <summary>Avviso breve con icona; passandoci sopra col mouse si apre <paramref name="page"/>.</summary>
    public void ShowHint(DotGlyph icon, Color iconColor, string text, NotchPage page)
    {
        ArgumentNullException.ThrowIfNull(icon);
        ShowBanner(text, icon, iconColor, Theme.Ink, page, NoticeDuration);
    }

    /// <summary>Le pagine delle funzioni accese, nell'ordine dello swipe; le altre spariscono dallo swipe e dagli indicatori.</summary>
    public void SetAvailablePages(IEnumerable<NotchPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        _allowedPages = pages.ToList();
        ApplyAvailablePages();
    }

    private void ApplyAvailablePages()
    {
        var current = _pager.Current;
        _pager.SetAvailable(_allowedPages.Where(p => !ConnectorSections.IsSection(p) || _sectionsInUse.Contains(p)));
        _indicators.Children.Clear(); // i pallini seguono l'ordine scelto in Impostazioni › Notch
        foreach (var page in _pager.Available)
            _indicators.Children.Add(_indicatorDots[page]);
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
        {
            // il notch si chiude appena il mouse esce: aperto da lontano gli si dà il tempo di arrivarci
            _machine.OnPointerLeave(_clock.Elapsed + NotchStateMachine.CardLeaveDelay);
            ScheduleDeadline();
        }
    }

    /// <summary>CPU e memoria: pagina Sistema e, se la CPU resta al massimo, la mascotte che barcolla.</summary>
    public void SetSystem(SystemStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        _system.Update(stats);
        _nudges.ObserveCpu(DateTime.Now, stats.Cpu);
    }

    /// <summary>Batteria: pagina Sistema e, quasi scarica, gli occhi stanchi della mascotte.</summary>
    public void SetBattery(BatteryStatus battery)
    {
        ArgumentNullException.ThrowIfNull(battery);
        _system.Update(battery);
        _lowBattery = battery.IsLow;
        _mascot.Tired = _lowBattery && _nudges.Level != MascotInitiative.Off;
    }

    /// <summary>Come il clic sul campo (per <c>--ask-demo</c>): true se ora la tastiera arriva davvero al campo.</summary>
    public bool BeginAskForDemo()
    {
        BeginAsk(_home.FocusAsk);
        return WindowNative.GetForegroundWindow() == new WindowInteropHelper(this).Handle && _home.IsAskFocused;
    }

    /// <summary>La pagina DNS con i risultati di una ricerca vera (per <c>--snapshot</c>).</summary>
    public Task RunDiagnosiForSnapshot(string text) => _diagnosi.RunAsync(text);

    /// <summary>La pagina Ore col negozio dato e un comando scritto nel campo (per <c>--snapshot</c>).</summary>
    public void ShowHoursForSnapshot(NothingShell.Core.Work.WorkStore store, string? command)
    {
        _hours.SetStore(store);
        if (command is not null)
            _hours.RunForSnapshot(command);
    }

    public void SetWorkStore(NothingShell.Core.Work.WorkStore? store) => _hours.SetStore(store);

    /// <summary>Clic sul nome di un gruppo nella pagina Ore: la shell apre il suo calendario.</summary>
    public event Action<string>? HoursGroupClicked;

    /// <summary>Una partita di Svago è finita: punti e tappa, per il record.</summary>
    public event Action<int, int>? LeisureFinished;

    public void SetLeisureBest(int best, int stage) => _leisure.SetBest(best, stage);

    /// <summary>La pagina Svago ferma su una partita preparata (per <c>--svago-sheet</c>).</summary>
    public void ShowLeisureForSnapshot(NothingShell.Core.Leisure.RooftopRun run, bool newRecord) => _leisure.ShowForSnapshot(run, newRecord);

    /// <summary>La Home con una richiesta scritta nel campo (per <c>--snapshot</c>).</summary>
    public void SetAskForSnapshot(string text) => _home.SetAskText(text);

    /// <summary>La mascotte che dice <paramref name="text"/> nell'avviso (per <c>--mascot-sheet</c>).</summary>
    public void SayForSnapshot(string text) => Say(text, page: null);

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
        var mascot = _mascotOn && !_quiet && state == NotchState.Idle;
        foreach (var (layer, visible) in new (UIElement, bool)[] { (_mascot, mascot), (_compactClock, !mascot) })
        {
            layer.BeginAnimation(OpacityProperty, null);
            layer.Opacity = visible ? 1 : 0;
        }
        _speaker.Visibility = _speaking ? Visibility.Visible : Visibility.Collapsed;
        _speaker.SetActive(_speaking && state == NotchState.Alert); // la finestra non è sullo schermo: qui si accende a mano
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
        _compact.Children.Add(_mascot);
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
        _alert.Children.Add(_speaker);

        var cardSize = NotchLayout.For(NotchState.Card);
        _card.Width = cardSize.Width;
        _card.Height = cardSize.Height;
        _card.HorizontalAlignment = HorizontalAlignment.Center;
        _card.Padding = new Thickness(NotchUi.Padding, 14, NotchUi.Padding, 14);

        foreach (var page in new NotchPageView[] { _home, _agenda, _mail, _phone, _work, _agents, _servers, _debugPage, _devices, _database, _ftp, _diagnosi, _hours, _clipboard, _timerPage, _system, _leisure }.OrderBy(p => p.Page))
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

        var content = _layers;
        content.Children.Add(_compact);
        content.Children.Add(_alert);
        content.Children.Add(_expanded);
        content.Children.Add(_card);
        var surface = new Grid();
        surface.Children.Add(_body);
        surface.Children.Add(_dress);
        surface.Children.Add(_dressPattern);
        surface.Children.Add(content);
        _pill.Child = surface;
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
            if (!_home.IsMouseOverAsk && !_diagnosi.IsMouseOverInput && !_hours.IsMouseOverInput) // lì si seleziona il testo
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
        _shadow.Effect = _shadowBlur;
        _shadowHost.Children.Add(_shadow);
        root.Children.Add(_shadowHost);
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
        // tenuto aperto per scrivere non si cambia pagina; per giocare sì, ma non mentre il gatto corre
        var held = _machine.Held && !(_gameHold && !_leisure.IsRunning);
        if (_machine.State != NotchState.Expanded || direction == SwipeDirection.None || held)
            return;
        if (_pager.Apply(direction))
            ShowPage(_pager.Current, animate: true, fromRight: direction == SwipeDirection.Next);
    }

    private void ShowPage(NotchPage page, bool animate, bool fromRight = true)
    {
        if (page != NotchPage.Leisure)
            EndGameHold();
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
            new DoubleAnimation(fromRight ? 40 : -40, 0, Motion.Scaled(Slide)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        shown.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.Scaled(Slide)));
    }

    private Size2 ExpandedSize(NotchPage page) => NotchLayout.Expanded(page, _media is not null);

    private Size2 SizeFor(NotchState state, NotchPage page)
    {
        var size = state == NotchState.Expanded ? ExpandedSize(page)
            : state == NotchState.Idle && _quiet ? NotchLayout.Quiet
            : NotchLayout.For(state);
        // col mouse sopra, in attesa di aprire, la pillola si gonfia un poco (docs/20-stile-nusa.md)
        return _machine.Peeking && state is not (NotchState.Expanded or NotchState.Card)
            ? new(size.Width * NotchMotion.PeekScale, size.Height * NotchMotion.PeekScale)
            : size;
    }

    private void ReadAppearance()
    {
        var look = Theme.Settings;
        _quiet = look.QuietNotch;
        _style = look.NotchStyle;
        _mascotOn = look.NotchMascot;
        _mascot.Initiative = _nudges.Level = look.NotchMascot ? look.NotchMascotInitiative : MascotInitiative.Off;
        _mascot.Playful = look.NotchMascotPlay;
        _mascot.Tired = _lowBattery && _nudges.Level != MascotInitiative.Off;
        _nudgeTimer.IsEnabled = _nudges.Level != MascotInitiative.Off;
        _shadowAmount = Math.Clamp(look.NotchShadow, 0, 1);
        _machine.OpenDelay = look.NotchOpenDelay;
        _machine.CloseDelay = look.NotchCloseDelay;
        _screen = look.NotchScreen;
        _monitorName = look.NotchMonitor;
        _feel = look.NotchFeel;
        _followTimer.IsEnabled = _screen == NotchScreen.Mouse;
        _outfit = look.NotchOutfit;
        _applied = AppearanceSettings.Default.WithNotchOf(look);
    }

    /// <summary>Scelte del notch cambiate dalla pagina Aspetto: forma, ombra, mascotte, attese e schermo si aggiornano subito.</summary>
    private void OnAppearanceChanged()
    {
        ApplyOutfit(); // anche col solo tema cambiato: i colori del vestito seguono il testo
        if (AppearanceSettings.Default.WithNotchOf(Theme.Settings) == _applied)
            return;
        ReadAppearance();
        PlaceOnScreen();
        ApplyQuiet();
        ApplyStyle();
        Retarget(SizeFor(_machine.State, _pager.Current));
        FadeTo(_compact, CompactOpacity(_machine.State));
        UpdateMascot();
        ApplyPillSize();
    }

    /// <summary>Contro il bordo alto, così basta spingere il mouse in su per trovarla; unita al bordo ci sta sempre.</summary>
    private void ApplyQuiet() =>
        _pill.Margin = new Thickness(0, _quiet || _style == NotchStyle.Attached ? 0 : NotchLayout.TopMargin, 0, 0);

    /// <summary>Sospesa la pillola si disegna da sé; unita al bordo il fondo è <see cref="_body"/>, con le orecchie.</summary>
    private void ApplyStyle()
    {
        var attached = _style == NotchStyle.Attached;
        _body.Visibility = attached ? Visibility.Visible : Visibility.Collapsed;
        _pill.Background = attached ? Brushes.Transparent : Theme.PanelBrush;
        _pill.BorderThickness = new Thickness(attached ? 0 : 1);
        if (attached)
        {
            _pill.CornerRadius = default;
            return;
        }
        _pill.Clip = null;
        _layers.Margin = default;
    }

    /// <summary>
    /// Il vestito scelto nella pagina Aspetto: fondo, motivo e bordo, con i colori regolati sul testo del tema. Si rifanno i
    /// pennelli solo se cambia il vestito o il colore del testo.
    /// </summary>
    private void ApplyOutfit()
    {
        _outfit = Theme.Settings.NotchOutfit;
        var palette = Theme.Current;
        if (_dressed is { } dressed && dressed.Outfit == _outfit && dressed.Palette == palette)
            return;
        _dressed = (_outfit, palette);
        if (_outfit is not { } outfit)
        {
            _dress.Visibility = _dressPattern.Visibility = Visibility.Collapsed;
            _body.Stroke = _pill.BorderBrush = Theme.RimBrush;
            return;
        }
        _dress.Fill = OutfitPaint.Fill(outfit, palette);
        _dress.Opacity = outfit.CoverageAmount;
        _dress.Visibility = Visibility.Visible;
        _dressPattern.Fill = OutfitPaint.Pattern(outfit, palette);
        _dressPattern.Opacity = outfit.PatternStrength * outfit.CoverageAmount;
        _dressPattern.Visibility = _dressPattern.Fill is null ? Visibility.Collapsed : Visibility.Visible;
        _body.Stroke = _pill.BorderBrush = OutfitPaint.Rim(outfit) ?? Theme.RimBrush;
        ApplyPillSize();
    }

    /// <summary>
    /// Nuova misura con la molla giusta (docs/20-stile-nusa.md): apertura e chiusura come Nusa, gonfiore morbido. La molla
    /// cambia a metà corsa senza perdere velocità.
    /// </summary>
    private void Retarget(Size2 size, bool peek = false)
    {
        var tune = NotchMotion.For(new Size2(_width.Target, _height.Target), size, peek, _feel);
        _width.Tune(tune.Stiffness, tune.Damping);
        _height.Tune(tune.Stiffness, tune.Damping);
        _width.Target = size.Width;
        _height.Target = size.Height;
        StartAnimation();
    }

    /// <summary>A riposo, se accesa nella pagina Aspetto, la mascotte prende il posto dell'ora; quando parla, sta nell'avviso.</summary>
    private void UpdateMascot()
    {
        var shown = _mascotOn && !_quiet && _machine.State == NotchState.Idle;
        _mascotShown = shown && IsVisible;
        _mascot.Greeting = _machine.Peeking;
        _mascot.SetActive(shown && IsVisible);
        FadeTo(_mascot, shown ? 1 : 0);
        FadeTo(_compactClock, shown ? 0 : 1);
        _speaker.Visibility = _speaking ? Visibility.Visible : Visibility.Collapsed;
        _speaker.SetActive(_speaking && _machine.State == NotchState.Alert && IsVisible);
    }

    /// <summary>
    /// Ogni pochi secondi: se la mascotte è a riposo sullo schermo e ha qualcosa da dirti, prima ti chiama e poi lo dice. Mai
    /// mentre il notch fa altro, mentre ci passi sopra o mentre un avviso sta per arrivare.
    /// </summary>
    private void OnNudgeTick()
    {
        var now = DateTime.Now;
        _nudges.Observe(now, NativeMethods.IdleTime());
        if (!_mascotShown || _machine.Peeking || _afterSurprise is not null || _nudges.Next(now) is not { } nudge)
            return;
        if (nudge.Text is not { } text)
            _mascot.React(nudge.Gesture);
        else
            Lead(nudge.Gesture, MascotBrain.LengthOf(nudge.Gesture), () => Say(text, nudge.Page));
    }

    /// <summary>La mascotte parla: nell'avviso al posto dell'icona c'è lei, che guarda il suo testo e sobbalza.</summary>
    private void Say(string text, NotchPage? page)
    {
        ShowBanner(text, icon: null, Theme.Ink, Theme.Ink, page, SpeechDuration, speaker: true);
        _speaker.React(MascotMood.Talking);
    }

    private double CompactOpacity(NotchState state) =>
        state is NotchState.Expanded or NotchState.Alert or NotchState.Card || (state == NotchState.Idle && _quiet) ? 0 : 1;

    /// <summary>Il notch espanso si allunga o si accorcia con la pagina: le pagine con un elenco sono più alte.</summary>
    private void ResizeExpanded(NotchPage page)
    {
        var size = ExpandedSize(page);
        if (Math.Abs(_height.Target - size.Height) < 0.5)
            return;
        _expanded.Height = size.Height; // la pillola taglia mentre la sua altezza anima
        Retarget(size);
    }

    /// <summary>
    /// Il notch non prende mai il focus da solo, così passarci sopra col mouse non toglie la tastiera a chi sta scrivendo
    /// altrove. Lo chiede il clic sul campo, e finché si scrive il notch resta aperto.
    /// </summary>
    private void BeginAsk(Action focus)
    {
        if (_machine.Held)
            return;
        _beforeAsk = WindowNative.GetForegroundWindow();
        WindowNative.BringToFront(new WindowInteropHelper(this).Handle);
        Activate();
        focus();
        _machine.Hold(true, _pill.IsMouseOver, _clock.Elapsed);
    }

    /// <summary>
    /// La partita parte o riprende: come il clic sul campo della Home, il notch prende la tastiera (Spazio salta) e resta aperto
    /// anche se il mouse va altrove. Ricominciando dopo una partita la tastiera c'è già.
    /// </summary>
    private void HoldForGame()
    {
        var own = new WindowInteropHelper(this).Handle;
        var foreground = WindowNative.GetForegroundWindow();
        if (foreground != own)
        {
            _beforeAsk = foreground;
            WindowNative.BringToFront(own);
            Activate();
        }
        _leisure.FocusGame();
        _gameHold = true;
        _machine.Hold(true, _pill.IsMouseOver, _clock.Elapsed);
    }

    /// <summary>Basta giocare (Esc, un'altra pagina): pausa, il notch si può richiudere e la tastiera torna dov'era.</summary>
    private void EndGameHold()
    {
        if (!_gameHold)
            return;
        _gameHold = false;
        _leisure.Interrupt();
        CancelAsk(() => { });
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
    private void CancelAsk() => CancelAsk(_home.ClearAsk);

    private void CancelAsk(Action clear)
    {
        clear();
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

    /// <summary>Si apre dopo <see cref="NotchMotion.EnterDelay"/>, se il mouse resta; intanto la pillola si gonfia.</summary>
    private void OnPointerEnter()
    {
        Apply(_machine.OnPointerHover(_clock.Elapsed));
        ScheduleDeadline();
    }

    private void OnPointerLeave()
    {
        Apply(_machine.OnPointerLeave(_clock.Elapsed));
        ScheduleDeadline();
    }

    private void OnDeadline()
    {
        _deadlineTimer.Stop();
        var alertPage = _machine.State == NotchState.Alert ? _alertPage : null; // passando sopra un avviso si apre la sua pagina
        var changed = _machine.Tick(_clock.Elapsed);
        Apply(changed);
        if (changed && _machine.State == NotchState.Expanded && alertPage is { } page && _pager.IsAvailable(page))
            ShowPage(page, animate: false);
        ScheduleDeadline();
    }

    private void ScheduleDeadline()
    {
        if (_machine.NextDeadline is not { } deadline)
            return;
        _deadlineTimer.Interval = deadline > _clock.Elapsed ? deadline - _clock.Elapsed : TimeSpan.Zero;
        _deadlineTimer.Start();
    }

    /// <summary>
    /// Avviso con testo che scorre: icona facoltativa a sinistra, durata standard se non indicata. Con <paramref name="speaker"/>
    /// a sinistra c'è la mascotte che parla, e niente sorpresa prima: ha già chiamato lei.
    /// </summary>
    private void ShowBanner(string message, DotGlyph? icon, Color iconColor, Color textColor, NotchPage? page, TimeSpan? duration, bool speaker = false)
    {
        _alertMessage = message.Length > 0 ? message : "(senza oggetto)";
        _alertPage = page;
        _speaking = speaker;
        if (icon is not null)
            SetAlertIcon(icon, iconColor);
        else
            _alertIcon.Visibility = Visibility.Collapsed;
        _alertText.Visibility = Visibility.Visible;
        _alertLevel.Visibility = Visibility.Collapsed;
        _alertValue.Visibility = Visibility.Collapsed;
        var lead = speaker ? _speaker.Margin.Left + _speaker.Width + 10
            : icon is null ? 20
            : 20 + (icon.Width * _alertIcon.Pitch) + 16;
        _alertText.Margin = new Thickness(lead, 0, 20, 0);
        _alertText.DotColor = textColor;
        _alertOffset = 0;
        RenderAlertText(0);
        if (_machine.State == NotchState.Expanded)
        {
            if (page is { } p && _pager.IsAvailable(p) && !_machine.Held) // già aperto: si va alla pagina, ma non mentre si scrive
                ShowPage(p, animate: true);
            return;
        }
        if (speaker)
            RaiseAlert(duration);
        else
            Surprise(() => RaiseAlert(duration));
    }

    /// <summary>
    /// Con la mascotte a riposo sullo schermo, prima la sorpresa e poi, dopo <see cref="SurpriseBeat"/>, <paramref name="show"/>:
    /// altrimenti l'avviso la coprirebbe prima di vederla reagire. Avvisi in fila durante l'attesa: vale l'ultimo.
    /// </summary>
    private void Surprise(Action show) => Lead(MascotMood.Surprised, SurpriseBeat.TimeSpan, show);

    /// <summary>Prima la mascotte fa <paramref name="mood"/> e poi, dopo <paramref name="beat"/>, <paramref name="show"/>.</summary>
    private void Lead(MascotMood mood, TimeSpan beat, Action show)
    {
        FollowMouse();
        if (!_mascotShown || !Motion.Enabled)
        {
            show();
            return;
        }
        if (_afterSurprise is null)
        {
            _mascot.React(mood);
            _surpriseTimer.Interval = Motion.Scaled(beat).TimeSpan;
            _surpriseTimer.Start();
        }
        _afterSurprise = show;
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
        FollowMouse(); // l'avviso arriva dove si sta guardando
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
        // il gonfiore e il suo ritiro hanno la loro molla; dal gonfiore all'apertura si passa a quella d'apertura
        Retarget(size, peek: _machine.Peeking || (_peeking && state != NotchState.Expanded));
        _peeking = _machine.Peeking;

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
        UpdateMascot();
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

        var moving = _width.Step(dt * Motion.Speed) | _height.Step(dt * Motion.Speed); // la velocità scelta accorcia il tempo
        ApplyPillSize();
        if (moving)
            return;
        CompositionTarget.Rendering -= OnFrame;
        _animating = false;
    }

    private void ApplyPillSize()
    {
        var size = new Size2(Math.Max(1, _width.Value), Math.Max(1, _height.Value));
        var attached = _style == NotchStyle.Attached;
        var radius = NotchLayout.CornerRadius(size, _style);
        var ear = attached ? NotchLayout.Ear(size) : 0;
        var left = (Width - size.Width) / 2;
        var top = _pill.Margin.Top;
        _pill.Width = size.Width + (2 * ear);
        _pill.Height = size.Height;
        if (attached)
        {
            var shape = NotchShape.Attached(size.Width, size.Height, ear, radius);
            _body.Data = shape;
            _pill.Clip = shape;
            if (_outfit is not null)
                _dress.Data = _dressPattern.Data = shape;
            _layers.Margin = new Thickness(ear, 0, ear, 0);
            // il vetro sale oltre il bordo dello schermo: i suoi angoli alti restano fuori, lassù la forma è squadrata
            _glass.SetShape(new Rect(left, top - radius, size.Width, size.Height + radius), radius);
        }
        else
        {
            _pill.CornerRadius = new CornerRadius(radius);
            _glass.SetShape(new Rect(left, top, size.Width, size.Height), radius);
            if (_outfit is not null) // dentro il bordo da 1 della pillola
                _dress.Data = _dressPattern.Data = NotchShape.Floating(Math.Max(0, size.Width - 2), Math.Max(0, size.Height - 2), Math.Max(0, radius - 1));
        }
        ApplyShadow(size, left, top, radius, ear);
    }

    /// <summary>
    /// Ombra ampia e morbida sotto il notch aperto (docs/20-stile-nusa.md), larga quanto chiede la pagina Aspetto. Cresce con
    /// l'apertura e a riposo non c'è: le sue sfumature prenderebbero i clic appena sotto il bordo dello schermo.
    /// </summary>
    private void ApplyShadow(Size2 size, double left, double top, double radius, double ear)
    {
        var idle = NotchLayout.For(NotchState.Idle).Height;
        var open = Math.Clamp((size.Height - idle) / (NotchLayout.For(NotchState.Card).Height - idle), 0, 1);
        if (_shadowAmount <= 0 || open <= 0)
        {
            _shadow.Visibility = Visibility.Collapsed;
            return;
        }
        var drop = 2 + (8 * _shadowAmount);
        Geometry At(double y) => _style == NotchStyle.Attached
            ? NotchShape.Attached(size.Width, size.Height, ear, radius, left - ear, y)
            : NotchShape.Floating(size.Width, size.Height, radius, left, y);
        _shadow.Data = At(top + drop);
        // solo fuori dalla forma: col vetro l'ombra non deve scurire ciò che si vede attraverso
        _shadowHost.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, Width, Height)), At(top));
        _shadowBlur.Radius = Math.Max(1, (NotchLayout.ShadowReach - drop) * _shadowAmount);
        _shadow.Opacity = 0.6 * open;
        _shadow.Visibility = Visibility.Visible;
    }

    /// <summary>Centrata contro il bordo alto dello schermo scelto nella pagina Aspetto, in pixel fisici di quello schermo.</summary>
    private void PlaceOnScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0 || NotchPlacement.Pick(WindowNative.Monitors(), _screen, _monitorName, WindowNative.MonitorAtCursor()) is not { } monitor)
            return;
        WindowNative.Place(hwnd, NotchPlacement.Place(monitor, NotchLayout.Host));
        ApplyPillSize(); // il vetro dietro la pillola segue la finestra
        if (monitor.Handle == MonitorHandle)
            return;
        MonitorHandle = monitor.Handle;
        MonitorChanged?.Invoke();
    }

    /// <summary>Seguendo il mouse: a notch chiuso (anche nascosto a schermo intero) passa sullo schermo dove sta il mouse.</summary>
    private void FollowMouse()
    {
        if (_screen != NotchScreen.Mouse || _machine.Peeking || _machine.Held || _machine.State is NotchState.Expanded or NotchState.Card)
            return;
        var cursor = WindowNative.MonitorAtCursor();
        if (cursor != 0 && cursor != MonitorHandle)
            PlaceOnScreen();
    }

    private static void FadeTo(UIElement element, double opacity) =>
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, Motion.Scaled(Fade)));
}
