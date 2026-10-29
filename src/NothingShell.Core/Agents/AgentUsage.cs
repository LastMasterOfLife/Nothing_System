using System.Globalization;
using System.Text.Json;

namespace NothingShell.Core.Agents;

/// <summary>Token di una o più risposte dell'API (docs/14-agenti-connettori.md).</summary>
public readonly record struct TokenUsage(long Input, long Output, long CacheWrite5m = 0, long CacheWrite1h = 0, long CacheRead = 0)
{
    /// <summary>Tutto ciò che il modello ha letto: non in cache, scritto in cache e letto dalla cache.</summary>
    public long AllInput => Input + CacheWrite5m + CacheWrite1h + CacheRead;

    public static TokenUsage operator +(TokenUsage a, TokenUsage b) => new(a.Input + b.Input, a.Output + b.Output,
        a.CacheWrite5m + b.CacheWrite5m, a.CacheWrite1h + b.CacheWrite1h, a.CacheRead + b.CacheRead);

    public static TokenUsage operator -(TokenUsage a, TokenUsage b) => new(a.Input - b.Input, a.Output - b.Output,
        a.CacheWrite5m - b.CacheWrite5m, a.CacheWrite1h - b.CacheWrite1h, a.CacheRead - b.CacheRead);
}

/// <summary>Prezzi in dollari per milione di token; la scrittura in cache costa 1,25× (5 minuti) o 2× (1 ora) l'input.</summary>
public sealed record ModelPrice(double Input, double Output, double CacheRead)
{
    /// <summary>Oltre questo numero di token di prompt per richiesta vale <see cref="LongPrompt"/> (Haiku 5.5).</summary>
    public long? LongPromptThreshold { get; init; }

    public ModelPrice? LongPrompt { get; init; }

    public double Cost(TokenUsage usage, bool fast = false)
    {
        var price = LongPrompt is not null && usage.AllInput > LongPromptThreshold ? LongPrompt : this;
        var dollars = (usage.Input * price.Input
                       + usage.CacheWrite5m * price.Input * 1.25
                       + usage.CacheWrite1h * price.Input * 2
                       + usage.CacheRead * price.CacheRead
                       + usage.Output * price.Output) / 1_000_000;
        return fast ? dollars * 2 : dollars; // modalità veloce: tariffa doppia
    }
}

/// <summary>
/// Listino dell'API di Anthropic (prezzi di prima parte, ottobre 2026). Per chi usa Claude Code con un abbonamento il
/// costo è una stima di quanto costerebbe a consumo, non quello che paga.
/// </summary>
public static class AgentPricing
{
    // Dal più specifico al più generico: "claude-opus-5" è anche il prefisso di "claude-opus-5-5".
    private static readonly (string Prefix, ModelPrice Price)[] Table =
    [
        ("claude-fable-5-1", new(10, 50, 0.25)),
        ("claude-mythos-5-1", new(10, 50, 0.25)),
        ("claude-fable-5", new(10, 50, 1)),
        ("claude-mythos-5", new(10, 50, 1)),
        ("claude-opus-5-5", new(4, 20, 0.20)),
        ("claude-opus-5", new(5, 25, 0.5)),
        ("claude-opus-4-8", new(5, 25, 0.5)),
        ("claude-opus-4-7", new(5, 25, 0.5)),
        ("claude-opus-4-6", new(5, 25, 0.5)),
        ("claude-opus-4-5", new(5, 25, 0.5)),
        ("claude-opus-4", new(15, 75, 1.5)),
        ("claude-sonnet-5-5", new(2, 10, 0.2)),
        ("claude-sonnet-5", new(2, 10, 0.2)),
        ("claude-sonnet-4", new(3, 15, 0.3)),
        ("claude-haiku-5-5", new(0.1, 0.5, 0.01) { LongPromptThreshold = 100_000, LongPrompt = new(0.5, 2.5, 0.05) }),
        ("claude-haiku-4-5", new(1, 5, 0.1)),
    ];

    /// <summary>Null per i modelli che non conosciamo: il costo resta ignoto invece che sbagliato.</summary>
    public static ModelPrice? For(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;
        var id = model.Trim().ToLowerInvariant();
        if (id.StartsWith("anthropic.", StringComparison.Ordinal))
            id = id["anthropic.".Length..]; // nomi di Bedrock
        foreach (var (prefix, price) in Table)
            if (id.StartsWith(prefix, StringComparison.Ordinal))
                return price;
        return null;
    }

