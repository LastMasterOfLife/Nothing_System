using System.Net;
using System.Net.Sockets;
using System.Text;
using NothingShell.Core.Network;

namespace NothingShell.Core.Tests;

/// <summary>Un server HTTP minimo sul PC: risponde a ogni percorso con quello che dice <c>route</c> (stato, intestazioni, corpo).</summary>
internal sealed class FakeHttp : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

    public FakeHttp(Func<string, (int Status, string Headers, string Body)> route)
    {
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                _ = Task.Run(async () =>
                {
                    using var c = client;
                    var stream = c.GetStream();
                    var buffer = new byte[4096];
                    var n = await stream.ReadAsync(buffer);
                    var path = Encoding.ASCII.GetString(buffer, 0, n).Split(' ')[1];
                    var (status, headers, body) = route(path);
                    var text = $"HTTP/1.1 {status} X\r\n{headers}Content-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(text));
                });
            }
        });
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public Uri Url(string path) => new($"http://127.0.0.1:{Port}{path}");

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}

public class HttpProbeTests
{
    [Fact]
    public void ParsesTheHead()
    {
        var (status, reason, headers) = HttpProbe.ParseHead("HTTP/1.1 301 Moved Permanently\r\nLocation: /x\r\nSet-Cookie: a=1\r\nSet-Cookie: b=2\r\nServer: nginx\r\n");
        Assert.Equal(301, status);
        Assert.Equal("Moved Permanently", reason);
        Assert.Equal("/x", headers["location"]);
        Assert.Equal("a=1, b=2", headers["set-cookie"]);
        Assert.Equal("nginx", headers["SERVER"]);
        Assert.Throws<InvalidDataException>(() => HttpProbe.ParseHead("<html>ciao"));
    }

    [Fact]
    public async Task MeasuresASingleRequest()
    {
        using var server = new FakeHttp(_ => (200, "Content-Type: text/plain\r\nServer: prova\r\n", "ciao mondo"));
        var hop = await HttpProbe.SendAsync(server.Url("/"), CancellationToken.None);
        Assert.Equal(200, hop.Status);
        Assert.Equal(10, hop.BodyBytes);
        Assert.Equal("prova", hop.Headers["server"]);
        Assert.Null(hop.TlsProtocol);
        Assert.Equal(0, hop.TlsMs);
        Assert.True(hop.TotalMs >= hop.FirstByteMs);
        Assert.False(hop.IsRedirect);
    }

    [Fact]
    public async Task FollowsRedirectsAndStops()
    {
        using var server = new FakeHttp(path => path switch
        {
            "/a" => (301, "Location: /b\r\n", string.Empty),
            "/b" => (302, "Location: http://127.0.0.1/ignorato-non-segue-fuori\r\n".Replace("http://127.0.0.1/ignorato-non-segue-fuori", "/c"), string.Empty),
            "/c" => (200, string.Empty, "fine"),
            "/giro1" => (302, "Location: /giro2\r\n", string.Empty),
            "/giro2" => (302, "Location: /giro1\r\n", string.Empty),
            _ => (404, string.Empty, string.Empty),
        });
        var chain = await HttpProbe.ChainAsync(server.Url("/a"), CancellationToken.None);
        Assert.Equal([301, 302, 200], chain.Hops.Select(h => h.Status));
        Assert.False(chain.Loop);
        Assert.False(chain.Downgrade);
        Assert.Equal("/c", chain.Last.Url.AbsolutePath);

        var loop = await HttpProbe.ChainAsync(server.Url("/giro1"), CancellationToken.None);
        Assert.True(loop.Loop);
        Assert.Equal(2, loop.Hops.Count);
    }

    [Fact]
    public async Task RefusesWhatIsNotHttp()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => HttpProbe.SendAsync(new Uri("ftp://esempio.it/"), CancellationToken.None));
    }
}

public class DiagnosiTests
{
    [Theory]
    [InlineData("", "", "")]
    [InlineData("miosito.it", "dns", "miosito.it")]
    [InlineData("miosito.it MX @8.8.8.8", "dns", "miosito.it MX @8.8.8.8")]
    [InlineData("http miosito.it", "http", "miosito.it")]
    [InlineData("HTTP https://x.it/a?b=1", "http", "https://x.it/a?b=1")]
    [InlineData("redirect x.it", "redirect", "x.it")]
    [InlineData("propaga x.it TXT", "propaga", "x.it TXT")]
    [InlineData("porta 5432", "porta", "5432")]
    public void SplitsTheCommand(string text, string verb, string rest) => Assert.Equal((verb, rest), Diagnosi.Split(text));

