using System.Runtime.InteropServices;
using System.Windows.Threading;
using NothingShell.App.Interop;
using NothingShell.Core.Sensors;

namespace NothingShell.App.Services;

/// <summary>Campiona CPU/RAM (ogni 2 s) e batteria (ogni 20 s) sul thread UI.</summary>
public sealed class SystemMonitor
{
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _batteryTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private CpuTimes? _previous;

    public SystemMonitor()
    {
        _statsTimer.Tick += (_, _) => PublishStats();
        _batteryTimer.Tick += (_, _) => PublishBattery();
    }

    public event Action<SystemStats>? StatsChanged;

    public event Action<BatteryStatus>? BatteryChanged;

    public void Start()
    {
        _previous = ReadCpuTimes();
        PublishBattery();
        _statsTimer.Start();
        _batteryTimer.Start();
    }

    public static BatteryStatus ReadBattery() =>
        NativeMethods.GetSystemPowerStatus(out var s)
            ? BatteryStatus.FromWin32(s.AcLineStatus, s.BatteryFlag, s.BatteryLifePercent)
            : BatteryStatus.NoBattery;

    public static double ReadMemoryLoad()
    {
        var status = new NativeMethods.MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>(),
        };
        return NativeMethods.GlobalMemoryStatusEx(ref status) && status.TotalPhys > 0
            ? 1 - (double)status.AvailPhys / status.TotalPhys
            : 0;
    }

    private static CpuTimes? ReadCpuTimes() =>
        NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user) ? new CpuTimes(idle, kernel, user) : null;

    private void PublishStats()
    {
        var current = ReadCpuTimes();
        var cpu = current is { } c && _previous is { } p ? CpuUsage.Between(p, c) : 0;
        _previous = current;
        StatsChanged?.Invoke(new SystemStats(cpu, ReadMemoryLoad()));
    }

    private void PublishBattery() => BatteryChanged?.Invoke(ReadBattery());
}
