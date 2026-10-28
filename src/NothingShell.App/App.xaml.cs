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
            new("Mario Rossi", "Riunione di domani alle 10: ordine del giorno aggiornato", "Outlook", now),
            new("Studio Bianchi", "Preventivo convalida", "Outlook", now.AddMinutes(-12)),
            new("GitHub", "[nothing-shell] Nuova issue aperta", "Brave", now.AddHours(-1)),
        ];
        notch.SetTime(DateTime.Now);
        notch.SetMail(demo, string.Empty);
        notch.SetSystem(new SystemStats(0.23, SystemMonitor.ReadMemoryLoad()));
        notch.SetBattery(SystemMonitor.ReadBattery());

        notch.ShowMailAlert(demo[0]);
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Alert, NotchPage.Home), Path.Combine(folder, "notch-alert.png"));
        foreach (var page in NotchPager.Pages)
            SaveSnapshot(notch.PrepareSnapshot(NotchState.Expanded, page),
                Path.Combine(folder, $"notch-{page}.png".ToLowerInvariant()));

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
