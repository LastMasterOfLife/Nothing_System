using System.Text.Json;
using NothingShell.Core.Notch;
using NothingShell.Core.Plugins;
using NothingShell.Core.Settings;
using NothingShell.Core.Widgets;

namespace NothingShell.Core.Tests;

public class PluginManifestTests
{
    private const string HttpManifest = """
        {
          // i commenti sono ammessi
          "name": "Meteo",
          "size": "wide",
          "refreshSeconds": 600,
          "source": { "type": "http", "url": "https://api.example.com/x?a=1" },
          "display": { "header": "METEO", "value": "{t}°", "bar": "{h}", "barMax": 100 },
        }
        """;

    [Fact]
    public void ParsesHttpManifest()
    {
        var result = PluginManifestParser.Parse("meteo", HttpManifest);
        Assert.Null(result.Error);
        var m = result.Manifest!;
        Assert.Equal("plugin:meteo", m.WidgetId);
        Assert.Equal(PluginSize.Wide, m.Size);
        Assert.Equal(TimeSpan.FromMinutes(10), m.Refresh);
        Assert.Equal(PluginSourceType.Http, m.SourceType);
        Assert.Equal(100, m.Display.BarMax);
    }

    [Fact]
    public void ParsesCommandManifestWithDefaults()
    {
        var result = PluginManifestParser.Parse("disco",
            """{ "name": "Disco", "source": { "type": "command", "command": "disco.ps1", "args": ["C"] } }""");
        var m = result.Manifest!;
        Assert.Equal(PluginSize.Small, m.Size);
        Assert.Equal(TimeSpan.FromSeconds(PluginManifest.DefaultRefreshSeconds), m.Refresh);
        Assert.Equal(["C"], m.Arguments);
    }

    /// <summary>Gli esempi della cartella <c>plugins/</c> del repository devono restare validi.</summary>
    [Fact]
    public void RepositoryExamplesAreValid()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "NothingShell.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var folders = Directory.GetDirectories(Path.Combine(root.FullName, "plugins"));
        Assert.NotEmpty(folders);
        foreach (var folder in folders)
        {
            var result = PluginManifestParser.Parse(Path.GetFileName(folder), File.ReadAllText(Path.Combine(folder, "plugin.json")));
            Assert.True(result.Manifest is not null, $"{Path.GetFileName(folder)}: {result.Error}");
        }
    }

    [Theory]
    [InlineData("Meteo", "{}", "nome della cartella")]
    [InlineData("meteo", "non json", "JSON non valido")]
    [InlineData("meteo", "[]", "oggetto JSON")]
    [InlineData("meteo", """{ "source": {} }""", "\"name\"")]
    [InlineData("meteo", """{ "name": "M", "size": "huge", "source": {} }""", "\"size\"")]
    [InlineData("meteo", """{ "name": "M", "refreshSeconds": 1, "source": {} }""", "refreshSeconds")]
    [InlineData("meteo", """{ "name": "M" }""", "\"source\"")]
    [InlineData("meteo", """{ "name": "M", "source": { "type": "ftp" } }""", "source.type")]
    [InlineData("meteo", """{ "name": "M", "source": { "type": "http", "url": "file:///c:/x" }, "display": { "value": "x" } }""", "source.url")]
    [InlineData("meteo", """{ "name": "M", "source": { "type": "http", "url": "https://x.it" } }""", "display.value")]
    [InlineData("meteo", """{ "name": "M", "source": { "type": "command" } }""", "source.command")]
    public void InvalidManifestsExplainWhy(string folder, string json, string expected)
    {
        var result = PluginManifestParser.Parse(folder, json);
        Assert.Null(result.Manifest);
        Assert.Contains(expected, result.Error, StringComparison.Ordinal);
    }
}

public class JsonTemplateTests
{
    private static readonly JsonElement Doc = JsonDocument.Parse("""
        { "current": { "temperature_2m": 18.46, "ok": true, "city": "Roma", "none": null },
          "hourly": { "time": ["08:00", "09:00"], "grid": [[1, 2], [3, 4]] } }
        """).RootElement;

    [Theory]
    [InlineData("{current.temperature_2m}°", "18.5°")]
    [InlineData("{current.city} {hourly.time[1]}", "Roma 09:00")]
    [InlineData("{hourly.grid[1][0]}", "3")]
    [InlineData("{current.ok}", "SI")]
    [InlineData("{current.none}", "--")]
    [InlineData("{current.missing}", "--")]
    [InlineData("{hourly.time[9]}", "--")]
    [InlineData("{current.city[0]}", "--")]
    [InlineData("senza segnaposto", "senza segnaposto")]
    [InlineData("graffa aperta {current.city", "graffa aperta {current.city")]
    public void Render(string template, string expected) => Assert.Equal(expected, JsonTemplate.Render(template, Doc));
}

