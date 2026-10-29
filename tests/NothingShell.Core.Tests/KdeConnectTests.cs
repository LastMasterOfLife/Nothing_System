using System.Text.Json.Nodes;
using NothingShell.Core.Notch;
using NothingShell.Core.Phone;

namespace NothingShell.Core.Tests;

public class KdeConnectTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 21, 0, 0, TimeSpan.Zero);
    private const string PhoneId = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6";

    private static KdePacket Packet(string json) => KdePacket.Parse(json)!;

    [Fact]
    public void IdentityRoundTrip()
    {
        var line = KdeConnect.IdentityPacket(PhoneId, "PC di Andrea", 1716).Serialize();
        Assert.EndsWith("\n", line);
        var identity = KdeConnect.ReadIdentity(KdePacket.Parse(line)!)!;
        Assert.Equal(new KdeIdentity(PhoneId, "PC di Andrea", "desktop", KdeConnect.ProtocolVersion, 1716)
        {
            Outgoing = KdeConnect.OutgoingCapabilities,
        }, identity);
    }

    [Fact]
    public void IdentityFromOlderClientsWithStringNumbers()
    {
        var identity = KdeConnect.ReadIdentity(Packet(
            $$$"""{"id":"123","type":"kdeconnect.identity","body":{"deviceId":"{{{PhoneId}}}","deviceName":"Pixel 8","deviceType":"phone","protocolVersion":"7","tcpPort":"1716"}}"""))!;
        Assert.Equal(7, identity.ProtocolVersion);
        Assert.Equal("Pixel 8", identity.Name);
    }

    [Theory]
    [InlineData("corto")]
    [InlineData("a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6")] // trattini non ammessi dalla versione 8
    [InlineData(null)]
    public void InvalidDeviceIdsAreRejected(string? id) => Assert.False(KdeConnect.IsValidDeviceId(id));

    [Theory]
    [InlineData("non json")]
    [InlineData("""{"type":"kdeconnect.ping"}""")]
    [InlineData("""{"type":5,"body":{}}""")]
    public void BrokenPacketsAreNull(string line) => Assert.Null(KdePacket.Parse(line));

    [Fact]
    public void PayloadTransferInfoRoundTrip()
    {
        var packet = KdePacket.Parse(KdeConnect.FilePacket("foto.jpg", 2048, 1739, Now).Serialize())!;
        Assert.Equal(2048, packet.PayloadSize);
        Assert.Equal(1739, packet.PayloadPort);
        Assert.Equal("foto.jpg", packet.Text("filename"));
    }

    [Fact]
    public void PairRequestsMustBeFresh()
    {
        Assert.True(KdeConnect.IsFreshPairRequest(Now.AddMinutes(-5).ToUnixTimeSeconds(), Now));
        Assert.False(KdeConnect.IsFreshPairRequest(Now.AddHours(-2).ToUnixTimeSeconds(), Now));
        Assert.False(KdeConnect.IsFreshPairRequest(Now.AddHours(2).ToUnixTimeSeconds(), Now));
        Assert.False(KdeConnect.IsFreshPairRequest(null, Now));
    }

    [Fact]
    public void VerificationKeyIsTheSameOnBothSides()
    {
        byte[] pc = [0x30, 0x82, 0x01, 0x22, 0x10];
        byte[] phone = [0x30, 0x82, 0x01, 0x22, 0x20];
        var onPc = KdeConnect.VerificationKey(pc, phone, 1_800_000_000);
        Assert.Equal(onPc, KdeConnect.VerificationKey(phone, pc, 1_800_000_000));
        Assert.Matches("^[0-9A-F]{8}$", onPc);
        Assert.NotEqual(onPc, KdeConnect.VerificationKey(pc, phone, 1_800_000_001));
    }

    [Fact]
    public void Battery()
    {
        var battery = KdeConnect.ReadBattery(Packet("""{"type":"kdeconnect.battery","body":{"currentCharge":14,"isCharging":false,"thresholdEvent":1}}"""));
        Assert.Equal(new PhoneBattery(14, false, true), battery);
    }

    [Theory]
    [InlineData("ringing", false, PhoneEventKind.IncomingCall)]
    [InlineData("ringing", true, PhoneEventKind.CallEnded)]
    [InlineData("talking", false, PhoneEventKind.CallEnded)]
    [InlineData("missedCall", false, PhoneEventKind.MissedCall)]
    public void Telephony(string kind, bool cancel, PhoneEventKind expected)
    {
        var body = new JsonObject { ["event"] = kind, ["contactName"] = "Giulia", ["phoneNumber"] = "+39 333", ["isCancel"] = cancel };
        var phone = KdeConnect.ReadEvent(new KdePacket(KdeConnect.Telephony, body), Now)!;
        Assert.Equal(expected, phone.Kind);
        Assert.Equal("Giulia", phone.Title);
    }

    [Fact]
    public void UnknownCallerFallsBackToNumber()
    {
        var body = new JsonObject { ["event"] = "ringing", ["phoneNumber"] = "+39 333 1234567" };
        Assert.Equal("+39 333 1234567", KdeConnect.ReadEvent(new KdePacket(KdeConnect.Telephony, body), Now)!.Title);
    }

    [Fact]
    public void NotificationsButNotSilentOrCancelled()
    {
        var message = KdeConnect.ReadEvent(Packet(
            """{"type":"kdeconnect.notification","body":{"id":"1","appName":"WhatsApp","title":"Giulia","text":"Arrivo\nalle 8"}}"""), Now)!;
        Assert.Equal("Giulia: Arrivo", PhoneLink.Summary(message));
        Assert.Equal("WhatsApp", message.App);

        Assert.Null(KdeConnect.ReadEvent(Packet("""{"type":"kdeconnect.notification","body":{"title":"x","silent":true}}"""), Now));
        Assert.Null(KdeConnect.ReadEvent(Packet("""{"type":"kdeconnect.notification","body":{"id":"1","isCancel":true}}"""), Now));
    }

    [Fact]
    public void Clipboard()
    {
        Assert.Equal("ciao", KdeConnect.ReadClipboard(KdePacket.Parse(KdeConnect.ClipboardPacket("ciao").Serialize())!));
        Assert.Null(KdeConnect.ReadClipboard(Packet("""{"type":"kdeconnect.clipboard","body":{"content":""}}""")));
    }

    [Theory]
    [InlineData("foto.jpg", "foto.jpg")]
    [InlineData("../../Windows/system32/evil.dll", "evil.dll")]
    [InlineData("..\\..\\evil.exe", "evil.exe")]
    [InlineData("CON.txt", "dal-telefono-CON.txt")]
    [InlineData("a:b?.txt", "a_b_.txt")]
    [InlineData("", "dal-telefono")]
    [InlineData(null, "dal-telefono")]
    public void SafeFileNames(string? name, string expected) => Assert.Equal(expected, KdeConnect.SafeFileName(name));

    [Fact]
    public void TrustStoreKeepsIdentityAndDropsInvalidPhones()
    {
        var trust = new PhoneTrust(PhoneId, [new TrustedPhone("f".PadLeft(32, 'f'), "Pixel", "AAAA")]);
        var json = PhoneTrustStore.Serialize(trust.With(new TrustedPhone("corto", "x", "y")));
        var loaded = PhoneTrustStore.Deserialize(json);
        Assert.Equal(PhoneId, loaded.DeviceId);
        Assert.Equal(["Pixel"], loaded.Phones.Select(p => p.Name));

        var fresh = PhoneTrustStore.Deserialize("rotto");
        Assert.True(KdeConnect.IsValidDeviceId(fresh.DeviceId));
        Assert.Empty(fresh.Phones);
    }
}
