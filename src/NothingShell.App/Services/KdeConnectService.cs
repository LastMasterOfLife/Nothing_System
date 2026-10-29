using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using NothingShell.App.Interop;
using NothingShell.Core.Notch;
using NothingShell.Core.Phone;
using NothingShell.Core.Storage;

namespace NothingShell.App.Services;

/// <summary>Cosa impostazioni e pannello rapido chiedono al collegamento diretto col telefono.</summary>
public interface IPhoneActions
{
    string Status { get; }

    IReadOnlyList<PhoneDevice> Devices { get; }

    /// <summary>Telefono associato e collegato adesso, se c'è.</summary>
    PhoneDevice? Current { get; }

    event Action? Changed;

    /// <summary>Annuncia la shell sulla rete (o al solo <paramref name="address"/>, se la rete blocca i broadcast).</summary>
    void Discover(IPAddress? address = null);

    void RequestPair(string deviceId);

    void AcceptPair(string deviceId);

    void RejectPair(string deviceId);

    void Unpair(string deviceId);

    void FindPhone();

    void SendClipboard(string text);

    /// <returns>Null se il file è arrivato, altrimenti il motivo.</returns>
    Task<string?> SendFileAsync(string path);

    /// <summary>Regole del firewall di Windows per le porte 1714–1764 (chiede l'elevazione).</summary>
    void AllowFirewall();
}

/// <summary>
/// Collegamento diretto col telefono Android tramite il protocollo KDE Connect (docs/13-telefono.md): scoperta
/// in UDP sulla 1716, connessione TCP e poi TLS con certificati autofirmati, associazione confermata sui due
/// lati. Da un telefono non associato si accettano solo le richieste di associazione. Le connessioni vivono sui
/// thread del pool; lo stato (collegamenti, associazioni, batterie) si tocca solo sul thread UI.
/// </summary>
public sealed class KdeConnectService : IPhoneActions, IDisposable
{
    private const string CredentialTarget = "NothingShell:KdeConnect";
    private const int MaxPacketBytes = 4 * 1024 * 1024; // appunti lunghi, ma non illimitati
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PayloadTimeout = TimeSpan.FromSeconds(60);

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly PhoneTrustStore _store = new(PhoneTrustStore.DefaultPath);
    private readonly Dictionary<string, Link> _links = [];
    private readonly Dictionary<string, PhoneBattery> _batteries = [];
    private readonly Dictionary<string, Pending> _pending = [];
    private readonly HashSet<string> _connecting = [];
    private readonly DispatcherTimer _pairTimeout = new() { Interval = TimeSpan.FromSeconds(1) };
    private volatile PhoneTrust _trust = new(KdeConnect.NewDeviceId(), []);
    private X509Certificate2? _certificate;
    private CancellationTokenSource? _stop;
    private UdpClient? _udp;
    private TcpListener? _tcp;
    private int _tcpPort = KdeConnect.Port;

    public KdeConnectService()
    {
        _pairTimeout.Tick += (_, _) => ExpirePairing();
    }

    public string Status { get; private set; } = "Spento";

