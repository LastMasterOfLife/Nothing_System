using System.Management;
using System.Windows.Threading;

namespace NothingShell.App.Services;

/// <summary>
/// Luminosità dello schermo del portatile (docs/10-live-activities.md): evento WMI
/// <c>WmiMonitorBrightnessEvent</c>, che Windows manda per i tasti, il cursore e il risparmio energia.
/// I monitor esterni non lo espongono: lì non arriva nulla. L'indicatore di Windows resta (non si può spegnere).
/// </summary>
public sealed class BrightnessWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private ManagementEventWatcher? _watcher;

    /// <summary>Luminosità 0–100, sul thread UI.</summary>
    public event Action<int>? Changed;

    public void Start()
    {
        if (_watcher is not null)
            return;
        try
        {
            _watcher = new ManagementEventWatcher(
                new ManagementScope(@"root\wmi"),
                new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            _watcher.EventArrived += OnEvent;
            _watcher.Start();
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Stop(); // nessuno schermo con luminosità regolabile da Windows
        }
    }

    public void Stop()
    {
        if (_watcher is null)
            return;
        try
        {
            _watcher.Stop();
        }
        catch (ManagementException)
        {
            // già fermo
        }
        _watcher.EventArrived -= OnEvent;
        _watcher.Dispose();
        _watcher = null;
    }

    public void Dispose() => Stop();

    private void OnEvent(object sender, EventArrivedEventArgs e)
    {
        if (e.NewEvent["Brightness"] is byte level)
            _dispatcher.BeginInvoke(() => Changed?.Invoke(level));
    }
}