    /// <summary>"claude-opus-5-5" → "Opus 5.5"; gli altri nomi restano come sono.</summary>
    public static string ShortName(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return string.Empty;
        var parts = model.Split('[')[0].Split('-');
        if (parts.Length < 3 || parts[0] != "claude")
            return model;
        var family = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(parts[1]);
        var version = parts.Skip(2).TakeWhile(p => p.Length <= 2 && p.All(char.IsAsciiDigit)).ToArray();
        return version.Length == 0 ? family : $"{family} {string.Join('.', version)}";
    }
}

public static class AgentFormat
{
    /// <summary>950 · 12,4K · 3,1M: compatto per la pillola e le righe.</summary>
    public static string Tokens(long tokens) => tokens switch
    {
        < 1_000 => tokens.ToString(CultureInfo.InvariantCulture),
        < 100_000 => (tokens / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        < 1_000_000 => (tokens / 1_000).ToString(CultureInfo.InvariantCulture) + "K",
        _ => (tokens / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M",
    };

    /// <summary>$0.004 · $0.42 · $12.30; null = prezzo del modello non noto.</summary>
    public static string Cost(double? dollars) => dollars switch
    {
        null => "$--",
        < 0.01 when dollars > 0 => "$" + dollars.Value.ToString("0.000", CultureInfo.InvariantCulture),
        _ => "$" + dollars.Value.ToString("0.00", CultureInfo.InvariantCulture),
    };
}

public enum TranscriptKind
{
    /// <summary>Richiesta scritta dall'utente: inizia un turno di lavoro.</summary>
    Prompt,

    /// <summary>Risultato di uno strumento: l'agente sta ancora lavorando.</summary>
    ToolResult,

    /// <summary>L'utente ha interrotto (Esc).</summary>
    Interrupted,

    Assistant,
}

/// <summary>Una riga utile di un transcript di Claude Code (<c>~/.claude/projects/&lt;progetto&gt;/&lt;sessione&gt;.jsonl</c>).</summary>
public sealed record TranscriptEntry(TranscriptKind Kind, DateTimeOffset Timestamp)
{
    public string? MessageId { get; init; }

    public string? Model { get; init; }

    public TokenUsage Usage { get; init; }

    public string? StopReason { get; init; }

    public bool Fast { get; init; }

    /// <summary>Riga di un sotto-agente: conta nei token ma non nello stato della sessione.</summary>
    public bool Sidechain { get; init; }

    public string? Cwd { get; init; }

    public string? SessionId { get; init; }
}

/// <summary>Lettura dei transcript di Claude Code: solo token, modello e stato, mai il contenuto dei messaggi.</summary>
public static class ClaudeTranscript
{
    /// <summary>Null per le righe che non servono (riepiloghi, allegati, righe rotte o tagliate a metà).</summary>
    public static TranscriptEntry? Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        // la maggior parte delle righe (richieste salvate, allegati) non interessa: si evita il parsing
        if (!line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal) && !line.Contains("\"type\":\"user\"", StringComparison.Ordinal))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "type") is not { } type)
                return null;
            var timestamp = Text(root, "timestamp") is { } t && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts)
                ? ts
                : DateTimeOffset.MinValue;
            var sidechain = root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True;
            var cwd = Text(root, "cwd");
            var session = Text(root, "sessionId");
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                return null;

            switch (type)
            {
                case "assistant":
                {
                    var model = Text(message, "model");
                    if (model == "<synthetic>")
                        return null; // messaggi generati da Claude Code, senza chiamata all'API
                    var usage = message.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? ReadUsage(u) : default;
                    return new TranscriptEntry(TranscriptKind.Assistant, timestamp)
                    {
                        MessageId = Text(message, "id"),
                        Model = model,
                        Usage = usage,
                        StopReason = Text(message, "stop_reason"),
                        Fast = u.ValueKind == JsonValueKind.Object && Text(u, "speed") == "fast",
                        Sidechain = sidechain,
                        Cwd = cwd,
                        SessionId = session,
                    };
                }
                case "user":
                {
                    if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True)
                        return null;
                    var kind = UserKind(message);
                    return kind is { } k ? new TranscriptEntry(k, timestamp) { Sidechain = sidechain, Cwd = cwd, SessionId = session } : null;
                }
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TranscriptKind? UserKind(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
            return null;
        if (content.ValueKind == JsonValueKind.String)
            return Classify(content.GetString() ?? string.Empty);
        if (content.ValueKind != JsonValueKind.Array)
            return null;
        string? text = null;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
                continue;
            switch (Text(block, "type"))
            {
                case "tool_result":
                    return TranscriptKind.ToolResult;
                case "text":
                    text ??= Text(block, "text");
                    break;
            }
        }
        return text is null ? null : Classify(text);
    }

    private static TranscriptKind? Classify(string text)
    {
        if (text.StartsWith("[Request interrupted", StringComparison.Ordinal))
            return TranscriptKind.Interrupted;
        // comandi locali (/model, /clear…) e il loro output non fanno lavorare l'agente
        if (text.StartsWith("<command-", StringComparison.Ordinal) || text.StartsWith("<local-command", StringComparison.Ordinal))
            return null;
        return text.Trim().Length == 0 ? null : TranscriptKind.Prompt;
    }

    private static TokenUsage ReadUsage(JsonElement usage)
    {
        long write5 = 0, write1h = 0;
        var write = Number(usage, "cache_creation_input_tokens");
        if (usage.TryGetProperty("cache_creation", out var split) && split.ValueKind == JsonValueKind.Object)
        {
            write5 = Number(split, "ephemeral_5m_input_tokens");
            write1h = Number(split, "ephemeral_1h_input_tokens");
        }
        if (write5 + write1h != write)
            (write5, write1h) = (write, 0); // senza dettaglio: durata di serie, 5 minuti
        return new TokenUsage(Number(usage, "input_tokens"), Number(usage, "output_tokens"), write5, write1h,
            Number(usage, "cache_read_input_tokens"));
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n > 0 ? n : 0;
}

