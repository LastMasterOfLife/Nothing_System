using NothingShell.Core.Connectors;

namespace NothingShell.Core.Tests;

public class ConnectorCatalogTests
{
    /// <summary>Un valore plausibile per ogni campo obbligatorio senza valore proposto.</summary>
    private static Dictionary<string, string> Sample(ConnectorTemplate template) =>
        template.Fields.ToDictionary(f => f.Key, f => f.Key switch
        {
            "host" => "192.168.1.10",
            "url" => "http://esempio.local",
            "target" => "pi@raspberry.local",
            "repo" => "utente/progetto",
            "user" => "utente",
            "service" => "Spooler",
            "process" => "ollama",
            "path" => @"C:\log\app.log",
            "entities" => "sensor.soggiorno, binary_sensor.porta",
            "topics" => "zigbee2mqtt/camera, casa/caldaia",
            "value" => "dati.valore",
            "script" => "Get-Date",
            "token" when !f.Optional => "segreto",
            _ => string.Empty,
        });

    public static TheoryData<string> TemplateIds()
    {
        var data = new TheoryData<string>();
        foreach (var t in ConnectorCatalog.All)
            data.Add(t.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void EveryTemplateBuildsAValidConnector(string id)
    {
        var template = ConnectorCatalog.Find(id)!;
        var result = ConnectorCatalog.Create(template, null, Sample(template), []);
        Assert.Null(result.Error);
        var parsed = ConnectorParser.Parse(result.Id!, result.Json!);
        Assert.Null(parsed.Error);
        Assert.Equal(template.Id, parsed.Manifest!.Template);
        Assert.Equal(template.Name, parsed.Manifest.Name);
        // chi esegue comandi o ha pulsanti lo dice prima di chiedere il permesso
        if (parsed.Manifest.NeedsGrant && parsed.Manifest.Actions.Count > 0 || parsed.Manifest.Kind == ConnectorKind.Command)
            Assert.NotNull(template.Permission);
    }

    [Fact]
    public void CatalogIsWellFormed()
    {
        Assert.Equal(ConnectorCatalog.All.Count, ConnectorCatalog.All.Select(t => t.Id).Distinct().Count());
        Assert.All(ConnectorCatalog.All, t => Assert.Contains(t.Category, ConnectorCatalog.Categories));
    }

    [Fact]
    public void SecretStaysOutOfTheFile()
    {
        var template = ConnectorCatalog.Find("homeassistant")!;
        var values = Sample(template);
        values["token"] = "abc.def.ghi";
        values["toggle"] = "light.soggiorno";
        var result = ConnectorCatalog.Create(template, "Casa mia", values, []);
        Assert.Equal("abc.def.ghi", result.Secret);
        Assert.DoesNotContain("abc.def.ghi", result.Json, StringComparison.Ordinal);
        var m = ConnectorParser.Parse(result.Id!, result.Json!).Manifest!;
        Assert.True(m.Secret);
        Assert.Equal("casa-mia", m.Id);
        Assert.Equal(["sensor.soggiorno", "binary_sensor.porta"], m.Readings.Select(r => r.Source));
        Assert.Equal("homeassistant.toggle", m.Actions.Single().Service);
    }

    [Theory]
    [InlineData("servizio", "service", "Spooler'; Remove-Item C:\\")]
    [InlineData("ping", "host", "a b")]
    [InlineData("server-ssh", "target", "pi@host; rm -rf /")]
    [InlineData("homeassistant", "entities", "sensor.ok, non valida")]
    public void RejectsValuesThatDoNotFit(string id, string key, string value)
    {
        var template = ConnectorCatalog.Find(id)!;
        var values = Sample(template);
        values[key] = value;
        var result = ConnectorCatalog.Create(template, null, values, []);
        Assert.NotNull(result.Error);
        Assert.Null(result.Json);
    }

    [Fact]
    public void RequiredFieldsAndNumbersAreChecked()
    {
        var ping = ConnectorCatalog.Find("ping")!;
        Assert.Contains("manca", ConnectorCatalog.Create(ping, null, new Dictionary<string, string>(), []).Error, StringComparison.Ordinal);
        var disk = ConnectorCatalog.Find("disco")!;
        Assert.Contains("numero", ConnectorCatalog.Create(disk, null, new Dictionary<string, string> { ["min"] = "tanti" }, []).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionalThresholdsAreLeftOut()
    {
        var weather = ConnectorCatalog.Find("meteo")!;
        var plain = ConnectorParser.Parse("m", ConnectorCatalog.Create(weather, null, new Dictionary<string, string>(), []).Json!).Manifest!;
        Assert.Null(plain.Readings[0].Above);
        var hot = ConnectorParser.Parse("m", ConnectorCatalog.Create(weather, null, new Dictionary<string, string> { ["above"] = "30,5" }, []).Json!).Manifest!;
        Assert.Equal(30.5, hot.Readings[0].Above);
    }

    [Fact]
    public void IdsComeFromTheNameAndDoNotCollide()
    {
        Assert.Equal("stampante-3d-klipper", ConnectorCatalog.Slug("Stampante 3D (Klipper)"));
        Assert.Equal("qualita-dell-aria", ConnectorCatalog.Slug("Qualità dell'aria"));
        Assert.Null(ConnectorCatalog.Slug("???"));
        var ip = ConnectorCatalog.Find("ip-pubblico")!;
        Assert.Equal("ip-pubblico-3", ConnectorCatalog.Create(ip, null, new Dictionary<string, string>(), ["ip-pubblico", "IP-PUBBLICO-2"]).Id);
        Assert.Equal("ip-pubblico", ConnectorCatalog.Create(ip, "???", new Dictionary<string, string>(), []).Id);
    }
}
