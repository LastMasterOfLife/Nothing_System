namespace NothingShell.Core.Sensors;

/// <summary>Tempi cumulativi della CPU (unità di 100 ns), come restituiti da GetSystemTimes.</summary>
public readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

public static class CpuUsage
{
    /// <summary>
    /// Frazione 0..1 di CPU occupata fra due campioni. Il tempo kernel include già l'idle,
    /// quindi il totale è kernel + user.
    /// </summary>
    public static double Between(CpuTimes previous, CpuTimes current)
    {
        var idle = current.Idle - previous.Idle;
        var total = current.Kernel - previous.Kernel + (current.User - previous.User);
        if (total == 0 || current.Idle < previous.Idle)
            return 0;
        return Math.Clamp(1 - (double)idle / total, 0, 1);
    }
}

public sealed record SystemStats(double Cpu, double Memory);

/// <summary>Stato della batteria. <see cref="Percent"/> è null se il valore non è noto.</summary>
public sealed record BatteryStatus(bool HasBattery, int? Percent, bool Charging, bool PluggedIn = false)
{
    public const int LowThreshold = 20;

    public static BatteryStatus NoBattery { get; } = new(false, null, false, PluggedIn: true);

    /// <summary>Da SYSTEM_POWER_STATUS (GetSystemPowerStatus): 255 = sconosciuto, flag 128 = nessuna batteria, flag 8 = in carica.</summary>
    public static BatteryStatus FromWin32(byte acLineStatus, byte batteryFlag, byte lifePercent)
    {
        if (batteryFlag == 255 || (batteryFlag & 128) != 0)
            return NoBattery;
        int? percent = lifePercent <= 100 ? lifePercent : null;
        var pluggedIn = acLineStatus == 1;
        var charging = (batteryFlag & 8) != 0 || (pluggedIn && percent is < 100);
        return new BatteryStatus(true, percent, charging, pluggedIn);
    }

    public bool IsLow => HasBattery && !Charging && !PluggedIn && Percent is < LowThreshold;

    public string Label => !HasBattery ? "AC" : Percent is { } p ? $"{p}%" : "--";

    public string StateText => !HasBattery ? "ALIMENTAZIONE DI RETE"
        : Charging ? "IN CARICA"
        : PluggedIn ? "COLLEGATO"
        : "SU BATTERIA";

    /// <summary>Frazione mostrata dalla barra: piena senza batteria.</summary>
    public double BarFraction => !HasBattery ? 1 : (Percent ?? 0) / 100.0;
}
