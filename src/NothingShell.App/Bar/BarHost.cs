using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using NothingShell.App.Interop;
using NothingShell.Core.Bar;
using NothingShell.Core.Sensors;

namespace NothingShell.App.Bar;

/// <summary>
/// Una barra per ogni monitor che in Windows ha la taskbar (docs/05-bar.md): segue i monitor collegati e
/// scollegati e le impostazioni di Windows "Mostra la barra delle applicazioni su tutti gli schermi" e
/// "Mostra le app della barra delle applicazioni su".
/// </summary>
public sealed class BarHost : IDisposable
{
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    private readonly Func<DisplayMonitor, MultiMonitorMode, BarWindow> _create;
    private readonly List<BarWindow> _bars = [];
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _visible;
    private nint _fullscreenMonitor;
    private DateTime? _time;
    private BatteryStatus? _battery;
    private NetworkKind? _network;

    public BarHost(Func<DisplayMonitor, MultiMonitorMode, BarWindow> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        _create = create;
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Rebuild();
        };
        // Questi eventi arrivano su un thread di sistema e a raffiche: un solo ricalcolo, sul thread UI.
        SystemEvents.DisplaySettingsChanged += OnSystemChange;
        SystemEvents.UserPreferenceChanged += OnSystemChange;
        SystemParameters.StaticPropertyChanged += OnWorkAreaChanged;
    }

    public event Action? SettingsRequested;

    /// <summary>Barre accese (impostazione Barra); restano nascoste sui monitor con un'app a schermo intero.</summary>
    public bool Visible
    {
        get => _visible;
        set
        {
            _visible = value;
            ApplyVisibility();
        }
    }

    /// <summary>Monitor con un'app a schermo intero in primo piano (0 = nessuno): lì la barra si nasconde.</summary>
    public nint FullscreenMonitor
    {
        get => _fullscreenMonitor;
        set
        {
            _fullscreenMonitor = value;
            ApplyVisibility();
        }
    }

    /// <summary>Crea, sposta o chiude le barre secondo i monitor e le impostazioni attuali.</summary>
    public void Rebuild()
    {
        var (allDisplays, mode) = ReadWindowsSettings();
        var monitors = WindowNative.Monitors().Where(m => m.IsPrimary || allDisplays).ToArray();

        foreach (var bar in _bars.Where(b => monitors.All(m => m.Name != b.Monitor.Name)).ToArray())
        {
            _bars.Remove(bar);
            bar.Close();
        }
        foreach (var monitor in monitors)
        {
            if (_bars.FirstOrDefault(b => b.Monitor.Name == monitor.Name) is { } existing)
            {
                existing.SetMonitor(monitor, mode);
                continue;
            }
            var bar = _create(monitor, mode);
            bar.SettingsRequested += () => SettingsRequested?.Invoke();
            if (_time is { } time)
                bar.SetTime(time);
            if (_battery is not null)
                bar.SetBattery(_battery);
            if (_network is { } network)
                bar.SetNetwork(network);
            _bars.Add(bar);
        }
        ApplyVisibility();
    }

    public void SetTime(DateTime now)
    {
        _time = now;
        foreach (var bar in _bars)
            bar.SetTime(now);
    }

    public void SetBattery(BatteryStatus status)
    {
        _battery = status;
        foreach (var bar in _bars)
            bar.SetBattery(status);
    }

    public void SetNetwork(NetworkKind kind)
    {
        _network = kind;
        foreach (var bar in _bars)
            bar.SetNetwork(kind);
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnSystemChange;
        SystemEvents.UserPreferenceChanged -= OnSystemChange;
        SystemParameters.StaticPropertyChanged -= OnWorkAreaChanged;
        _debounce.Stop();
        foreach (var bar in _bars)
            bar.Close();
        _bars.Clear();
    }

    /// <summary>
    /// <c>MMTaskbarEnabled</c> (assente = barra su tutti gli schermi, il predefinito di Windows 11) e
    /// <c>MMTaskbarMode</c> (assente = tutte le app su tutte le barre).
    /// </summary>
    private static (bool AllDisplays, MultiMonitorMode Mode) ReadWindowsSettings()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AdvancedKey);
        var enabled = key?.GetValue("MMTaskbarEnabled") is not int e || e != 0;
        var mode = key?.GetValue("MMTaskbarMode") is int m && Enum.IsDefined((MultiMonitorMode)m)
            ? (MultiMonitorMode)m
            : MultiMonitorMode.AllTaskbars;
        return (enabled, mode);
    }

    private void OnSystemChange(object? sender, EventArgs e) =>
        _debounce.Dispatcher.BeginInvoke(() =>
        {
            _debounce.Stop();
            _debounce.Start();
        });

    private void OnWorkAreaChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.WorkArea))
            OnSystemChange(sender, e);
    }

    private void ApplyVisibility()
    {
        foreach (var bar in _bars)
        {
            var visible = _visible && bar.Monitor.Handle != _fullscreenMonitor;
            if (visible && !bar.IsVisible)
                bar.Show();
            else if (!visible && bar.IsVisible)
                bar.Hide();
        }
    }
}
