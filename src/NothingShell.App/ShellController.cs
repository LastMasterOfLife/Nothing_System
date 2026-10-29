using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using NothingShell.App.Access;
using NothingShell.App.Bar;
using NothingShell.App.Launcher;
using NothingShell.App.Notch;
using NothingShell.App.Plugins;
using NothingShell.App.Services;
using NothingShell.App.Settings;
using NothingShell.App.Widgets;
using NothingShell.Core.Design;
using NothingShell.Core.Launcher;
using NothingShell.Core.Notch;
using NothingShell.Core.Plugins;
using NothingShell.Core.Settings;
using NothingShell.Core.Storage;
using NothingShell.Core.Widgets;
using Windows.UI.Notifications.Management;

namespace NothingShell.App;

/// <summary>
/// Collega servizi e finestre della shell e applica le impostazioni dal vivo
/// (docs/01-architecture.md, docs/07-settings-plugins.md).
/// </summary>
public sealed partial class ShellController : IShellSettingsActions, IMailReaderActions, IDisposable
{
    private readonly ShellSettingsStore _settingsStore = new(ShellSettingsStore.DefaultPath);
    private readonly ClockService _clock = new();
    private readonly SystemMonitor _monitor = new();
    private readonly NetworkStatus _network = new();
    private readonly MediaService _media = new();
    private readonly AppIdResolver _appIds = new();
    private readonly TaskbarPins _pins = new();
    private readonly WindowTracker _tracker;
    private readonly WindowsTaskbar _taskbar = new();
    private readonly AppCatalog _catalog = new();
    private readonly PluginHost _plugins = new();
    private readonly NotificationService _notifications = new();
    private readonly ImapMailService _imap = new();
    private readonly AudioService _audio = new();
    private readonly BrightnessWatcher _brightness = new();
    private readonly PrivacyMonitor _privacy = new();
    private readonly DownloadWatcher _downloads = new();
    private readonly ScreenshotWatcher _screenshots = new();
    private readonly BluetoothAudioWatcher _bluetooth = new();
    private readonly CalendarService _calendar = new();
    private readonly AppointmentReminder _reminder = new();
    private readonly DispatcherTimer _morningDelay = new();
    private readonly LauncherWindow _launcher;
    private readonly WinKeyHook _winKey;
    private readonly NotchWindow _notch;
    private readonly BarHost _bars;
    private readonly WidgetHost _widgets;
    private readonly AccessController _access = new();

    private ShellSettings _settings;
    private SettingsWindow? _settingsWindow;
    private MailReaderWindow? _mailReader;
    private QuickPanelWindow? _quickPanel;
    private bool _fullscreen; // app a schermo intero sul monitor principale: lì sta il notch

