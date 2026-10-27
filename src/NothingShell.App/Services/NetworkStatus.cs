using System.Net.NetworkInformation;
using System.Windows.Threading;
using NothingShell.Core.Bar;

namespace NothingShell.App.Services;

/// <summary>Tipo di connessione attiva; notifica sul thread UI quando cambia.</summary>
public sealed class NetworkStatus
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    public NetworkKind Current { get; private set; } = Read();

    public event Action<NetworkKind>? Changed;

    public void Start()
    {
        NetworkChange.NetworkAddressChanged += (_, _) => Schedule();
        NetworkChange.NetworkAvailabilityChanged += (_, _) => Schedule();
    }

    private void Schedule() => _dispatcher.BeginInvoke(() =>
    {
        var kind = Read();
        if (kind == Current)
            return;
        Current = kind;
        Changed?.Invoke(kind);
    });

    private static NetworkKind Read()
    {
        if (!NetworkInterface.GetIsNetworkAvailable())
            return NetworkKind.Offline;

        var up = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .ToArray();
        if (up.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
            return NetworkKind.Wifi;

        // Gli adattatori virtuali (Hyper-V, VPN) sono "Ethernet" ma senza gateway: contano solo quelli veri.
        var wired = up.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                                && n.GetIPProperties().GatewayAddresses.Count > 0);
        return wired ? NetworkKind.Ethernet : NetworkKind.Offline;
    }
}
