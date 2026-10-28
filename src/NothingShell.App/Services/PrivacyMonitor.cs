using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using NothingShell.Core.Notch;

namespace NothingShell.App.Services;

/// <summary>App che stanno usando microfono e webcam, per nome.</summary>
public sealed record PrivacyState(IReadOnlyList<string> Microphone, IReadOnlyList<string> Camera)
{
    public static PrivacyState None { get; } = new([], []);

    public bool Any => Microphone.Count > 0 || Camera.Count > 0;
}

/// <summary>
/// Microfono e webcam in uso (docs/10-live-activities.md): Windows annota in
/// <c>CapabilityAccessManager\ConsentStore</c> inizio e fine di ogni uso. Si rilegge quando il registro cambia,
/// senza polling; per le app desktop si controlla anche che il processo esista (un crash lascia l'uso "aperto").
/// </summary>
public sealed class PrivacyMonitor : IDisposable
{
    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private const uint RegNotifyChangeLastSet = 0x4;
    private const uint RegNotifyThreadAgnostic = 0x10000000;

    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private RegistryKey? _key;
    private AutoResetEvent? _signal;
    private RegisteredWaitHandle? _wait;

    public PrivacyMonitor()
    {
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Reload();
        };
    }

    /// <summary>Nome dell'app dello Store dal nome della famiglia di pacchetti (dal catalogo dello Start).</summary>
    public Func<string, string?> PackagedAppName { get; set; } = _ => null;

    public PrivacyState Current { get; private set; } = PrivacyState.None;

    public event Action<PrivacyState>? Changed;

    public void Start()
    {
        if (_key is not null)
            return;
        _key = Registry.CurrentUser.OpenSubKey(ConsentStore);
        if (_key is null)
            return;
        _signal = new AutoResetEvent(false);
        if (!Arm())
            return;
        var dispatcher = Dispatcher.CurrentDispatcher;
        _wait = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) =>
        {
            Arm();
            dispatcher.BeginInvoke(_debounce.Start);
        }, null, Timeout.Infinite, executeOnlyOnce: false);
        Reload();
    }

    public void Stop()
    {
        _wait?.Unregister(null);
        _wait = null;
        _signal?.Dispose();
        _signal = null;
        _key?.Dispose();
        _key = null;
        _debounce.Stop();
        Set(PrivacyState.None);
    }

    public void Dispose() => Stop();

    private void Reload()
    {
        try
        {
            Set(new PrivacyState(Names("microphone"), Names("webcam")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // si tiene l'ultimo stato letto
        }
    }

    private void Set(PrivacyState state)
    {
        if (state.Microphone.SequenceEqual(Current.Microphone) && state.Camera.SequenceEqual(Current.Camera))
            return;
        Current = state;
        Changed?.Invoke(state);
    }

    private string[] Names(string capability)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{ConsentStore}\{capability}");
        if (key is null)
            return [];
        var uses = Read(key, packaged: true);
        using (var nonPackaged = key.OpenSubKey("NonPackaged"))
        {
            if (nonPackaged is not null)
                uses = uses.Concat(Read(nonPackaged, packaged: false)).ToList();
        }
        return PrivacyUsage.InUse(uses)
            .Where(IsRunning)
            .Select(Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static List<CapabilityUse> Read(RegistryKey parent, bool packaged)
    {
        var uses = new List<CapabilityUse>();
        foreach (var name in parent.GetSubKeyNames())
        {
            if (packaged && name == "NonPackaged")
                continue;
            using var app = parent.OpenSubKey(name);
            if (app?.GetValue("LastUsedTimeStart") is long start)
                uses.Add(new CapabilityUse(name, start, app.GetValue("LastUsedTimeStop") as long? ?? 0, packaged));
        }
        return uses;
    }

    private static bool IsRunning(CapabilityUse use)
    {
        if (PrivacyUsage.ExePath(use) is not { } exe)
            return true;
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
        foreach (var p in processes)
            p.Dispose();
        return processes.Length > 0;
    }

    private string Name(CapabilityUse use)
    {
        if (PrivacyUsage.ExePath(use) is { } exe)
        {
            try
            {
                if (FileVersionInfo.GetVersionInfo(exe).FileDescription is { Length: > 0 } description)
                    return description;
            }
            catch (FileNotFoundException)
            {
                // si usa il nome del file
            }
        }
        else if (PackagedAppName(use.Key) is { } packagedName)
            return packagedName;
        return PrivacyUsage.FallbackName(use);
    }

    private bool Arm() =>
        _key is not null && _signal is not null &&
        RegNotifyChangeKeyValue(_key.Handle, true, RegNotifyChangeLastSet | RegNotifyThreadAgnostic,
            _signal.SafeWaitHandle, true) == 0;

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        uint filter, SafeWaitHandle eventHandle, [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
}