    public ShellController()
    {
        _settings = _settingsStore.Load();
        _tracker = new WindowTracker(_appIds);
        var shellIcons = new ShellIcons();

        _launcher = new LauncherWindow(_catalog, shellIcons, new LauncherSettingsStore(LauncherSettingsStore.DefaultPath));
        _winKey = new WinKeyHook { Enabled = _launcher.UseWinKey };
        _winKey.Pressed += () => Application.Current.Dispatcher.BeginInvoke(() => _launcher.Toggle());
        _winKey.TogglePressed += () => Application.Current.Dispatcher.BeginInvoke(() => ToggleRequested?.Invoke());
        _launcher.UseWinKeyChanged += enabled => _winKey.Enabled = enabled;
        _launcher.CommandRequested += id =>
        {
            if (id == AppCatalog.SettingsEntry.Id)
                OpenSettings();
        };
        _launcher.LockRequested += Lock;
        _access.Enabled = _settings.Access;

        _notch = new NotchWindow(_media, DevActions);
        var appIcons = new AppIcons();
        _bars = new BarHost((monitor, mode) =>
            new BarWindow(_tracker, appIcons, shellIcons, _catalog, _taskbar, _launcher, monitor, mode));
        _pins.Changed += ApplyPins;
        _widgets = new WidgetHost(_clock, _monitor, new WidgetSettingsStore(WidgetSettingsStore.DefaultPath));

        _notch.SettingsRequested += OpenSettings;
        _notch.MailOpened += OpenMail;
        _notch.AppointmentOpened += OpenAppointment;
        _bars.QuickPanelRequested += bar =>
        {
            _quickPanel ??= new QuickPanelWindow(Lock, OpenSettings);
            _quickPanel.Toggle(bar.Monitor);
        };
        _bars.SettingsRequested += OpenSettings;
        _widgets.SettingsRequested += OpenSettings;

        _clock.MinuteChanged += _notch.SetTime;
        _clock.MinuteChanged += _bars.SetTime;
        _monitor.BatteryChanged += _bars.SetBattery;
        _monitor.BatteryChanged += _notch.SetBattery;
        _monitor.StatsChanged += _notch.SetSystem;

        _notifications.ExtraMailApps = () => _settings.ExtraMailApps ?? [];
        // Casella collegata e notifiche possono vedere la stessa mail (es. Outlook aperto): un solo avviso.
        _notifications.MailArrived += mail =>
        {
            if (_settings.MailInNotch && !_imap.Recent.Any(m => MailFeed.IsSame(m, mail)))
                _notch.ShowMailAlert(mail);
        };
        _imap.MailArrived += mail =>
        {
            if (_settings.MailInNotch && !_notifications.Recent.Any(m => MailFeed.IsSame(m, mail)))
                _notch.ShowMailAlert(mail);
        };
        _notifications.RecentChanged += RefreshMailPage;
        _imap.Changed += () =>
        {
            RefreshMailPage();
            MailAccountChanged?.Invoke();
        };
        WireLiveActivities();
        WireAgenda();
        WireDev();
        _network.Changed += _bars.SetNetwork;
        _bars.SetNetwork(_network.Current);
        _tracker.FullscreenChanged += monitor =>
        {
            _fullscreen = monitor != 0 && Interop.WindowNative.Monitors().Any(m => m.Handle == monitor && m.IsPrimary);
            _bars.FullscreenMonitor = monitor;
            ApplyTopLevelVisibility();
            ApplyVolumeKeys();
        };
    }

    /// <summary>Ctrl+Win: l'app spegne la shell e resta in attesa (docs/06-launcher.md).</summary>
    public event Action? ToggleRequested;

    public bool NotchEnabled
    {
        get => _settings.Notch;
        set => Update(_settings with { Notch = value });
    }

    public bool BarEnabled
    {
        get => _settings.Bar;
        set => Update(_settings with { Bar = value });
    }

    public bool UseWinKey
    {
        get => _launcher.UseWinKey;
        set => _launcher.UseWinKey = value;
    }

    public bool WidgetsLocked
    {
        get => _widgets.Locked;
        set => _widgets.SetLocked(value);
    }

    public bool MailInNotch
    {
        get => _settings.MailInNotch;
        set
        {
            Update(_settings with { MailInNotch = value });
            if (value)
            {
                _notifications.Start();
                if (_settings.MailAccount is { } account)
                    _imap.Start(account);
            }
            else
            {
                _notifications.Stop();
                _imap.Stop();
            }
            RefreshMailPage();
        }
    }

    public bool AccessAnimations
    {
        get => _settings.Access;
        set
        {
            Update(_settings with { Access = value });
            _access.Enabled = value;
        }
    }

    public string NotificationAccessText => _notifications.Access switch
    {
        UserNotificationListenerAccessStatus.Allowed => "Accesso alle notifiche consentito",
        UserNotificationListenerAccessStatus.Denied =>
            "Accesso negato: abilitalo in Impostazioni di Windows › Privacy e sicurezza › Notifiche",
        _ => "Accesso alle notifiche non ancora richiesto",
    };

    public bool CanRequestNotificationAccess => _notifications.Access == UserNotificationListenerAccessStatus.Unspecified;

