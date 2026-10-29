using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NothingShell.Core.Notch;

namespace NothingShell.Core.Phone;

/// <summary>
/// Pacchetto del protocollo KDE Connect: una riga JSON terminata da <c>\n</c> (docs/13-telefono.md).
/// <see cref="PayloadSize"/> e <see cref="PayloadPort"/> annunciano un file che viaggia su un'altra connessione.
/// </summary>
public sealed record KdePacket(string Type, JsonObject Body)
{
    public long Id { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public long? PayloadSize { get; init; }

    public int? PayloadPort { get; init; }

    public string Serialize()
    {
        var json = new JsonObject
        {
            ["id"] = Id,
            ["type"] = Type,
            ["body"] = Body.DeepClone(),
        };
        if (PayloadSize is { } size)
            json["payloadSize"] = size;
        if (PayloadPort is { } port)
            json["payloadTransferInfo"] = new JsonObject { ["port"] = port };
        return json.ToJsonString() + "\n";
    }

    /// <summary>Null se la riga non è un pacchetto valido (JSON rotto, senza tipo o senza corpo).</summary>
    public static KdePacket? Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        try
        {
            if (JsonNode.Parse(line) is not JsonObject json
                || json["type"] is not JsonValue type || type.GetValueKind() != JsonValueKind.String
                || json["body"] is not JsonObject body)
                return null;
            return new KdePacket(type.GetValue<string>(), (JsonObject)body.DeepClone())
            {
                Id = Number(json["id"]) ?? 0,
                PayloadSize = Number(json["payloadSize"]) is { } size and >= 0 ? size : null,
                PayloadPort = (json["payloadTransferInfo"] as JsonObject)?["port"] is { } port && Number(port) is { } p and > 0 and < 65536
                    ? (int)p
                    : null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string? Text(string key) =>
        Body[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    public bool Flag(string key) => Body[key] is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    public long? Whole(string key) => Number(Body[key]);

    /// <summary>I client più vecchi mandano anche gli interi come stringhe.</summary>
    private static long? Number(JsonNode? node) => node is JsonValue v
        ? v.GetValueKind() switch
        {
            JsonValueKind.Number when v.TryGetValue<long>(out var l) => l,
            JsonValueKind.Number when v.TryGetValue<double>(out var d) => (long)d,
            JsonValueKind.String when long.TryParse(v.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
            _ => null,
        }
        : null;
}

/// <summary>Identità annunciata da un dispositivo (pacchetto <c>kdeconnect.identity</c>).</summary>
public sealed record KdeIdentity(string DeviceId, string Name, string DeviceType, int ProtocolVersion, int TcpPort)
{
    /// <summary>Cosa il telefono manda: i plugin accesi nell'app (vuoto se non lo dichiara).</summary>
    public IReadOnlyList<string> Outgoing { get; init; } = [];

    /// <summary>Due identità sono uguali se lo sono anche le capacità, non solo l'elenco come oggetto.</summary>
    public bool Equals(KdeIdentity? other) =>
        other is not null && DeviceId == other.DeviceId && Name == other.Name && DeviceType == other.DeviceType
        && ProtocolVersion == other.ProtocolVersion && TcpPort == other.TcpPort && Outgoing.SequenceEqual(other.Outgoing);

    public override int GetHashCode() => HashCode.Combine(DeviceId, Name, DeviceType, ProtocolVersion, TcpPort, Outgoing.Count);
}

public readonly record struct PhoneBattery(int Charge, bool Charging, bool Low);

/// <summary>Stato di un telefono visto in rete o associato, per impostazioni e pannello rapido.</summary>
public sealed record PhoneDevice(string DeviceId, string Name, bool Connected, bool Paired)
{
    public PhoneBattery? Battery { get; init; }

    public PhoneSignal? Signal { get; init; }

    /// <summary>Cosa il telefono condivide con la shell, in italiano (batteria, notifiche…).</summary>
    public IReadOnlyList<string> Shares { get; init; } = [];

    /// <summary>Richiesta di associazione in corso: da noi (<see cref="PairRequestedByPhone"/> false) o dal telefono.</summary>
    public bool PairPending { get; init; }

    public bool PairRequestedByPhone { get; init; }

    /// <summary>Codice di 8 caratteri uguale sui due schermi durante l'associazione.</summary>
    public string? VerificationKey { get; init; }
}

/// <summary>Regole del protocollo KDE Connect, versione 8 (compatibile con l'app Android).</summary>
public static class KdeConnect
{
    public const int Port = 1716;
    public const int PortMax = 1764;
    public const int PayloadPortMin = 1739;
    public const int ProtocolVersion = 8;

    /// <summary>Le richieste di associazione più vecchie (o "nel futuro") di così si ignorano.</summary>
    public static readonly TimeSpan PairTimestampTolerance = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan PairTimeout = TimeSpan.FromSeconds(30);

    public const string Identity = "kdeconnect.identity";
    public const string Pair = "kdeconnect.pair";
    public const string Battery = "kdeconnect.battery";
    public const string BatteryRequest = "kdeconnect.battery.request";
    public const string Clipboard = "kdeconnect.clipboard";
    public const string ClipboardConnect = "kdeconnect.clipboard.connect";
    public const string Share = "kdeconnect.share.request";
    public const string Notification = "kdeconnect.notification";
    public const string Telephony = "kdeconnect.telephony";
    public const string TelephonyMute = "kdeconnect.telephony.request_mute";
    public const string FindMyPhone = "kdeconnect.findmyphone.request";
    public const string Ping = "kdeconnect.ping";

    /// <summary>Cosa la shell sa ricevere: l'app sul telefono attiva solo i plugin che hanno un corrispondente.</summary>
    public static readonly string[] IncomingCapabilities =
        [Battery, Clipboard, ClipboardConnect, Share, Notification, Telephony, Ping, PhoneInfo.ConnectivityReport];

    public static readonly string[] OutgoingCapabilities =
        [BatteryRequest, Clipboard, ClipboardConnect, Share, FindMyPhone, TelephonyMute, Ping, PhoneInfo.ConnectivityReportRequest];

    /// <summary>32–38 caratteri fra lettere, cifre e <c>_</c> (vincolo della versione 8; è anche il CN del certificato).</summary>
    public static bool IsValidDeviceId(string? id) =>
        id is { Length: >= 32 and <= 38 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    public static string NewDeviceId() => Guid.NewGuid().ToString("N");

    public static KdePacket IdentityPacket(string deviceId, string name, int tcpPort) => new(Identity, new JsonObject
    {
        ["deviceId"] = deviceId,
        ["deviceName"] = name,
        ["deviceType"] = "desktop",
        ["protocolVersion"] = ProtocolVersion,
        ["tcpPort"] = tcpPort,
        ["incomingCapabilities"] = new JsonArray([.. IncomingCapabilities.Select(c => (JsonNode)c)]),
        ["outgoingCapabilities"] = new JsonArray([.. OutgoingCapabilities.Select(c => (JsonNode)c)]),
    });

    /// <summary>Null se non è un'identità o il deviceId non è valido.</summary>
    public static KdeIdentity? ReadIdentity(KdePacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Type != Identity || packet.Text("deviceId") is not { } id || !IsValidDeviceId(id))
            return null;
        var name = packet.Text("deviceName") is { Length: > 0 } n ? n.Trim() : "Telefono";
        return new KdeIdentity(id, name, packet.Text("deviceType") ?? "phone",
            (int)(packet.Whole("protocolVersion") ?? 7), (int)(packet.Whole("tcpPort") ?? Port))
        {
            Outgoing = PhoneInfo.Capabilities(packet, "outgoingCapabilities"),
        };
    }

    public static KdePacket PairPacket(bool pair, DateTimeOffset? timestamp = null)
    {
        var body = new JsonObject { ["pair"] = pair };
        if (timestamp is { } t)
            body["timestamp"] = t.ToUnixTimeSeconds();
        return new KdePacket(Pair, body);
    }

    /// <summary>Una richiesta del telefono vale solo se l'orologio è vicino al nostro (difesa contro le richieste ripetute).</summary>
    public static bool IsFreshPairRequest(long? timestampSeconds, DateTimeOffset now) =>
        timestampSeconds is { } t && (now - DateTimeOffset.FromUnixTimeSeconds(t)).Duration() <= PairTimestampTolerance;

    /// <summary>
    /// Codice mostrato sui due schermi: SHA-256 delle due chiavi pubbliche (SubjectPublicKeyInfo DER, la maggiore
    /// per prima) e del timestamp della richiesta, prime 8 cifre esadecimali maiuscole. Come l'app KDE Connect.
    /// </summary>
    public static string VerificationKey(byte[] ownPublicKey, byte[] peerPublicKey, long timestampSeconds)
    {
        ArgumentNullException.ThrowIfNull(ownPublicKey);
        ArgumentNullException.ThrowIfNull(peerPublicKey);
        var (first, second) = ownPublicKey.AsSpan().SequenceCompareTo(peerPublicKey) >= 0
            ? (ownPublicKey, peerPublicKey)
            : (peerPublicKey, ownPublicKey);
        var data = first.Concat(second).Concat(Encoding.ASCII.GetBytes(timestampSeconds.ToString(CultureInfo.InvariantCulture))).ToArray();
        return Convert.ToHexString(SHA256.HashData(data))[..8];
    }

    public static PhoneBattery? ReadBattery(KdePacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Type != Battery || packet.Whole("currentCharge") is not { } charge || charge < 0)
            return null;
        return new PhoneBattery((int)Math.Min(100, charge), packet.Flag("isCharging"), packet.Whole("thresholdEvent") == 1);
    }

    /// <summary>
    /// Chiamate (<c>kdeconnect.telephony</c>) e notifiche (<c>kdeconnect.notification</c>) come eventi del notch.
    /// Le notifiche "silenziose" (quelle già presenti al collegamento) e le cancellazioni non diventano avvisi;
    /// la fine di una chiamata sì (<see cref="PhoneEventKind.CallEnded"/>), per chiudere la scheda.
    /// </summary>
    public static PhoneEvent? ReadEvent(KdePacket packet, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(packet);
        switch (packet.Type)
        {
            case Telephony:
            {
                var who = packet.Text("contactName") is { Length: > 0 } name ? name
                    : packet.Text("phoneNumber") ?? string.Empty;
                var kind = packet.Text("event") switch
                {
                    "ringing" when packet.Flag("isCancel") => PhoneEventKind.CallEnded,
                    "ringing" => PhoneEventKind.IncomingCall,
                    "missedCall" => PhoneEventKind.MissedCall,
                    "talking" => PhoneEventKind.CallEnded, // risposto: la scheda non serve più
                    _ => (PhoneEventKind?)null,
                };
                return kind is { } k ? new PhoneEvent(k, who, string.Empty, now) : null;
            }
            case Notification when !packet.Flag("isCancel") && !packet.Flag("silent"):
            {
                var app = packet.Text("appName");
                var title = packet.Text("title") ?? string.Empty;
                var text = (packet.Text("text") ?? packet.Text("ticker") ?? string.Empty).Split('\n', 2)[0].Trim();
                if (title.Length == 0 && text.Length == 0)
                    return null;
                return new PhoneEvent(PhoneEventKind.Message, title.Trim(), text, now) { App = app };
            }
            default:
                return null;
        }
    }

    public static KdePacket ClipboardPacket(string text) => new(Clipboard, new JsonObject { ["content"] = text });

    /// <summary>Testo degli appunti dal telefono (<c>clipboard</c> o <c>clipboard.connect</c>); null se vuoto.</summary>
    public static string? ReadClipboard(KdePacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return packet.Type is Clipboard or ClipboardConnect && packet.Text("content") is { Length: > 0 } text ? text : null;
    }

    public static KdePacket FilePacket(string fileName, long size, int port, DateTimeOffset lastModified) => new(Share, new JsonObject
    {
        ["filename"] = fileName,
        ["lastModified"] = lastModified.ToUnixTimeMilliseconds(),
        ["numberOfFiles"] = 1,
        ["totalPayloadSize"] = size,
    })
    {
        PayloadSize = size,
        PayloadPort = port,
    };

    /// <summary>
    /// Nome sicuro per salvare un file arrivato dal telefono: niente cartelle, niente caratteri non validi,
    /// niente nomi riservati di Windows.
    /// </summary>
    public static string SafeFileName(string? name)
    {
        var file = (name ?? string.Empty).Replace('\\', '/').Split('/')[^1]; // "a:b" non è un'unità: niente Path.GetFileName
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '|', '?', '*']).ToHashSet();
        file = new string(file.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        var stem = file.Split('.')[0].ToUpperInvariant();
        string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
        if (file.Length == 0 || reserved.Contains(stem))
            file = "dal-telefono" + (file.Length > 0 ? "-" + file : string.Empty);
        return file.Length > 200 ? file[..200] : file;
    }
}
