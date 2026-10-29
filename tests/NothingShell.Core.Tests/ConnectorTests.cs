using System.Text;
using NothingShell.Core.Connectors;
using NothingShell.Core.Phone;
using NothingShell.Core.Settings;

namespace NothingShell.Core.Tests;

public class ConnectorTests
{
    [Fact]
    public void ParsesHomeAssistantWithActions()
    {
        var result = ConnectorParser.Parse("casa", """
            {
              // commenti ammessi
              "name": "Casa",
              "type": "homeassistant",
              "url": "http://homeassistant.local:8123",
              "secret": true,
              "readings": [ { "entity": "sensor.soggiorno", "above": 26 } ],
              "actions": [ { "label": "Luci off", "service": "light.turn_off", "data": { "entity_id": "all" }, "confirm": true } ],
            }
            """);
        var m = result.Manifest!;
        Assert.Null(result.Error);
        Assert.Equal(ConnectorKind.HomeAssistant, m.Kind);
        Assert.Equal("sensor.soggiorno", m.Readings[0].Source);
        Assert.Equal("LUCI OFF", m.Actions[0].Label);
        Assert.Equal(ActionKind.HomeAssistant, m.Actions[0].Kind);
        Assert.Equal("""{ "entity_id": "all" }""", m.Actions[0].Body);
        Assert.True(m.Actions[0].Confirm);
        Assert.True(m.NeedsGrant);
        Assert.Equal("NothingShell:connector:casa", m.CredentialTarget);
    }

    [Theory]
    [InlineData("Casa", """{"name":"x","type":"http","url":"http://a","readings":[{"value":"{a}"}]}""", "nome del file")]
    [InlineData("a", """{"type":"ping","host":"x"}""", "name")]
    [InlineData("a", """{"name":"x","type":"telnet"}""", "type")]
    [InlineData("a", """{"name":"x","type":"http","url":"ftp://a","readings":[{"value":"{a}"}]}""", "url")]
    [InlineData("a", """{"name":"x","type":"http","url":"http://a"}""", "readings")]
    [InlineData("a", """{"name":"x","type":"command"}""", "command")]
    [InlineData("a", """{"name":"x","type":"mqtt","host":"b","readings":[{"label":"t"}]}""", "topic")]
    [InlineData("a", """{"name":"x","type":"ping","host":"b","refreshSeconds":1}""", "refreshSeconds")]
    [InlineData("a", """{"name":"x","type":"ping","host":"b","actions":[{"label":"x","type":"mqtt"}]}""", "azione")]
    public void RejectsInvalidConnectors(string id, string json, string reason)
    {
        var result = ConnectorParser.Parse(id, json);
        Assert.Null(result.Manifest);
        Assert.Contains(reason, result.Error, StringComparison.Ordinal);
    }