    public bool NotificationAccessAllowed => _notifications.Access == UserNotificationListenerAccessStatus.Allowed;

    public IReadOnlyList<(string App, bool IsMail)> MailAppCandidates =>
        MailDetector.SetupCandidates(_notifications.NotifyingApps, _settings.ExtraMailApps ?? []);

    public void SetMailApp(string app, bool isMail) =>
        Update(_settings with { ExtraMailApps = MailDetector.WithExtraApp(_settings.ExtraMailApps ?? [], app, isMail) });

    public MailItem? LastMail => Mails is [var first, ..] ? first : null;

    public MailAccount? MailAccount => _settings.MailAccount;

    public string MailAccountStatus => _imap.Status;

    public bool MailAccountConnected => _imap.IsConnected;

    public event Action? MailAccountChanged;

    /// <summary>Prova la casella; se l'accesso riesce salva la password nel Gestore credenziali e la collega.</summary>
    public async Task<string?> ConnectMailAccountAsync(MailAccount account, string password)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(password);
        if (ImapSetup.Validate(account, password) is { } invalid)
            return invalid;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        if (await ImapMailService.TestAsync(account, password, timeout.Token) is { } failed)
            return failed;
        if (!Interop.CredentialStore.Write(account.CredentialTarget, account.Login, password))
            return "Impossibile salvare la password nel Gestore credenziali di Windows.";

