using System.Text;

namespace NothingShell.Core.Connectors;

/// <summary>Un pacchetto MQTT 3.1.1 letto dal broker: tipo (4 bit alti), flag (4 bit bassi) e corpo.</summary>
public sealed record MqttPacket(int Type, int Flags, byte[] Body);

/// <summary>
/// Il minimo di MQTT 3.1.1 per leggere i sensori: CONNECT, SUBSCRIBE con QoS 0, PINGREQ e PUBLISH in uscita (solo per le
/// azioni con permesso). Niente sessioni persistenti né QoS 1/2 (docs/14-agenti-connettori.md).
/// </summary>
public static class Mqtt
{
    public const int Connack = 2;
    public const int Publish = 3;
    public const int Suback = 9;
    public const int Pingresp = 13;

    public const int MaxPacketBytes = 256 * 1024;

    public static byte[] Connect(string clientId, string? userName, string? password, ushort keepAliveSeconds)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        var body = new List<byte>();
        AddString(body, "MQTT");
        body.Add(4); // livello del protocollo: 3.1.1
        byte flags = 0x02; // sessione pulita
        if (userName is not null)
            flags |= 0x80;
        if (userName is not null && password is not null)
            flags |= 0x40;
        body.Add(flags);
        body.Add((byte)(keepAliveSeconds >> 8));
        body.Add((byte)keepAliveSeconds);
        AddString(body, clientId);
        if (userName is not null)
            AddString(body, userName);
        if (userName is not null && password is not null)
            AddString(body, password);
        return Frame(0x10, body);
    }

    public static byte[] Subscribe(ushort packetId, IEnumerable<string> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        var body = new List<byte> { (byte)(packetId >> 8), (byte)packetId };
        foreach (var filter in filters.Distinct(StringComparer.Ordinal))
        {
            AddString(body, filter);
            body.Add(0); // QoS 0
        }
        return Frame(0x82, body);
    }

    public static byte[] PublishPacket(string topic, string payload, bool retain = false)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(payload);
        var body = new List<byte>();
        AddString(body, topic);
        body.AddRange(Encoding.UTF8.GetBytes(payload));
        return Frame(retain ? 0x31 : 0x30, body);
    }

    public static byte[] PingReq() => [0xC0, 0x00];

    public static byte[] Disconnect() => [0xE0, 0x00];

    /// <summary>
    /// Toglie dall'inizio di <paramref name="buffer"/> un pacchetto completo; null se ne mancano ancora dei byte.
    /// <paramref name="consumed"/> dice quanti byte scartare.
    /// </summary>
    public static MqttPacket? TryRead(ReadOnlySpan<byte> buffer, out int consumed)
    {
        consumed = 0;
        if (buffer.Length < 2)
            return null;
        var length = 0;
        var multiplier = 1;
        var index = 1;
        while (true)
        {
            if (index >= buffer.Length)
                return null;
            if (index > 4)
                throw new InvalidDataException("lunghezza MQTT non valida");
            var b = buffer[index++];
            length += (b & 0x7F) * multiplier;
            if ((b & 0x80) == 0)
                break;
            multiplier *= 128;
        }
        if (length > MaxPacketBytes)
            throw new InvalidDataException("pacchetto MQTT troppo grande");
        if (buffer.Length < index + length)
            return null;
        consumed = index + length;
        return new MqttPacket(buffer[0] >> 4, buffer[0] & 0x0F, buffer.Slice(index, length).ToArray());
    }

    /// <summary>Topic e payload di un PUBLISH (con QoS &gt; 0 c'è anche l'id del pacchetto, che si salta).</summary>
    public static (string Topic, string Payload)? ReadPublish(MqttPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Type != Publish || packet.Body.Length < 2)
            return null;
        var topicLength = (packet.Body[0] << 8) | packet.Body[1];
        var offset = 2 + topicLength;
        if (((packet.Flags >> 1) & 0x03) > 0)
            offset += 2;
        if (offset > packet.Body.Length)
            return null;
        var topic = Encoding.UTF8.GetString(packet.Body, 2, topicLength);
        return (topic, Encoding.UTF8.GetString(packet.Body, offset, packet.Body.Length - offset));
    }

    /// <summary>Codice di ritorno del CONNACK: 0 = accettato.</summary>
    public static int ConnackCode(MqttPacket packet) =>
        packet.Type == Connack && packet.Body.Length >= 2 ? packet.Body[1] : -1;

    public static string ConnackReason(int code) => code switch
    {
        1 => "versione di MQTT non accettata",
        2 => "id del client rifiutato",
        3 => "broker non disponibile",
        4 => "utente o password errati",
        5 => "accesso non autorizzato",
        _ => $"risposta inattesa ({code})",
    };

    /// <summary>Filtri con <c>+</c> (un livello) e <c>#</c> (tutto il resto), come nello standard.</summary>
    public static bool Matches(string filter, string topic)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(topic);
        var f = filter.Split('/');
        var t = topic.Split('/');
        for (var i = 0; i < f.Length; i++)
        {
            if (f[i] == "#")
                return true;
            if (i >= t.Length)
                return false;
            if (f[i] != "+" && f[i] != t[i])
                return false;
        }
        return f.Length == t.Length;
    }

    private static void AddString(List<byte> body, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        body.Add((byte)(bytes.Length >> 8));
        body.Add((byte)bytes.Length);
        body.AddRange(bytes);
    }

    private static byte[] Frame(int header, List<byte> body)
    {
        var packet = new List<byte> { (byte)header };
        var length = body.Count;
        do
        {
            var digit = (byte)(length % 128);
            length /= 128;
            if (length > 0)
                digit |= 0x80;
            packet.Add(digit);
        }
        while (length > 0);
        packet.AddRange(body);
        return [.. packet];
    }
}
