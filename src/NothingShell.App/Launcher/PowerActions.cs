using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NothingShell.App.Launcher;

/// <summary>Azioni di alimentazione del launcher (docs/06-launcher.md).</summary>
public static class PowerActions
{
    public static void Lock() => LockWorkStation();

    public static void Sleep() => SetSuspendState(false, false, false);

    public static void Restart() => Shutdown("/r /t 0");

    public static void PowerOff() => Shutdown("/s /t 0");

    private static void Shutdown(string arguments) =>
        Process.Start(new ProcessStartInfo("shutdown.exe", arguments) { CreateNoWindow = true, UseShellExecute = false })?.Dispose();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.Bool)] bool hibernate,
        [MarshalAs(UnmanagedType.Bool)] bool forceCritical,
        [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);
}