public enum AgentState
{
    /// <summary>Nessun turno visto ancora (sessione appena letta dal disco).</summary>
    Idle,
    Working,
    Done,
    Interrupted,
}

/// <summary>Un turno finito: per la scheda nel notch.</summary>
public sealed record AgentTurn(string SessionId, string Title, string? Model, TimeSpan Duration, TokenUsage Usage, double? Cost);

/// <summary>Stato di una sessione da mostrare: totali, turno in corso, costo.</summary>
public sealed record AgentSnapshot(
    string Id,
    string Source,
    string Title,
    string? Cwd,
    string? Model,
    AgentState State,
    TokenUsage Total,
    double? Cost,
    TokenUsage TurnUsage,
    double? TurnCost,
    DateTimeOffset TurnStarted,
    DateTimeOffset LastActivity);

/// <summary>
/// Somma i token di una sessione di Claude Code riga per riga. Claude Code scrive la stessa risposta su più righe (una
/// per blocco di contenuto) ripetendo l'uso: per ogni id di messaggio vale l'ultimo.
/// </summary>
public sealed class AgentSession(string id, string source = "Claude Code")
{
    private readonly string _source = source;
    private readonly Dictionary<string, Counted> _messages = new(StringComparer.Ordinal);
    private TokenUsage _total;
    private TokenUsage _turn;
    private double _cost;
    private double _turnCost;
    private bool _costUnknown;
    private bool _turnCostUnknown;

    public string Id { get; } = id;

    public string? Cwd { get; private set; }

    public string? Model { get; private set; }

    public AgentState State { get; private set; } = AgentState.Idle;

    public DateTimeOffset TurnStarted { get; private set; }

    public DateTimeOffset LastActivity { get; private set; }

    /// <summary>Nome del progetto: l'ultima cartella di <see cref="Cwd"/>.</summary>
    public string Title => Cwd is { Length: > 0 } cwd ? Path.GetFileName(cwd.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : cwd : Id[..Math.Min(8, Id.Length)];

    /// <summary>Applica una riga; restituisce il turno se questa riga lo chiude (fine del lavoro richiesto).</summary>
    public AgentTurn? Apply(TranscriptEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Cwd is { Length: > 0 } cwd && !entry.Sidechain)
            Cwd = cwd;
        if (entry.Timestamp > LastActivity)
            LastActivity = entry.Timestamp;

        switch (entry.Kind)
        {
            case TranscriptKind.Prompt when !entry.Sidechain:
                State = AgentState.Working;
                TurnStarted = entry.Timestamp;
                _turn = default;
                _turnCost = 0;
                _turnCostUnknown = false;
                return null;
            case TranscriptKind.ToolResult when !entry.Sidechain:
                if (State != AgentState.Idle)
                    State = AgentState.Working;
                return null;
            case TranscriptKind.Interrupted when !entry.Sidechain:
                if (State == AgentState.Working)
                    State = AgentState.Interrupted;
                return null;
            case TranscriptKind.Assistant:
                Count(entry);
                if (entry.Sidechain)
                    return null;
                if (entry.StopReason == "end_turn")
                {
                    var finished = State == AgentState.Working;
                    State = AgentState.Done;
                    return finished
                        ? new AgentTurn(Id, Title, Model, entry.Timestamp - TurnStarted, _turn, _turnCostUnknown ? null : _turnCost)
                        : null;
                }
                if (entry.StopReason is "tool_use" or "pause_turn" && State != AgentState.Idle)
                    State = AgentState.Working;
                return null;
            default:
                return null;
        }
    }

