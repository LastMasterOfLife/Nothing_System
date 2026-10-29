using NothingShell.Core.Agents;

namespace NothingShell.Core.Tests;

public class AgentTests
{
    private const string Prompt =
        """{"type":"user","timestamp":"2026-10-29T19:00:00.000Z","cwd":"C:\\dev\\nothing-shell","sessionId":"s1","message":{"role":"user","content":"aggiungi i test"}}""";

    private const string ToolResult =
        """{"type":"user","timestamp":"2026-10-29T19:00:20.000Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""";

    private const string Interrupted =
        """{"type":"user","timestamp":"2026-10-29T19:00:30.000Z","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]}}""";

    private static string Assistant(string id, string stop, int output, string time = "19:00:10", string model = "claude-opus-5-5", bool sidechain = false) =>
        """{"type":"assistant","isSidechain":SIDE,"timestamp":"2026-10-29TTIME.000Z","message":{"model":"MODEL","id":"ID","role":"assistant","stop_reason":"STOP","usage":{"input_tokens":10,"cache_creation_input_tokens":1000,"cache_read_input_tokens":50000,"output_tokens":OUT,"cache_creation":{"ephemeral_1h_input_tokens":1000,"ephemeral_5m_input_tokens":0}}}}"""
            .Replace("SIDE", sidechain ? "true" : "false", StringComparison.Ordinal)
            .Replace("TIME", time, StringComparison.Ordinal)
            .Replace("MODEL", model, StringComparison.Ordinal)
            .Replace("\"ID\"", $"\"{id}\"", StringComparison.Ordinal)
            .Replace("STOP", stop, StringComparison.Ordinal)
            .Replace("OUT", output.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    [Fact]
    public void ReadsUsageWithoutTheText()
    {
        var entry = ClaudeTranscript.Parse(Assistant("msg_1", "tool_use", 640))!;
        Assert.Equal(TranscriptKind.Assistant, entry.Kind);
        Assert.Equal("msg_1", entry.MessageId);
        Assert.Equal(new TokenUsage(10, 640, 0, 1000, 50000), entry.Usage);
        Assert.Equal(51010, entry.Usage.AllInput);
        Assert.Equal("tool_use", entry.StopReason);
    }

    [Theory]
    [InlineData(Prompt, TranscriptKind.Prompt)]
    [InlineData(ToolResult, TranscriptKind.ToolResult)]
    [InlineData(Interrupted, TranscriptKind.Interrupted)]
    public void ClassifiesUserLines(string line, TranscriptKind kind) => Assert.Equal(kind, ClaudeTranscript.Parse(line)!.Kind);

    [Theory]
    [InlineData("""{"type":"user","isMeta":true,"message":{"role":"user","content":"Caveat"}}""")]
    [InlineData("""{"type":"user","message":{"role":"user","content":"<command-name>/model</command-name>"}}""")]
    [InlineData("""{"type":"assistant","message":{"model":"<synthetic>","id":"x","usage":{"input_tokens":0}}}""")]
    [InlineData("""{"type":"summary","summary":"..."}""")]
    [InlineData("""{"type":"assistant","message":""")]
    public void IgnoresLinesThatAreNotWork(string line) => Assert.Null(ClaudeTranscript.Parse(line));

    [Fact]
    public void SameMessageOnSeveralLinesCountsOnce()
    {
        var session = new AgentSession("s1");
        session.Apply(ClaudeTranscript.Parse(Prompt)!);
        session.Apply(ClaudeTranscript.Parse(Assistant("msg_1", "tool_use", 100))!);
        session.Apply(ClaudeTranscript.Parse(Assistant("msg_1", "tool_use", 640))!); // stessa risposta, blocco successivo
        var snapshot = session.Snapshot();
        Assert.Equal(640, snapshot.Total.Output);
        Assert.Equal(640, snapshot.TurnUsage.Output);
    }

    [Fact]
    public void EndTurnFinishesTheWork()
    {
        var session = new AgentSession("s1");
        Assert.Null(session.Apply(ClaudeTranscript.Parse(Prompt)!));
        Assert.Equal(AgentState.Working, session.State);
        Assert.Null(session.Apply(ClaudeTranscript.Parse(Assistant("msg_1", "tool_use", 200))!));
        Assert.Null(session.Apply(ClaudeTranscript.Parse(ToolResult)!));
        var turn = session.Apply(ClaudeTranscript.Parse(Assistant("msg_2", "end_turn", 300, "19:00:45"))!);

        Assert.NotNull(turn);
        Assert.Equal(AgentState.Done, session.State);
        Assert.Equal("nothing-shell", turn.Title);
        Assert.Equal(TimeSpan.FromSeconds(45), turn.Duration);
        Assert.Equal(500, turn.Usage.Output);
        Assert.True(AgentRules.ShouldAnnounce(turn));
    }

    [Fact]
    public void HistoryWithoutPromptDoesNotAnnounce()
    {
        var session = new AgentSession("s1");
        Assert.Null(session.Apply(ClaudeTranscript.Parse(Assistant("msg_2", "end_turn", 300))!));
        Assert.Equal(AgentState.Done, session.State);
    }

    [Fact]
    public void InterruptionStopsWithoutFinishing()
    {
        var session = new AgentSession("s1");
        session.Apply(ClaudeTranscript.Parse(Prompt)!);
        session.Apply(ClaudeTranscript.Parse(Interrupted)!);
        Assert.Equal(AgentState.Interrupted, session.State);
    }

    [Fact]
    public void SubagentsCountButDoNotEndTheTurn()
    {
        var session = new AgentSession("s1");
        session.Apply(ClaudeTranscript.Parse(Prompt)!);
        Assert.Null(session.Apply(ClaudeTranscript.Parse(Assistant("msg_s", "end_turn", 50, sidechain: true))!));
        Assert.Equal(AgentState.Working, session.State);
        Assert.Equal(50, session.Snapshot().Total.Output);
    }

    [Fact]
    public void NewPromptStartsANewTurn()
    {
        var session = new AgentSession("s1");
        session.Apply(ClaudeTranscript.Parse(Prompt)!);
        session.Apply(ClaudeTranscript.Parse(Assistant("msg_1", "end_turn", 300, "19:00:10"))!);
        session.Apply(ClaudeTranscript.Parse(Prompt.Replace("19:00:00", "19:05:00", StringComparison.Ordinal))!);
        session.Apply(ClaudeTranscript.Parse(Assistant("msg_2", "tool_use", 40, "19:05:05"))!);
        var snapshot = session.Snapshot();
        Assert.Equal(340, snapshot.Total.Output);
        Assert.Equal(40, snapshot.TurnUsage.Output);
    }

    [Fact]
    public void CostFollowsTheListPrice()
    {
        // Opus 5.5: $4 input, $20 output, lettura cache $0.20, scrittura 1 h = 2× input
        var price = AgentPricing.For("claude-opus-5-5")!;
        var cost = price.Cost(new TokenUsage(1_000_000, 1_000_000, 0, 1_000_000, 1_000_000));
        Assert.Equal(4 + 20 + 8 + 0.2, cost, 6);
        Assert.Equal(2 * cost, price.Cost(new TokenUsage(1_000_000, 1_000_000, 0, 1_000_000, 1_000_000), fast: true), 6);
    }

    [Theory]
    [InlineData("claude-opus-5-5", 4)]
    [InlineData("claude-opus-5", 5)]
    [InlineData("claude-fable-5-1", 10)]
    [InlineData("claude-sonnet-5-5", 2)]
    [InlineData("claude-sonnet-4-6", 3)]
    [InlineData("claude-haiku-5-5", 0.1)]
    [InlineData("claude-opus-4-1-20250805", 15)]
    [InlineData("anthropic.claude-opus-5-5", 4)]
    public void PicksTheMostSpecificModel(string model, double input) => Assert.Equal(input, AgentPricing.For(model)!.Input);

    [Fact]
    public void UnknownModelHasNoCost()
    {
        Assert.Null(AgentPricing.For("gpt-x"));
        var session = new AgentSession("s1");
        session.Apply(ClaudeTranscript.Parse(Prompt)!);
        session.Apply(ClaudeTranscript.Parse(Assistant("m", "end_turn", 10, model: "modello-x"))!);
        Assert.Null(session.Snapshot().Cost);
        Assert.Equal("$--", AgentFormat.Cost(null));
    }

    [Fact]
    public void HaikuLongPromptsCostMore()
    {
        var price = AgentPricing.For("claude-haiku-5-5")!;
        Assert.Equal(0.1, price.Cost(new TokenUsage(1_000_000 / 10, 0)) * 10, 6);
        Assert.Equal(0.5 * 0.2, price.Cost(new TokenUsage(200_000, 0)), 6);
    }

    [Theory]
    [InlineData(950, "950")]
    [InlineData(12_400, "12.4K")]
    [InlineData(262_000, "262K")]
    [InlineData(3_100_000, "3.1M")]
    public void FormatsTokens(long tokens, string text) => Assert.Equal(text, AgentFormat.Tokens(tokens));

    [Theory]
    [InlineData(0.004, "$0.004")]
    [InlineData(0.42, "$0.42")]
    [InlineData(12.3, "$12.30")]
    public void FormatsCost(double dollars, string text) => Assert.Equal(text, AgentFormat.Cost(dollars));

    [Theory]
    [InlineData("claude-opus-5-5", "Opus 5.5")]
    [InlineData("claude-sonnet-4-6[1m]", "Sonnet 4.6")]
    [InlineData("claude-opus-4-1-20250805", "Opus 4.1")]
    [InlineData("gpt-x", "gpt-x")]
    public void ShortModelNames(string model, string name) => Assert.Equal(name, AgentPricing.ShortName(model));

    [Fact]
    public void ReportsFromOtherAgents()
    {
        var report = AgentProtocol.Parse("""{"event":"agent","id":"a1","title":"Report","model":"claude-sonnet-5-5","input":1000000,"output":100000,"state":"done"}""")!;
        Assert.Equal(AgentState.Done, report.State);
        Assert.Equal(2 + 1, report.Cost!.Value, 6);
        Assert.Equal(0.5, AgentProtocol.Parse("""{"event":"agent","id":"a1","cost":0.5}""")!.Cost);
        Assert.Null(AgentProtocol.Parse("""{"event":"start","id":"a1","title":"x"}"""));
        Assert.Null(AgentProtocol.Parse("""{"event":"agent"}"""));
    }
}
