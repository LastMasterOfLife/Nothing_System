using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace NothingShell.App.Services;

/// <summary>
/// Wi-Fi e Bluetooth accesi e spenti come dalle impostazioni rapide di Windows (API <c>Windows.Devices.Radios</c>,
/// aperta anche alle app desktop). Senza scheda o senza permesso il riquadro del pannello resta spento.
/// </summary>
public sealed class RadioSwitch
{
    private readonly Radio _radio;
    private readonly Dispatcher _dispatcher;

    private RadioSwitch(Radio radio, Dispatcher dispatcher)
    {
        _radio = radio;
        _dispatcher = dispatcher;
        _radio.StateChanged += (_, _) => _dispatcher.BeginInvoke(() => Changed?.Invoke());
    }

    /// <summary>Lo stato è cambiato (anche da Windows), sul thread UI.</summary>
    public event Action? Changed;

    public bool IsOn => _radio.State == RadioState.On;

    /// <summary>Spenta dall'hardware o dal firmware: non si può accendere da qui.</summary>
    public bool IsDisabled => _radio.State == RadioState.Disabled;

    public static async Task<(RadioSwitch? WiFi, RadioSwitch? Bluetooth)> LoadAsync()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed)
                return (null, null);
            var radios = await Radio.GetRadiosAsync();
            var wifi = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
            var bluetooth = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            return (wifi is null ? null : new RadioSwitch(wifi, dispatcher), bluetooth is null ? null : new RadioSwitch(bluetooth, dispatcher));
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            return (null, null);
        }
    }

    /// <returns>False se Windows non l'ha permesso.</returns>
    public async Task<bool> ToggleAsync()
    {
        try
        {
            return await _radio.SetStateAsync(IsOn ? RadioState.Off : RadioState.On) == RadioAccessStatus.Allowed;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Nome della rete Wi-Fi collegata; null se non c'è o se Windows non lo dà (serve il permesso Posizione).</summary>
    public static string? ConnectedNetwork()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is not { IsWlanConnectionProfile: true })
                return null;
            var ssid = profile.WlanConnectionProfileDetails?.GetConnectedSsid();
            return string.IsNullOrWhiteSpace(ssid) ? profile.ProfileName : ssid;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Luminosità dello schermo del portatile via WMI (<c>WmiMonitorBrightness</c>, <c>WmiSetBrightness</c>); i
/// monitor esterni non la espongono a Windows. Le chiamate WMI sono lente: vanno fatte fuori dal thread UI.
/// </summary>
public static class ScreenBrightness
{
    /// <summary>0–100; null senza uno schermo regolabile da Windows.</summary>
    public static int? Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\wmi"),
                new ObjectQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=True"));
            using var results = searcher.Get();
            foreach (var item in results)
                using (item)
                    if (item["CurrentBrightness"] is byte level)
                        return level;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            // nessuno schermo con luminosità regolabile
        }
        return null;
    }

    public static void Set(int level)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\wmi"),
                new ObjectQuery("SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=True"));
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
                using (item)
                    item.InvokeMethod("WmiSetBrightness", [1u, (byte)Math.Clamp(level, 0, 100)]);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            // schermo scollegato nel frattempo
        }
    }
}
