using System.Runtime.InteropServices;
using System.Windows.Threading;
using Windows.Devices.Enumeration;

namespace NothingShell.App.Services;

/// <summary>
/// Cuffie e altoparlanti Bluetooth che si collegano o scollegano (docs/10-live-activities.md). Si osservano i
/// dispositivi associati e la loro proprietà <c>IsConnected</c>; contano solo quelli audio (classe Bluetooth
/// "Audio/Video"). La batteria è quella che Windows mostra nelle impostazioni (profilo vivavoce), se c'è.
/// </summary>
public sealed class BluetoothAudioWatcher : IDisposable
{
    private const string IsConnected = "System.Devices.Aep.IsConnected";
    private const string ContainerId = "System.Devices.Aep.ContainerId";
    private const string MajorClass = "System.Devices.Aep.Bluetooth.Cod.Major";
    private const string BatteryLevel = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string PairedBluetooth = "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";
    private const ushort AudioVideo = 4;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<string, Device> _devices = [];
    private DeviceWatcher? _watcher;
    private bool _primed;

    /// <summary>Dispositivo audio collegato: nome e batteria in percentuale (null se non nota), sul thread UI.</summary>
    public event Action<string, int?>? Connected;

    public event Action<string>? Disconnected;

    public void Start()
    {
        if (_watcher is not null)
            return;
        try
        {
            _watcher = DeviceInformation.CreateWatcher(PairedBluetooth, [IsConnected, ContainerId, MajorClass],
                DeviceInformationKind.AssociationEndpoint);
            _watcher.Added += (_, info) => _dispatcher.BeginInvoke(() => OnAdded(info));
            _watcher.Updated += (_, update) => _dispatcher.BeginInvoke(() => OnUpdated(update));
            _watcher.Removed += (_, update) => _dispatcher.BeginInvoke(() => _devices.Remove(update.Id));
            _watcher.EnumerationCompleted += (_, _) => _dispatcher.BeginInvoke(() => _primed = true);
            _watcher.Start();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            _watcher = null; // niente Bluetooth su questo PC
        }
    }

    public void Stop()
    {
        if (_watcher is null)
            return;
        try
        {
            if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                _watcher.Stop();
        }
        catch (COMException)
        {
            // già fermo
        }
        _watcher = null;
        _devices.Clear();
        _primed = false;
    }

    public void Dispose() => Stop();

    /// <summary>Per <c>--live-check</c>: i dispositivi associati con stato, classe e batteria.</summary>
    public static async Task<IReadOnlyList<string>> DescribeAsync()
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(PairedBluetooth, [IsConnected, ContainerId, MajorClass],
                DeviceInformationKind.AssociationEndpoint);
            var lines = new List<string>();
            foreach (var d in devices)
            {
                var battery = Get<Guid>(d.Properties, ContainerId) is { } container ? await ReadBatteryAsync(container) : null;
                lines.Add($"{d.Name}: collegato={Get<bool>(d.Properties, IsConnected)} audio={Get<ushort>(d.Properties, MajorClass) == AudioVideo} batteria={battery}");
            }
            return lines.Count > 0 ? lines : ["nessun dispositivo associato"];
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            return [$"non disponibile: {ex.Message}"];
        }
    }

    private void OnAdded(DeviceInformation info)
    {
        var device = new Device(
            info.Name,
            Get<bool>(info.Properties, IsConnected) == true,
            Get<Guid>(info.Properties, ContainerId),
            Get<ushort>(info.Properties, MajorClass) == AudioVideo);
        _devices[info.Id] = device;
        if (_primed && device.Connected)
            _ = AnnounceAsync(device); // associato e collegato in un colpo solo
    }

    private void OnUpdated(DeviceInformationUpdate update)
    {
        if (!_devices.TryGetValue(update.Id, out var device) || !update.Properties.TryGetValue(IsConnected, out var value) || value is not bool connected)
            return;
        if (connected == device.Connected)
            return;
        _devices[update.Id] = device with { Connected = connected };
        if (!device.IsAudio || !_primed)
            return;
        if (connected)
            _ = AnnounceAsync(device);
        else
            Disconnected?.Invoke(device.Name);
    }

    /// <summary>La batteria arriva qualche secondo dopo il collegamento: si attende fino a 4 s.</summary>
    private async Task AnnounceAsync(Device device)
    {
        if (!device.IsAudio)
            return;
        int? battery = null;
        for (var attempt = 0; attempt < 4 && battery is null && device.Container is { } container; attempt++)
        {
            battery = await ReadBatteryAsync(container);
            if (battery is null)
                await Task.Delay(1000);
        }
        Connected?.Invoke(device.Name, battery);
    }

    private static async Task<int?> ReadBatteryAsync(Guid container)
    {
        try
        {
            var nodes = await DeviceInformation.FindAllAsync($"System.Devices.ContainerId:=\"{{{container}}}\"", [BatteryLevel],
                DeviceInformationKind.Device);
            var level = nodes.Select(n => Get<byte>(n.Properties, BatteryLevel)).FirstOrDefault(b => b is > 0 and <= 100);
            return level is { } percent ? percent : null;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            return null;
        }
    }

    private static T? Get<T>(IReadOnlyDictionary<string, object> properties, string key) where T : struct =>
        properties.TryGetValue(key, out var value) && value is T typed ? typed : null;

    private sealed record Device(string Name, bool Connected, Guid? Container, bool IsAudio);
}