    [Fact]
    public void AddsHttpsAndChecksTheAddress()
    {
        Assert.Equal("https://miosito.it/", Diagnosi.UrlOf("miosito.it").AbsoluteUri);
        Assert.Equal("http://x.it:8080/a", Diagnosi.UrlOf("http://x.it:8080/a").AbsoluteUri);
        Assert.Throws<InvalidOperationException>(() => Diagnosi.UrlOf(""));
        Assert.Throws<InvalidOperationException>(() => Diagnosi.UrlOf("ftp://x.it"));
        Assert.Equal(5432, Diagnosi.PortOf(" 5432 "));
        Assert.Throws<InvalidOperationException>(() => Diagnosi.PortOf("0"));
        Assert.Throws<InvalidOperationException>(() => Diagnosi.PortOf("70000"));
        Assert.Throws<InvalidOperationException>(() => Diagnosi.PortOf("abc"));
    }

    [Fact]
    public async Task ErrorsBecomeALineNotAnException()
    {
        var bad = await Diagnosi.RunAsync("porta xyz", CancellationToken.None);
        Assert.Equal(DiagLevel.Bad, Assert.Single(bad.Lines).Level);
        var empty = await Diagnosi.RunAsync("  ", CancellationToken.None);
        Assert.Empty(empty.Lines);
        var noHost = await Diagnosi.RunAsync("http", CancellationToken.None);
        Assert.Contains("manca", noHost.Lines[0].Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpReportNamesTheSlowestPhase()
    {
        using var server = new FakeHttp(_ => (503, "Server: prova\r\n", "giù"));
        var report = await Diagnosi.RunAsync($"http {server.Url("/")}", CancellationToken.None);
        Assert.Equal("503 X", report.Lines[0].Value);
        Assert.Equal(DiagLevel.Bad, report.Lines[0].Level);
        Assert.Contains("fase più lenta", report.Title, StringComparison.Ordinal);
        Assert.Contains(report.Lines, l => l.Label == "server" && l.Value == "prova");
        Assert.DoesNotContain(report.Lines, l => l.Label == "TLS");
    }

    [Fact]
    public async Task RedirectReportShowsEveryHop()
    {
        using var server = new FakeHttp(path => path == "/" ? (301, "Location: /dopo\r\n", string.Empty) : (200, string.Empty, "ok"));
        var report = await Diagnosi.RunAsync($"redirect {server.Url("/")}", CancellationToken.None);
        Assert.Equal(2, report.Lines.Count);
        Assert.Contains("→", report.Lines[0].Value, StringComparison.Ordinal);
        Assert.StartsWith("1 salto", report.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void PropagationReportFlagsDifferences()
    {
        PropagationRow Row(string name, bool auth, string value, string? serial = null) => new(name, "1.1.1.1", [value], 12, null, auth, serial);
        var same = new PropagationResult("x.it", DnsType.A, [Row("Google", false, "1.2.3.4"), Row("ns1", true, "1.2.3.4", "10")], true, true);
        Assert.Equal(DiagLevel.Good, Diagnosi.FormatPropagation(same).Lines[0].Level);
        var different = new PropagationResult("x.it", DnsType.A, [Row("Google", false, "1.2.3.4"), Row("ns1", true, "5.6.7.8", "11")], false, false);
        var report = Diagnosi.FormatPropagation(different);
        Assert.Equal(DiagLevel.Warn, report.Lines[0].Level);
        Assert.Contains(report.Lines, l => l.Label == "SOA");
    }

    [Theory]
    [InlineData("www.sinapsi.it", new[] { "www.sinapsi.it", "sinapsi.it" })]
    [InlineData("sinapsi.it.", new[] { "sinapsi.it" })]
    [InlineData("localhost", new string[0])]
    public void WalksUpToTheZone(string host, string[] expected) => Assert.Equal(expected, DnsPropagation.Zones(host));

    [Fact]
    public void FindsTheProcessHoldingAPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var owners = PortOwners.Find(port);
        var mine = Assert.Single(owners, o => o.State == "In ascolto");
        Assert.Equal(Environment.ProcessId, mine.Pid);
        Assert.Equal("127.0.0.1", mine.Address);

        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        Assert.Contains(PortOwners.Find(port), o => o.State == "Connessa");
        var report = Diagnosi.FormatPort(port, PortOwners.Find(port));
        Assert.Contains("in ascolto", report.Title, StringComparison.Ordinal);
    }
}
