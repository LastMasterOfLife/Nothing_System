using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NothingShell.App.Access;
using NothingShell.App.Bar;
using NothingShell.App.Launcher;
using NothingShell.Core.Launcher;
using NothingShell.App.Notch;
using NothingShell.App.Plugins;
using NothingShell.App.Services;
using NothingShell.App.Settings;
using NothingShell.App.Widgets;
using NothingShell.Core.Access;
using NothingShell.Core.Notch;
using NothingShell.Core.Plugins;
using NothingShell.Core.Sensors;
using NothingShell.Core.Storage;
using NothingShell.Core.Widgets;

namespace NothingShell.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001",
    Justification = "Il ciclo di vita è quello dell'Application: le risorse sono rilasciate in OnExit.")]
public partial class App : Application
{
    private const string ExitEventName = @"Local\NothingShell.Exit";
    private const string LockEventName = @"Local\NothingShell.Lock";

    private Mutex? _singleInstance;
    private EventWaitHandle? _exitSignal;
    private RegisteredWaitHandle? _exitWait;
    private EventWaitHandle? _lockSignal;
    private RegisteredWaitHandle? _lockWait;
    private ShellController? _shell;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Le superfici sono piccole: il rendering software evita di caricare il driver GPU (~200 MB).
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        switch (e.Args)
        {
            case ["--snapshot", var folder]:
                RenderSnapshots(folder);
                await RenderLauncherSnapshot(folder);
                await RenderBarSnapshots(folder);
                await RenderPluginAndSettingsSnapshots(folder);
                RenderNotchSnapshots(folder);
                await RenderAccessSnapshots(folder);
                Shutdown();
                return;
            case ["--dump-windows", var file]:
                await DumpWindows(file);
                Shutdown();
                return;
            case ["--live-check", var report]:
                await LiveCheck(report);
                Shutdown();
                return;
            case ["--preview-demo", var image]:
                await PreviewDemo(image);
                Shutdown();
                return;
            case ["--restore-taskbar"]:
                WindowsTaskbar.ForceShow();
                Shutdown();
                return;
            case ["--lock"]:
                if (EventWaitHandle.TryOpenExisting(LockEventName, out var shell))
                    using (shell)
                        shell.Set();
                else
                    PowerActions.Lock();
                Shutdown();
                return;
            case ["--access-demo", var report]:
                await PlayAccessDemo(report);
                return;
            case ["--exit"]:
                if (EventWaitHandle.TryOpenExisting(ExitEventName, out var running))
                    using (running)
                        running.Set();
                Shutdown();
                return;
        }

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\NothingShell", out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }
        ListenForExitSignal();
        ListenForLockSignal();
        RestoreTaskbarOnCrash();