    public IReadOnlyList<PhoneDevice> Devices
    {
        get
        {
            var ids = _trust.Phones.Select(p => p.DeviceId).Union(_links.Keys);
            return ids.Select(Describe).OfType<PhoneDevice>()
                .OrderByDescending(d => d.Connected).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
    }

    public PhoneDevice? Current => Devices.FirstOrDefault(d => d.Connected && d.Paired);

    public event Action? Changed;

    /// <summary>Il telefono chiede l'associazione: va confermata con il codice mostrato su entrambi.</summary>
    public event Action<PhoneDevice>? PairRequested;

    /// <summary>Telefono associato appena collegato (o appena associato).</summary>
    public event Action<PhoneDevice>? PhoneConnected;

    public event Action<PhoneDevice>? BatteryLow;

    /// <summary>Chiamate e notifiche dal telefono (le notifiche già presenti al collegamento no).</summary>
    public event Action<PhoneEvent>? EventArrived;

    public event Action<string>? ClipboardReceived;

    /// <summary>File ricevuto e salvato nella cartella Download.</summary>
    public event Action<string>? FileReceived;

    public event Action<Uri>? UrlReceived;

    public void Start()
    {
        if (_stop is not null)
            return;
        try
        {
            _trust = _store.Load();
            _store.Save(_trust); // la prima volta fissa l'identità
            _certificate = LoadCertificate(_trust.DeviceId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            SetStatus($"Certificato non disponibile: {ex.Message}");
            return;
        }
        _stop = new CancellationTokenSource();
        _tcp = Listen(KdeConnect.Port, KdeConnect.PortMax);
        if (_tcp is null)
        {
            SetStatus("Nessuna porta TCP libera fra 1716 e 1764");
            Stop();
            return;
        }
        _tcpPort = ((IPEndPoint)_tcp.LocalEndpoint).Port;
        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, KdeConnect.Port));
            _ = ReceiveDiscoveryAsync(_udp, _stop.Token);
        }
        catch (SocketException)
        {
            // un'altra app KDE Connect ascolta già: si annuncia comunque, il telefono si collegherà in TCP
            _udp?.Dispose();
            _udp = null;
        }
        _ = AcceptAsync(_tcp, _stop.Token);
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        _pairTimeout.Start();
        SetStatus($"In attesa del telefono sulla rete locale (porta {_tcpPort})");
        Discover();
    }

    public void Stop()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _pairTimeout.Stop();
        _stop?.Cancel();
        _stop?.Dispose();
        _stop = null;
        _udp?.Dispose();
        _udp = null;
        _tcp?.Stop();
        _tcp = null;
        foreach (var link in _links.Values.ToArray())
            link.Dispose();
        _links.Clear();
        _pending.Clear();
        _connecting.Clear();
        _certificate?.Dispose();
        _certificate = null;
        if (Status != "Spento")
            SetStatus("Spento");
    }

    public void Dispose() => Stop();

    public void Discover(IPAddress? address = null)
    {
        if (_stop is null)
            return;
        var bytes = Encoding.UTF8.GetBytes(KdeConnect.IdentityPacket(_trust.DeviceId, Environment.MachineName, _tcpPort).Serialize());
        var targets = address is not null ? [address] : BroadcastAddresses().Append(IPAddress.Broadcast).Distinct().ToArray();
        using var sender = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        foreach (var target in targets)
        {
            try
            {
                sender.Send(bytes, bytes.Length, new IPEndPoint(target, KdeConnect.Port));
            }
            catch (SocketException)
            {
                // interfaccia senza rete: le altre bastano
            }
        }
    }

    public void RequestPair(string deviceId)
    {
        if (!_links.TryGetValue(deviceId, out var link))
            return;
        var now = DateTimeOffset.UtcNow;
        _pending[deviceId] = new Pending(ByPhone: false, now.ToUnixTimeSeconds(), now);
        _ = link.SendAsync(KdeConnect.PairPacket(true, now));
        RaiseChanged();
    }

    public void AcceptPair(string deviceId)
    {
        if (!_links.TryGetValue(deviceId, out var link) || !_pending.Remove(deviceId, out var pending) || !pending.ByPhone)
            return;
        Trust(link);
        _ = link.SendAsync(KdeConnect.PairPacket(true));
        RaiseChanged();
        if (Describe(deviceId) is { } device)
            PhoneConnected?.Invoke(device);
        _ = link.SendAsync(new KdePacket(KdeConnect.BatteryRequest, new() { ["request"] = true }));
    }

    public void RejectPair(string deviceId)
    {
        _pending.Remove(deviceId);
        if (_links.TryGetValue(deviceId, out var link))
            _ = link.SendAsync(KdeConnect.PairPacket(false));
        RaiseChanged();
    }

    public void Unpair(string deviceId)
    {
        _pending.Remove(deviceId);
        if (_links.TryGetValue(deviceId, out var link))
            _ = link.SendAsync(KdeConnect.PairPacket(false));
        Untrust(deviceId);
        RaiseChanged();
    }

    public void FindPhone()
    {
        if (CurrentLink() is { } link)
            _ = link.SendAsync(new KdePacket(KdeConnect.FindMyPhone, new JsonObject()));
    }

    /// <summary>Toglie la suoneria alla chiamata in arrivo.</summary>
    public void MuteRinger()
    {
        if (CurrentLink() is { } link)
            _ = link.SendAsync(new KdePacket(KdeConnect.TelephonyMute, new JsonObject()));
    }

    public void SendClipboard(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 0 && CurrentLink() is { } link)
            _ = link.SendAsync(KdeConnect.ClipboardPacket(text));
    }

    public async Task<string?> SendFileAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (CurrentLink() is not { } link)
            return "Nessun telefono associato collegato";
        var info = new FileInfo(path);
        if (!info.Exists)
            return "File non trovato";
        var listener = Listen(KdeConnect.PayloadPortMin, KdeConnect.PortMax);
        if (listener is null)
            return "Nessuna porta libera per il trasferimento";
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await link.SendAsync(KdeConnect.FilePacket(info.Name, info.Length, port, info.LastWriteTimeUtc));
            using var timeout = new CancellationTokenSource(PayloadTimeout);
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(ServerOptions(), timeout.Token);
            if (!SameCertificate(ssl.RemoteCertificate, link.PeerCertificate))
                return "Il telefono ha presentato un certificato diverso";
            await using var file = File.OpenRead(path);
            await file.CopyToAsync(ssl, CancellationToken.None);
            await ssl.FlushAsync(CancellationToken.None);
            return null;
        }
        catch (OperationCanceledException)
        {
            return "Il telefono non ha ritirato il file (KDE Connect aperto e plugin Condividi attivo?)";
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException)
        {
            return $"Trasferimento interrotto: {ex.Message}";
        }
        finally
        {
            listener.Stop();
            listener.Dispose();
        }
    }

    public void AllowFirewall()
    {
        if (Environment.ProcessPath is not { } exe)
            return;
        const string rule = "Nothing Shell - telefono";
        var command = $"/c netsh advfirewall firewall delete rule name=\"{rule}\" & " +
                      $"netsh advfirewall firewall add rule name=\"{rule}\" dir=in action=allow program=\"{exe}\" protocol=TCP localport=1714-1764 profile=private & " +
                      $"netsh advfirewall firewall add rule name=\"{rule}\" dir=in action=allow program=\"{exe}\" protocol=UDP localport=1714-1764 profile=private";
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", command) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })?.Dispose();
        }
        catch (Win32Exception)
        {
            // elevazione annullata
        }
    }

    /// <summary>Diagnostica (<c>--phone-check</c>): identità, porte e telefoni associati, senza collegarsi.</summary>
    public static IEnumerable<string> Diagnose()
    {
        var trust = new PhoneTrustStore(PhoneTrustStore.DefaultPath).Load();
        var names = string.Join(", ", trust.Phones.Select(p => p.Name));
        yield return $"kde connect: id {trust.DeviceId}, associati: {(names.Length > 0 ? names : "nessuno")}";
        foreach (var port in new[] { KdeConnect.Port })
        {
            var udp = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Any(e => e.Port == port);
            var tcp = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);
            yield return $"porta {port}: udp {(udp ? "in ascolto" : "libera")}, tcp {(tcp ? "in ascolto" : "libera")}";
        }
        yield return $"broadcast: {string.Join(", ", BroadcastAddresses())}";
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() => Discover());

    private async Task ReceiveDiscoveryAsync(UdpClient udp, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(stop);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
            {
                if (stop.IsCancellationRequested)
                    return;
                continue; // ICMP "porta irraggiungibile" di un invio precedente
            }
            if (result.Buffer.Length > MaxPacketBytes
                || KdePacket.Parse(Encoding.UTF8.GetString(result.Buffer)) is not { } packet
                || KdeConnect.ReadIdentity(packet) is not { } identity
                || identity.DeviceId == _trust.DeviceId)
                continue;
            var address = result.RemoteEndPoint.Address;
            _ = _dispatcher.BeginInvoke(() =>
            {
                if (_stop is null || _links.ContainsKey(identity.DeviceId) || !_connecting.Add(identity.DeviceId))
                    return;
                _ = ConnectAsync(address, identity, _stop.Token);
            });
        }
    }

    /// <summary>Abbiamo sentito l'annuncio: ci colleghiamo noi, mandiamo l'identità e facciamo da server TLS.</summary>
    private async Task ConnectAsync(IPAddress address, KdeIdentity identity, CancellationToken stop)
    {
        TcpClient? client = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(HandshakeTimeout);
            client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(address, identity.TcpPort, timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(OwnIdentity().Serialize()), timeout.Token);
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(ServerOptions(), timeout.Token);
            await CompleteAsync(client, ssl, identity, address, timeout.Token);
            client = null; // ora appartiene al collegamento
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException or InvalidDataException)
        {
            // telefono sparito o TLS rifiutato: riproverà al prossimo annuncio
        }
        finally
        {
            client?.Dispose();
            _ = _dispatcher.BeginInvoke(() => _connecting.Remove(identity.DeviceId));
        }
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            _ = HandleIncomingAsync(client, stop);
        }
    }

    /// <summary>Il telefono ha sentito il nostro annuncio: manda l'identità in chiaro, poi noi facciamo da client TLS.</summary>
    private async Task HandleIncomingAsync(TcpClient client, CancellationToken stop)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(HandshakeTimeout);
            var stream = client.GetStream();
            // byte per byte: subito dopo la riga comincia il TLS, non va letto nulla di più
            var line = await PacketReader.ReadPlainLineAsync(stream, 64 * 1024, timeout.Token);
            if (line is null || KdePacket.Parse(line) is not { } packet || KdeConnect.ReadIdentity(packet) is not { } identity
                || identity.DeviceId == _trust.DeviceId)
            {
                client.Dispose();
                return;
            }
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(ClientOptions(identity.DeviceId), timeout.Token);
            await CompleteAsync(client, ssl, identity, ((IPEndPoint)client.Client.RemoteEndPoint!).Address, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException or InvalidDataException)
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// TLS fatto: dalla versione 8 le identità si ripetono cifrate. Un telefono associato deve presentare lo stesso
    /// certificato della prima volta, e il certificato deve portare il suo deviceId.
    /// </summary>
    private async Task CompleteAsync(TcpClient client, SslStream ssl, KdeIdentity identity, IPAddress address, CancellationToken timeout)
    {
        if (ssl.RemoteCertificate is null)
            throw new AuthenticationException("certificato mancante");
        var certificate = X509CertificateLoader.LoadCertificate(ssl.RemoteCertificate.GetRawCertData());
        var reader = new PacketReader(ssl, MaxPacketBytes);
        if (identity.ProtocolVersion >= 8)
        {
            await ssl.WriteAsync(Encoding.UTF8.GetBytes(OwnIdentity().Serialize()), timeout);
            if (await reader.ReadLineAsync(timeout) is not { } line || KdePacket.Parse(line) is not { } packet
                || KdeConnect.ReadIdentity(packet) is not { } secure || secure.DeviceId != identity.DeviceId
                || certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false) != identity.DeviceId)
            {
                certificate.Dispose();
                throw new AuthenticationException("identità non corrispondente");
            }
            identity = secure;
        }
        if (_trust.Find(identity.DeviceId) is { } trusted && !SameCertificate(certificate, trusted.Certificate))
        {
            certificate.Dispose();
            _ = _dispatcher.BeginInvoke(() => SetStatus($"{identity.Name}: certificato cambiato. Dissocia e associa di nuovo."));
            throw new AuthenticationException("certificato cambiato");
        }
        var link = new Link(client, ssl, reader, identity, certificate, address);
        _ = _dispatcher.BeginInvoke(() => Register(link));
    }

    private void Register(Link link)
    {
        var id = link.Identity.DeviceId;
        if (_stop is null)
        {
            link.Dispose();
            return;
        }
        if (_links.Remove(id, out var previous))
            previous.Dispose();
        _links[id] = link;
        _ = ReadAsync(link);
        RaiseChanged();
        if (_trust.Find(id) is not null)
        {
            if (Describe(id) is { } device)
                PhoneConnected?.Invoke(device);
            _ = link.SendAsync(new KdePacket(KdeConnect.BatteryRequest, new() { ["request"] = true }));
        }
    }

    private async Task ReadAsync(Link link)
    {
        try
        {
            while (await link.Reader.ReadLineAsync(CancellationToken.None) is { } line)
            {
                if (KdePacket.Parse(line) is { } packet)
                    _ = _dispatcher.BeginInvoke(() => Handle(link, packet));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException or SocketException)
        {
            // collegamento chiuso
        }
        _ = _dispatcher.BeginInvoke(() =>
        {
            link.Dispose();
            if (_links.TryGetValue(link.Identity.DeviceId, out var current) && current == link)
            {
                _links.Remove(link.Identity.DeviceId);
                _pending.Remove(link.Identity.DeviceId);
                RaiseChanged();
            }
        });
    }

    private void Handle(Link link, KdePacket packet)
    {
        if (!_links.TryGetValue(link.Identity.DeviceId, out var current) || current != link)
            return;
        if (packet.Type == KdeConnect.Pair)
        {
            HandlePair(link, packet);
            return;
        }
        if (_trust.Find(link.Identity.DeviceId) is null)
            return; // da un telefono non associato valgono solo le richieste di associazione

        switch (packet.Type)
        {
            case KdeConnect.Battery when KdeConnect.ReadBattery(packet) is { } battery:
                var wasLow = _batteries.TryGetValue(link.Identity.DeviceId, out var old) && old.Low;
                _batteries[link.Identity.DeviceId] = battery;
                RaiseChanged();
                if (battery.Low && !battery.Charging && !wasLow && Describe(link.Identity.DeviceId) is { } device)
                    BatteryLow?.Invoke(device);
                break;
            case KdeConnect.Telephony or KdeConnect.Notification:
                if (KdeConnect.ReadEvent(packet, DateTimeOffset.Now) is { } phoneEvent)
                    EventArrived?.Invoke(phoneEvent);
                break;
            case KdeConnect.Clipboard or KdeConnect.ClipboardConnect:
                if (KdeConnect.ReadClipboard(packet) is { } text)
                    ClipboardReceived?.Invoke(text);
                break;
            case KdeConnect.Ping:
                EventArrived?.Invoke(new PhoneEvent(PhoneEventKind.Message, link.Identity.Name,
                    packet.Text("message") ?? "Ping", DateTimeOffset.Now));
                break;
            case KdeConnect.Share:
                HandleShare(link, packet);
                break;
        }
    }

    private void HandlePair(Link link, KdePacket packet)
    {
        var id = link.Identity.DeviceId;
        if (!packet.Flag("pair"))
        {
            _pending.Remove(id);
            Untrust(id); // rifiuto o dissociazione dal telefono
            RaiseChanged();
            return;
        }
        if (_pending.TryGetValue(id, out var ours) && !ours.ByPhone)
        {
            _pending.Remove(id);
            Trust(link);
            RaiseChanged();
            if (Describe(id) is { } device)
                PhoneConnected?.Invoke(device);
            _ = link.SendAsync(new KdePacket(KdeConnect.BatteryRequest, new() { ["request"] = true }));
            return;
        }
        if (_trust.Find(id) is not null)
        {
            _ = link.SendAsync(KdeConnect.PairPacket(true)); // il telefono ha perso l'associazione: confermiamo
            return;
        }
        var timestamp = packet.Whole("timestamp");
        if (link.Identity.ProtocolVersion >= 8 && !KdeConnect.IsFreshPairRequest(timestamp, DateTimeOffset.UtcNow))
        {
            _ = link.SendAsync(KdeConnect.PairPacket(false));
            return;
        }
        _pending[id] = new Pending(ByPhone: true, timestamp ?? 0, DateTimeOffset.UtcNow);
        RaiseChanged();
        if (Describe(id) is { } requesting)
            PairRequested?.Invoke(requesting);
    }

    private void HandleShare(Link link, KdePacket packet)
    {
        if (packet.PayloadSize is { } size && packet.PayloadPort is { } port)
        {
            _ = ReceiveFileAsync(link, KdeConnect.SafeFileName(packet.Text("filename")), size, port);
            return;
        }
        if (packet.Text("url") is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            UrlReceived?.Invoke(uri);
        else if (packet.Text("text") is { Length: > 0 } text)
            ClipboardReceived?.Invoke(text);
    }

    /// <summary>Il telefono apre una porta per il file: noi ci colleghiamo come client TLS e lo salviamo in Download.</summary>
    private async Task ReceiveFileAsync(Link link, string name, long size, int port)
    {
        var folder = ShellNative.DownloadsFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var target = UniquePath(folder, name);
        var temp = target + ".nsphone";
        try
        {
            using var timeout = new CancellationTokenSource(PayloadTimeout);
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(link.Address, port, timeout.Token);
            await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(ClientOptions(link.Identity.DeviceId), timeout.Token);
            if (!SameCertificate(ssl.RemoteCertificate, link.PeerCertificate))
                return;
            await using (var file = File.Create(temp))
            {
                var buffer = new byte[81920];
                var left = size;
                while (left > 0)
                {
                    var read = await ssl.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), CancellationToken.None);
                    if (read == 0)
                        throw new IOException("trasferimento interrotto");
                    await file.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None);
                    left -= read;
                }
            }
            File.Move(temp, target);
            _ = _dispatcher.BeginInvoke(() => FileReceived?.Invoke(target));
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
                // resta il file parziale
            }
        }
    }

    private static string UniquePath(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
        return path;
    }

    private void ExpirePairing()
    {
        var expired = _pending.Where(p => DateTimeOffset.UtcNow - p.Value.Started > KdeConnect.PairTimeout).Select(p => p.Key).ToArray();
        foreach (var id in expired)
            _pending.Remove(id);
        if (expired.Length > 0)
            RaiseChanged();
    }

    private void Trust(Link link)
    {
        _trust = _trust.With(new TrustedPhone(link.Identity.DeviceId, link.Identity.Name, Convert.ToBase64String(link.PeerCertificate.RawData)));
        _store.Save(_trust);
    }

    private void Untrust(string deviceId)
    {
        if (_trust.Find(deviceId) is null)
            return;
        _trust = _trust.Without(deviceId);
        _batteries.Remove(deviceId);
        _store.Save(_trust);
    }

    private PhoneDevice? Describe(string id)
    {
        _links.TryGetValue(id, out var link);
        var trusted = _trust.Find(id);
        if (link is null && trusted is null)
            return null;
        _pending.TryGetValue(id, out var pending);
        string? key = null;
        if (pending is not null && link is not null && _certificate is not null)
            key = KdeConnect.VerificationKey(_certificate.PublicKey.ExportSubjectPublicKeyInfo(),
                link.PeerCertificate.PublicKey.ExportSubjectPublicKeyInfo(), pending.Timestamp);
        return new PhoneDevice(id, link?.Identity.Name ?? trusted!.Name, link is not null, trusted is not null)
        {
            Battery = _batteries.TryGetValue(id, out var battery) ? battery : null,
            PairPending = pending is not null,
            PairRequestedByPhone = pending?.ByPhone == true,
            VerificationKey = key,
        };
    }

    private Link? CurrentLink() =>
        _links.Values.FirstOrDefault(l => _trust.Find(l.Identity.DeviceId) is not null);

    private KdePacket OwnIdentity() => KdeConnect.IdentityPacket(_trust.DeviceId, Environment.MachineName, _tcpPort);

    private void SetStatus(string status)
    {
        Status = status;
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();

    private SslServerAuthenticationOptions ServerOptions() => ServerOptions(_certificate!);

    private SslClientAuthenticationOptions ClientOptions(string peerId) => ClientOptions(_certificate!, peerId);

    private static SslServerAuthenticationOptions ServerOptions(X509Certificate2 certificate) => new()
    {
        ServerCertificate = certificate,
        ClientCertificateRequired = true,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        RemoteCertificateValidationCallback = AcceptSelfSigned,
    };

    private static SslClientAuthenticationOptions ClientOptions(X509Certificate2 certificate, string peerId) => new()
    {
        TargetHost = peerId,
        ClientCertificates = [certificate],
        LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        RemoteCertificateValidationCallback = AcceptSelfSigned,
    };

    /// <summary>
    /// I certificati sono autofirmati: la catena non dice nulla. Conta che ci sia, e che sia quello salvato
    /// all'associazione (controllato dopo, in <see cref="CompleteAsync"/>).
    /// </summary>
    private static bool AcceptSelfSigned(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        certificate is not null;

    private static bool SameCertificate(X509Certificate? presented, X509Certificate2 expected) =>
        presented is not null && presented.GetRawCertData().AsSpan().SequenceEqual(expected.RawData);

    private static bool SameCertificate(X509Certificate2 presented, string expectedBase64)
    {
        try
        {
            return presented.RawData.AsSpan().SequenceEqual(Convert.FromBase64String(expectedBase64));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static TcpListener? Listen(int first, int last)
    {
        for (var port = first; port <= last; port++)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            try
            {
                listener.Start();
                return listener;
            }
            catch (SocketException)
            {
                listener.Dispose();
            }
        }
        return null;
    }

    /// <summary>Broadcast di ogni rete IPv4 attiva: 255.255.255.255 da solo esce da una scheda sola.</summary>
    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                    continue;
                var address = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                yield return new IPAddress(address.Select((b, i) => (byte)(b | ~mask[i])).ToArray());
            }
        }
    }

    /// <summary>
    /// Certificato della shell: RSA 2048 autofirmato con CN = deviceId, come quelli di KDE Connect. Il PFX sta nella
    /// cartella dei dati, la sua password nel Gestore credenziali; se manca o non corrisponde se ne crea uno nuovo
    /// (i telefoni dovranno essere associati di nuovo).
    /// </summary>
    private static X509Certificate2 LoadCertificate(string deviceId)
    {
        var path = Path.Combine(AtomicFile.DataFolder, "phone.pfx");
        if (File.Exists(path) && CredentialStore.Read(CredentialTarget) is { } saved)
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(path, saved, X509KeyStorageFlags.UserKeySet);
                if (existing.GetNameInfo(X509NameType.SimpleName, forIssuer: false) == deviceId && existing.HasPrivateKey)
                    return existing;
                existing.Dispose();
            }
            catch (CryptographicException)
            {
                // password o file non validi: si rigenera
            }
        }

        var (pfx, password) = CreatePfx(deviceId);
        if (!CredentialStore.Write(CredentialTarget, "kdeconnect", password, "Nothing Shell: certificato del collegamento col telefono"))
            throw new CryptographicException("Gestore credenziali non disponibile");
        Directory.CreateDirectory(AtomicFile.DataFolder);
        File.WriteAllBytes(path, pfx);
        // SChannel non usa le chiavi effimere: la chiave va caricata in un contenitore dell'utente
        return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.UserKeySet);
    }

    private static (byte[] Pfx, string Password) CreatePfx(string deviceId)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={deviceId}, O=KDE, OU=Kde connect", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;
        using var created = request.CreateSelfSigned(now.AddYears(-1), now.AddYears(10));
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return (created.Export(X509ContentType.Pkcs12, password), password);
    }

    /// <summary>
    /// Diagnostica: negoziazione TLS in locale fra due certificati usa e getta, con le stesse opzioni del telefono
    /// (certificati autofirmati richiesti a entrambi). Non salva nulla.
    /// </summary>
    public static async Task<string> SelfTestAsync()
    {
        static X509Certificate2 Throwaway(string id)
        {
            var (pfx, password) = CreatePfx(id);
            return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.UserKeySet);
        }
        using var server = Throwaway(KdeConnect.NewDeviceId());
        using var client = Throwaway(KdeConnect.NewDeviceId());
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            using var timeout = new CancellationTokenSource(HandshakeTimeout);
            using var outgoing = new TcpClient();
            var accept = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await outgoing.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
            using var incoming = await accept;
            await using var serverSsl = new SslStream(incoming.GetStream());
            await using var clientSsl = new SslStream(outgoing.GetStream());
            await Task.WhenAll(
                serverSsl.AuthenticateAsServerAsync(ServerOptions(server), timeout.Token),
                clientSsl.AuthenticateAsClientAsync(ClientOptions(client, server.GetNameInfo(X509NameType.SimpleName, false)), timeout.Token));
            return SameCertificate(serverSsl.RemoteCertificate, client) && SameCertificate(clientSsl.RemoteCertificate, server)
                ? $"tls: ok ({clientSsl.SslProtocol}, certificati scambiati)"
                : "tls: negoziato, ma i certificati non arrivano";
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException or CryptographicException)
        {
            return $"tls: errore {ex.GetType().Name}: {ex.Message}";
        }
    }

    private sealed record Pending(bool ByPhone, long Timestamp, DateTimeOffset Started);

    /// <summary>Connessione TLS con un telefono; le scritture sono serializzate, la lettura è un ciclo solo.</summary>
    private sealed class Link(TcpClient client, SslStream stream, PacketReader reader, KdeIdentity identity, X509Certificate2 peerCertificate, IPAddress address) : IDisposable
    {
        private readonly SemaphoreSlim _write = new(1, 1);
        private bool _disposed;

        public KdeIdentity Identity { get; } = identity;

        public X509Certificate2 PeerCertificate { get; } = peerCertificate;

        public IPAddress Address { get; } = address;

        public PacketReader Reader { get; } = reader;

        public async Task SendAsync(KdePacket packet)
        {
            var bytes = Encoding.UTF8.GetBytes(packet.Serialize());
            try
            {
                await _write.WaitAsync();
                try
                {
                    await stream.WriteAsync(bytes);
                    await stream.FlushAsync();
                }
                finally
                {
                    _write.Release();
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                // il ciclo di lettura se ne accorge e chiude
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            stream.Dispose();
            client.Dispose();
            PeerCertificate.Dispose();
        }
    }
}

/// <summary>Righe terminate da <c>\n</c> con un limite di lunghezza: un pacchetto enorme chiude la connessione.</summary>
internal sealed class PacketReader(Stream stream, int maxBytes)
{
    private readonly byte[] _buffer = new byte[16 * 1024];
    private readonly ArrayBufferWriter<byte> _line = new();
    private int _start;
    private int _end;

    public async Task<string?> ReadLineAsync(CancellationToken cancellation)
    {
        while (true)
        {
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline >= 0)
            {
                _line.Write(_buffer.AsSpan(_start, newline - _start));
                _start = newline + 1;
                var text = Encoding.UTF8.GetString(_line.WrittenSpan);
                _line.ResetWrittenCount();
                if (text.Trim().Length > 0)
                    return text;
                continue;
            }
            _line.Write(_buffer.AsSpan(_start, _end - _start));
            if (_line.WrittenCount > maxBytes)
                throw new InvalidDataException("pacchetto troppo lungo");
            _start = 0;
            _end = await stream.ReadAsync(_buffer, cancellation);
            if (_end == 0)
                return null;
        }
    }

    /// <summary>Prima del TLS: un byte alla volta, per non consumare l'inizio della negoziazione.</summary>
    public static async Task<string?> ReadPlainLineAsync(Stream stream, int maxBytes, CancellationToken cancellation)
    {
        var line = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one, cancellation) == 1)
        {
            if (one[0] == '\n')
                return Encoding.UTF8.GetString(line.ToArray());
            line.Add(one[0]);
            if (line.Count > maxBytes)
                throw new InvalidDataException("identità troppo lunga");
        }
        return null;
    }
}
