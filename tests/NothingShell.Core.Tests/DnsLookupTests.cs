using System.Net;
using NothingShell.Core.Network;

namespace NothingShell.Core.Tests;

public class DnsLookupTests
{
    [Theory]
    [InlineData("miosito.it", "miosito.it")]
    [InlineData("https://miosito.it/pagina?x=1#top", "miosito.it")]
    [InlineData("https://utente:pw@WWW.MioSito.it:8443/a", "www.miosito.it")]
    [InlineData("miosito.it.", "miosito.it")]
    [InlineData("ftp://ftp.esempio.org", "ftp.esempio.org")]
    [InlineData("http://[2001:db8::1]:80/", "2001:db8::1")]
    [InlineData("8.8.4.4", "8.8.4.4")]
    public void HostOfStripsEverythingButTheName(string input, string expected) =>
        Assert.Equal(expected, DnsRequest.HostOf(input));

    [Fact]
    public void ParseTakesHostTypesAndServerInAnyOrder()
    {
        var r = DnsRequest.Parse("https://miosito.it/x mx txt @8.8.8.8")!;
        Assert.Equal("miosito.it", r.Host);
        Assert.Equal([DnsType.MX, DnsType.TXT], r.Types);
        Assert.Equal("8.8.8.8", r.Server);
    }

    [Fact]
    public void ParseWithoutTypeAsksForTheMainRecords()
    {
        var r = DnsRequest.Parse("miosito.it")!;
        Assert.Equal(DnsRequest.DefaultTypes, r.Types);
        Assert.Null(r.Server);
        Assert.Equal(DnsRequest.DefaultTypes, DnsRequest.Parse("miosito.it tutti")!.Types);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("miosito.it parola-a-caso")]
    [InlineData("mio sito.it")]
    [InlineData("miosito.it @a;b")]
    [InlineData("https://")]
    public void ParseRejectsNonsense(string input) => Assert.Null(DnsRequest.Parse(input));

    [Fact]
    public void ReverseNames()
    {
        Assert.Equal("4.4.8.8.in-addr.arpa", DnsLookup.ReverseName(IPAddress.Parse("8.8.4.4")));
        Assert.StartsWith("1.0.0.0", DnsLookup.ReverseName(IPAddress.Parse("2001:db8::1")));
        Assert.EndsWith(".ip6.arpa", DnsLookup.ReverseName(IPAddress.Parse("2001:db8::1")));
    }

    [Fact]
    public void QueryIsWellFormed()
    {
        var q = DnsLookup.BuildQuery(0x1234, "www.ab.it", DnsType.MX);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x01, 0x00, 0x00, 0x01 }, q[..6]);
        Assert.Equal(new byte[] { 3, (byte)'w', (byte)'w', (byte)'w', 2, (byte)'a', (byte)'b', 2, (byte)'i', (byte)'t', 0, 0, 15, 0, 1 }, q[12..]);
    }

    /// <summary>Risposta costruita a mano: A con puntatore di compressione, MX e TXT.</summary>
    [Fact]
    public void ParsesAnswersWithCompression()
    {
        var packet = new List<byte> { 0x12, 0x34, 0x81, 0x80, 0, 1, 0, 3, 0, 0, 0, 0 };
        packet.AddRange([2, (byte)'a', (byte)'b', 2, (byte)'i', (byte)'t', 0, 0, 1, 0, 1]); // domanda ab.it A
        packet.AddRange([0xC0, 12, 0, 1, 0, 1, 0, 0, 1, 0x2C, 0, 4, 1, 2, 3, 4]); // A 1.2.3.4 ttl 300
        packet.AddRange([0xC0, 12, 0, 15, 0, 1, 0, 0, 0, 60, 0, 9, 0, 10, 4, (byte)'m', (byte)'a', (byte)'i', (byte)'l', 0xC0, 12]); // MX 10 mail.ab.it
        packet.AddRange([0xC0, 12, 0, 16, 0, 1, 0, 0, 0, 5, 0, 8, 3, (byte)'v', (byte)'=', (byte)'1', 3, (byte)'a', (byte)'b', (byte)'c']); // TXT "v=1abc"

        var parsed = DnsLookup.ParseResponse([.. packet], 0x1234)!;
        Assert.Equal(0, parsed.Code);
        Assert.Collection(parsed.Records,
            a => Assert.Equal((DnsType.A, 300, "1.2.3.4", "ab.it"), (a.Type, a.Ttl, a.Value, a.Name)),
            mx => Assert.Equal((DnsType.MX, "10 mail.ab.it"), (mx.Type, mx.Value)),
            txt => Assert.Equal((DnsType.TXT, "v=1abc"), (txt.Type, txt.Value)));
    }

    [Fact]
    public void IgnoresOtherIdsAndLoops()
    {
        var packet = new byte[] { 0x99, 0x99, 0x81, 0x80, 0, 0, 0, 0, 0, 0, 0, 0 };
        Assert.Null(DnsLookup.ParseResponse(packet, 0x1234));
        var loop = new byte[] { 0xC0, 0x00 };
        var offset = 0;
        Assert.Throws<InvalidDataException>(() => DnsLookup.ReadName(loop, ref offset));
    }
}
