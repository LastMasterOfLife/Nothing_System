using System.IO;
using System.Windows;
using NothingShell.App.Notch;
using NothingShell.App.Services;
using NothingShell.Core.Dev;
using NothingShell.Core.PortalDebug;

namespace NothingShell.App;

/// <summary>Debug potenziato: portali collegati col codice, dashboard e avvisi nel notch (docs/24-debug-potenziato.md).</summary>
public sealed partial class ShellController
{
    private static readonly TimeSpan DebugCardDuration = TimeSpan.FromSeconds(10);

    private readonly PortalDebugService _debug = new();
    private readonly Dictionary<string, DebugDashboardWindow> _debugWindows = new(StringComparer.Ordinal);

    public event Action? DebugChanged;

    public DebugSettings DebugSettings => _settings.DebugOrDefault();

    public IReadOnlyList<PortalLink> DebugLinks => _debug.Links;

    /// <summary>L'app Django: accanto all'eseguibile, o nel sorgente se si gira da lì (così pip install -e segue le modifiche).</summary>
    public string DebugSdkFolder
    {
        get
        {
            if (SelfUpdate.FindSource() is { } source)
            {
                var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source.Project)!, "..", "..", "sdk", "django"));
                if (Directory.Exists(repo))
                    return repo;
            }
            return Path.Combine(AppContext.BaseDirectory, "sdk", "django");
        }
    }

    private void WireDebug()
    {
        var known = new Dictionary<string, (PortalReach, string?)>(StringComparer.Ordinal);
        _debug.Changed += link =>
        {
            // le impostazioni si ridisegnano solo quando cambia lo stato del collegamento, non a ogni evento
            var now = (link.Reach, link.Problem);
            if (known.TryGetValue(link.Portal.Id, out var before) && before == now)
                return;
            known[link.Portal.Id] = now;
            DebugChanged?.Invoke();
        };
        _debug.Changed += _ => ScheduleDebugPage();
        _debug.Alert += OnDebugAlert;
        _debugPageTimer.Tick += (_, _) =>
        {
            _debugPageTimer.Stop();
            RefreshDebugPage();
        };
        _notch.DebugOpened += OpenDebugDashboard;
        _notch.DebugSettingsRequested += () =>
        {
            OpenSettings();
            _settingsWindow?.Show(Settings.SettingsPage.Debug);
        };
        _debug.Apply(DebugSettings);
        RefreshDebugPage();
    }

    private readonly System.Windows.Threading.DispatcherTimer _debugPageTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    /// <summary>Gli eventi arrivano ogni secondo: la pagina del notch si rifà al massimo due volte al secondo.</summary>
    private void ScheduleDebugPage()
    {
        if (!_debugPageTimer.IsEnabled)
            _debugPageTimer.Start();
    }

    /// <summary>La pagina Debug del notch: per portale stato, numeri e le cose da guardare (Core DebugHighlights).</summary>
    private void RefreshDebugPage()
    {
        var now = DateTimeOffset.Now;
        var rules = DebugSettings.AlertRules;
        _notch.SetDebug([.. _debug.Links.Select(l => new DebugPortalView(l.Portal.Id, l.Portal.Name, l.Reach, l.Problem, l.Paused,
            DebugHighlights.Summary(l.Log, l.Reach, l.Problem, l.Paused, now),
            DebugHighlights.For(l.Log, l.Status, rules, now)))]);
    }

    private void DisposeDebug()
    {
        _debugPageTimer.Stop();
        foreach (var window in _debugWindows.Values.ToArray())
            window.Close();
        _debug.Dispose();
    }

    public void SetDebugEnabled(bool enabled)
    {
        UpdateDebug(DebugSettings with { Enabled = enabled });
        if (!enabled)
        {
            foreach (var window in _debugWindows.Values.ToArray())
                window.Close();
        }
    }

    public void SetDebugPortalWatch(string id, bool watch)
    {
        if (DebugSettings.PortalList.FirstOrDefault(p => p.Id == id) is { } portal)
            UpdateDebug(DebugSettings.WithPortal(portal with { Watch = watch }));
    }

    public void SetDebugRules(DebugAlertRules rules) => UpdateDebug(DebugSettings with { Rules = rules });

    public async Task<string?> PairDebugPortalAsync(string address, string code)
    {
        var (portal, error) = await _debug.PairAsync(address, code, DebugSettings);
        if (portal is null)
            return error;
        UpdateDebug(DebugSettings.WithPortal(portal));
        OpenDebugDashboard(portal.Id);
        return null;
    }

    public async Task UnpairDebugPortalAsync(string id)
    {
        if (_debugWindows.TryGetValue(id, out var window))
            window.Close();
        await _debug.UnpairAsync(id);
        UpdateDebug(DebugSettings.WithoutPortal(id));
    }

    public void OpenDebugDashboard(string id) => OpenDebugDashboard(id, null);

    private void OpenDebugDashboard(string id, DebugEvent? focus)
    {
        if (_debug.Find(id) is not { } link)
            return;
        if (!_debugWindows.TryGetValue(id, out var window))
        {
            window = new DebugDashboardWindow(link, _debug, (file, line) => DevTools.OpenInEditor(new CodeLocation(file, line, 1, null), _settings.Editor));
            window.Closed += (_, _) =>
            {
                _debugWindows.Remove(id);
                RefreshDebugPage();
            };
            _debugWindows[id] = window;
        }
        window.Present(focus);
    }

    private void UpdateDebug(DebugSettings debug)
    {
        Update(_settings with { Debug = debug }); // anche la pagina Debug del notch c'è solo se acceso
        _debug.Apply(debug);
        RefreshDebugPage();
        DebugChanged?.Invoke();
    }

    /// <summary>
    /// L'attenzione autonoma arriva qui: scheda nel notch col pulsante che apre la dashboard sull'evento. Niente schede se
    /// la dashboard di quel portale è già davanti (la si sta guardando) o con un'app a schermo intero.
    /// </summary>
    private void OnDebugAlert(PortalLink link, DebugAlert alert)
    {
        if (!_settings.Notch || _fullscreen)
            return;
        if (_debugWindows.TryGetValue(link.Portal.Id, out var window) && window.IsActive)
            return;
        var copy = alert.Event is DebugCrash crash ? () => Clipboard.SetDataObject($"{crash.Summary}\n{alert.Detail}", copy: true) : (Action?)null;
        _notch.ShowCard(NotchCards.DebugAlert(link.Portal.Name, alert, () => OpenDebugDashboard(link.Portal.Id, alert.Event), copy, _notch.DismissCard),
            DebugCardDuration);
    }
}