        _shell = new ShellController();
        if (e.Args is ["--settings"])
            _shell.OpenSettings();
        await _shell.StartAsync();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        WindowsTaskbar.ForceShow();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shell?.Dispose();
        _exitWait?.Unregister(null);
        _lockWait?.Unregister(null);
        _lockSignal?.Dispose();
        _exitSignal?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary><c>NothingShell.exe --exit</c> segnala questo evento: chiusura pulita (taskbar ripristinata).</summary>
    private void ListenForExitSignal()
    {
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitSignal,
            (_, _) => Dispatcher.BeginInvoke(() => Shutdown()), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    /// <summary><c>NothingShell.exe --lock</c>: blocco con l'animazione Nothing (utile per scorciatoie).</summary>
    private void ListenForLockSignal()
    {
        _lockSignal = new EventWaitHandle(false, EventResetMode.AutoReset, LockEventName);
        _lockWait = ThreadPool.RegisterWaitForSingleObject(_lockSignal,
            (_, _) => Dispatcher.BeginInvoke(() => _shell?.Lock()), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Bentornato dal vivo senza bloccare il PC; scrive fotogrammi e durata in <paramref name="report"/>.</summary>
    private async Task PlayAccessDemo(string report)
    {
        var access = new AccessController();
        await access.StartAsync();
        var started = DateTimeOffset.Now;
        access.Hidden += () =>
        {
            var welcome = DateTimeOffset.Now - started - TimeSpan.FromMilliseconds(700);
            File.WriteAllText(report,
                $"frames={access.FramesRendered} welcomeMs={welcome.TotalMilliseconds:0} fps={access.FramesRendered / welcome.TotalSeconds:0.0}");
            access.Dispose();
            Shutdown();
        };
        var watchdog = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        watchdog.Tick += (_, _) => Shutdown();
        watchdog.Start();
        access.PlayWelcomeDemo();
    }

    /// <summary>Overlay di accesso: stato bloccato e fotogrammi chiave delle due sequenze, con il profilo reale.</summary>
    private static async Task RenderAccessSnapshots(string folder)
    {
        var window = new AccessWindow();
        window.SetProfile(await UserProfile.LoadAsync());
        var now = DateTime.Now;
        (string Name, AccessFrame Frame)[] frames =
        [
            ("access-locked", AccessTimeline.Locked),
            ("access-lock-200", AccessTimeline.Lock(TimeSpan.FromMilliseconds(200))),
            ("access-lock-450", AccessTimeline.Lock(TimeSpan.FromMilliseconds(450))),
            ("access-lock-900", AccessTimeline.Lock(TimeSpan.FromMilliseconds(900))),
            ("access-welcome-300", AccessTimeline.Welcome(TimeSpan.FromMilliseconds(300))),
            ("access-welcome-700", AccessTimeline.Welcome(TimeSpan.FromMilliseconds(700))),
            ("access-welcome-1100", AccessTimeline.Welcome(TimeSpan.FromMilliseconds(1100))),
            ("access-welcome-1350", AccessTimeline.Welcome(TimeSpan.FromMilliseconds(1350))),
        ];
        foreach (var (name, frame) in frames)
        {
            window.Render(frame, now);
            SaveSnapshot(window.Surface, Path.Combine(folder, $"{name}.png"), scale: 1);
        }
        window.Close();
    }

    /// <summary>Qualunque crash deve lasciare la taskbar di Windows visibile.</summary>
    private void RestoreTaskbarOnCrash()
    {
        DispatcherUnhandledException += (_, _) => WindowsTaskbar.ForceShow();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => WindowsTaskbar.ForceShow();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => WindowsTaskbar.ForceShow();
    }

    /// <summary>Notch: avviso mail, ogni pagina espansa e timer in corso. Mail di esempio, solo per l'anteprima.</summary>
    private static void RenderNotchSnapshots(string folder)
    {
        using var media = new MediaService(); // non avviato: nessuna sessione, Home mostra l'ora
        var notch = new NotchWindow(media);
        var now = DateTimeOffset.Now;
        MailItem[] demo =
        [
            new("Mario Rossi", "Riunione di domani alle 10: ordine del giorno aggiornato", "Casella collegata", now)
            {
                Imap = new ImapMessageRef(1, 3), IsUnread = true, SenderAddress = "mario.rossi@example.it",
            },
            new("Studio Bianchi", "Preventivo convalida", "Casella collegata", now.AddMinutes(-12)) { Imap = new ImapMessageRef(1, 2) },
            new("GitHub", "[nothing-shell] Nuova issue aperta", "Brave", now.AddHours(-1)),
        ];
        notch.SetTime(DateTime.Now);
        notch.SetMail(demo, string.Empty);
        notch.SetSystem(new SystemStats(0.23, SystemMonitor.ReadMemoryLoad()));
        notch.SetBattery(SystemMonitor.ReadBattery());
        var today = DateTime.Today;
        CalendarEvent[] agenda =
        [
            new("Ferie Marco", new DateTimeOffset(today), new DateTimeOffset(today.AddDays(1)), AllDay: true),
            new("Standup", new DateTimeOffset(DateTime.Now.AddMinutes(8)), new DateTimeOffset(DateTime.Now.AddMinutes(23)),
                MeetingLink: "https://teams.microsoft.com/l/meetup-join/demo"),
            new("Revisione budget", new DateTimeOffset(DateTime.Now.AddMinutes(95)), new DateTimeOffset(DateTime.Now.AddMinutes(155)), Location: "Sala 2"),
            new("Dentista", new DateTimeOffset(DateTime.Now.AddHours(5)), new DateTimeOffset(DateTime.Now.AddHours(6))),
        ];
        notch.SetAgenda(agenda, DateTimeOffset.Now, string.Empty);

        notch.ShowMailAlert(demo[0]);
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Alert, NotchPage.Home), Path.Combine(folder, "notch-alert.png"));
        foreach (var page in NotchPager.Pages)
            SaveSnapshot(notch.PrepareSnapshot(NotchState.Expanded, page),
                Path.Combine(folder, $"notch-{page}.png".ToLowerInvariant()));

        var reader = new MailReaderWindow(new SnapshotMail(demo));
        reader.Select(demo[0]);
        var readerRoot = (FrameworkElement)reader.Content;
        readerRoot.Width = reader.Width;
        readerRoot.Height = reader.Height;
        SaveSnapshot(readerRoot, Path.Combine(folder, "mail-reader.png"));

        // Attività dal vivo (docs/10-live-activities.md)
        notch.ShowLevel(NothingShell.Core.Design.DotIcons.Speaker, 0.64, muted: false);
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Alert, NotchPage.Home), Path.Combine(folder, "notch-volume.png"));
        notch.ShowLevel(NothingShell.Core.Design.DotIcons.SpeakerMuted, 0.64, muted: true);
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Alert, NotchPage.Home), Path.Combine(folder, "notch-volume-muted.png"));
        notch.ShowLevel(NothingShell.Core.Design.DotIcons.Sun, 0.8, muted: false);
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Alert, NotchPage.Home), Path.Combine(folder, "notch-brightness.png"));
        notch.ShowNotice(NothingShell.Core.Design.DotIcons.Headphones, Rendering.Theme.White, "Nothing Ear (a)  80%");
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Alert, NotchPage.Home), Path.Combine(folder, "notch-bluetooth.png"));

        notch.SetPrivacy(new PrivacyState(["Zoom"], ["Zoom"]));
        notch.SetDownloads([new ActiveDownload(@"C:\x.crdownload", "report-trimestre.pdf", 13_002_342, 2_400_000, TimeSpan.Zero)]);
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Activity, NotchPage.Home), Path.Combine(folder, "notch-activity-live.png"));
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Expanded, NotchPage.System), Path.Combine(folder, "notch-system-live.png"));
        notch.SetPrivacy(PrivacyState.None);
        notch.SetDownloads([]);

        var shot = new RenderTargetBitmap(1600, 900, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(40, 40, 48), Color.FromRgb(200, 30, 40), 30), null, new Rect(0, 0, 1600, 900));
            dc.DrawRectangle(Brushes.White, null, new Rect(200, 200, 700, 400));
        }
        shot.Render(visual);
        shot.Freeze();
        notch.ShowCard(NotchCards.Screenshot(new ScreenshotCapture(shot, null), () => { }), TimeSpan.FromSeconds(8));
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Card, NotchPage.Home), Path.Combine(folder, "notch-screenshot.png"));
        notch.ShowCard(NotchCards.Download(Path.Combine(folder, "notch-volume.png"), () => { }), TimeSpan.FromSeconds(8));
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Card, NotchPage.Home), Path.Combine(folder, "notch-download.png"));

        // Agenda (docs/11-agenda-pannello-rapido.md)
        notch.ShowCard(NotchCards.Appointment(agenda[1], ReminderKind.Soon, DateTimeOffset.Now, _ => { }, () => { }, () => { }), TimeSpan.FromSeconds(8));
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Card, NotchPage.Home), Path.Combine(folder, "notch-appointment.png"));
        var brief = MorningBrief.Compose(today.AddHours(8), agenda, 5);
        notch.ShowCard(NotchCards.MorningBrief(brief, today.AddHours(8), () => { }, () => { }, () => { }), TimeSpan.FromSeconds(8));
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Card, NotchPage.Home), Path.Combine(folder, "notch-morning.png"));
        notch.SetAgenda([], DateTimeOffset.Now, "Collega un calendario nelle impostazioni di Nothing Shell");
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Expanded, NotchPage.Agenda), Path.Combine(folder, "notch-agenda-empty.png"));

        var quick = new QuickPanelWindow(() => { }, () => { });
        SaveSnapshot(quick.PrepareSnapshot(), Path.Combine(folder, "quick-panel.png"));
        quick.Close();

        notch.StartTimer(TimeSpan.FromMinutes(25));
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Expanded, NotchPage.Timer), Path.Combine(folder, "notch-timer-running.png"));
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Activity, NotchPage.Home), Path.Combine(folder, "notch-activity-timer.png"));
    }

    /// <summary>Widget dei plugin installati (dati veri dalle loro sorgenti) e finestra delle impostazioni, in PNG.</summary>
    private static async Task RenderPluginAndSettingsSnapshots(string folder)
    {
        using var plugins = new PluginHost();
        plugins.Load();
        foreach (var manifest in plugins.Manifests)
        {
            var view = new PluginWidget(manifest);
            view.Update(await plugins.FetchAsync(manifest));
            SaveSnapshot(view, Path.Combine(folder, $"plugin-{manifest.Id}.png"));
        }

        var settings = new SettingsWindow(new SnapshotSettings(plugins.Results));
        var root = (FrameworkElement)settings.Content;
        root.Width = settings.Width;
        root.Height = settings.Height;
        SaveSnapshot(root, Path.Combine(folder, "settings.png"));
        root.Height = 1400; // tutte le sezioni, senza scorrimento
        SaveSnapshot(root, Path.Combine(folder, "settings-full.png"));
    }

    /// <summary>
    /// Diagnostica delle attività dal vivo, in sola lettura: volume attuale, chi usa microfono e webcam, cartelle
    /// osservate, dispositivi Bluetooth associati con stato, classe e batteria.
    /// </summary>
    private static async Task LiveCheck(string file)
    {
        var lines = new List<string>();
        using (var audio = new AudioService())
        {
            audio.Start();
            lines.Add($"volume: {audio.Read()?.ToString() ?? "non disponibile"}");
        }
        using (var privacy = new PrivacyMonitor())
        {
            privacy.Start();
            lines.Add($"microfono: [{string.Join(", ", privacy.Current.Microphone)}] webcam: [{string.Join(", ", privacy.Current.Camera)}]");
        }
        lines.Add($"download: {Interop.ShellNative.DownloadsFolder}");
        lines.Add($"screenshot: {ScreenshotWatcher.Folder}");
        lines.Add($"greenshot: {ScreenshotWatcher.GreenshotFolder ?? "non installato"}");
        using (var calendar = new CalendarService())
        {
            calendar.Start([]);
            await calendar.RefreshAsync();
            lines.Add($"calendario: {calendar.Status}");
            foreach (var e in calendar.Events.Take(5))
                lines.Add($"  {e.Start:ddd dd/MM HH:mm} {e.Title}{(e.MeetingLink is null ? string.Empty : " [online]")}");
        }
        var (wifi, bluetooth) = await RadioSwitch.LoadAsync();
        lines.Add($"radio: wifi={(wifi is null ? "n/d" : wifi.IsOn ? "on" : "off")} bluetooth={(bluetooth is null ? "n/d" : bluetooth.IsOn ? "on" : "off")} rete={RadioSwitch.ConnectedNetwork() ?? "-"}");
        lines.Add($"luminosità: {ScreenBrightness.Read()?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "non regolabile"}");
        using (var microphone = new AudioService(capture: true))
        {
            microphone.Start();
            lines.Add($"microfono: {microphone.Read()?.ToString() ?? "nessuno"}");
        }
        lines.Add($"bluetooth: {string.Join(Environment.NewLine + "  ", await BluetoothAudioWatcher.DescribeAsync())}");
        await File.WriteAllLinesAsync(file, lines);
    }

    /// <summary>Mail d'esempio per disegnare il lettore senza collegarsi a una casella.</summary>
    private sealed class SnapshotMail(IReadOnlyList<MailItem> mails) : IMailReaderActions
    {
        public IReadOnlyList<MailItem> Mails => mails;

        public int UnreadCount => 4;

        public event Action? MailsChanged
        {
            add { }
            remove { }
        }

        public Task<(MailContent? Content, string? Error)> LoadAsync(MailItem mail, CancellationToken cancellation) =>
            Task.FromResult<(MailContent?, string?)>((new MailContent(
                $"{mail.Sender} <{mail.SenderAddress}>",
                "Andrea",
                mail.Received,
                mail.Subject,
                MailText.FromHtml("<p>Ciao Andrea,</p><p>ti giro l'ordine del giorno aggiornato:</p><ul><li>stato del progetto</li>" +
                                  "<li>budget del trimestre</li><li>varie ed eventuali</li></ul><p>Per entrare nella stanza " +
                                  "virtuale usa il codice 482913.</p><p>A domani,<br>Mario</p>"),
                ["ordine-del-giorno.pdf"],
                mail.SenderAddress), null));

        public Task<string?> MarkReadAsync(MailItem mail) => Task.FromResult<string?>(null);

        public string? WebmailName => "Outlook";

        public void OpenWebmail()
        {
        }

        public bool OpenSourceApp(MailItem mail) => false;
    }

    /// <summary>Stato fittizio ma realistico per disegnare le impostazioni senza avviare la shell.</summary>
    private sealed class SnapshotSettings(IReadOnlyList<PluginLoadResult> results) : IShellSettingsActions
    {
        public bool NotchEnabled { get; set; } = true;

        public bool BarEnabled { get; set; } = true;

        public bool UseWinKey { get; set; } = true;

        public bool WidgetsLocked { get; set; }

        public bool MailInNotch { get; set; } = true;

        public bool AccessAnimations { get; set; } = true;

        public string NotificationAccessText => "Accesso alle notifiche consentito";

        public bool CanRequestNotificationAccess => false;

        public Task RequestNotificationAccessAsync() => Task.CompletedTask;

        public bool NotificationAccessAllowed => true;

        public IReadOnlyList<(string App, bool IsMail)> MailAppCandidates => [("Aruba Webmail", true), ("Slack", false)];

        public void SetMailApp(string app, bool isMail)
        {
        }

        public MailItem? LastMail => new("Mario Rossi", "Preventivo sito", "Outlook", DateTimeOffset.Now);

        public void ShowTestMailAlert()
        {
        }

        public MailAccount? MailAccount => null;

        public string MailAccountStatus => "Nessuna casella collegata";

        public bool MailAccountConnected => false;

        public event Action? MailAccountChanged
        {
            add { }
            remove { }
        }

        public Task<string?> ConnectMailAccountAsync(MailAccount account, string password) => Task.FromResult<string?>(null);

        public void DisconnectMailAccount()
        {
        }

        public void ReconnectMailAccount()
        {
        }

        public string? WebmailAddress => null;

        public bool IsActivityEnabled(LiveActivity activity) => activity != LiveActivity.Brightness;

        public void SetActivityEnabled(LiveActivity activity, bool enabled)
        {
        }

        public void SetWebmail(string? url)
        {
        }

        public IReadOnlyList<string> CalendarFeeds => ["https://outlook.office365.com/owa/calendar/abc/reachcalendar.ics"];

        public string? SetCalendarFeeds(IReadOnlyList<string> feeds) => null;

        public string CalendarStatus => "Calendario di Windows: 0 eventi · Link iCal: 14 eventi";

        public event Action? CalendarChanged
        {
            add { }
            remove { }
        }

        public void RefreshCalendar()
        {
        }

        public void ShowMorningBriefNow()
        {
        }

        public IEnumerable<(string Id, string Name)> Widgets =>
            new[] { ("Clock", "Orologio"), ("Battery", "Batteria"), ("System", "Sistema"), ("Calendar", "Calendario") }
                .Concat(results.Where(r => r.Manifest is not null).Select(r => (r.Manifest!.WidgetId, $"{r.Manifest.Name} (plugin)")));

        public IReadOnlyList<PluginLoadResult> PluginResults => results;

        public string DataFolder => AtomicFile.DataFolder;

        public string PluginFolder => PluginHost.Folder;

        public bool IsWidgetEnabled(string id) => id != "System";

        public void SetWidgetEnabled(string id, bool enabled)
        {
        }

        public void ResetWidgetLayout()
        {
        }

        public void ReloadPlugins()
        {
        }
    }

    /// <summary>
    /// Finestre (<c>+</c> in barra con la chiave dell'app, <c>-</c> col motivo dell'esclusione), poi le app
    /// appuntate con AppUserModelID e numero di recenti.
    /// </summary>
    private static async Task DumpWindows(string file)
    {
        var catalog = new AppCatalog();
        await catalog.RefreshAsync();
        var pins = TaskbarPins.Read();
        var resolver = new AppIdResolver();
        resolver.SetSources(pins, catalog.Apps);

        using var tracker = new WindowTracker(resolver);
        var lines = tracker.Diagnose().ToList();
        lines.Add(string.Empty);
        lines.Add("Appuntate:");
        foreach (var pin in pins)
        {
            var (items, frequent) = Interop.ShellNative.ReadJumpList(pin.AppId);
            lines.Add($"  {pin.Name} | {pin.AppId} | {pin.LaunchPath} | exe={pin.ExePath} args={pin.HasArguments} | " +
                      $"{(frequent ? "frequenti" : "recenti")}={NothingShell.Core.Bar.JumpLists.Clean(items).Count}");
        }
        File.WriteAllLines(file, lines);
    }

    /// <summary>
    /// Anteprime dal vivo dell'app con più finestre, sul monitor principale, catturate dallo schermo in PNG
    /// (le miniature le disegna DWM: un rendering WPF non le vedrebbe).
    /// </summary>
    private static async Task PreviewDemo(string image)
    {
        var catalog = new AppCatalog();
        await catalog.RefreshAsync();
        var pins = TaskbarPins.Read();
        var resolver = new AppIdResolver();
        resolver.SetSources(pins, catalog.Apps);
        using var tracker = new WindowTracker(resolver);
        tracker.Refresh();
        var monitor = Interop.WindowNative.Monitors().First(m => m.IsPrimary);
        if (tracker.Groups.MaxBy(g => g.Windows.Count) is not { IsRunning: true } group)
            return;

        var preview = new PreviewWindow();
        preview.Open(group, null, 0, monitor.Bounds.X + monitor.Bounds.Width / 2, monitor);
        await Task.Delay(1000);
        var hwnd = new WindowInteropHelper(preview).Handle;
        if (Interop.WindowNative.GetWindowRect(hwnd, out var r) &&
            Interop.NativeMethods.CaptureScreen(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) is { } shot)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(shot));
            using var file = File.Create(image);
            encoder.Save(file);
        }
        preview.Close();
    }

    /// <summary>Barra con le app appuntate e aperte, ed elenco dei recenti della prima app che ne ha, in PNG.</summary>
    private static async Task RenderBarSnapshots(string folder)
    {
        var catalog = new AppCatalog();
        await catalog.RefreshAsync();
        var pins = TaskbarPins.Read();
        var resolver = new AppIdResolver();
        resolver.SetSources(pins, catalog.Apps);
        using var tracker = new WindowTracker(resolver);
        var shellIcons = new ShellIcons();
        var launcher = new LauncherWindow(catalog, shellIcons, new LauncherSettingsStore(LauncherSettingsStore.DefaultPath));
        tracker.SetPinned(pins);
        tracker.Refresh();

        // Una barra per monitor (bar-1.png sul principale), come le crea BarHost
        var monitors = Interop.WindowNative.Monitors().OrderByDescending(m => m.IsPrimary).ToArray();
        for (var i = 0; i < monitors.Length; i++)
        {
            var bar = new BarWindow(tracker, new AppIcons(), shellIcons, catalog, new WindowsTaskbar(), launcher,
                monitors[i], NothingShell.Core.Bar.MultiMonitorMode.AllTaskbars);
            bar.SetTime(DateTime.Now);
            var root = (FrameworkElement)bar.Content;
            var rect = NothingShell.Core.Bar.MultiMonitorBar.Place(monitors[i]);
            root.Width = rect.Width / monitors[i].Scale;
            root.Height = rect.Height / monitors[i].Scale;
            SaveSnapshot(root, Path.Combine(folder, $"bar-{i + 1}.png"), scale: 1);
        }

        var appIcons = new AppIcons();
        foreach (var pin in pins)
        {
            var (items, frequent) = Interop.ShellNative.ReadJumpList(pin.AppId);
            var clean = NothingShell.Core.Bar.JumpLists.Clean(items);
            if (clean.Count < 3)
                continue;
            var icon = shellIcons.GetForPath(pin.LaunchPath);
            var model = new JumpListModel(pin.Name, icon, frequent, clean, 2,
                item => item.IsLink ? icon : appIcons.ForFile(item.Path), _ => { }, () => { }, () => { });
            SaveSnapshot(new JumpListWindow().PrepareSnapshot(model), Path.Combine(folder, "jumplist.png"));
            break;
        }
    }

    /// <summary>Launcher con il catalogo reale e le preferenze reali (sola lettura), in PNG.</summary>
    private static async Task RenderLauncherSnapshot(string folder)
    {
        var catalog = new AppCatalog();
        await catalog.RefreshAsync();
        var launcher = new LauncherWindow(catalog, new ShellIcons(), new LauncherSettingsStore(LauncherSettingsStore.DefaultPath));

        launcher.PrepareSnapshot(query: null);
        SaveSnapshot((FrameworkElement)launcher.Content, Path.Combine(folder, "launcher.png"));
        launcher.PrepareSnapshot(query: "vsc");
        SaveSnapshot((FrameworkElement)launcher.Content, Path.Combine(folder, "launcher-search.png"));
        File.WriteAllText(Path.Combine(folder, "launcher-apps.txt"), $"{catalog.Apps.Count} app");
    }

    /// <summary>Disegna ogni widget in PNG (2×) nella cartella indicata: verifica grafica senza toccare il desktop.</summary>
    private static void RenderSnapshots(string folder)
    {
        Directory.CreateDirectory(folder);
        var clock = new ClockService();
        var monitor = new SystemMonitor();
        var views = WidgetHost.CreateViews(clock, monitor);
        var now = DateTime.Now;

        foreach (var view in views)
        {
            switch (view)
            {
                case ClockWidget c: c.Update(now); break;
                case CalendarWidget c: c.Update(now); break;
                case BatteryWidget b: b.Update(SystemMonitor.ReadBattery()); break;
                case SystemWidget s: s.Update(new SystemStats(0.23, SystemMonitor.ReadMemoryLoad())); break;
            }

            SaveSnapshot(view, Path.Combine(folder, $"{view.Id}.png".ToLowerInvariant()));
        }

        // Variante batteria scarica, per controllare l'accento rosso
        var low = new BatteryWidget();
        low.Update(new BatteryStatus(true, 12, false));
        SaveSnapshot(low, Path.Combine(folder, "battery-low.png"));
    }

    private static void SaveSnapshot(FrameworkElement view, string path, double scale = 2)
    {
        var size = new Size(view.Width, view.Height);
        view.Measure(size);
        view.Arrange(new Rect(size));
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(view);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
