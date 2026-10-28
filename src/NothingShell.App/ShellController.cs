using System.IO;
using System.Windows;
using System.Windows.Threading;
using NothingShell.App.Access;
using NothingShell.App.Bar;
using NothingShell.App.Launcher;
using NothingShell.App.Notch;
using NothingShell.App.Plugins;
using NothingShell.App.Services;
using NothingShell.App.Settings;
using NothingShell.App.Widgets;
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
public sealed class ShellController : IShellSettingsActions, IDisposable
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
    private readonly LauncherWindow _launcher;
    private readonly WinKeyHook _winKey;
    private readonly NotchWindow _notch;
    private readonly BarHost _bars;
    private readonly WidgetHost _widgets;
    private readonly AccessController _access = new();

    private ShellSettings _settings;
    private SettingsWindow? _settingsWindow;
    private bool _fullscreen; // app a schermo intero sul monitor principale: lì sta il notch

    public ShellController()
    {
        _settings = _settingsStore.Load();
        _tracker = new WindowTracker(_appIds);
        var shellIcons = new ShellIcons();

        _launcher = new LauncherWindow(_catalog, shellIcons, new LauncherSettingsStore(LauncherSettingsStore.DefaultPath));
        _winKey = new WinKeyHook { Enabled = _launcher.UseWinKey };
        _winKey.Pressed += () => Application.Current.Dispatcher.BeginInvoke(() => _launcher.Toggle());
        _launcher.UseWinKeyChanged += enabled => _winKey.Enabled = enabled;
        _launcher.CommandRequested += id =>
        {
            if (id == AppCatalog.SettingsEntry.Id)
                OpenSettings();
        };
        _launcher.LockRequested += Lock;
        _access.Enabled = _settings.Access;

        _notch = new NotchWindow(_media);
        var appIcons = new AppIcons();
        _bars = new BarHost((monitor, mode) =>
            new BarWindow(_tracker, appIcons, shellIcons, _catalog, _taskbar, _launcher, monitor, mode));
        _pins.Changed += ApplyPins;
        _widgets = new WidgetHost(_clock, _monitor, new WidgetSettingsStore(WidgetSettingsStore.DefaultPath));

        _notch.SettingsRequested += OpenSettings;
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
        _network.Changed += _bars.SetNetwork;
        _bars.SetNetwork(_network.Current);
        _tracker.FullscreenChanged += monitor =>
        {
            _fullscreen = monitor != 0 && Interop.WindowNative.Monitors().Any(m => m.Handle == monitor && m.IsPrimary);
            _bars.FullscreenMonitor = monitor;
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
    private IReadOnlyList<MailItem> Mails => MailFeed.Merge(_imap.Recent, _notifications.Recent, NotificationService.RecentCapacity);

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
        _taskbar.Show();
        _access.Dispose();
        _plugins.Dispose();
        _notifications.Dispose();
        _imap.Dispose();
        _winKey.Dispose();
        _pins.Dispose();
        _bars.Dispose();
        _tracker.Dispose();
        _media.Dispose();
    }

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
        _notch.SetMail(_settings.MailInNotch ? Mails : [], empty);
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
    }
}
