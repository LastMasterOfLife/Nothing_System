using System.IO;
using System.Windows;
using System.Windows.Threading;
using NothingShell.App.Bar;
using NothingShell.App.Launcher;
using NothingShell.App.Notch;
using NothingShell.App.Plugins;
using NothingShell.App.Services;
using NothingShell.App.Settings;
using NothingShell.App.Widgets;
using NothingShell.Core.Launcher;
using NothingShell.Core.Plugins;
using NothingShell.Core.Settings;
using NothingShell.Core.Storage;
using NothingShell.Core.Widgets;

namespace NothingShell.App;

/// <summary>
/// Collega servizi e finestre della shell e applica le impostazioni dal vivo
/// (docs/01-architecture.md, docs/07-settings-plugins.md).
/// </summary>
public sealed class ShellController : IShellSettingsActions, IDisposable
{
    private readonly ShellSettingsStore _settingsStore = new(ShellSettingsStore.DefaultPath);
    private readonly ClockService _clock = new();
    private readonly SystemMonitor _monitor = new();
    private readonly NetworkStatus _network = new();
    private readonly MediaService _media = new();
    private readonly WindowTracker _tracker = new();
    private readonly WindowsTaskbar _taskbar = new();
    private readonly AppCatalog _catalog = new();
    private readonly PluginHost _plugins = new();
    private readonly LauncherWindow _launcher;
    private readonly WinKeyHook _winKey;
    private readonly NotchWindow _notch;
    private readonly BarWindow _bar;
    private readonly WidgetHost _widgets;

    private ShellSettings _settings;
    private SettingsWindow? _settingsWindow;
    private bool _fullscreen;

    public ShellController()
    {
        _settings = _settingsStore.Load();

        _launcher = new LauncherWindow(_catalog, new ShellIcons(), new LauncherSettingsStore(LauncherSettingsStore.DefaultPath));
        _winKey = new WinKeyHook { Enabled = _launcher.UseWinKey };
        _winKey.Pressed += () => Application.Current.Dispatcher.BeginInvoke(_launcher.Toggle);
        _launcher.UseWinKeyChanged += enabled => _winKey.Enabled = enabled;
        _launcher.CommandRequested += id =>
        {
            if (id == AppCatalog.SettingsEntry.Id)
                OpenSettings();
        };

        _notch = new NotchWindow(_media);
        _bar = new BarWindow(_tracker, new AppIcons(), _taskbar, _launcher);
        _widgets = new WidgetHost(_clock, _monitor, new WidgetSettingsStore(WidgetSettingsStore.DefaultPath));

        _notch.SettingsRequested += OpenSettings;
        _bar.SettingsRequested += OpenSettings;
        _widgets.SettingsRequested += OpenSettings;

        _clock.MinuteChanged += _notch.SetTime;
        _clock.MinuteChanged += _bar.SetTime;
        _monitor.BatteryChanged += _bar.SetBattery;
        _network.Changed += _bar.SetNetwork;
        _bar.SetNetwork(_network.Current);
        _tracker.FullscreenChanged += fullscreen =>
        {
            _fullscreen = fullscreen;
            ApplyTopLevelVisibility();
        };
    }

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
        _tracker.Start();
        ApplyTopLevelVisibility();
        _winKey.Start();

        await _media.StartAsync();
        try
        {
            await _catalog.RefreshAsync(); // pronto prima della prima apertura del launcher
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
        _taskbar.Show();
        _plugins.Dispose();
        _winKey.Dispose();
        _tracker.Dispose();
        _media.Dispose();
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
        SetVisible(_bar, _settings.Bar && !_fullscreen);

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
    }
}