        if (_settings.MailAccount is { } previous && previous.CredentialTarget != account.CredentialTarget)
            Interop.CredentialStore.Delete(previous.CredentialTarget);
        Update(_settings with { MailAccount = account });
        if (_settings.MailInNotch)
            _imap.Start(account);
        return null;
    }

    public void DisconnectMailAccount()
    {
        _imap.Stop();
        if (_settings.MailAccount is { } account)
            Interop.CredentialStore.Delete(account.CredentialTarget);
        Update(_settings with { MailAccount = null });
        RefreshMailPage();
    }

    public void ReconnectMailAccount()
    {
        if (_settings.MailAccount is { } account && _settings.MailInNotch)
            _imap.Start(account);
    }

    /// <summary>Mail della casella collegata e delle notifiche, le più recenti per prime e senza doppioni.</summary>
    public IReadOnlyList<MailItem> Mails => _settings.MailInNotch
        ? MailFeed.Merge(_imap.Recent, _notifications.Recent, NotificationService.RecentCapacity * 2)
        : [];

    public int UnreadCount => _imap.UnreadCount;

    public event Action? MailsChanged;

    public Task<(MailContent? Content, string? Error)> LoadAsync(MailItem mail, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(mail);
        return mail.Imap is { } message
            ? _imap.LoadAsync(message, cancellation)
            : Task.FromResult<(MailContent?, string?)>((null, "Questa mail non viene dalla casella collegata."));
    }

    public async Task<string?> MarkReadAsync(MailItem mail)
    {
        ArgumentNullException.ThrowIfNull(mail);
        if (mail.Imap is not { } message)
            return "Questa mail non viene dalla casella collegata.";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await _imap.MarkReadAsync(message, timeout.Token);
    }

    public string? WebmailName =>
        Webmail() is not { } site ? null : MailLinks.InstalledWebApp(site, _catalog.Apps)?.Name ?? site.Host;

    /// <summary>La web app installata per la webmail (es. Outlook di Edge) se c'è, altrimenti il sito nel browser.</summary>
    public void OpenWebmail()
    {
        if (Webmail() is not { } site)
            return;
        if (MailLinks.InstalledWebApp(site, _catalog.Apps) is { } app)
            AppCatalog.Launch(app);
        else
            Process.Start(new ProcessStartInfo(site.AbsoluteUri) { UseShellExecute = true })?.Dispose();
    }

    public bool OpenSourceApp(MailItem mail)
    {
        ArgumentNullException.ThrowIfNull(mail);
        if (_catalog.Apps.FirstOrDefault(a => a.Id.Equals(mail.AppId, StringComparison.OrdinalIgnoreCase)) is not { } app)
            return false;
        AppCatalog.Launch(app);
        return true;
    }

    public void SetWebmail(string? url)
    {
        if (_settings.MailAccount is { } account)
            Update(_settings with { MailAccount = account with { Webmail = string.IsNullOrWhiteSpace(url) ? null : url.Trim() } });
    }

    public string? WebmailAddress => _settings.MailAccount is { } account ? MailLinks.Webmail(account)?.AbsoluteUri : null;

    private Uri? Webmail() => _settings.MailAccount is { } account ? MailLinks.Webmail(account) : null;

    /// <summary>
    /// Clic su una mail del notch: quella della casella collegata si apre nel lettore; quella di una notifica
    /// nell'app che l'ha mandata (il testo completo è lì), o nel lettore se l'app non si può aprire.
    /// </summary>
    public void OpenMail(MailItem mail)
    {
        ArgumentNullException.ThrowIfNull(mail);
        if (mail.Imap is null && mail.AppId is not null && OpenSourceApp(mail))
            return;
        if (_mailReader is not { IsVisible: true })
        {
            _mailReader = new MailReaderWindow(this);
            _mailReader.Closed += (_, _) => _mailReader = null;
            _mailReader.Show();
        }
        _mailReader.Select(mail);
        _mailReader.Activate();
    }

    public void ShowTestMailAlert()
    {
        if (!_settings.Notch)
            return;
        _notch.ShowMailAlert(new MailItem("Nothing Shell", "Prova: la posta nel notch funziona", "Nothing Shell", DateTimeOffset.Now));
    }

    public async Task RequestNotificationAccessAsync()
    {
        await _notifications.RequestAccessAsync();
        RefreshMailPage();
    }

    public bool IsActivityEnabled(LiveActivity activity) => _settings.IsActivityEnabled(activity);

    public void SetActivityEnabled(LiveActivity activity, bool enabled) => Update(_settings.WithActivity(activity, enabled));

    public IReadOnlyList<string> CalendarFeeds => _settings.CalendarFeeds ?? [];

    public string CalendarStatus => _calendar.Status;

    public event Action? CalendarChanged;

    /// <summary>Salva i link iCal; restituisce l'errore se un indirizzo non è valido.</summary>
    public string? SetCalendarFeeds(IReadOnlyList<string> feeds)
    {
        ArgumentNullException.ThrowIfNull(feeds);
        var clean = feeds.Select(f => f.Trim()).Where(f => f.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (clean.FirstOrDefault(f => CalendarService.Normalize(f) is null) is { } bad)
            return $"Indirizzo non valido: {bad}";
        Update(_settings with { CalendarFeeds = clean });
        return null;
    }

    public void RefreshCalendar() => _ = _calendar.RefreshAsync();

    /// <summary>Prova dalle impostazioni: il riepilogo del mattino adesso, a qualsiasi ora.</summary>
    public void ShowMorningBriefNow() => _ = ShowMorningBriefAsync(force: true);

    public IEnumerable<(string Id, string Name)> Widgets => _widgets.Widgets;

    public IReadOnlyList<PluginLoadResult> PluginResults => _plugins.Results;

    public string DataFolder => AtomicFile.DataFolder;

    public string PluginFolder => PluginHost.Folder;

    public async Task StartAsync()
    {
        _widgets.Show(_settings.IsWidgetEnabled);
        LoadPlugins();

        _clock.Start();
        _monitor.Start();
        _network.Start();
        _pins.Start();
        ApplyPins();
        _tracker.Start();
        _bars.Rebuild();
        ApplyTopLevelVisibility();
        _winKey.Start();
        if (_settings.MailInNotch)
        {
            _notifications.Start();
            if (_settings.MailAccount is { } account)
                _imap.Start(account);
        }
        RefreshMailPage();
        ApplyLiveActivities();
        ScheduleMorningBrief(TimeSpan.FromSeconds(8)); // il tempo di collegarsi a casella e calendario

        await _access.StartAsync();
        await _media.StartAsync();
        try
        {
            await _catalog.RefreshAsync(); // pronto prima della prima apertura del launcher
            ApplyPins(); // lo Start dà l'AppUserModelID delle app che lo impostano solo sul processo
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // il launcher riproverà all'apertura
        }
    }

    public bool IsWidgetEnabled(string id) => _settings.IsWidgetEnabled(id);

    public void SetWidgetEnabled(string id, bool enabled)
    {
        Update(_settings.WithWidget(id, enabled));
        _widgets.SetVisible(id, enabled);
    }

    public void ResetWidgetLayout() => _widgets.ResetLayout();

    public void ReloadPlugins()
    {
        _widgets.RemovePlugins();
        LoadPlugins();
    }

    /// <summary>Blocca il PC con l'animazione Nothing (launcher, <c>--lock</c>).</summary>
    public void Lock() => _access.Lock();

    public void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _taskbar.Show();
        _quickPanel?.Close();
        _calendar.Dispose();
        _access.Dispose();
        _plugins.Dispose();
        _notifications.Dispose();
        _imap.Dispose();
        _audio.Dispose();
        _brightness.Dispose();
        _privacy.Dispose();
        _downloads.Dispose();
        _screenshots.Dispose();
        _bluetooth.Dispose();
        DisposeDev();
        _winKey.Dispose();
        _pins.Dispose();
        _bars.Dispose();
        _tracker.Dispose();
        _media.Dispose();
    }

    /// <summary>Volume, luminosità, microfono e webcam, download, screenshot e cuffie nel notch (docs/10-live-activities.md).</summary>
    private void WireLiveActivities()
    {
        _winKey.VolumeKey += key => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            switch (key)
            {
                case 0xAD:
                    _audio.ToggleMute();
                    break;
                case 0xAE:
                    _audio.StepDown();
                    break;
                default:
                    _audio.StepUp();
                    break;
            }
        });
        _audio.Changed += (level, muted) =>
        {
            if (CanShow(LiveActivity.Volume))
                _notch.ShowLevel(muted ? DotIcons.SpeakerMuted : DotIcons.Speaker, level, muted);
        };
        _brightness.Changed += level =>
        {
            if (CanShow(LiveActivity.Brightness))
                _notch.ShowLevel(DotIcons.Sun, level / 100.0, muted: false);
        };
        // Le app dello Store sono nel registro col nome della famiglia di pacchetti: il nome vero è nello Start.
        _privacy.PackagedAppName = family =>
            _catalog.Apps.FirstOrDefault(a => a.Id.StartsWith($"{family}!", StringComparison.OrdinalIgnoreCase))?.Name;
        _privacy.Changed += _notch.SetPrivacy;
        _downloads.Changed += _notch.SetDownloads;
        _downloads.Completed += path =>
        {
            if (CanShow(LiveActivity.Downloads))
                _notch.ShowCard(NotchCards.Download(path, _notch.DismissCard), CardDuration);
        };
        _screenshots.Captured += capture =>
        {
            if (CanShow(LiveActivity.Screenshots))
                _notch.ShowCard(NotchCards.Screenshot(capture, _notch.DismissCard), CardDuration);
        };
        _bluetooth.Connected += (name, battery) =>
        {
            if (CanShow(LiveActivity.Bluetooth))
                _notch.ShowNotice(DotIcons.Headphones, battery is < 20 ? Rendering.Theme.Red : Rendering.Theme.White,
                    battery is { } level ? $"{name}  {level}%" : $"{name} COLLEGATO");
        };
        _bluetooth.Disconnected += name =>
        {
            if (CanShow(LiveActivity.Bluetooth))
                _notch.ShowNotice(DotIcons.Headphones, Rendering.Theme.Grey, $"{name} SCOLLEGATO");
        };
    }

    private static readonly TimeSpan CardDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan AppointmentDuration = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan MorningDuration = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Prossimo appuntamento e riepilogo del mattino (docs/11-agenda-pannello-rapido.md): l'agenda si aggiorna a
    /// ogni minuto, i promemoria arrivano 10 minuti prima e all'inizio, il riepilogo al primo accesso del mattino
    /// (avvio, sblocco, ripresa dalla sospensione, o primo uso di mouse e tastiera se il PC è rimasto acceso).
    /// </summary>
    private void WireAgenda()
    {
        _calendar.Changed += () =>
        {
            RefreshAgenda();
            CalendarChanged?.Invoke();
        };
        _clock.MinuteChanged += minute =>
        {
            RefreshAgenda();
            if (Interop.NativeMethods.IdleTime() < TimeSpan.FromMinutes(1))
                _ = ShowMorningBriefAsync(force: false);
        };
        _morningDelay.Tick += (_, _) =>
        {
            _morningDelay.Stop();
            _ = ShowMorningBriefAsync(force: false);
        };
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock)
            Application.Current?.Dispatcher.BeginInvoke(() => ScheduleMorningBrief(TimeSpan.FromSeconds(3))); // dopo il bentornato
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
            return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _ = _calendar.RefreshAsync();
            ScheduleMorningBrief(TimeSpan.FromSeconds(10)); // la rete torna qualche secondo dopo
        });
    }

    private void ScheduleMorningBrief(TimeSpan delay)
    {
        _morningDelay.Stop();
        _morningDelay.Interval = delay;
        _morningDelay.Start();
    }

    private async Task ShowMorningBriefAsync(bool force)
    {
        var now = DateTime.Now;
        if (!force && (!CanShow(LiveActivity.MorningBrief) || !MorningBrief.ShouldShow(now, _settings.MorningBriefShown)))
            return;
        if (!force)
            Update(_settings with { MorningBriefShown = DateOnly.FromDateTime(now) }); // subito: il minuto dopo non riparte
        await Task.WhenAny(_calendar.RefreshAsync(), Task.Delay(TimeSpan.FromSeconds(10)));
        int? unread = _imap.IsConnected ? _imap.UnreadCount : null;
        var brief = MorningBrief.Compose(DateTime.Now, CalendarOn ? _calendar.Events : [], unread);
        _notch.ShowCard(NotchCards.MorningBrief(brief, now,
            () => _notch.OpenPage(NotchPage.Agenda),
            unread > 0 ? () => _notch.OpenPage(NotchPage.Mail) : null,
            _notch.DismissCard), MorningDuration);
    }

    private bool CalendarOn => ActivityOn(LiveActivity.Agenda) || ActivityOn(LiveActivity.MorningBrief);

    private void RefreshAgenda()
    {
        var now = DateTimeOffset.Now;
        var agenda = ActivityOn(LiveActivity.Agenda);
        var empty = !agenda ? "Agenda disattivata (Impostazioni)"
            : (_settings.CalendarFeeds ?? []).Count == 0 && _calendar.Events.Count == 0
                ? "Collega un calendario nelle impostazioni di Nothing Shell"
                : "Nessun appuntamento questa settimana";
        _notch.SetAgenda(agenda ? _calendar.Events : [], now, empty);
        if (!CanShow(LiveActivity.Agenda))
            return;
        foreach (var (appointment, kind) in _reminder.Due(_calendar.Events, now))
            _notch.ShowCard(NotchCards.Appointment(appointment, kind, now, OpenAppointment, OpenCalendarApp, _notch.DismissCard),
                AppointmentDuration);
    }

    /// <summary>Clic su un appuntamento: la riunione online se c'è un link, altrimenti il calendario.</summary>
    private static void OpenAppointment(CalendarEvent appointment)
    {
        if (appointment.MeetingLink is { } link)
            OpenUri(link);
        else
            OpenCalendarApp();
    }

    /// <summary>L'app Calendario di Windows (o il calendario di Outlook, che la sostituisce).</summary>
    private static void OpenCalendarApp() => OpenUri("outlookcal:");

    private static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // nessuna app associata
        }
    }

    private bool ActivityOn(LiveActivity activity) => _settings.Notch && _settings.IsActivityEnabled(activity);

    /// <summary>Si mostra solo se il notch è visibile (non con un'app a schermo intero sul monitor principale).</summary>
    private bool CanShow(LiveActivity activity) => ActivityOn(activity) && !_fullscreen;

    /// <summary>Avvia o ferma i servizi secondo le impostazioni: spenti non consumano nulla.</summary>
    private void ApplyLiveActivities()
    {
        Toggle(LiveActivity.Volume, _audio.Start, _audio.Stop);
        Toggle(LiveActivity.Brightness, _brightness.Start, _brightness.Stop);
        Toggle(LiveActivity.Privacy, _privacy.Start, _privacy.Stop);
        Toggle(LiveActivity.Downloads, _downloads.Start, _downloads.Stop);
        Toggle(LiveActivity.Screenshots, _screenshots.Start, _screenshots.Stop);
        Toggle(LiveActivity.Bluetooth, _bluetooth.Start, _bluetooth.Stop);
        if (CalendarOn)
            _calendar.Start(_settings.CalendarFeeds ?? []);
        else
            _calendar.Stop();
        RefreshAgenda();
        ApplyDevActivities();
        ApplyVolumeKeys();
    }

    private void Toggle(LiveActivity activity, Action start, Action stop)
    {
        if (ActivityOn(activity))
            start();
        else
            stop();
    }

    /// <summary>I tasti del volume restano a Windows quando il notch non si vede, così l'indicatore c'è sempre.</summary>
    private void ApplyVolumeKeys() => _winKey.InterceptVolumeKeys = CanShow(LiveActivity.Volume);

    /// <summary>App appuntate nella barra e fonti degli AppUserModelID (docs/05-bar.md).</summary>
    private void ApplyPins()
    {
        _appIds.SetSources(_pins.Pins, _catalog.Apps);
        _tracker.SetPinned(_pins.Pins);
        _tracker.Refresh();
    }

    private void RefreshMailPage()
    {
        var empty = !_settings.MailInNotch ? "Mail nel notch disattivate (Impostazioni)"
            : _imap.IsConnected || _notifications.Access == UserNotificationListenerAccessStatus.Allowed ? "Nessuna mail recente"
            : _settings.MailAccount is not null ? _imap.Status
            : "Collega la casella nelle impostazioni di Nothing Shell";
        _notch.SetMail(Mails, empty);
        MailsChanged?.Invoke();
    }

    private void LoadPlugins()
    {
        _plugins.Load();
        foreach (var manifest in _plugins.Manifests)
        {
            var view = new PluginWidget(manifest);
            _widgets.AddPlugin(view);
            _plugins.Start(manifest, view.Update);
        }
    }

    /// <summary>Notch e barra: visibili se abilitati e se non c'è un'app a schermo intero.</summary>
    private void ApplyTopLevelVisibility()
    {
        SetVisible(_notch, _settings.Notch && !_fullscreen);
        _bars.Visible = _settings.Bar; // le barre si nascondono da sole sui monitor a schermo intero

        // La taskbar di Windows torna solo se la barra è spenta per scelta, non per lo schermo intero.
        if (_settings.Bar)
            _taskbar.Hide();
        else
            _taskbar.Show();
    }

    private static void SetVisible(Window window, bool visible)
    {
        if (visible && !window.IsVisible)
            window.Show();
        else if (!visible && window.IsVisible)
            window.Hide();
    }

    private void Update(ShellSettings settings)
    {
        _settings = settings;
        try
        {
            _settingsStore.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // la scelta resta valida per questa sessione
        }
        ApplyTopLevelVisibility();
        ApplyLiveActivities();
    }
}