    /// <summary>Gli esempi della cartella <c>connectors/</c> del repository devono restare validi.</summary>
    [Fact]
    public void RepositoryExamplesAreValid()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "NothingShell.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var files = Directory.GetFiles(Path.Combine(root.FullName, "connectors"), "*.json");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var result = ConnectorParser.Parse(Path.GetFileNameWithoutExtension(file), File.ReadAllText(file));
            Assert.True(result.Manifest is not null, $"{Path.GetFileName(file)}: {result.Error}");
        }
    }

    [Fact]
    public void ReadOnlyConnectorsNeedNoGrant()
    {
        var m = ConnectorParser.Parse("router", """{"name":"Router","type":"ping","host":"192.168.1.1"}""").Manifest!;
        Assert.False(m.NeedsGrant);
        Assert.True(ConnectorParser.Parse("pi", """{"name":"Pi","type":"command","command":"ssh"}""").Manifest!.NeedsGrant);
    }

    [Fact]
    public void GrantIsBoundToTheFileContent()
    {
        var before = ConnectorParser.Parse("pi", """{"name":"Pi","type":"command","command":"ssh"}""").Manifest!;
        var after = ConnectorParser.Parse("pi", """{"name":"Pi","type":"command","command":"ssh","args":["rm"]}""").Manifest!;
        var settings = ShellSettings.Default.WithConnectorGrant(before.Id, before.Hash, true);
        Assert.True(settings.IsConnectorGranted(before.Id, before.Hash));
        Assert.False(settings.IsConnectorGranted(after.Id, after.Hash));
        Assert.False(settings.WithConnectorGrant(before.Id, before.Hash, false).IsConnectorGranted(before.Id, before.Hash));

        var json = ShellSettingsStore.Serialize(settings);
        Assert.True(ShellSettingsStore.Deserialize(json).IsConnectorGranted(before.Id, before.Hash));
    }

    [Fact]
    public void EvaluatesJsonPathsWithScaleAndThresholds()
    {
        using var doc = ConnectorReadings.Document("""{"volumes":[{"used":0.93}],"temp":48123}""");
        var disk = ConnectorReadings.Evaluate(new ReadingSpec("Disco", "{volumes[0].used}") { Scale = 100, Above = 90, Unit = "%" }, doc.RootElement, "x");
        Assert.Equal("93", disk.Text);
        Assert.Equal(ReadingLevel.Warning, disk.Level);
        var temp = ConnectorReadings.Evaluate(new ReadingSpec("CPU", "{temp}") { Scale = 0.001, Decimals = 1 }, doc.RootElement, "x");
        Assert.Equal("48.1", temp.Text);
        Assert.Equal(ReadingLevel.Normal, temp.Level);
    }

    [Fact]
    public void PlainTextOutputHasTextAndLines()
    {
        using var doc = ConnectorReadings.Document("temp=48.3'C\r\nseconda riga\n");
        Assert.Equal("temp=48.3'C", ConnectorReadings.Evaluate(new ReadingSpec(null, "{lines[0]}"), doc.RootElement, "Uscita").Text);
        Assert.Equal("seconda riga", ConnectorReadings.Evaluate(new ReadingSpec(null, "{lines[1]}"), doc.RootElement, "Uscita").Text);
        Assert.Equal("Uscita", ConnectorReadings.Evaluate(new ReadingSpec(null, "{text}"), doc.RootElement, "Uscita").Label);
    }

    [Fact]
    public void TextAlertsAndBelowThreshold()
    {
        Assert.Equal(ReadingLevel.Warning, ConnectorReadings.FromText(new ReadingSpec("Porta", null) { AlertOn = "aperto" }, "APERTO", "x").Level);
        Assert.Equal(ReadingLevel.Warning, ConnectorReadings.FromText(new ReadingSpec("Batteria", null) { Below = 20 }, "12", "x").Level);
        Assert.Equal(ReadingLevel.Normal, ConnectorReadings.FromText(new ReadingSpec("Batteria", null) { Below = 20 }, "80", "x").Level);
    }

    [Fact]
    public void AlertsOnlyWhenEnteringWarning()
    {
        var alerts = new ConnectorAlerts();
        ConnectorReading Hot(ReadingLevel level) => new("Temp", "30", "°C", level);
        Assert.Single(alerts.Update("casa", [Hot(ReadingLevel.Warning)]));
        Assert.Empty(alerts.Update("casa", [Hot(ReadingLevel.Warning)]));
        Assert.Empty(alerts.Update("casa", [Hot(ReadingLevel.Normal)]));
        Assert.Single(alerts.Update("casa", [Hot(ReadingLevel.Warning)]));
    }

    [Theory]
    [InlineData("casa/+/temperatura", "casa/soggiorno/temperatura", true)]
    [InlineData("casa/#", "casa/soggiorno/temperatura", true)]
    [InlineData("casa/+", "casa/soggiorno/temperatura", false)]
    [InlineData("zigbee2mqtt/sensore", "zigbee2mqtt/sensore", true)]
    [InlineData("zigbee2mqtt/sensore", "zigbee2mqtt/altro", false)]
    public void MqttTopicFilters(string filter, string topic, bool expected) => Assert.Equal(expected, Mqtt.Matches(filter, topic));

    [Fact]
    public void MqttPublishRoundTrip()
    {
        var bytes = Mqtt.PublishPacket("casa/luce", "ON");
        var packet = Mqtt.TryRead(bytes, out var consumed)!;
        Assert.Equal(bytes.Length, consumed);
        Assert.Equal(Mqtt.Publish, packet.Type);
        Assert.Equal(("casa/luce", "ON"), Mqtt.ReadPublish(packet));
    }

    [Fact]
    public void MqttWaitsForTheWholePacket()
    {
        var bytes = Mqtt.PublishPacket("t", new string('x', 300)); // lunghezza su due byte
        Assert.Null(Mqtt.TryRead(bytes.AsSpan(0, bytes.Length - 1), out _));
        Assert.NotNull(Mqtt.TryRead(bytes, out var consumed));
        Assert.Equal(bytes.Length, consumed);
    }

    [Fact]
    public void MqttConnectCarriesCredentials()
    {
        var connect = Mqtt.Connect("client", "utente", "segreto", 60);
        Assert.Equal(0x10, connect[0]);
        Assert.Equal(0xC2, connect[9]); // utente + password + sessione pulita
        Assert.Contains("segreto", Encoding.UTF8.GetString(connect), StringComparison.Ordinal);
        Assert.Equal(0, Mqtt.ConnackCode(new MqttPacket(Mqtt.Connack, 0, [0, 0])));
        Assert.Equal("utente o password errati", Mqtt.ConnackReason(4));
    }

    [Fact]
    public void ReadsPhoneSignalAndShares()
    {
        var packet = KdePacket.Parse("""{"id":1,"type":"kdeconnect.connectivity_report","body":{"signalStrengths":{"6":{"networkType":"LTE","signalStrength":2},"7":{"networkType":"NR","signalStrength":4}}}}""")!;
        Assert.Equal(new PhoneSignal("5G", 4), PhoneInfo.ReadSignal(packet));
        Assert.Equal(["batteria", "notifiche"], PhoneInfo.SharedLabels(["kdeconnect.notification", "kdeconnect.battery", "kdeconnect.mpris"]));
        var identity = KdePacket.Parse("""{"id":1,"type":"kdeconnect.identity","body":{"deviceId":"a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4","deviceName":"P","outgoingCapabilities":["kdeconnect.battery"]}}""")!;
        Assert.Equal(["kdeconnect.battery"], KdeConnect.ReadIdentity(identity)!.Outgoing);
    }

    [Fact]
    public void ReceivedFilesKeepTheNewestFirst()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ns-files-{Guid.NewGuid():N}.json");
        try
        {
            var files = new ReceivedFiles(path);
            var now = DateTimeOffset.Now;
            files.Add(new ReceivedFile(@"C:\d\a.pdf", "P", 10, now));
            files.Add(new ReceivedFile(@"C:\d\b.jpg", "P", 20, now));
            files.Add(new ReceivedFile(@"C:\d\A.pdf", "P", 30, now)); // stesso file riscritto
            var reloaded = new ReceivedFiles(path);
            reloaded.Load();
            Assert.Equal([@"C:\d\A.pdf", @"C:\d\b.jpg"], reloaded.Files.Select(f => f.Path));
            Assert.True(reloaded.Prune(p => p.EndsWith(".jpg", StringComparison.Ordinal)));
            Assert.Single(reloaded.Files);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
