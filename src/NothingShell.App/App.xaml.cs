using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NothingShell.App.Bar;
using NothingShell.App.Launcher;
using NothingShell.Core.Launcher;
using NothingShell.App.Plugins;
using NothingShell.App.Services;
using NothingShell.App.Settings;
using NothingShell.App.Widgets;
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

    private Mutex? _singleInstance;
    private EventWaitHandle? _exitSignal;
    private RegisteredWaitHandle? _exitWait;
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
                await RenderPluginAndSettingsSnapshots(folder);
                Shutdown();
                return;
            case ["--dump-windows", var file]:
                using (var tracker = new WindowTracker())
                    File.WriteAllLines(file, tracker.Diagnose());
                Shutdown();
                return;
            case ["--restore-taskbar"]:
                WindowsTaskbar.ForceShow();
                Shutdown();
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

    /// <summary>Qualunque crash deve lasciare la taskbar di Windows visibile.</summary>
    private void RestoreTaskbarOnCrash()
    {
        DispatcherUnhandledException += (_, _) => WindowsTaskbar.ForceShow();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => WindowsTaskbar.ForceShow();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => WindowsTaskbar.ForceShow();
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

    private static void SaveSnapshot(FrameworkElement view, string path)
    {
        var size = new Size(view.Width, view.Height);
        view.Measure(size);
        view.Arrange(new Rect(size));
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)(size.Width * 2), (int)(size.Height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(view);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
