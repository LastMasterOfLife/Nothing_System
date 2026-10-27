using System.Windows.Threading;

namespace NothingShell.App.Services;

/// <summary>Notifica sul thread UI a ogni cambio di minuto, allineandosi al confine del minuto.</summary>
public sealed class ClockService
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Normal);

    public ClockService()
    {
        _timer.Tick += (_, _) =>
        {
            Schedule();
            MinuteChanged?.Invoke(DateTime.Now);
        };
    }

    public event Action<DateTime>? MinuteChanged;

    public void Start()
    {
        Schedule();
        MinuteChanged?.Invoke(DateTime.Now);
    }

    private void Schedule()
    {
        var now = DateTime.Now;
        var next = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind).AddMinutes(1);
        _timer.Interval = next - now + TimeSpan.FromMilliseconds(50);
        _timer.Start();
    }
}