    public AgentSnapshot Snapshot() => new(Id, _source, Title, Cwd, Model, State, _total,
        _costUnknown ? null : _cost, _turn, _turnCostUnknown ? null : _turnCost, TurnStarted, LastActivity);

    /// <summary>Costo delle risposte arrivate da <paramref name="since"/> in poi (es. da mezzanotte); null se un modello non ha prezzo.</summary>
    public double? CostSince(DateTimeOffset since)
    {
        double sum = 0;
        foreach (var counted in _messages.Values.Where(m => m.At >= since))
        {
            if (counted.Cost is not { } c)
                return null;
            sum += c;
        }
        return sum;
    }

    private void Count(TranscriptEntry entry)
    {
        if (entry.Model is { Length: > 0 } model && !entry.Sidechain)
            Model = model;
        var price = AgentPricing.For(entry.Model);
        double? cost = price?.Cost(entry.Usage, entry.Fast);
        var key = entry.MessageId ?? $"#{_messages.Count}";
        var inTurn = entry.Timestamp >= TurnStarted;
        if (_messages.TryGetValue(key, out var old))
        {
            _total -= old.Usage;
            _cost -= old.Cost ?? 0;
            if (old.InTurn)
            {
                _turn -= old.Usage;
                _turnCost -= old.Cost ?? 0;
            }
            inTurn = old.InTurn;
        }
        _messages[key] = new Counted(entry.Usage, cost, entry.Timestamp, inTurn);
        _total += entry.Usage;
        _cost += cost ?? 0;
        _costUnknown |= cost is null && entry.Usage != default;
        if (inTurn)
        {
            _turn += entry.Usage;
            _turnCost += cost ?? 0;
            _turnCostUnknown |= cost is null && entry.Usage != default;
        }
    }

    private sealed record Counted(TokenUsage Usage, double? Cost, DateTimeOffset At, bool InTurn);
}

/// <summary>
/// Agenti qualsiasi che parlano alla shell dalla pipe <c>NothingShell.Tasks</c>: un messaggio
/// <c>{"event":"agent","id":"…","title":"…","model":"…","input":…,"output":…,"cacheRead":…,"cost":…,"state":"working|done"}</c>
/// con i totali della sessione fino a quel momento (docs/14-agenti-connettori.md).
/// </summary>
public sealed record AgentReport(string Id, string Title, string? Model, TokenUsage Usage, double? Cost, AgentState State, string? Cwd);

public static class AgentProtocol
{
    /// <summary>Null se la riga non è un messaggio di un agente (o è rotto).</summary>
    public static AgentReport? Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !string.Equals(Text(root, "event"), "agent", StringComparison.OrdinalIgnoreCase)
                || Text(root, "id") is not { Length: > 0 } id)
                return null;
            var state = Text(root, "state")?.ToLowerInvariant() switch
            {
                "done" or "finished" or "end" => AgentState.Done,
                "interrupted" or "cancelled" or "canceled" => AgentState.Interrupted,
                _ => AgentState.Working,
            };
            var usage = new TokenUsage(Whole(root, "input"), Whole(root, "output"), Whole(root, "cacheWrite"), 0, Whole(root, "cacheRead"));
            var model = Text(root, "model");
            var cost = root.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out var d) && d >= 0
                ? d
                : AgentPricing.For(model)?.Cost(usage);
            return new AgentReport(id, Text(root, "title") ?? id, model, usage, cost, state, Text(root, "cwd"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static long Whole(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n > 0 ? n : 0;
}

public static class AgentRules
{
    /// <summary>Le risposte brevi non meritano una scheda: solo i turni di almeno così.</summary>
    public static readonly TimeSpan AnnounceAfter = TimeSpan.FromSeconds(10);

    /// <summary>Una sessione senza righe nuove da così tanto non compare più fra le attive.</summary>
    public static readonly TimeSpan Recent = TimeSpan.FromHours(3);

    /// <summary>"In corso" senza righe nuove da così tanto: probabilmente chiusa senza fine turno.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(20);

    public static bool ShouldAnnounce(AgentTurn turn) => turn.Duration >= AnnounceAfter;
}
