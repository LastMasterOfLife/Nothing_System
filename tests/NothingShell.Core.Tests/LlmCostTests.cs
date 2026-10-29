using System.Text.Json;
using NothingShell.Core.Connectors;
using NothingShell.Core.Plugins;

namespace NothingShell.Core.Tests;

public class LlmCostTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.LocalDateTime);

    private static LlmSpec Spec(string json) =>
        LlmSpec.Parse(JsonDocument.Parse(json).RootElement, out var error) ?? throw new InvalidOperationException(error);

    private static bool Apply(LlmLedger ledger, LlmSpec spec, string json, DateTimeOffset? at = null)
    {
        using var doc = JsonDocument.Parse(json);
        return ledger.Apply("sito", spec, doc.RootElement, at ?? Now);
    }

    [Fact]
    public void CallsAreCountedOnceWithTheAnthropicPriceList()
    {
        var spec = Spec("""{"mode":"calls","list":"calls","model":"{model}","input":"{input_tokens}","output":"{output_tokens}","id":"{id}"}""");
        var ledger = new LlmLedger();
        const string response = """{"calls":[{"id":"a","model":"claude-sonnet-5-5","input_tokens":1000000,"output_tokens":0},{"id":"b","model":"claude-sonnet-5-5","input_tokens":0,"output_tokens":100000}]}""";
        Assert.True(Apply(ledger, spec, response));
        Assert.False(Apply(ledger, spec, response)); // stesse chiamate: niente di nuovo

        var summary = ledger.Summary("sito", Today, 0.9);
        Assert.Equal(2, summary.Today.Calls);
        Assert.Equal(3.0, summary.Today.Usd, 6); // 1M in × 2 $ + 100K out × 10 $/M
        Assert.Equal(2.7, summary.TodayEur!.Value, 6);
        Assert.Equal(2, ledger.Books["sito"].Recent.Count);
    }

    [Fact]
    public void TotalsCountTheGrowthAndSurviveAReset()
    {
        var spec = Spec("""{"model":"claude-haiku-5-5","input":"{in}","output":"{out}","requests":"{n}","price":{"input":1,"output":2,"currency":"EUR"}}""");
        var ledger = new LlmLedger();
        Apply(ledger, spec, """{"in":5000000,"out":0,"n":900}"""); // da sempre: punto di partenza
        Assert.Equal(0, ledger.Summary("sito", Today, null).Today.Calls);

        Apply(ledger, spec, """{"in":6000000,"out":500000,"n":910}""");
        var day = ledger.Summary("sito", Today, null);
        Assert.Equal(10, day.Today.Calls);
        Assert.Equal(2.0, day.TodayEur!.Value, 6); // 1M × 1 € + 0,5M × 2 €, prezzi propri in euro

        Apply(ledger, spec, """{"in":1000000,"out":0,"n":3}"""); // il sito è ripartito da zero
        Assert.Equal(13, ledger.Summary("sito", Today, null).Today.Calls);
    }

    [Fact]
    public void CountFirstReadsDailyCountersPerModel()
    {
        // forma dell'Admin API di Anthropic: un elenco per modello, contatori del giorno
        var spec = Spec("""{"list":"data[0].results","model":"{model}","input":"{uncached_input_tokens}","output":"{output_tokens}","countFirst":true,"dailyBudget":1}""");
        var ledger = new LlmLedger();
        Apply(ledger, spec, """{"data":[{"results":[{"model":"claude-opus-5-5","uncached_input_tokens":100000,"output_tokens":20000},{"model":"claude-haiku-5-5","uncached_input_tokens":1000000,"output_tokens":0}]}]}""");
        var summary = ledger.Summary("sito", Today, 1);
        Assert.Equal(0.4 + 0.4 + 0.1, summary.TodayEur!.Value, 6);

        var readings = LlmLedger.Readings(spec, summary);
        Assert.Equal("LLM oggi", readings[0].Label);
        Assert.Equal("0.90", readings[0].Text);
        Assert.Equal(ReadingLevel.Normal, readings[0].Level);

        Apply(ledger, spec, """{"data":[{"results":[{"model":"claude-opus-5-5","uncached_input_tokens":300000,"output_tokens":20000},{"model":"claude-haiku-5-5","uncached_input_tokens":1000000,"output_tokens":0}]}]}""");
        var over = LlmLedger.Readings(spec, ledger.Summary("sito", Today, 1));
        Assert.Equal(ReadingLevel.Warning, over[0].Level); // 1,70 € oltre il budget di 1 €
    }

    [Fact]
    public void SiteCostWinsAndUnknownModelsAreFlagged()
    {
        var withCost = Spec("""{"mode":"calls","list":"calls","model":"{model}","input":"{i}","output":"{o}","cost":"{c}","costCurrency":"EUR","costScale":0.01}""");
        var ledger = new LlmLedger();
        Apply(ledger, withCost, """{"calls":[{"model":"gpt-x","i":10,"o":10,"c":250}]}""");
        Assert.Equal(2.5, ledger.Summary("sito", Today, null).TodayEur!.Value, 6);

        var noPrice = Spec("""{"mode":"calls","list":"calls","model":"{model}","input":"{i}","output":"{o}"}""");
        var other = new LlmLedger();
        Apply(other, noPrice, """{"calls":[{"model":"gpt-x","i":10,"o":5}]}""");
        var summary = other.Summary("sito", Today, null);
        Assert.Equal(15, summary.Today.UnpricedTokens);
        Assert.EndsWith("*", LlmLedger.Readings(noPrice, summary)[0].Label);
    }

    [Fact]
    public void DollarsWaitForTheExchangeRate()
    {
        var day = new LlmDay { Usd = 2, Eur = 1 };
        Assert.Null(day.InEuro(null));
        Assert.Equal(2.8, day.InEuro(0.9)!.Value, 6);
        Assert.Equal(1, new LlmDay { Eur = 1 }.InEuro(null));
    }

    [Fact]
    public void CallsLandOnTheirOwnDay()
    {
        var spec = Spec("""{"mode":"calls","list":"calls","model":"claude-haiku-5-5","input":"{i}","at":"{at}"}""");
        var ledger = new LlmLedger();
        Apply(ledger, spec, """{"calls":[{"id":1,"i":100,"at":"2026-10-01T10:00:00Z"},{"id":2,"i":100}]}""");
        Assert.Equal(1, ledger.Summary("sito", Today, 1).Today.Calls);
        Assert.Equal(2, ledger.Summary("sito", Today, 1).Month.Calls);
    }

    [Fact]
    public void LedgerRoundTripsAndExportsCsv()
    {
        var spec = Spec("""{"mode":"calls","list":"calls","model":"claude-sonnet-5-5","input":"{i}","output":"{o}"}""");
        var ledger = new LlmLedger { UsdToEur = 0.9 };
        Apply(ledger, spec, """{"calls":[{"i":1000,"o":100}]}""");
        var path = Path.Combine(Path.GetTempPath(), $"llm-{Guid.NewGuid():N}.json");
        try
        {
            ledger.Save(path);
            var loaded = LlmLedger.Load(path);
            Assert.Equal(0.9, loaded.UsdToEur);
            Assert.False(Apply(loaded, spec, """{"calls":[{"i":1000,"o":100}]}""")); // già contata prima del riavvio
            var csv = loaded.Csv(_ => "Negozio; online", 0.9);
            Assert.Contains("Negozio, online;1;1000;100", csv, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("""{"mode":"boh","input":"{i}"}""")]
    [InlineData("""{"model":"x"}""")]
    [InlineData("""{"input":"{i}","price":{"input":1}}""")]
    public void BadBlocksAreRejected(string json) =>
        Assert.Null(LlmSpec.Parse(JsonDocument.Parse(json).RootElement, out _));

    [Fact]
    public void ParserReadsTheBlockAndAllowsConnectorsWithOnlyCosts()
    {
        var result = ConnectorParser.Parse("sito", """{"name":"Sito","type":"http","url":"https://s.example/uso","llm":{"input":"{i}","output":"{o}","dailyBudget":3}}""");
        Assert.Null(result.Error);
        Assert.Equal(3, result.Manifest!.Llm!.DailyBudget);

        Assert.NotNull(ConnectorParser.Parse("sito", """{"name":"Sito","type":"ping","host":"h","llm":{"input":"{i}"}}""").Error);
        Assert.NotNull(ConnectorParser.Parse("sito", """{"name":"Sito","type":"http","url":"https://s.example/uso"}""").Error);
    }

    [Fact]
    public void SpecSurvivesTheFile()
    {
        var spec = Spec("""{"mode":"calls","list":"calls","model":"{m}","input":"{i}","output":"{o}","cost":"{c}","costCurrency":"EUR","price":{"input":1,"output":2,"currency":"EUR"},"dailyBudget":2,"usdToEur":0.9,"countFirst":true}""");
        var json = ConnectorCatalog.WithLlm("""{"name":"Sito","type":"http","url":"https://s.example"}""", spec);
        var again = ConnectorParser.Parse("sito", json).Manifest!.Llm!;
        Assert.Equal(spec, again with { Price = spec.Price });
        Assert.Equal(spec.Price, again.Price);
        Assert.Null(ConnectorParser.Parse("sito", ConnectorCatalog.WithLlm(json, null)).Manifest);
    }

    [Fact]
    public void NegativeIndexIsFromTheEnd()
    {
        using var doc = JsonDocument.Parse("""{"v":[{"id":"a"},{"id":"b"}]}""");
        Assert.True(JsonTemplate.TryResolve(doc.RootElement, "v[-1].id", out var last));
        Assert.Equal("b", last.GetString());
        Assert.False(JsonTemplate.TryResolve(doc.RootElement, "v[-3].id", out _));
    }
}

public class ConnectorEditTests
{
    [Fact]
    public void ValuesAreRememberedWithoutSecretsForEditAndDuplicate()
    {
        var template = ConnectorCatalog.Find("sito-llm")!;
        var values = new Dictionary<string, string> { ["url"] = "https://a.example/uso", ["token"] = "segreto", ["daily"] = "3" };
        var result = ConnectorCatalog.Create(template, "Negozio", values, []);
        var manifest = ConnectorParser.Parse(result.Id!, result.Json!).Manifest!;
        Assert.Equal("https://a.example/uso", manifest.TemplateValues["url"]);
        Assert.False(manifest.TemplateValues.ContainsKey("token"));
        Assert.DoesNotContain("segreto", result.Json, StringComparison.Ordinal);
        Assert.Equal(3, manifest.Llm!.DailyBudget);

        // lo stesso connettore per un altro sito: id nuovo
        var copy = ConnectorCatalog.Create(template, "Negozio", new Dictionary<string, string>(manifest.TemplateValues) { ["url"] = "https://b.example/uso" }, [manifest.Id]);
        Assert.Equal("negozio-2", copy.Id);

        // Modifica: stesso id, il token salvato resta
        var edit = ConnectorCatalog.Create(template, "Negozio", new Dictionary<string, string>(manifest.TemplateValues), [manifest.Id], manifest.Id, keepSecret: true);
        Assert.Null(edit.Error);
        Assert.Equal(manifest.Id, edit.Id);
        Assert.Null(edit.Secret);
        Assert.True(ConnectorParser.Parse(edit.Id!, edit.Json!).Manifest!.Secret);
    }

    [Fact]
    public void RequiredSecretCanBeKeptOnlyWhenEditing()
    {
        var template = ConnectorCatalog.Find("anthropic-costi")!;
        Assert.NotNull(ConnectorCatalog.Create(template, null, new Dictionary<string, string>(), []).Error);
        Assert.Null(ConnectorCatalog.Create(template, null, new Dictionary<string, string>(), [], "costi", keepSecret: true).Error);
    }

    [Fact]
    public void AnthropicCostsFollowOneKey()
    {
        var template = ConnectorCatalog.Find("anthropic-costi")!;
        var result = ConnectorCatalog.Create(template, null, new Dictionary<string, string> { ["key"] = "sk-ant-admin", ["keyId"] = "apikey_01" }, []);
        var m = ConnectorParser.Parse(result.Id!, result.Json!).Manifest!;
        Assert.Contains("api_key_ids[]=apikey_01", m.Url, StringComparison.Ordinal);
        Assert.Contains("starting_at={today}", m.Url, StringComparison.Ordinal);
        Assert.Equal("x-api-key", m.SecretHeader);
        Assert.True(m.Llm!.CountFirst);
    }
}
