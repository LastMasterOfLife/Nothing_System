using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using NothingShell.Core.Dev;

namespace NothingShell.App.Services;

/// <summary>
/// Porte TCP in ascolto e server di sviluppo (docs/12-sviluppo.md): <c>GetExtendedTcpTable</c> ogni 3 s, fuori dal
/// thread della UI. Nome e sessione dei processi restano in cache finché il PID esiste.
/// </summary>
public sealed class PortWatcher : IDisposable
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const int Ipv4RowSize = 24;
    private const int Ipv6RowSize = 56;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<int, PortOwner?> _owners = [];
    private CancellationTokenSource? _stop;
    private bool _first = true;

    public IReadOnlyList<DevServer> Servers { get; private set; } = [];

    /// <summary>Elenco cambiato (sul thread della UI).</summary>
    public event Action<IReadOnlyList<DevServer>>? Changed;

    /// <summary>Server nuovi dall'ultima lettura (non alla prima).</summary>
    public event Action<IReadOnlyList<DevServer>>? Started;

    public void Start()
    {
        if (_stop is not null)
            return;
        _stop = new CancellationTokenSource();
        _first = true;
        var token = _stop.Token;
        _ = Task.Run(() => LoopAsync(token), token);
    }

    public void Stop()
    {
        _stop?.Cancel();
        _stop?.Dispose();
        _stop = null;
        if (Servers.Count == 0)
            return;
        Servers = [];
        Changed?.Invoke(Servers);
    }

    public void Dispose() => Stop();

    /// <summary>Termina il processo di un server; false se serve l'amministratore o non c'è più.</summary>
    public static bool Kill(DevServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        try
        {
            using var process = Process.GetProcessById(server.Pid);
            process.Kill();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return ex is ArgumentException; // già chiuso: va bene lo stesso
        }
    }

    /// <summary>Tutte le porte in ascolto con il proprietario (per <c>--dev-check</c>).</summary>
    public static IReadOnlyList<(PortListener Listener, PortOwner? Owner)> ReadAll()
    {
        var owners = new Dictionary<int, PortOwner?>();
        return Listeners().Select(l => (l, Owner(l.Pid, owners))).ToArray();
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var listeners = Listeners();
            var alive = listeners.Select(l => l.Pid).ToHashSet();
            foreach (var gone in _owners.Keys.Where(pid => !alive.Contains(pid)).ToArray())
                _owners.Remove(gone);
            var servers = DevServers.Select(listeners, pid => Owner(pid, _owners));
            await _dispatcher.InvokeAsync(() => Publish(servers));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Publish(IReadOnlyList<DevServer> servers)
    {
        if (_stop is null)
            return; // fermato nel frattempo
        var before = Servers;
        var first = _first;
        _first = false;
        if (DevServers.Same(before, servers))
            return;
        Servers = servers;
        Changed?.Invoke(servers);
        if (!first && DevServers.Added(before, servers) is { Count: > 0 } added)
            Started?.Invoke(added);
    }

    private static PortOwner? Owner(int pid, Dictionary<int, PortOwner?> cache)
    {
        if (cache.TryGetValue(pid, out var known))
            return known;
        PortOwner? owner = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            owner = new PortOwner(process.ProcessName, process.SessionId == 0);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // processo chiuso o protetto
        }
        cache[pid] = owner;
        return owner;
    }

    private static List<PortListener> Listeners()
    {
        var listeners = new List<PortListener>();
        Read(AfInet, Ipv4RowSize, portOffset: 8, pidOffset: 20, ipv6: false, listeners);
        Read(AfInet6, Ipv6RowSize, portOffset: 20, pidOffset: 52, ipv6: true, listeners);
        return listeners;
    }

    /// <summary>
    /// Righe <c>MIB_TCPROW_OWNER_PID</c> (stato, indirizzo, porta, …, PID) e <c>MIB_TCP6ROW_OWNER_PID</c>
    /// (indirizzo 16 byte, scope, porta, …, stato, PID); la porta è in ordine di rete nei 16 bit bassi.
    /// </summary>
    private static void Read(int family, int rowSize, int portOffset, int pidOffset, bool ipv6, List<PortListener> into)
    {
        var size = 0;
        _ = GetExtendedTcpTable(0, ref size, false, family, TcpTableOwnerPidListener, 0);
        for (var attempt = 0; attempt < 3 && size > 0; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var result = GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidListener, 0);
                if (result == 122) // ERROR_INSUFFICIENT_BUFFER: la tabella è cresciuta
                    continue;
                if (result != 0)
                    return;
                var count = Marshal.ReadInt32(buffer);
                for (var i = 0; i < count; i++)
                {
                    var row = buffer + 4 + (i * rowSize);
                    var raw = Marshal.ReadInt32(row + portOffset);
                    var port = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                    into.Add(new PortListener(port, Marshal.ReadInt32(row + pidOffset), ipv6));
                }
                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
}
