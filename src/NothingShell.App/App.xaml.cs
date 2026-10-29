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
using NothingShell.Core.Connectors;
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
    private const string DormantMutexName = @"Local\NothingShell.Dormant";

    private Mutex? _singleInstance;
    private EventWaitHandle? _exitSignal;
    private RegisteredWaitHandle? _exitWait;
    private EventWaitHandle? _lockSignal;
    private RegisteredWaitHandle? _lockWait;
    private ShellController? _shell;
    private WinKeyHook? _dormantHook;

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
            case ["--phone-check", var report]:
                await PhoneCheck(report);
                Shutdown();
                return;
            case ["--preview-demo", var image]:
                await PreviewDemo(image);
                Shutdown();
                return;
            case ["--task", var json]:
                TaskWatcher.Send(json, TimeSpan.FromMilliseconds(500));
                Shutdown();
                return;
            case ["--connectors-check", var report, .. var rest]:
                var connectors = rest.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? ConnectorHost.Folder;
                File.WriteAllLines(report, await ConnectorHost.CheckAsync(connectors, rest.Contains("--esegui")));
                Shutdown();
                return;
            case ["--agents-check", var report]:
                AgentsCheck(report);
                Shutdown();
                return;
            case ["--dev-check", var report]:
                await DevCheck(report);
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
            case ["--dormant"]:
                StartDormant();
                return;
            case ["--exit"]:
                if (EventWaitHandle.TryOpenExisting(ExitEventName, out var running))
                    using (running)
                        running.Set();
                Shutdown();
                return;
        }

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\NothingShell", out var createdNew);
        // Dopo Ctrl+Win dallo stato dormiente l'istanza precedente può star ancora chiudendo.
        if (!createdNew && !(e.Args is ["--wake"] && WaitForPreviousShell()))
        {
            Shutdown();
            return;
        }
        ListenForExitSignal();
        ListenForLockSignal();
        RestoreTaskbarOnCrash();

        _shell = new ShellController();
        _shell.ToggleRequested += () => Relaunch("--dormant");
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
        _dormantHook?.Dispose();
        _exitWait?.Unregister(null);
        _lockWait?.Unregister(null);
        _lockSignal?.Dispose();
        _exitSignal?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private bool WaitForPreviousShell()
    {
        try
        {
            return _singleInstance!.WaitOne(TimeSpan.FromSeconds(10));
        }
        catch (AbandonedMutexException)
        {
            return true; // terminata di forza: il mutex ora è nostro
        }
    }

    /// <summary>
    /// Shell spenta con Ctrl+Win: nessuna finestra né servizio, solo l'hook che aspetta Ctrl+Win
    /// per riaccenderla. Win da solo e i tasti del volume restano a Windows.
    /// </summary>
    private void StartDormant()
    {
        _singleInstance = new Mutex(initiallyOwned: true, DormantMutexName, out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }
        ListenForExitSignal(); // --exit chiude anche lo stato dormiente
        _dormantHook = new WinKeyHook { Enabled = false };
        _dormantHook.TogglePressed += () => Dispatcher.BeginInvoke(() => Relaunch("--wake"));
        _dormantHook.Start();
    }

    /// <summary>Avvia la stessa app nell'altro stato e chiude questa (pulizia completa, taskbar ripristinata).</summary>
    private void Relaunch(string argument)
    {
        if (Environment.ProcessPath is { } exe)
            System.Diagnostics.Process.Start(exe, argument)?.Dispose();
        Shutdown();
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
    /// <summary>
    /// Per <c>--agents-check</c>: le sessioni di Claude Code di oggi come le vede il notch (progetto, modello, stato, token,
    /// costo), senza il testo dei messaggi (docs/14-agenti-connettori.md).
    /// </summary>
    private static void AgentsCheck(string report)
    {
        var lines = new List<string> { $"Cartella: {AgentWatcher.ProjectsFolder}" };
        var today = new DateTimeOffset(DateTime.Today);
        double total = 0;
        var files = Directory.Exists(AgentWatcher.ProjectsFolder)
            ? Directory.EnumerateFiles(AgentWatcher.ProjectsFolder, "*.jsonl", SearchOption.AllDirectories).Where(f => File.GetLastWriteTime(f) >= DateTime.Today)
            : [];
        foreach (var file in files)
        {
            var sub = string.Equals(Path.GetFileName(Path.GetDirectoryName(file)), "subagents", StringComparison.OrdinalIgnoreCase);
            var session = new NothingShell.Core.Agents.AgentSession(Path.GetFileNameWithoutExtension(file));
            var turns = 0;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                while (reader.ReadLine() is { } line)
                    if (NothingShell.Core.Agents.ClaudeTranscript.Parse(line) is { } entry && session.Apply(sub ? entry with { Sidechain = true } : entry) is not null)
                        turns++;
            }
            var s = session.Snapshot();
            var cost = session.CostSince(today);
            total += cost ?? 0;
            lines.Add($"{(sub ? "  sotto-agente " : string.Empty)}{s.Title} · {NothingShell.Core.Agents.AgentPricing.ShortName(s.Model)} · {s.State} · " +
                      $"IN {NothingShell.Core.Agents.AgentFormat.Tokens(s.Total.AllInput)} (cache letta {NothingShell.Core.Agents.AgentFormat.Tokens(s.Total.CacheRead)}) · " +
                      $"OUT {NothingShell.Core.Agents.AgentFormat.Tokens(s.Total.Output)} · sessione {NothingShell.Core.Agents.AgentFormat.Cost(s.Cost)} · oggi {NothingShell.Core.Agents.AgentFormat.Cost(cost)} · " +
                      $"turni finiti {turns} · turno attuale OUT {NothingShell.Core.Agents.AgentFormat.Tokens(s.TurnUsage.Output)} {NothingShell.Core.Agents.AgentFormat.Cost(s.TurnCost)}");
        }
        lines.Add($"Oggi in tutto: {NothingShell.Core.Agents.AgentFormat.Cost(total)}");
        File.WriteAllLines(report, lines);
    }

    private static void RenderNotchSnapshots(string folder)
    {
        using var media = new MediaService(); // non avviato: nessuna sessione, Home mostra l'ora
        var notch = new NotchWindow(media, DevPageActions.None, ConnectedPageActions.None);
        var now = DateTimeOffset.Now;
        MailItem[] demo =
        [
            new("Mario Rossi", "Riunione di domani alle 10: ordine del giorno aggiornato", "Casella collegata", now)
            {
                Imap = new ImapMessageRef(1, 3), IsUnread = true, SenderAddress = "mario.rossi@example.it",
            },
            new("Studio Bianchi", "Preventivo convalida", "Casella collegata", now.AddMinutes(-12)) { Imap = new ImapMessageRef(1, 2) },
            new("GitHub", "[nothing-shell] Nuova issue aperta", "Brave", now.AddHours(-1)),
            new("Amazon", "Il tuo ordine è stato spedito", "Casella collegata", now.AddHours(-2)) { IsUnread = true },
            new("Banca", "Codice di verifica 482913", "Casella collegata", now.AddHours(-3)),
            new("Laura Verdi", "Foto della gita", "Casella collegata", now.AddHours(-5)),
            new("Trenitalia", "Biglietto Roma - Milano", "Casella collegata", now.AddDays(-1)),
            new("Comune", "Avviso di pagamento TARI", "Casella collegata", now.AddDays(-1)),
            new("Newsletter", "Le novità di ottobre", "Casella collegata", now.AddDays(-2)),
            new("Marco Neri", "Re: preventivo", "Casella collegata", now.AddDays(-3)),
        ];
        notch.SetPhone(new PhonePageData(true,
            new NothingShell.Core.Phone.PhoneDevice("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4", "Nothing Phone (2)", true, true)
            {
                Battery = new NothingShell.Core.Phone.PhoneBattery(64, false, false),
                Signal = new NothingShell.Core.Phone.PhoneSignal("5G", 3),
                Shares = ["batteria", "segnale", "notifiche", "chiamate", "appunti", "file e link"],
            },
            "Collegato", new NothingShell.Core.Phone.IncomingFile("VID_20261029.mp4", 31_000_000, 52_000_000),
            [new NothingShell.Core.Phone.ReceivedFile(@"C:\Users\Download\scontrino.pdf", "Nothing Phone (2)", 182_000, now.AddMinutes(-4)),
             new NothingShell.Core.Phone.ReceivedFile(@"C:\Users\Download\IMG_2041.jpg", "Nothing Phone (2)", 3_400_000, now.AddHours(-2))],
            [new PhoneEvent(PhoneEventKind.Message, "Laura", "Arrivo tra 10 minuti", now.AddMinutes(-2)) { App = "WhatsApp" },
             new PhoneEvent(PhoneEventKind.MissedCall, "Mamma", string.Empty, now.AddMinutes(-30))]));
        var opus = new NothingShell.Core.Agents.TokenUsage(12_400, 3_100, 2_000, 8_000, 240_000);
        notch.SetAgents(
        [
            new("s1", "Claude Code", "nothing-shell", @"C:\dev\nothing-shell", "claude-opus-5-5", NothingShell.Core.Agents.AgentState.Working,
                opus, NothingShell.Core.Agents.AgentPricing.For("claude-opus-5-5")!.Cost(opus), new(400, 900, 0, 2_000, 180_000), 0.12, now.AddMinutes(-3), now),
            new("s2", "Claude Code", "microcosmo", @"C:\dev\microcosmo", "claude-sonnet-5-5", NothingShell.Core.Agents.AgentState.Done,
                new(3_000, 1_200, 0, 4_000, 60_000), 0.07, new(300, 400, 0, 1_000, 30_000), 0.02, now.AddMinutes(-20), now.AddMinutes(-18)),
        ], 1.84);
        var casa = ConnectorParser.Parse("casa", """{"name":"Casa","type":"homeassistant","url":"http://h:8123","readings":[{"entity":"sensor.t"}],"actions":[{"label":"Luci off","service":"light.turn_off"},{"label":"Cancello","service":"cover.open_cover","confirm":true}]}""").Manifest!;
        var router = ConnectorParser.Parse("router", """{"name":"Router","type":"ping","host":"192.168.1.1"}""").Manifest!;
        notch.SetConnectors(
        [
            new ConnectorState(casa, ConnectorStatus.Ok,
                [new("Soggiorno", "21.5", "°C", ReadingLevel.Normal), new("Umidità", "48", "%", ReadingLevel.Normal), new("Porta", "APERTO", null, ReadingLevel.Warning)],
                null, now.AddSeconds(-20)) { Granted = true },
            new ConnectorState(router, ConnectorStatus.Ok, [new("Stato", "ONLINE", null, ReadingLevel.Normal), new("Latenza", "3", "ms", ReadingLevel.Normal)], null, now),
        ]);
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
        notch.SetPhone(new PhonePageData(true,
            new NothingShell.Core.Phone.PhoneDevice("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4", "Nothing Phone (2)", true, true)
            {
                Battery = new NothingShell.Core.Phone.PhoneBattery(18, false, true),
                Signal = new NothingShell.Core.Phone.PhoneSignal("4G", 2),
                Shares = ["batteria", "segnale", "notifiche", "chiamate"],
            },
            "Collegato", null, [], []));
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Expanded, NotchPage.Phone), Path.Combine(folder, "notch-phone-only.png"));

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
        notch.ShowCard(NotchCards.Call(new PhoneEvent(PhoneEventKind.IncomingCall, "Giulia Bianchi", "Chiamata in arrivo", DateTimeOffset.Now), "RISPONDI", () => { }, () => { }), TimeSpan.FromSeconds(20));
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Card, NotchPage.Home), Path.Combine(folder, "notch-call.png"));
        notch.ShowCard(NotchCards.PairRequest(new NothingShell.Core.Phone.PhoneDevice("a", "Pixel 8", true, false) { VerificationKey = "3FA9C21B" }, () => { }, () => { }, () => { }), TimeSpan.FromSeconds(30));
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Card, NotchPage.Home), Path.Combine(folder, "notch-pair.png"));

        // Agenda (docs/11-agenda-pannello-rapido.md)
        notch.ShowCard(NotchCards.Appointment(agenda[1], ReminderKind.Soon, DateTimeOffset.Now, _ => { }, () => { }, () => { }), TimeSpan.FromSeconds(8));
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Card, NotchPage.Home), Path.Combine(folder, "notch-appointment.png"));
        var brief = MorningBrief.Compose(today.AddHours(8), agenda, 5);
        notch.ShowCard(NotchCards.MorningBrief(brief, today.AddHours(8), () => { }, () => { }, () => { }), TimeSpan.FromSeconds(8));
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Card, NotchPage.Home), Path.Combine(folder, "notch-morning.png"));
        notch.SetAgenda([], DateTimeOffset.Now, "Collega un calendario nelle impostazioni di Nothing Shell");
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Expanded, NotchPage.Agenda), Path.Combine(folder, "notch-agenda-empty.png"));

        var quick = new QuickPanelWindow(() => { }, () => { }, new SnapshotPhone(), () => true);
        SaveSnapshot(quick.PrepareSnapshot(), Path.Combine(folder, "quick-panel.png"));
        quick.Close();

        RenderDevSnapshots(notch, folder);

        notch.StartTimer(TimeSpan.FromMinutes(25));
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Expanded, NotchPage.Timer), Path.Combine(folder, "notch-timer-running.png"));
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Activity, NotchPage.Home), Path.Combine(folder, "notch-activity-timer.png"));
    }

    /// <summary>Lavoro, Server e Appunti con dati di esempio (docs/12-sviluppo.md).</summary>
    private static void RenderDevSnapshots(NotchWindow notch, string folder)
    {
        var now = DateTimeOffset.Now;
        var failed = new NothingShell.Core.Dev.FinishedTask("dotnet build", @"C:\dev\app", null, 1, TimeSpan.FromSeconds(83), now.AddMinutes(-2), @"C:\temp\task.log", false)
        {
            Error = new NothingShell.Core.Dev.CodeLocation(@"C:\dev\app\src\Program.cs", 57, 9, "CS1002: ; previsto"),
        };
        var ok = new NothingShell.Core.Dev.FinishedTask("npm test", @"C:\dev\web", null, 0, TimeSpan.FromSeconds(41), now.AddMinutes(-15), null, true);
        notch.SetTasks([new NothingShell.Core.Dev.RunningTask("1", "cargo build --release", @"C:\dev\cli", null, now.AddSeconds(-95))], [failed, ok]);
        notch.SetCi(new NothingShell.Core.Dev.CiStatus(@"C:\dev\nothing-shell", "master",
            [new NothingShell.Core.Dev.CiRun(1, "build", "in_progress", null, "u", now), new NothingShell.Core.Dev.CiRun(2, "test", "queued", null, "u", now)]), null);
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Activity, NotchPage.Home), Path.Combine(folder, "notch-activity-task.png"));
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Expanded, NotchPage.Work), Path.Combine(folder, "notch-work.png"));
        notch.ShowCard(NotchCards.TaskFinished(failed, () => { }, () => { }, () => { }, () => { }), TimeSpan.FromSeconds(8));
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Card, NotchPage.Home), Path.Combine(folder, "notch-task-failed.png"));
        notch.ShowCard(NotchCards.TaskFinished(ok, null, null, () => { }, () => { }), TimeSpan.FromSeconds(8));
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Card, NotchPage.Home), Path.Combine(folder, "notch-task-ok.png"));
        var ci = new NothingShell.Core.Dev.CiStatus(@"C:\dev\nothing-shell", "master",
            [new NothingShell.Core.Dev.CiRun(1, "build", "completed", "success", "u", now), new NothingShell.Core.Dev.CiRun(2, "test", "completed", "failure", "u", now)]);
        notch.ShowCard(NotchCards.CiFinished(ci, () => { }, () => { }, () => { }), TimeSpan.FromSeconds(8));
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Card, NotchPage.Home), Path.Combine(folder, "notch-ci-failed.png"));
        notch.SetTasks([], []);
        notch.SetCi(null, null);

        notch.SetServers(
        [
            new NothingShell.Core.Dev.DevServer(3000, 18244, "node", null, false),
            new NothingShell.Core.Dev.DevServer(5173, 18990, "node", null, false),
            new NothingShell.Core.Dev.DevServer(5432, 4120, "postgres", "Postgres", true),
        ]);
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Expanded, NotchPage.Servers), Path.Combine(folder, "notch-servers.png"));
        notch.ShowHint(NothingShell.Core.Design.DotIcons.Plug, Rendering.Theme.White, "LOCALHOST:5173 - NODE", NotchPage.Servers);
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Alert, NotchPage.Home), Path.Combine(folder, "notch-server-hint.png"));

        var history = new NothingShell.Core.Dev.ClipboardHistory();
        history.Add("#FF3B30", NothingShell.Core.Dev.ClipKind.Color, now);
        history.Add("ghp_abcdefghijklmnopqrstuvwxyz0123456789", NothingShell.Core.Dev.ClipKind.Secret, now);
        history.Add("{\"id\": 42, \"nome\": \"Nothing\", \"attivo\": true}", NothingShell.Core.Dev.ClipKind.Json, now);
        notch.SetClipboard(history.Entries);
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Expanded, NotchPage.Clipboard), Path.Combine(folder, "notch-clipboard.png"));
        notch.ShowHint(NothingShell.Core.Design.DotIcons.Clip, Rendering.Theme.White, "JSON - 2,1 KB", NotchPage.Clipboard);
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Alert, NotchPage.Home), Path.Combine(folder, "notch-clipboard-hint.png"));
        notch.ShowCard(NotchCards.Secret("CHIAVE GITHUB", "ghp_••••6789", () => { }, () => { }, null, () => { }), TimeSpan.FromSeconds(8));
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Card, NotchPage.Home), Path.Combine(folder, "notch-secret.png"));
        notch.ShowCard(NotchCards.Secret("TOKEN JWT", "scade tra 2 h", () => { }, () => { }, () => { }, () => { }), TimeSpan.FromSeconds(8));
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Card, NotchPage.Home), Path.Combine(folder, "notch-jwt.png"));
        notch.SetServers([]);
        notch.SetClipboard([]);
    }

    /// <summary>
    /// Diagnostica di docs/12-sviluppo.md, in sola lettura: server trovati ed esclusi con il motivo, editor, gh,
    /// tipo del testo negli appunti (senza il contenuto).
    /// </summary>
    private static async Task DevCheck(string file)
    {
        var lines = new List<string>();
        foreach (var (listener, owner) in PortWatcher.ReadAll().DistinctBy(x => (x.Listener.Port, x.Listener.Pid)).OrderBy(x => x.Listener.Port))
        {
            var reason = NothingShell.Core.Dev.DevServers.Exclusion(listener, owner);
            lines.Add($"porta {listener.Port} pid {listener.Pid} {owner?.Name ?? "?"}{(owner?.IsService == true ? " (servizio)" : string.Empty)}: {reason ?? "MOSTRATA"}");
        }
        lines.Add($"editor: {DevTools.EditorTemplate(new NothingShell.Core.Settings.ShellSettingsStore(NothingShell.Core.Settings.ShellSettingsStore.DefaultPath).Load().Editor) ?? "nessuno (app associata)"}");
        var gh = await DevTools.RunAsync("gh", "auth status", Environment.CurrentDirectory, TimeSpan.FromSeconds(15));
        lines.Add($"gh: {(gh is null ? "non trovato" : gh.Value.ExitCode == 0 ? "autenticato" : "non autenticato")}");
        try
        {
            var text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            lines.Add($"appunti: {(text is null ? "nessun testo" : NothingShell.Core.Dev.ClipClassifier.Classify(text, p => File.Exists(p) || Directory.Exists(p)).ToString())}");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            lines.Add("appunti: occupati");
        }
        await File.WriteAllLinesAsync(file, lines);
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
        root.Height = 2200; // tutta la pagina, senza scorrimento
        foreach (var page in Enum.GetValues<SettingsPage>())
        {
            settings.Show(page);
            SaveSnapshot(root, Path.Combine(folder, $"settings-{page.ToString().ToLowerInvariant()}.png"));
        }
        settings.ShowConnectorTemplate("homeassistant");
        root.Height = settings.Height;
        SaveSnapshot(root, Path.Combine(folder, "settings-connector-form.png"));
        var shop = new SnapshotSettings(plugins.Results).ConnectorResults.Select(r => r.Manifest).OfType<ConnectorManifest>().First(m => m.Id == "negozio");
        root.Height = 2200;
        settings.EditConnectorCosts(shop);
        SaveSnapshot(root, Path.Combine(folder, "settings-connector-costs.png"));
        settings.EditConnector(shop, duplicate: true);
        root.Height = settings.Height;
        SaveSnapshot(root, Path.Combine(folder, "settings-connector-duplicate.png"));
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

    /// <summary>
    /// Forma delle notifiche presenti, per capire come le scrive Collegamento al telefono: il testo è oscurato
    /// (lettere → a/A, cifre → 9) tranne poche parole chiave, così il rapporto si può condividere.
    /// </summary>
    private static async Task PhoneCheck(string file)
    {
        string[] keep = ["chiamata", "call", "in", "arrivo", "incoming", "persa", "missed", "messaggio", "messaggi",
            "message", "messages", "sms", "whatsapp", "telegram", "instagram", "gmail", "telefono", "phone", "risposta",
            "rispondi", "rifiuta", "answer", "decline", "batteria", "battery", "da", "from", "e", "nuovo", "nuovi", "new"];
        string Mask(string text) => string.Join(' ', text.Split(' ').Select(word =>
            keep.Contains(word.Trim(':', ',', '.', '·', '•').ToLowerInvariant()) ? word
                : new string(word.Select(c => char.IsUpper(c) ? 'A' : char.IsLetter(c) ? 'a' : char.IsDigit(c) ? '9' : c).ToArray())));

        var lines = new List<string>();
        var listener = global::Windows.UI.Notifications.Management.UserNotificationListener.Current;
        lines.Add($"accesso: {listener.GetAccessStatus()}");
        var toasts = await listener.GetNotificationsAsync(global::Windows.UI.Notifications.NotificationKinds.Toast);
        foreach (var n in toasts.OrderBy(n => n.CreationTime))
        {
            string id, name;
            try
            {
                id = n.AppInfo.AppUserModelId;
                name = n.AppInfo.DisplayInfo.DisplayName;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
            {
                id = name = "?";
            }
            lines.Add($"{n.CreationTime:HH:mm:ss} {name} [{id}]");
            if (!PhoneLink.IsPhoneLink(id))
                continue;
            foreach (var binding in n.Notification.Visual?.Bindings ?? [])
            {
                lines.Add($"  binding {binding.Template} lingua={binding.Language}");
                var texts = binding.GetTextElements();
                for (var i = 0; i < texts.Count; i++)
                    lines.Add($"    testo {i}: {Mask(texts[i].Text ?? string.Empty).Replace('\n', '|')}");
                lines.Add($"    → {PhoneLink.Parse(texts.Select(t => t.Text ?? string.Empty).ToArray(), n.CreationTime)?.Kind.ToString() ?? "ignorata"}");
                foreach (var hint in binding.Hints)
                    lines.Add($"    hint {hint.Key}={hint.Value}");
            }
        }
        lines.AddRange(KdeConnectService.Diagnose());
        lines.Add(await KdeConnectService.SelfTestAsync());
        await File.WriteAllLinesAsync(file, lines);
    }

    /// <summary>Telefono collegato d'esempio per disegnare pannello rapido e impostazioni.</summary>
    private sealed class SnapshotPhone : IPhoneActions
    {
        public string Status => "Collegato a Pixel 8";

        public IReadOnlyList<NothingShell.Core.Phone.PhoneDevice> Devices { get; } =
        [
            new("0123456789abcdef0123456789abcdef", "Pixel 8", Connected: true, Paired: true) { Battery = new(76, false, false) },
            new("fedcba9876543210fedcba9876543210", "Galaxy Tab", Connected: true, Paired: false),
        ];

        public NothingShell.Core.Phone.PhoneDevice? Current => Devices[0];

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public void Discover(System.Net.IPAddress? address = null)
        {
        }

        public void RequestPair(string deviceId)
        {
        }

        public void AcceptPair(string deviceId)
        {
        }

        public void RejectPair(string deviceId)
        {
        }

        public void Unpair(string deviceId)
        {
        }

        public void FindPhone()
        {
        }

        public void SendClipboard(string text)
        {
        }

        public Task<string?> SendFileAsync(string path) => Task.FromResult<string?>(null);

        public void AllowFirewall()
        {
        }
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
        public bool PhoneDirect { get; set; } = true;

        public IPhoneActions Phone { get; } = new SnapshotPhone();

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

        public IReadOnlyList<ConnectorLoadResult> ConnectorResults { get; } =
        [
            ConnectorParser.Parse("casa", """{"name":"Casa","template":"homeassistant","type":"homeassistant","url":"http://homeassistant.local:8123","secret":true,"readings":[{"entity":"sensor.soggiorno"}],"actions":[{"label":"Luci off","service":"light.turn_off","data":{"entity_id":"all"}}]}"""),
            ConnectorParser.Parse("raspberry", """{"name":"Raspberry","type":"command","command":"ssh","args":["pi@raspberry","vcgencmd measure_temp"]}"""),
            ConnectorParser.Parse("router", """{"name":"Router","type":"ping","host":"192.168.1.1"}"""),
            ConnectorParser.Parse("negozio", """{"name":"Negozio online","template":"sito-llm","type":"http","url":"https://negozio.example/llm-usage","values":{"url":"https://negozio.example/llm-usage","mode":"chiamate"},"llm":{"mode":"calls","list":"calls","model":"{model}","input":"{input_tokens}","output":"{output_tokens}","id":"{id}","dailyBudget":2,"monthlyBudget":40}}"""),
            ConnectorParser.Parse("blog", """{"name":"Blog","template":"anthropic-costi","type":"http","url":"https://api.anthropic.com/v1/organizations/usage_report/messages?starting_at={today}","secret":true,"llm":{"list":"data[0].results","model":"{model}","input":"{uncached_input_tokens}","output":"{output_tokens}","countFirst":true,"dailyBudget":1}}"""),
            new ConnectorLoadResult("vecchio", null, "manca \"name\""),
        ];

        public double? UsdToEur => 0.9214;

        public LlmSummary ConnectorCosts(ConnectorManifest manifest)
        {
            var today = new LlmDay { Calls = manifest.Id == "negozio" ? 148 : 31, Input = 412_000, Output = 96_000, Usd = manifest.Id == "negozio" ? 1.42 : 1.31 };
            var month = new LlmDay { Calls = 3_120, Input = 9_800_000, Output = 2_100_000, Usd = manifest.Id == "negozio" ? 27.6 : 9.4 };
            return new LlmSummary(today, month, today.InEuro(UsdToEur), month.InEuro(UsdToEur));
        }

        public IReadOnlyList<LlmCall> ConnectorCalls(ConnectorManifest manifest) =>
        [
            new(DateTimeOffset.Now.AddMinutes(-3), "claude-sonnet-5-5", 2_400, 610, 1, 0.011, 0, true),
            new(DateTimeOffset.Now.AddMinutes(-9), "claude-haiku-5-5", 1_100, 220, 1, 0.0002, 0, true),
            new(DateTimeOffset.Now.AddMinutes(-15), "gpt-x", 900, 300, 1, 0, 0, false),
        ];

        public string? SetConnectorLlm(ConnectorManifest manifest, LlmSpec? llm) => null;

        public void ResetConnectorCosts(ConnectorManifest manifest)
        {
        }

        public void ExportConnectorCosts()
        {
        }

        public string ConnectorFolder => ConnectorHost.Folder;

        public bool IsConnectorGranted(ConnectorManifest manifest) => manifest.Id == "casa";

        public void SetConnectorGranted(ConnectorManifest manifest, bool granted)
        {
        }

        public bool HasConnectorSecret(ConnectorManifest manifest) => true;

        public void SetConnectorSecret(ConnectorManifest manifest, string secret)
        {
        }

        public string? AddConnector(ConnectorTemplate connector, string? name, IReadOnlyDictionary<string, string> values, bool grant, ConnectorManifest? replace = null) => null;

        public void RemoveConnector(ConnectorManifest manifest)
        {
        }

        public void ReloadConnectors()
        {
        }

        public void CopyConnectorExamples()
        {
        }

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

        // Widget dei connettori nelle tre taglie
        var at = DateTimeOffset.Now.AddMinutes(-2);
        (string Json, ConnectorReading[] Readings)[] connectors =
        [
            ("""{"name":"Router","type":"ping","host":"192.168.1.1"}""", [new("Latenza", "3", "ms", ReadingLevel.Normal)]),
            ("""{"name":"Casa","type":"homeassistant","url":"http://h:8123","readings":[{"entity":"sensor.a"},{"entity":"sensor.b"},{"entity":"sensor.c"}]}""",
                [new("Soggiorno", "21.5", "°C", ReadingLevel.Normal), new("Umidità", "48", "%", ReadingLevel.Normal), new("Porta", "APERTO", null, ReadingLevel.Warning)]),
            ("""{"name":"Server","type":"prometheus","url":"http://p:9090","readings":[{"query":"a"},{"query":"b"},{"query":"c"},{"query":"d"},{"query":"e"},{"query":"f"}]}""",
                [new("CPU", "34", "%", ReadingLevel.Normal), new("RAM", "71", "%", ReadingLevel.Normal), new("Disco", "92", "%", ReadingLevel.Warning),
                 new("Carico", "1.42", null, ReadingLevel.Normal), new("Uptime", "12", "giorni", ReadingLevel.Normal), new("Rete", "Interfaccia eth0 attiva", null, ReadingLevel.Normal)]),
        ];
        foreach (var (json, readings) in connectors)
        {
            var manifest = ConnectorParser.Parse("demo", json).Manifest!;
            var widget = new ConnectorWidget(manifest);
            widget.Update(new ConnectorState(manifest, ConnectorStatus.Ok, readings, null, at));
            SaveSnapshot(widget, Path.Combine(folder, $"connector-{readings.Length}.png"));
        }
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
