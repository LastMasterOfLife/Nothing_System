using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using NothingShell.App.Interop;
using NothingShell.Core.Bar;

namespace NothingShell.App.Bar;

/// <summary>
/// App appuntate alla taskbar di Windows, nel loro ordine (docs/05-bar.md). Si leggono da
/// <c>Taskband\Favorites</c> (che include anche le app dello Store, senza collegamento) e si
/// rileggono quando Windows le cambia, senza polling.
/// </summary>
public sealed class TaskbarPins : IDisposable
{
    private const string TaskbandKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband";
    private const uint RegNotifyChangeLastSet = 0x4;
    private const uint RegNotifyThreadAgnostic = 0x10000000;

    private static readonly string PinnedFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private RegistryKey? _key;
    private AutoResetEvent? _signal;
    private RegisteredWaitHandle? _wait;

    public TaskbarPins()
    {
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Reload();
        };
    }

    public IReadOnlyList<PinnedApp> Pins { get; private set; } = [];

    public event Action? Changed;

    public void Start()
    {
        Reload();
        _key = Registry.CurrentUser.OpenSubKey(TaskbandKey);
        if (_key is null)
            return;
        _signal = new AutoResetEvent(false);
        if (!Arm())
            return;
        var dispatcher = Dispatcher.CurrentDispatcher;
        // Explorer scrive più volte di fila quando si appunta un'app: si rilegge una volta, 300 ms dopo l'ultima.
        _wait = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) =>
        {
            Arm();
            dispatcher.BeginInvoke(_debounce.Start);
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>App appuntate in questo momento (anche senza <see cref="Start"/>, per la diagnostica).</summary>
    public static IReadOnlyList<PinnedApp> Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(TaskbandKey);
        var items = key?.GetValue("Favorites") is byte[] blob
            ? TaskbandFavorites.Split(blob).Select(ShellNative.ItemFromIdList)
            : ReadFolder();

        var pins = new List<PinnedApp>();
        foreach (var item in items)
        {
            if (item is null)
                continue;
            if (Describe(item) is { } pin)
                pins.Add(pin);
            Marshal.ReleaseComObject(item);
        }
        return pins;
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _signal?.Dispose();
        _key?.Dispose();
        _debounce.Stop();
    }

    private void Reload()
    {
        IReadOnlyList<PinnedApp> pins;
        try
        {
            pins = Read();
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        {
            return; // si tengono le ultime app lette
        }
        if (pins.SequenceEqual(Pins))
            return;
        Pins = pins;
        Changed?.Invoke();
    }

    private bool Arm() =>
        _key is not null && _signal is not null &&
        RegNotifyChangeKeyValue(_key.Handle, false, RegNotifyChangeLastSet | RegNotifyThreadAgnostic,
            _signal.SafeWaitHandle, true) == 0;

    /// <summary>Senza il valore del registro: i collegamenti della cartella, in ordine alfabetico.</summary>
    private static IEnumerable<ShellNative.IShellItem2?> ReadFolder() =>
        Directory.Exists(PinnedFolder)
            ? Directory.EnumerateFiles(PinnedFolder, "*.lnk").Order(StringComparer.OrdinalIgnoreCase).Select(ShellNative.ItemFromPath)
            : [];

    private static PinnedApp? Describe(ShellNative.IShellItem2 item)
    {
        var name = ShellNative.DisplayName(item, ShellNative.SigdnNormalDisplay);
        var file = ShellNative.DisplayName(item, ShellNative.SigdnFileSysPath);
        var target = ShellNative.GetString(item, ShellNative.LinkTargetParsingPath);
        var exe = target?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true ? target : null;
        var appId = ShellNative.GetString(item, ShellNative.AppUserModelId)
                    ?? (exe is null ? null : AppIds.Default(exe, ShellNative.KnownFolders));
        if (name is null || appId is null)
            return null;

        var hasArguments = ShellNative.GetString(item, ShellNative.LinkArguments) is not null;
        return new PinnedApp(appId, name, file ?? $"shell:AppsFolder\\{appId}", exe, hasArguments);
    }

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        uint filter, SafeWaitHandle eventHandle, [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
}