public class WidgetDataTests
{
    private static PluginManifest Manifest(PluginDisplay display) =>
        new("disco", "Disco", PluginSize.Small, TimeSpan.FromMinutes(1), PluginSourceType.Command, "x", [], display);

    private static readonly PluginManifest Plain = Manifest(new PluginDisplay(null, null, "C:", null, 1));

    [Fact]
    public void JsonOutputOverridesDisplay()
    {
        var data = WidgetData.FromCommandOutput(Plain, """{ "value": "73%", "bar": 0.73, "alert": true }""");
        Assert.Equal(new WidgetData("DISCO", "73%", "C:", 0.73, true), data);
    }

    [Fact]
    public void PlainTextBecomesValue() =>
        Assert.Equal("42", WidgetData.FromCommandOutput(Plain, "  42\r\nseconda riga").Value);

    [Fact]
    public void EmptyOutputIsMissing() => Assert.Equal("--", WidgetData.FromCommandOutput(Plain, "").Value);

    [Fact]
    public void BrokenJsonIsShownAsText() => Assert.Equal("{rotto", WidgetData.FromCommandOutput(Plain, "{rotto").Value);

    [Fact]
    public void HttpUsesTemplatesAndBarMax()
    {
        var m = Manifest(new PluginDisplay("METEO", "{t}°", "ROMA", "{h}", 100));
        var data = WidgetData.FromHttp(m, JsonDocument.Parse("""{ "t": 21, "h": 64 }""").RootElement);
        Assert.Equal(new WidgetData("METEO", "21°", "ROMA", 0.64, false), data);
    }

    [Theory]
    [InlineData("73%", 100, 0.73)]
    [InlineData("0,5", 1, 0.5)]
    [InlineData("250", 100, 1.0)]
    public void ParseFraction(string text, double max, double expected) =>
        Assert.Equal(expected, WidgetData.ParseFraction(text, max)!.Value, 6);

    [Fact]
    public void ParseFractionRejectsText() => Assert.Null(WidgetData.ParseFraction("--", 100));
}

public class ShellSettingsTests
{
    [Fact]
    public void WidgetToggleIsIdempotent()
    {
        var s = ShellSettings.Default.WithWidget("System", false).WithWidget("System", false);
        Assert.Equal(["System"], s.DisabledWidgets);
        Assert.False(s.IsWidgetEnabled("System"));
        Assert.True(s.WithWidget("System", true).IsWidgetEnabled("System"));
    }

    [Fact]
    public void RoundTripAndFallback()
    {
        var s = new ShellSettings(false, true, ["plugin:meteo"]);
        Assert.Equal(s.DisabledWidgets, ShellSettingsStore.Deserialize(ShellSettingsStore.Serialize(s)).DisabledWidgets);
        Assert.Same(ShellSettings.Default, ShellSettingsStore.Deserialize("rotto"));
        Assert.Empty(ShellSettingsStore.Deserialize("""{ "notch": false }""").DisabledWidgets);
    }
}

public class FreeSlotTests
{
    private static readonly Rect2 WorkArea = new(0, 0, 1536, 912);

    [Fact]
    public void FirstPluginGoesNextToDefaultWidgets()
    {
        var occupied = WidgetLayout.Default(WorkArea).Select(p => p.Bounds).ToArray();
        var size = new Size2(WidgetSizes.Cell, WidgetSizes.Cell);
        var (x, y) = WidgetLayout.FindFreeSlot(size, occupied, WorkArea);

        var slot = new Rect2(x, y, size.Width, size.Height);
        Assert.DoesNotContain(occupied, slot.IntersectsWith);
        Assert.True(slot.Right <= WorkArea.Right && slot.Bottom <= WorkArea.Bottom);
    }

    [Fact]
    public void EmptyDesktopUsesTopRightCorner()
    {
        var (x, y) = WidgetLayout.FindFreeSlot(new Size2(168, 168), [], WorkArea);
        Assert.Equal(1536 - WidgetLayout.Margin - 168, x);
        Assert.Equal(WidgetLayout.Margin, y);
    }

    [Fact]
    public void SettleRectSnapsAndClamps()
    {
        var (x, y) = WidgetLayout.Settle(new Rect2(1530, 101, 168, 168), new Rect2(0, 0, 1536, 960));
        Assert.Equal(1536 - 168, x);
        Assert.Equal(104, y);
    }
}
