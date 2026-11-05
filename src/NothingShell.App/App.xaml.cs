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
using NothingShell.Core.Design;
using NothingShell.Core.Brain;
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
            case ["--snapshot", var folder, .. var look]:
                Rendering.Motion.Suspended = true; // le immagini mostrano lo stato finale, non l'inizio delle animazioni
                Rendering.Theme.Apply(SnapshotAppearance(look));
                NotchLayout.Use(SnapshotAppearance(look).NotchSize);
                RenderSnapshots(folder);
                RenderIconSheet(folder);
                await RenderLauncherSnapshot(folder);
                await RenderBarSnapshots(folder);
                await RenderPluginAndSettingsSnapshots(folder);
                await RenderNotchSnapshots(folder);
                RenderRemoteSnapshots(folder);
                await RenderAccessSnapshots(folder);
                Shutdown();
                return;
            case ["--mascot-sheet", var folder]:
                Rendering.Motion.Suspended = true;
                Rendering.Theme.Apply(SnapshotAppearance(["scuro", "mascotte"]));
                RenderMascotSheet(folder);
                Shutdown();
                return;
            case ["--outfit-sheet", var folder]:
                Rendering.Motion.Suspended = true;
                RenderOutfitSheet(folder);
                Shutdown();
                return;
            case ["--glass-check", var folder, .. var mode]:
                await GlassCheck(folder, mode.Contains("solido"));
                Shutdown();
                return;
            case ["--ask-demo", var report]:
                await AskDemo(report);
                Shutdown();
                return;
            case ["--remote-demo", var address, var image]:
                await RemoteDemo(address, image);
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
            case ["--db-check", .. var rest]:
                Environment.ExitCode = await DbProbe.RunAsync(rest);
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
            case ["--phone-files-check", var report, var seconds]:
                await PhoneFilesCheck(report, double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture));
                Shutdown();
                return;
            case ["--brain", var model]:
                await BrainWorker.RunAsync(model);
                Shutdown();
                return;
            case ["--assistant", var model]:
                await AssistantWorker.RunAsync(model);
                Shutdown();
                return;
            case ["--brain-check", var report]:
                await BrainCheck(report);
                Shutdown();
                return;
            case ["--brain-serve", var memory, var pipe, var seconds]:
                // Per le prove: solo il cervello, senza barra né notch, con scorciatoie e pipe proprie (NOTHINGSHELL_BRAIN_PIPE).
                using (var brain = new BrainService(memory, pipe))
                {
                    brain.Start();
                    await Task.Delay(TimeSpan.FromSeconds(double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)));
                }
                Shutdown();
                return;
            case ["--assistant-check", var report, .. var questions]:
                await AssistantCheck(report, questions);
                Shutdown();
                return;
            case ["--brain-install"]:
                using (var brain = new BrainService())
                    Console.WriteLine(await brain.InstallModelAsync() ?? "installato");
                Shutdown();
                return;
            case ["--assistant-install"]:
                using (var brain = new BrainService())
                    Console.WriteLine(await brain.InstallModelAsync(BrainModel.Assistant) ?? "installato");
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
        if (!createdNew && !(e.Args is ["--wake", ..] && WaitForPreviousShell()))
        {
            Shutdown();
            return;
        }
        ListenForExitSignal();
        ListenForLockSignal();
        RestoreTaskbarOnCrash();

        _shell = new ShellController();
        _shell.ToggleRequested += () => Relaunch("--dormant");
        _shell.RestartRequested += () => Dispatcher.BeginInvoke(() => Relaunch("--wake --settings"));
        if (e.Args is ["--settings"] or ["--wake", "--settings"])
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

    /// <summary>Accoda l'errore a ~/.nothing-shell/crash.log; il log non deve mai causare un altro errore.</summary>
    private static void LogError(Exception ex)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nothing-shell");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "crash.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    /// <summary>Qualunque crash deve lasciare la taskbar di Windows visibile.</summary>
    private void RestoreTaskbarOnCrash()
    {
        // Un errore in un handler UI non deve chiudere la shell: lo registro e vado avanti.
        DispatcherUnhandledException += (_, e) =>
        {
            LogError(e.Exception);
            e.Handled = true;
        };
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

    /// <summary>Progetti, gruppi e qualche ora di prova in una cartella temporanea, per le istantanee.</summary>
    private static NothingShell.Core.Work.WorkStore DemoHours()
    {
        var hours = new NothingShell.Core.Work.WorkStore(Path.Combine(Path.GetTempPath(), "ns-hours-snapshot-" + Guid.NewGuid().ToString("N")));
        foreach (var line in new[] { "progetto sinapsi_web = sinapsi_web, sinapsi", "progetto printer = printer", "progetto elfin = elfin, as400", "gruppo Cliente Elfin = elfin, printer" })
        {
            var answer = NothingShell.Core.Work.WorkCommands.Run(line, hours, DateTime.Now);
            if (answer.Config is { } config)
                hours.SetConfig(config);
        }
        var at = DateTime.Now.AddHours(-3);
        foreach (var (title, minutes) in new[] { ("sinapsi_web - Code", 95), ("printer - Code", 40), ("elfin - Code", 55) })
        {
            hours.Sample(at, title, TimeSpan.Zero);
            for (var i = 0; i < minutes * 12; i++)
                hours.Sample(at = at.AddSeconds(5), title, TimeSpan.Zero);
        }
        foreach (var line in new[] { "aggiungi 6h a printer e 2h a elfin ieri", "aggiungi 3h a elfin avantieri", "aggiungi 5h a sinapsi_web e 3h al gruppo Cliente Elfin lunedì", "aggiungi 1h a printer 26/10" })
            NothingShell.Core.Work.WorkCommands.Run(line, hours, DateTime.Now);
        return hours;
    }

    private static async Task RenderNotchSnapshots(string folder)
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
        await notch.RunDiagnosiForSnapshot("sinapsi.it");
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Expanded, NotchPage.Diagnosi), Path.Combine(folder, "notch-dns-risultati.png"));
        await notch.RunDiagnosiForSnapshot("http https://www.sinapsi.it");
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Expanded, NotchPage.Diagnosi), Path.Combine(folder, "notch-diagnosi-http.png"));
        await notch.RunDiagnosiForSnapshot("propaga sinapsi.it");
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Expanded, NotchPage.Diagnosi), Path.Combine(folder, "notch-diagnosi-propaga.png"));
        var hours = DemoHours();
        notch.ShowHoursForSnapshot(hours, null);
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Expanded, NotchPage.Hours), Path.Combine(folder, "notch-ore.png"));
        notch.ShowHoursForSnapshot(hours, "ore gruppo Cliente Elfin");
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Expanded, NotchPage.Hours), Path.Combine(folder, "notch-ore-gruppo.png"));
        notch.ShowHoursForSnapshot(hours, "aggiungi 7h a printer e 1h al gruppo Cliente Elfin che ho fatto ieri e aggiungi 4h su printer perché ho fatto questa mattina.");
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Expanded, NotchPage.Hours), Path.Combine(folder, "notch-ore-frase.png"));
        var groupWindow = new HoursDetailWindow(hours, "Cliente Elfin");
        SaveSnapshot(groupWindow.PrepareSnapshot(), Path.Combine(folder, "hours-group-window.png"));
        notch.SetWorkStore(null);
        var dbTemplate = NothingShell.Core.Connectors.ConnectorCatalog.Find("database")!;
        var dbJson = NothingShell.Core.Connectors.ConnectorCatalog.Create(dbTemplate, "Gestionale", new Dictionary<string, string> { ["host"] = "sql.azienda.local", ["database"] = "gestionale", ["username"] = "monitor" }, []);
        var db = NothingShell.Core.Connectors.ConnectorParser.Parse(dbJson.Id!, dbJson.Json!).Manifest!;
        notch.SetConnectors([new NothingShell.Core.Connectors.ConnectorState(db, NothingShell.Core.Connectors.ConnectorStatus.Ok,
            [new("Stato", "ONLINE", null, NothingShell.Core.Connectors.ReadingLevel.Normal), new("Connessione", "42", "ms", NothingShell.Core.Connectors.ReadingLevel.Normal),
             new("Query", "8", "ms", NothingShell.Core.Connectors.ReadingLevel.Normal), new("Versione", "16.00.1000", null, NothingShell.Core.Connectors.ReadingLevel.Normal),
             new("Valore", "1250", null, NothingShell.Core.Connectors.ReadingLevel.Warning)], null, DateTimeOffset.Now) { Granted = true }]);
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Expanded, NotchPage.Database), Path.Combine(folder, "notch-database-connettore.png"));
        notch.SetConnectors([]);
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
             new PhoneEvent(PhoneEventKind.MissedCall, "Mamma", string.Empty, now.AddMinutes(-30))])
        {
            Arriving = [new ActiveDownload(@"C:\D\Mobile Devices\Ponyo sulla scogliera.mp4", "Ponyo sulla scogliera.mp4", 214_000_000, 1_200_000, TimeSpan.Zero) { FromPhone = true }],
        });
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
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Idle, NotchPage.Home), Path.Combine(folder, "notch-idle.png"));
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

        RenderAssistantSnapshots(folder);

        var reader = new MailReaderWindow(new SnapshotMail(demo));
        reader.Select(demo[0]);
        var readerRoot = (FrameworkElement)reader.Content;
        readerRoot.Width = reader.Width;
        readerRoot.Height = reader.Height;
        SaveSnapshot(readerRoot, Path.Combine(folder, "mail-reader.png"));
        reader.Width = readerRoot.Width = 620; // stretta: un riquadro alla volta
        reader.Select(demo[1]);
        SaveSnapshot(readerRoot, Path.Combine(folder, "mail-reader-compact.png"));

        // Attività dal vivo (docs/10-live-activities.md)
        notch.ShowLevel(NothingShell.Core.Design.DotIcons.Speaker, 0.64, muted: false);
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Alert, NotchPage.Home), Path.Combine(folder, "notch-volume.png"));
        notch.ShowLevel(NothingShell.Core.Design.DotIcons.SpeakerMuted, 0.64, muted: true);
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Alert, NotchPage.Home), Path.Combine(folder, "notch-volume-muted.png"));
        notch.ShowLevel(NothingShell.Core.Design.DotIcons.Sun, 0.8, muted: false);
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Alert, NotchPage.Home), Path.Combine(folder, "notch-brightness.png"));
        notch.ShowNotice(NothingShell.Core.Design.DotIcons.Headphones, Rendering.Theme.Ink, "Nothing Ear (a)  80%");
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
        notch.ShowHint(NothingShell.Core.Design.DotIcons.Plug, Rendering.Theme.Ink, "LOCALHOST:5173 - NODE", NotchPage.Servers);
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Alert, NotchPage.Home), Path.Combine(folder, "notch-server-hint.png"));

        var history = new NothingShell.Core.Dev.ClipboardHistory();
        history.Add("#FF3B30", NothingShell.Core.Dev.ClipKind.Color, now);
        history.Add("ghp_abcdefghijklmnopqrstuvwxyz0123456789", NothingShell.Core.Dev.ClipKind.Secret, now);
        history.Add("{\"id\": 42, \"nome\": \"Nothing\", \"attivo\": true}", NothingShell.Core.Dev.ClipKind.Json, now);
        notch.SetClipboard(history.Entries);
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Expanded, NotchPage.Clipboard), Path.Combine(folder, "notch-clipboard.png"));
        notch.ShowHint(NothingShell.Core.Design.DotIcons.Clip, Rendering.Theme.Ink, "JSON - 2,1 KB", NotchPage.Clipboard);
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

    /// <summary>Cosa vede il controllo dei download per il tempo indicato, una riga ogni 10 s (docs/10-live-activities.md).</summary>
    private static async Task PhoneFilesCheck(string report, double seconds)
    {
        void Log(string line) => File.AppendAllText(report, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
        using var downloads = new DownloadWatcher();
        downloads.PhoneFileStarted += path => Log($"inizio: {path}");
        downloads.Completed += path => Log($"arrivato: {path} {new FileInfo(path).Length} byte");
        downloads.Start();
        var end = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < end)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            foreach (var d in downloads.Active)
                Log($"in corso: {d.Name} {d.Bytes} byte, {d.BytesPerSecond:0} byte/s, telefono={d.FromPhone}");
        }
    }

    /// <summary>
    /// Diagnostica di docs/15-comandi-cervello.md: tempi delle scorciatoie, ogni scorciatoia di serie provata in PowerShell
    /// (sono tutte di sola lettura) e, se il modello è scaricato, tempi e risposte del modello. Non impara nulla.
    /// </summary>
    private static async Task BrainCheck(string file)
    {
        var lines = new List<string>();
        var memory = new ShortcutMemory();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        const int Rounds = 10_000;
        for (var i = 0; i < Rounds; i++)
            memory.Decide("mi mostri quanta ram è libera?");
        lines.Add($"scorciatoia: {clock.Elapsed.TotalMilliseconds * 1000 / Rounds:0.0} µs a decisione");

        var runner = new AssistantRunner();
        foreach (var command in ShortcutSeeds.All.Select(s => s.Command).Distinct())
        {
            var run = await runner.RunReadOnlyAsync(command, CancellationToken.None);
            lines.Add(run.Status == AssistantRunStatus.Ok ? $"ok  {command}" : $"NO  {command} ({run.Status}: {run.Reason}{run.Error})");
        }

        using var brain = new BrainService();
        lines.Add(brain.Status);
        if (brain.ModelInstalled)
        {
            string[] phrases = ["elenca le variabili di ambiente", "chi sta usando la porta 3000", "crea una cartella chiamata prova", "che tempo fa a parigi", "cancella tutti i file temporanei"];
            foreach (var phrase in phrases)
            {
                var reply = await brain.DecideAsync(phrase, forced: true, Environment.CurrentDirectory, CancellationToken.None);
                lines.Add($"{reply.Ms,6:0} ms {reply.Risk,-9} | {phrase} => {reply.Command ?? reply.Note ?? "nessuna decisione"}");
            }
        }
        await File.WriteAllLinesAsync(file, lines);
    }

    /// <summary>
    /// L'assistente vero, col modello incorporato o Ollama e l'esecutore limitato, senza interfaccia. Non conferma mai nulla e
    /// non impara: ogni richiesta di conferma risponde no, così quello che non è sola lettura non parte.
    /// </summary>
    private static async Task AssistantCheck(string file, string[] questions)
    {
        var lines = new List<string>();
        var models = await OllamaAssistantModel.InstalledAsync(CancellationToken.None);
        IAssistantModel? model = File.Exists(BrainModel.Assistant.LocalPath) ? new EmbeddedAssistantModel(BrainModel.Assistant)
            : models.Count > 0 ? new OllamaAssistantModel(models[0]) : null;
        using var disposal = model as IDisposable;
        lines.Add(model is null ? "nessun modello trovato" : $"modello: {model.Name}");
        foreach (var question in questions)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var assistant = new CommandAssistant(new ShortcutMemory(null), new AssistantRunner(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) { Model = model };
            lines.Add(string.Empty);
            lines.Add($"### {question}");
            await assistant.AskAsync(question, new CheckView(lines), CancellationToken.None);
            lines.Add($"    {clock.Elapsed.TotalSeconds:0.0} s");
        }
        await File.WriteAllLinesAsync(file, lines);
    }

    private sealed class CheckView(List<string> lines) : IAssistantView
    {
        public void Thinking()
        {
        }

        public void Running(string command)
        {
        }

        public void Finished(AssistantStep result) =>
            lines.Add($"  [{result.Run.Status}] {result.Command}{(result.Run.Reason is { Length: > 0 } reason ? $"  ({reason})" : string.Empty)}\n{result.Run.Output}{result.Run.Error}".TrimEnd());

        public Task<bool> ConfirmAsync(AssistantConfirm request, CancellationToken token)
        {
            lines.Add($"  CONFERMA {request.Level}: {request.Command}  ({request.Reason})  -> no");
            return Task.FromResult(false);
        }

        public void Answer(string text, bool verified) => lines.Add($"  RISPOSTA{(verified ? string.Empty : " NON VERIFICATA")}: {text}");

        public void Choices(IReadOnlyList<string> choices) => lines.Add($"  SCELTE: {string.Join(" | ", choices)}");

        public void Note(string text) => lines.Add($"  nota: {text}");

        public void OfferShortcut(string phrase, string command) => lines.Add($"  si può ricordare: {phrase} -> {command}");
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
        var learned = new SnapshotSettings(plugins.Results).LearnedShortcuts;
        foreach (var (shortcut, name) in new[] { (learned[0], "old"), (learned[1], "ok"), (learned[2], "template"), (learned[3], "builtin"), (learned[4], "user") })
        {
            settings.ShowShortcut(shortcut);
            SaveSnapshot(root, Path.Combine(folder, $"settings-shortcut-{name}.png"));
        }
        settings.ShowBuiltIn(new ShortcutMemory().BuiltIn[0].Commands[0]);
        SaveSnapshot(root, Path.Combine(folder, "settings-shortcut-seed.png"));
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

        public MailReaderLayout ReaderLayout { get; set; } = MailReaderLayout.Default;
    }

    /// <summary>Stato fittizio ma realistico per disegnare le impostazioni senza avviare la shell.</summary>
    private sealed class SnapshotSettings(IReadOnlyList<PluginLoadResult> results) : IShellSettingsActions
    {
        public bool PhoneDirect { get; set; } = true;

        public IPhoneActions Phone { get; } = new SnapshotPhone();

        public AppearanceSettings Appearance { get; set; } = AppearanceSettings.Default;

        public IReadOnlyList<NotchOutfit> Wardrobe { get; set; } =
            [OutfitCatalog.Copy(OutfitCatalog.BuiltIn[3]) with { Name = "Il mio mare", Pattern = OutfitPattern.Stars, Rim = "#BDF4FF" }];

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

        public string BrainStatus => "Modello Qwen2.5 0.5B pronto";

        public int BrainLearned => 12;

        public bool BrainModelInstalled => true;

        public bool BrainInstalling => false;

        public event Action? BrainChanged
        {
            add { }
            remove { }
        }

        public void InstallBrainModel()
        {
        }

        public void CancelBrainInstall()
        {
        }

        public void ClearBrainMemory()
        {
        }

        public IReadOnlyList<BuiltInSection> BuiltInShortcuts { get; } = new ShortcutMemory().BuiltIn;

        public IReadOnlyList<Shortcut> LearnedShortcuts { get; } =
        [
            new(["spazio", "libero", "pc"], "Get-PSDrive -PSProvider FileSystem | Select-Object Name, @{n='LiberoGB';e={$_.Free/1GB}}, @{n='UsatoGB';e={$_.Used/1GB}}", 2, 0, new DateTimeOffset(2026, 10, 31, 21, 32, 0, TimeSpan.FromHours(1))),
            new(["servizi", "attivi", "windows"], "Get-Service | Where-Object Status -eq Running", 4, 0, new DateTimeOffset(2026, 10, 31, 19, 52, 0, TimeSpan.FromHours(1)), Created: new DateTimeOffset(2026, 10, 20, 9, 15, 0, TimeSpan.FromHours(2))),
            new(["processo", ShortcutMemory.Slot], "Get-Process {0}", 2, 0, new DateTimeOffset(2026, 10, 29, 11, 0, 0, TimeSpan.FromHours(1)), Created: new DateTimeOffset(2026, 10, 29, 11, 0, 0, TimeSpan.FromHours(1))),
            new(["svuota", "cestino"], "Clear-RecycleBin -Force", 1, 0, new DateTimeOffset(2026, 10, 25, 18, 0, 0, TimeSpan.FromHours(2)), Created: new DateTimeOffset(2026, 10, 25, 18, 0, 0, TimeSpan.FromHours(2))),
            new(["spegni", "vm"], "vmctl stop lavoro", 1, 1, new DateTimeOffset(2026, 10, 24, 18, 0, 0, TimeSpan.FromHours(2)), Created: new DateTimeOffset(2026, 10, 22, 8, 30, 0, TimeSpan.FromHours(2))),
        ];

        public bool IsUserCritical(string command) => command.StartsWith("vmctl", StringComparison.Ordinal);

        public string? SetUserCritical(string command, bool critical) => null;

        public bool AssistantModelInstalled => true;

        public bool PowerfulAssistant { get; set; }

        public void InstallAssistantModel()
        {
        }

        public string AssistantStatus => "Risponde Qwen3.5 4B, incorporato: occupa circa 3 GB mentre lavora";

        public IReadOnlyList<string> AssistantModels => ["Qwen3.5 4B"];

        public Task NextAssistantModelAsync() => Task.CompletedTask;

        public void OpenAssistant()
        {
        }

        public void OpenCriticalCommands()
        {
        }

        public void OpenTerminal()
        {
        }

        public IReadOnlyList<NothingShell.Core.RemoteAccess.RemoteComputer> RemoteComputers { get; } =
            [new("Computer di Luca", "192.168.1.50", UserName: "luca"), new("Server dell'ufficio", "server.local", 3390, "admin")];

        public void AddRemoteComputer()
        {
        }

        public void EditRemoteComputer(NothingShell.Core.RemoteAccess.RemoteComputer computer)
        {
        }

        public void RemoveRemoteComputer(NothingShell.Core.RemoteAccess.RemoteComputer computer)
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

        public NothingShell.Core.Work.WorkStore? Work { get; } = DemoHours();

        public void Update()
        {
        }

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

    /// <summary>Pannello dell'assistente con una risposta, una richiesta di conferma critica e le scelte.</summary>
    private static void RenderAssistantSnapshots(string folder)
    {
        var panel = new NothingShell.App.Notch.AssistantWindow(new SnapshotAssistant());
        var pending = default(Task<bool>);
        panel.PrepareSnapshot(p =>
        {
            p.ShowQuestion("quanto spazio libero ho sul disco C e quanta ram?");
            p.Finished(new AssistantStep("Get-PSDrive -PSProvider FileSystem | Select-Object Name, @{n='LiberoGB';e={$_.Free/1GB}}",
                new AssistantRun(AssistantRunStatus.Ok, "Name LiberoGB\n---- --------\nC    110,09\nE    839,24"), ShowOutput: true));
            p.Finished(new AssistantStep("Get-CimInstance Win32_OperatingSystem | Select-Object FreePhysicalMemory",
                new AssistantRun(AssistantRunStatus.Ok, "dati"), ShowOutput: false));
            p.Answer("Sul disco C hai 110 GB liberi su 475 GB. La RAM totale è 15,7 GB e ne hai 2 GB liberi.", verified: true);
            p.OfferShortcut("spazio e ram", "Get-PSDrive");
            p.Choices(["Controlla le cartelle più grandi", "Mostra chi usa più memoria"]);
        });
        SaveAssistantSnapshot(panel, Path.Combine(folder, "assistant-answer.png"));
        panel.PrepareSnapshot(p =>
        {
            p.ShowQuestion("cancella i file temporanei");
            pending = p.ConfirmAsync(new AssistantConfirm(@"Get-ChildItem -Path $env:TEMP\* -Recurse | Remove-Item -Force -Recurse", ConfirmLevel.Critical,
                CommandSafety.Reason("Remove-Item x")!), CancellationToken.None);
        });
        SaveAssistantSnapshot(panel, Path.Combine(folder, "assistant-critical.png"));
        panel.PrepareSnapshot(p =>
        {
            p.ShowQuestion("crea una cartella prova");
            pending = p.ConfirmAsync(new AssistantConfirm("New-Item -ItemType Directory prova", ConfirmLevel.Change, "Può cambiare qualcosa: New-Item non è in elenco"), CancellationToken.None);
        });
        SaveAssistantSnapshot(panel, Path.Combine(folder, "assistant-change.png"));
        panel.PrepareSnapshot(p =>
        {
            p.ShowQuestion("sistemalo");
            p.Answer("Ci sono 0 file nella cartella.", verified: false);
        });
        SaveAssistantSnapshot(panel, Path.Combine(folder, "assistant-unverified.png"));
        _ = pending;
    }

    private static void SaveAssistantSnapshot(NothingShell.App.Notch.AssistantWindow panel, string path) =>
        SaveSnapshot((FrameworkElement)panel.Content, path);

    private sealed class SnapshotAssistant : NothingShell.App.Notch.IAssistantPanelActions
    {
        public CommandAssistant Assistant { get; } = new(new ShortcutMemory(null, []), new AssistantRunner(), string.Empty);

        public Task<string> PrepareAsync(CancellationToken token) => Task.FromResult("Qwen3.5 4B · su questo PC");

        public void OpenTerminal()
        {
        }

        public void OpenInTerminal(string? phrase, string command, string? criticalReason)
        {
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
        var seeds = new ShortcutMemory();
        launcher.Decide = (input, _) => Task.FromResult<BrainReply?>(seeds.Decide(input) is { } known ? BrainProtocol.Reply(known, 0.2) : null);
        launcher.PrepareSnapshot(query: "> quanta ram è libera");
        await Task.Yield();
        SaveSnapshot((FrameworkElement)launcher.Content, Path.Combine(folder, "launcher-command.png"));
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

    /// <summary>Il campo sotto la data e le tre facce della finestra dei collegamenti (docs/16-assistente-notch.md), in PNG.</summary>
    private static void RenderRemoteSnapshots(string folder)
    {
        var luca = new NothingShell.Core.RemoteAccess.RemoteComputer("Computer di Luca", "192.168.1.50", UserName: "luca");
        var marta = new NothingShell.Core.RemoteAccess.RemoteComputer("Computer di Marta", "192.168.1.51", UserName: "marta");
        var memory = new ShortcutMemory();
        NothingShell.Core.RemoteAccess.RequestRoute Route(string text) => NothingShell.Core.RemoteAccess.RequestRouter.Route(text, [luca, marta], memory);

        using var media = new MediaService();
        var notch = new NotchWindow(media, DevPageActions.None, ConnectedPageActions.None) { AskPreview = text => Route(text).Preview };
        notch.SetTime(DateTime.Now);
        var pill = notch.PrepareSnapshot(NotchState.Expanded, NotchPage.Home);
        SaveSnapshot(pill, Path.Combine(folder, "notch-ask-empty.png"));
        foreach (var (name, text) in new[] { ("sure", "collegati al computer di luca"), ("many", "collegati al computer"), ("new", "collegati al computer di marco") })
        {
            notch.SetAskForSnapshot(text);
            SaveSnapshot(pill, Path.Combine(folder, $"notch-ask-{name}.png"));
        }

        var actions = new DemoRemoteActions();
        var choice = new RemoteConnectWindow(actions);
        choice.ShowChoice(Route("collegati al compiuter"));
        SaveSnapshot(choice.PrepareSnapshot(), Path.Combine(folder, "remote-choice.png"));
        var different = new RemoteConnectWindow(actions);
        different.ShowChoice(Route("collegati al computer di marco"));
        SaveSnapshot(different.PrepareSnapshot(), Path.Combine(folder, "remote-different.png"));
        var form = new RemoteConnectWindow(actions);
        form.ShowForm(NothingShell.Core.RemoteAccess.RequestRouter.Route("collegati al computer di marco", [], memory));
        SaveSnapshot(form.PrepareSnapshot(), Path.Combine(folder, "remote-form.png"));
        var log = new RemoteConnectWindow(actions);
        log.ShowSampleLog(luca);
        SaveSnapshot(log.PrepareSnapshot(), Path.Combine(folder, "remote-log.png"));
        var password = new RemoteConnectWindow(actions);
        password.ShowSamplePassword(luca);
        SaveSnapshot(password.PrepareSnapshot(), Path.Combine(folder, "remote-password.png"));
    }

    /// <summary>
    /// Prova che il notch, che non prende mai il focus da solo, riceva la tastiera dopo il clic sul campo. I tasti partono
    /// solo se il notch è davvero in primo piano: non possono finire in un'altra finestra.
    /// </summary>
    private static async Task AskDemo(string report)
    {
        var lines = new List<string>();
        using var media = new MediaService();
        var notch = new NotchWindow(media, DevPageActions.None, ConnectedPageActions.None)
        {
            AskPreview = text =>
            {
                lines.Add($"anteprima: {text}");
                return text;
            },
        };
        var submitted = new TaskCompletionSource<string>();
        notch.AskSubmitted += (text, choose) => submitted.TrySetResult($"{text} (ctrl={choose})");
        notch.SetTime(DateTime.Now);
        var before = Interop.WindowNative.GetForegroundWindow();
        notch.Show();
        notch.OpenPage(NotchPage.Home);
        await Task.Delay(400);
        var focused = notch.BeginAskForDemo();
        lines.Add($"tastiera al campo: {focused}");
        if (focused)
        {
            var hwnd = new WindowInteropHelper(notch).Handle;
            foreach (var key in new byte[] { 0x41, 0x42, 0x43, 0x0D }) // A B C Invio
            {
                if (Interop.WindowNative.GetForegroundWindow() != hwnd)
                {
                    lines.Add("primo piano perso: mi fermo");
                    break;
                }
                Interop.WindowNative.PressKeys(key);
                await Task.Delay(60);
            }
            lines.Add(await Task.WhenAny(submitted.Task, Task.Delay(1500)) == submitted.Task ? $"inviato: {submitted.Task.Result}" : "Invio non arrivato");
        }
        notch.Close();
        if (before != 0)
            Interop.WindowNative.SetForegroundWindow(before);
        File.WriteAllLines(report, lines);
    }

    /// <summary>Una connessione vera (con <c>mstsc</c> vero) a un indirizzo di prova, con registro in PNG. Non salva nulla.</summary>
    private static async Task RemoteDemo(string address, string image)
    {
        var (computer, problem) = NothingShell.Core.RemoteAccess.RemoteDesktop.Build("Prova", address, null, false);
        if (computer is null)
        {
            File.WriteAllText(image + ".txt", problem);
            return;
        }
        var window = new RemoteConnectWindow(new DemoRemoteActions());
        window.Connect(computer, null);
        window.Present();
        while (window.IsBusy)
            await Task.Delay(200);
        await Task.Delay(300);
        SaveSnapshot(window.PrepareSnapshot(), image);
        window.Close();
    }

    private sealed class DemoRemoteActions : IRemoteConnectActions
    {
        public string? SaveComputer(NothingShell.Core.RemoteAccess.RemoteComputer computer, NothingShell.Core.RemoteAccess.RemoteComputer? replacing, string password) => null;

        public void ComputerConnected(NothingShell.Core.RemoteAccess.RemoteComputer computer, string? phrase)
        {
        }

        public NothingShell.Core.RemoteAccess.RemoteComputer? Current(NothingShell.Core.RemoteAccess.RemoteComputer computer) => computer;

        public bool AsksPasswords => false; // la prova va da sola: nessuno risponderebbe

        public bool LendPassword(NothingShell.Core.RemoteAccess.RemoteComputer computer, string userName, string password) => false;

        public void ReturnPassword(NothingShell.Core.RemoteAccess.RemoteComputer computer, int? processId)
        {
        }

        public void AskAssistant(string text)
        {
        }
    }

    /// <summary>Tutte le icone Fluent usate, accanto a quelle a punti che sostituiscono: per controllare che significhino lo stesso.</summary>
    private static void RenderIconSheet(string folder)
    {
        var sheet = new System.Windows.Controls.WrapPanel { Width = 660, Height = 400, Background = Rendering.Theme.BaseSolidBrush };
        foreach (var glyph in Rendering.FluentIcons.All.Keys)
        {
            var pair = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Width = 110, Height = 44 };
            pair.Children.Add(new Rendering.DotMatrixView { Pitch = 3, Dots = NothingShell.Core.DotMatrix.DotGrid.From(glyph), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            pair.Children.Add(new System.Windows.Controls.TextBlock { Text = Rendering.FluentIcons.For(glyph).ToString(), FontFamily = Rendering.Theme.IconFont, FontSize = 22, Foreground = Rendering.Theme.InkBrush, VerticalAlignment = VerticalAlignment.Center });
            sheet.Children.Add(pair);
        }
        var previous = Rendering.Theme.Settings;
        Rendering.Theme.Apply(previous with { Display = DisplayStyle.Dots });
        SaveSnapshot(sheet, Path.Combine(folder, "icons.png"));
        Rendering.Theme.Apply(previous);
    }

    /// <summary>
    /// Il vetro non si vede nelle immagini di --snapshot: qui finestre vere sopra una tavola colorata, fotografate dallo
    /// schermo. Stessi oggetti, tema cambiato dal vivo: scuro e chiaro col vetro, poi senza. Fotografa solo le proprie finestre.
    /// </summary>
    private static async Task GlassCheck(string folder, bool solid)
    {
        Directory.CreateDirectory(folder);
        Rendering.Theme.Apply(new AppearanceSettings { Theme = AppTheme.Dark, Glass = !solid });
        var report = new List<string>();
        var memoryBefore = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
        var stripes = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1 };
        foreach (var color in new[] { "#E81123", "#FFB900", "#107C10", "#0078D4", "#000000", "#FFFFFF", "#F7630C", "#8764B8" })
            stripes.Children.Add(new System.Windows.Controls.Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                Child = new System.Windows.Controls.TextBlock { Text = "Testo dietro al vetro", FontSize = 26, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray },
            });
        var board = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Left = (SystemParameters.PrimaryScreenWidth - 1120) / 2,
            Top = 0,
            Width = 1120,
            Height = 720,
            Content = stripes,
        };
        board.Show();

        using var media = new MediaService();
        var notch = new NotchWindow(media, DevPageActions.None, ConnectedPageActions.None) { ShowActivated = false };
        notch.SetTime(DateTime.Now);
        notch.Show();

        var plugins = new PluginHost();
        var quick = new QuickPanelWindow(() => { }, () => { }, new SnapshotPhone(), () => true) { ShowActivated = false };
        var quickRoot = quick.PrepareSnapshot();
        quick.Width = quickRoot.Width;
        quick.Height = quickRoot.Height;
        quick.Left = board.Left + 720;
        quick.Top = 90;
        quick.Show();

        var settings = new SettingsWindow(new SnapshotSettings(plugins.Results)) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Topmost = true, Left = board.Left + 20, Top = 240, Height = 460, Width = 680 };
        settings.Show(SettingsPage.Appearance);
        settings.Show();

        var clock = new ClockWidget();
        clock.Update(DateTime.Now);
        var widget = new WidgetWindow(clock) { Left = board.Left + 20, Top = 90 };
        widget.Show();
        var widgets = new List<Window> { widget };
        foreach (var view in new WidgetView[] { new BatteryWidget(), new SystemWidget(), new CalendarWidget() })
        {
            var extra = new WidgetWindow(view) { Left = board.Left + 20 + (180 * widgets.Count), Top = 730 };
            extra.Show();
            widgets.Add(extra);
        }

        var catalog = new AppCatalog();
        var pins = TaskbarPins.Read();
        var resolver = new AppIdResolver();
        resolver.SetSources(pins, catalog.Apps);
        using var tracker = new WindowTracker(resolver);
        tracker.SetPinned(pins);
        tracker.Refresh();
        var monitor = Interop.WindowNative.Monitors().First(m => m.IsPrimary);
        var shellIcons = new ShellIcons();
        // Solo la finestra della barra: la taskbar di Windows non si tocca e il tasto Win resta a Windows.
        var bar = new BarWindow(tracker, new AppIcons(), shellIcons, catalog, new WindowsTaskbar(),
            new LauncherWindow(catalog, shellIcons, new LauncherSettingsStore(Path.Combine(folder, "launcher.json"))), monitor, NothingShell.Core.Bar.MultiMonitorMode.AllTaskbars);
        bar.SetTime(DateTime.Now);
        bar.Show();
        await Task.Delay(1500);

        report.Add($"memoria {(solid ? "senza vetro" : "col vetro")}: {memoryBefore / 1048576} MB prima delle finestre, {System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1048576} MB con 4 superfici");
        if (solid)
        {
            File.WriteAllLines(Path.Combine(folder, "report-solido.txt"), report);
            foreach (var window in new Window[] { notch, quick, settings, widget, bar, board })
                window.Close();
            return;
        }
        report.Add(WidgetOrder(widgets));
        report.Add(await Stillness(widgets));
        await CaptureWidgets(board, widgets, Path.Combine(folder, "widget.png"));

        // Qualcosa li alza, si nascondono e tornano: devono restare in fondo, senza innescare cicli.
        var raised = new WindowInteropHelper(widgets[1]).Handle;
        RaiseWindow(raised);
        widgets[2].Hide();
        await Task.Delay(100);
        widgets[2].Show();
        await Task.Delay(300);
        report.Add($"dopo averli alzati e riaperti: {WidgetOrder(widgets)}");
        report.Add(await Stillness(widgets));
        report.Add(HitTests(notch, board));
        report.Add(CompanionFollows(widget));
        foreach (var extra in widgets.Skip(1))
            extra.Close();

        var looks = new (string Name, AppearanceSettings Look)[]
        {
            ("scuro-vetro", new AppearanceSettings { Theme = AppTheme.Dark }),
            ("chiaro-vetro", new AppearanceSettings { Theme = AppTheme.Light }),
            ("scuro-limpido-viola", new AppearanceSettings { Theme = AppTheme.Dark, Accent = "#8764B8", GlassStrength = 1 }),
            ("chiaro-solido", new AppearanceSettings { Theme = AppTheme.Light, Glass = false }),
            ("nothing", AppearanceChoices.Presets[2].Settings),
        };
        foreach (var (name, look) in looks)
        {
            Rendering.Theme.Apply(look);
            await Task.Delay(500);
            foreach (var (state, page) in new[] { (NotchState.Idle, NotchPage.Home), (NotchState.Expanded, NotchPage.Home) })
            {
                notch.PrepareSnapshot(state, page);
                await Task.Delay(250);
                CaptureWindow(board, Path.Combine(folder, $"{name}-{(state == NotchState.Idle ? "riposo" : "aperto")}.png"));
            }
            CaptureWindow(bar, Path.Combine(folder, $"{name}-barra.png"));
        }

        Rendering.Theme.Apply(new AppearanceSettings { Theme = AppTheme.Dark, QuietNotch = true });
        notch.PrepareSnapshot(NotchState.Idle, NotchPage.Home);
        await Task.Delay(400);
        CaptureWindow(board, Path.Combine(folder, "discreto.png"));
        report.Add(QuietHit(notch));

        Rendering.Theme.Apply(new AppearanceSettings { Theme = AppTheme.Dark });
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = board, Placement = System.Windows.Controls.Primitives.PlacementMode.Center };
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Impostazioni…" });
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Usa il tasto Win per il launcher", IsCheckable = true, IsChecked = true });
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Esci da Nothing Shell" });
        menu.IsOpen = true;
        await Task.Delay(400);
        CaptureWindow(board, Path.Combine(folder, "menu.png"));
        menu.IsOpen = false;

        // Le compagne devono sparire con la loro finestra, e la tastiera non deve mai finire su di loro.
        quick.Hide();
        await Task.Delay(300);
        CaptureWindow(board, Path.Combine(folder, "pannello-chiuso.png"));

        File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
        foreach (var window in new Window[] { notch, quick, settings, widget, bar, board })
            window.Close();
    }

    /// <summary>Dentro la pillola il mouse arriva al notch; nelle zone vuote della sua finestra passa a ciò che sta sotto.</summary>
    private static string HitTests(Window notch, Window board)
    {
        var scale = VisualTreeHelper.GetDpi(notch).DpiScaleX;
        var center = (int)((notch.Left + (notch.Width / 2)) * scale);
        var onPill = WindowFromPoint(new NativePoint { X = center, Y = (int)(16 * scale) });
        var beside = WindowFromPoint(new NativePoint { X = (int)((notch.Left + 10) * scale), Y = (int)(16 * scale) });
        var below = WindowFromPoint(new NativePoint { X = center, Y = (int)(200 * scale) });
        var ownNotch = new WindowInteropHelper(notch).Handle;
        var ownBoard = new WindowInteropHelper(board).Handle;
        return $"clic: sulla pillola {(onPill == ownNotch ? "notch ok" : "ERRORE " + Interop.WindowNative.GetClassName(onPill))}, accanto {(beside == ownBoard ? "passa sotto ok" : "ERRORE " + Interop.WindowNative.GetClassName(beside))}, sotto {(below == ownBoard ? "passa sotto ok" : "ERRORE " + Interop.WindowNative.GetClassName(below))}";
    }

    private static string QuietHit(Window notch)
    {
        var scale = VisualTreeHelper.GetDpi(notch).DpiScaleX;
        var top = WindowFromPoint(new NativePoint { X = (int)((notch.Left + (notch.Width / 2)) * scale), Y = 0 });
        return $"notch discreto: bordo alto dello schermo {(top == new WindowInteropHelper(notch).Handle ? "lo trova ok" : "ERRORE " + Interop.WindowNative.GetClassName(top))}";
    }

    /// <summary>La compagna del widget resta esattamente sotto il widget quando lo si sposta.</summary>
    private static string CompanionFollows(Window widget)
    {
        var hwnd = new WindowInteropHelper(widget).Handle;
        widget.Left += 40;
        widget.Top += 15;
        Interop.WindowNative.GetWindowRect(hwnd, out var own);
        Interop.WindowNative.GetWindowRect(Interop.NativeMethods.GetOwner(hwnd), out var glass);
        var same = own.Left == glass.Left && own.Top == glass.Top && own.Right == glass.Right && own.Bottom == glass.Bottom;
        return $"widget spostato: compagna {(same ? "allineata ok" : $"ERRORE {own.Left},{own.Top} contro {glass.Left},{glass.Top}")}";
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);

    /// <summary>Ogni widget ha la sua compagna come proprietaria e sta sopra di lei; sotto ci sono solo widget, compagne e poi il desktop.</summary>
    private static string WidgetOrder(List<Window> widgets)
    {
        var own = widgets.Select(w => new WindowInteropHelper(w).Handle).ToHashSet();
        var problems = new List<string>();
        foreach (var widget in widgets)
        {
            var hwnd = new WindowInteropHelper(widget).Handle;
            var glass = Interop.NativeMethods.GetOwner(hwnd);
            if (Interop.WindowNative.GetClassName(glass) != Rendering.GlassBackdrop.ClassName)
            {
                problems.Add($"{widget.Title} senza compagna");
                continue;
            }
            var sawGlass = false;
            for (var next = GetNextWindow(hwnd, 2); next != 0 && Interop.WindowNative.GetClassName(next) != "Progman"; next = GetNextWindow(next, 2))
            {
                sawGlass |= next == glass;
                if (Interop.WindowNative.IsWindowVisible(next) && !own.Contains(next) && Interop.WindowNative.GetClassName(next) != Rendering.GlassBackdrop.ClassName)
                {
                    problems.Add($"{widget.Title} è sopra {Interop.WindowNative.GetClassName(next)}");
                    break;
                }
            }
            if (!sawGlass)
                problems.Add($"{widget.Title} è sotto la sua compagna");
        }
        return problems.Count == 0 ? $"ordine: {widgets.Count} widget ok, ognuno sopra la sua compagna, in fondo al desktop" : $"ordine ERRORE: {string.Join("; ", problems)}";
    }

    /// <summary>A riposo l'ordine delle finestre non cambia e il processore resta fermo: un ciclo fra widget si vede qui.</summary>
    private static async Task<string> Stillness(List<Window> widgets)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var first = process.TotalProcessorTime;
        var changes = 0;
        string? last = null;
        for (var i = 0; i < 100; i++)
        {
            var order = string.Join(' ', widgets.Select(w => GetNextWindow(new WindowInteropHelper(w).Handle, 2)));
            if (last is not null && order != last)
                changes++;
            last = order;
            await Task.Delay(15);
        }
        process.Refresh();
        var busy = (process.TotalProcessorTime - first).TotalSeconds;
        return $"riposo: ordine cambiato {changes} volte, {busy:0.00} s di processore in 1,5 s{(changes > 0 || busy > 0.5 ? " ERRORE" : " ok")}";
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindow")]
    private static extern nint GetNextWindow(nint hwnd, uint command);

    /// <summary>I widget stanno sotto a tutto: si fotografano dalle miniature di DWM, che li disegnano anche se sono coperti.</summary>
    private static async Task CaptureWidgets(Window board, List<Window> widgets, string path)
    {
        var target = new WindowInteropHelper(board).Handle;
        var scale = VisualTreeHelper.GetDpi(board).DpiScaleX;
        var thumbnails = new List<nint>();
        var x = 20.0;
        foreach (var widget in widgets)
        {
            var thumbnail = Interop.DwmNative.Register(target, new WindowInteropHelper(widget).Handle);
            Interop.DwmNative.Show(thumbnail, new NothingShell.Core.Widgets.Rect2(x, 20, widget.Width * scale, widget.Height * scale));
            thumbnails.Add(thumbnail);
            x += (widget.Width * scale) + 20;
        }
        await Task.Delay(400);
        CaptureWindow(board, path);
        thumbnails.ForEach(Interop.DwmNative.Unregister);
    }

    private static void RaiseWindow(nint hwnd) => SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    private static void CaptureWindow(Window window, string path)
    {
        if (!Interop.WindowNative.GetWindowRect(new WindowInteropHelper(window).Handle, out var r) ||
            Interop.NativeMethods.CaptureScreen(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) is not { } shot)
            return;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(shot));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>
    /// La mascotte (docs/21-mascotte.md): <c>mascotte-espressioni.png</c> con ogni espressione in sei momenti della sua durata,
    /// fermi sulle pose a cui puntano le molle, e <c>mascotte-parla.png</c> con l'avviso in cui parla lei.
    /// </summary>
    private static void RenderMascotSheet(string folder)
    {
        const int Frames = 6;
        const double LabelWidth = 96;
        Directory.CreateDirectory(folder);
        var idle = NotchLayout.For(NotchState.Idle);
        var moods = Enum.GetValues<MascotMood>();
        var sheet = new System.Windows.Controls.Grid
        {
            Background = Rendering.Theme.BaseSolidBrush,
            Width = LabelWidth + (Frames * (idle.Width + 8)) + 8,
            Height = moods.Length * (idle.Height + 8),
        };
        sheet.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(LabelWidth) });
        for (var frame = 0; frame < Frames; frame++)
            sheet.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(idle.Width + 8) });
        foreach (var (mood, row) in moods.Select((m, i) => (m, i)))
        {
            sheet.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(idle.Height + 8) });
            var label = new System.Windows.Controls.TextBlock
            {
                Text = mood switch
                {
                    MascotMood.Surprised => "Sorpresa",
                    MascotMood.Happy => "Contenta",
                    MascotMood.Sad => "Triste",
                    MascotMood.Wink => "Occhiolino",
                    MascotMood.LookAround => "Giro d'occhi",
                    MascotMood.Hop => "Saltello",
                    MascotMood.Yawn => "Sbadiglio",
                    MascotMood.Calling => "Ti chiama",
                    MascotMood.Dizzy => "Capogiro",
                    MascotMood.Talking => "Parla",
                    MascotMood.Purr => "Fusa",
                    MascotMood.Giggle => "Ridacchia",
                    MascotMood.WaveBack => "Saluta",
                    MascotMood.Nod => "Annuisce",
                    MascotMood.Poked => "Colpetto",
                    _ => "Offesa",
                },
                Foreground = Rendering.Theme.InkBrush,
                FontFamily = Rendering.Theme.BodyFont,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            System.Windows.Controls.Grid.SetRow(label, row);
            sheet.Children.Add(label);
            for (var frame = 0; frame < Frames; frame++)
            {
                var mascot = new MascotView(idle.Height / 28) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                mascot.Freeze(MascotBrain.Feel(mood, MascotBrain.LengthOf(mood) * frame / Frames));
                var pill = new System.Windows.Controls.Border
                {
                    Width = idle.Width,
                    Height = idle.Height,
                    CornerRadius = new CornerRadius(NotchLayout.CornerRadius(idle)),
                    Background = Rendering.Theme.PanelBrush,
                    BorderBrush = Rendering.Theme.OutlineBrush,
                    BorderThickness = new Thickness(1),
                    Child = mascot,
                };
                System.Windows.Controls.Grid.SetRow(pill, row);
                System.Windows.Controls.Grid.SetColumn(pill, frame + 1);
                sheet.Children.Add(pill);
            }
        }
        SaveSnapshot(sheet, Path.Combine(folder, "mascotte-espressioni.png"));

        using var media = new MediaService(); // non avviato
        var notch = new NotchWindow(media, DevPageActions.None, ConnectedPageActions.None);
        notch.SayForSnapshot("MENTRE ERI VIA: 2 MAIL, 1 AGENTE HA FINITO");
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Alert, NotchPage.Home), Path.Combine(folder, "mascotte-parla.png"));
    }

    /// <summary>
    /// Vestiti del notch (docs/22-vestiti-notch.md): ogni vestito di serie a riposo e aperto, in scuro e in chiaro, sospeso e unito
    /// al bordo; poi la pagina Aspetto col guardaroba e il selettore colore aperto, e l'editor.
    /// </summary>
    private static void RenderOutfitSheet(string folder)
    {
        Directory.CreateDirectory(folder);
        using var media = new MediaService(); // non avviato
        foreach (var (theme, style) in new[] { ("scuro", NotchStyle.Floating), ("chiaro", NotchStyle.Floating), ("scuro", NotchStyle.Attached) })
        {
            var baseLook = new AppearanceSettings { Theme = theme == "scuro" ? AppTheme.Dark : AppTheme.Light, Glass = false, NotchStyle = style };
            Rendering.Theme.Apply(baseLook);
            var notch = new NotchWindow(media, DevPageActions.None, ConnectedPageActions.None);
            notch.SetTime(new DateTime(2026, 11, 5, 12, 34, 0));
            var sheet = new System.Windows.Controls.Grid { Background = new SolidColorBrush(Rendering.Theme.ToColor(Rendering.Theme.Current.BaseSolid.Mix(Argb.FromRgb(0x808080), 0.25))) };
            var outfits = new NotchOutfit?[] { null }.Concat(OutfitCatalog.BuiltIn).ToArray();
            sheet.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(130) });
            sheet.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
            sheet.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
            for (var i = 0; i < outfits.Length; i++)
            {
                sheet.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
                var name = new System.Windows.Controls.TextBlock { Text = outfits[i]?.Name ?? "Nessuno", Foreground = Rendering.Theme.InkBrush, Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Center };
                System.Windows.Controls.Grid.SetRow(name, i);
                sheet.Children.Add(name);
                Rendering.Theme.Apply(baseLook with { NotchOutfit = outfits[i] });
                foreach (var (state, column) in new[] { (NotchState.Idle, 1), (NotchState.Expanded, 2) })
                {
                    var pill = notch.PrepareSnapshot(state, NotchPage.Home);
                    var size = new Size(pill.Width, pill.Height);
                    pill.Measure(size);
                    pill.Arrange(new Rect(size));
                    pill.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)size.Width * 2, (int)size.Height * 2, 192, 192, PixelFormats.Pbgra32);
                    bitmap.Render(pill);
                    var image = new System.Windows.Controls.Image { Source = bitmap, Width = size.Width, Height = size.Height, Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
                    System.Windows.Controls.Grid.SetRow(image, i);
                    System.Windows.Controls.Grid.SetColumn(image, column);
                    sheet.Children.Add(image);
                }
            }
            sheet.Width = 1200;
            sheet.Measure(new Size(1200, double.PositiveInfinity));
            sheet.Height = sheet.DesiredSize.Height;
            SaveSnapshot(sheet, Path.Combine(folder, $"vestiti-{theme}-{(style == NotchStyle.Attached ? "unito" : "sospeso")}.png"), scale: 1);
            notch.Close();
        }

        Rendering.Theme.Apply(new AppearanceSettings { Theme = AppTheme.Dark, Glass = false, Accent = "#3A9D8F", NotchOutfit = OutfitCatalog.BuiltIn[1] });
        var actions = new SnapshotSettings([]) { Appearance = Rendering.Theme.Settings };
        var settings = new SettingsWindow(actions);
        var root = (FrameworkElement)settings.Content;
        root.Width = settings.Width;
        root.Height = 3000;
        settings.Show(SettingsPage.Appearance);
        settings.OpenPickerForSnapshot("accent");
        SaveSnapshot(root, Path.Combine(folder, "aspetto-guardaroba.png"), scale: 1);
        settings.EditOutfit(actions.Wardrobe[0], isNew: false);
        settings.OpenPickerForSnapshot("outfit-fill");
        root.Height = 1900;
        SaveSnapshot(root, Path.Combine(folder, "aspetto-editor.png"), scale: 1);
        settings.Close();
    }

    /// <summary>L'aspetto delle immagini: <c>chiaro</c>, <c>scuro</c>, <c>punti</c>, <c>#RRGGBB</c>; RenderTargetBitmap non vede il vetro, quindi senza.</summary>
    private static AppearanceSettings SnapshotAppearance(string[] look) => new()
    {
        Theme = look.Contains("chiaro") ? AppTheme.Light : look.Contains("scuro") ? AppTheme.Dark : AppTheme.System,
        Display = look.Contains("punti") ? DisplayStyle.Dots : DisplayStyle.Windows,
        Accent = look.FirstOrDefault(a => a.StartsWith('#')),
        Glass = false,
        NotchStyle = look.Contains("unito") ? NotchStyle.Attached : NotchStyle.Floating,
        NotchSize = look.Contains("grande") ? NotchSize.Large : NotchSize.Small,
        NotchMascot = look.Contains("mascotte"),
    };

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
