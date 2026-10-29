using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NothingShell.Core.Agents;
using NothingShell.Core.Plugins;
using NothingShell.Core.Storage;

namespace NothingShell.Core.Connectors;

/// <summary>Come il sito racconta l'uso del modello: totali che crescono o un elenco di singole chiamate.</summary>
public enum LlmMode
{
    /// <summary>Contatori totali (da sempre, del giorno…): conta la differenza fra un giro e l'altro.</summary>
    Totals,

    /// <summary>Un elenco di chiamate, ognuna con un id: ogni chiamata si conta una volta sola.</summary>
    Calls,
}

/// <summary>Prezzo per milione di token, per modelli che non sono nel listino di Anthropic (o prezzi propri).</summary>
public sealed record LlmPrice(double Input, double Output, double CacheRead, string Currency);

/// <summary>
/// Blocco <c>"llm"</c> di un connettore (docs/14-agenti-connettori.md): dove sono modello, token e costo nella risposta
/// del sito, budget e cambio. I campi sono segnaposto come nelle letture (<c>"{usage.input_tokens}"</c>); il modello
/// può essere anche un nome fisso.
/// </summary>
public sealed record LlmSpec
{
    public const string Usd = "USD";
    public const string Eur = "EUR";

    public LlmMode Mode { get; init; } = LlmMode.Totals;

    /// <summary>Percorso dell'elenco (una voce per modello o per chiamata); null = la risposta è una voce sola.</summary>
    public string? List { get; init; }

    public string? Model { get; init; }

    public string? Input { get; init; }

    public string? Output { get; init; }

    public string? CacheRead { get; init; }

    /// <summary>Scrittura in cache di 5 minuti (1,25× l'input).</summary>
    public string? CacheWrite { get; init; }

    /// <summary>Scrittura in cache di 1 ora (2× l'input).</summary>
    public string? CacheWrite1h { get; init; }

    /// <summary>Numero di chiamate (nei totali); con <see cref="LlmMode.Calls"/> ogni voce vale una chiamata.</summary>
    public string? Requests { get; init; }

    /// <summary>Costo già calcolato dal sito: vince sul listino.</summary>
    public string? Cost { get; init; }

    public string CostCurrency { get; init; } = Usd;

    /// <summary><c>0.01</c> se il costo è in centesimi.</summary>
    public double CostScale { get; init; } = 1;

    /// <summary>Id della chiamata (<see cref="LlmMode.Calls"/>); senza, l'impronta della voce.</summary>
    public string? Id { get; init; }

    /// <summary>Quando è avvenuta la chiamata: la conta nel suo giorno invece che oggi.</summary>
    public string? At { get; init; }

    public LlmPrice? Price { get; init; }

    /// <summary>Euro al giorno oltre i quali il valore diventa un avviso.</summary>
    public double? DailyBudget { get; init; }

    public double? MonthlyBudget { get; init; }

    /// <summary>Cambio fisso dollari → euro; senza, quello del giorno della BCE.</summary>
    public double? UsdToEur { get; init; }

    /// <summary>
    /// Nei totali la prima lettura fa da punto di partenza e non si conta (un contatore "da sempre" non è la spesa di
    /// oggi). Con contatori del giorno (l'Admin API di Anthropic) va contata: true.
    /// </summary>
    public bool CountFirst { get; init; }

    public static LlmSpec? Parse(JsonElement element, out string? error)
    {
        error = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "\"llm\" deve essere un oggetto";
            return null;
        }
        var mode = Text(element, "mode")?.ToLowerInvariant() switch
        {
            null or "totals" or "totali" => LlmMode.Totals,
            "calls" or "chiamate" => LlmMode.Calls,
            _ => (LlmMode?)null,
        };
        if (mode is null)
        {
            error = "\"llm.mode\" deve essere totals o calls";
            return null;
        }
        LlmPrice? price = null;
        if (element.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            if (Number(p, "input") is not { } input || Number(p, "output") is not { } output)
            {
                error = "\"llm.price\" vuole input e output (per milione di token)";
                return null;
            }
            price = new LlmPrice(input, output, Number(p, "cacheRead") ?? input / 10, Currency(Text(p, "currency")) ?? Usd);
        }
        var spec = new LlmSpec
        {
            Mode = mode.Value,
            List = Text(element, "list"),
            Model = Text(element, "model"),
            Input = Text(element, "input"),
            Output = Text(element, "output"),
            CacheRead = Text(element, "cacheRead"),
            CacheWrite = Text(element, "cacheWrite"),
            CacheWrite1h = Text(element, "cacheWrite1h"),
            Requests = Text(element, "requests"),
            Cost = Text(element, "cost"),
            CostCurrency = Currency(Text(element, "costCurrency")) ?? Usd,
            CostScale = Number(element, "costScale") ?? 1,
            Id = Text(element, "id"),
            At = Text(element, "at"),
            Price = price,
            DailyBudget = Number(element, "dailyBudget"),
            MonthlyBudget = Number(element, "monthlyBudget"),
            UsdToEur = Number(element, "usdToEur") is > 0 and var rate ? rate : null,
            CountFirst = element.TryGetProperty("countFirst", out var c) && c.ValueKind == JsonValueKind.True,
        };
        if (spec.Input is null && spec.Output is null && spec.Cost is null)
            error = "\"llm\" vuole almeno input, output o cost";
        return error is null ? spec : null;
    }

    /// <summary>Il blocco da scrivere nel file (impostazioni); i campi vuoti restano fuori.</summary>
    public JsonObject ToJson()
    {
        var o = new JsonObject();
        void Put(string key, JsonNode? value)
        {
            if (value is not null)
                o[key] = value;
        }
        Put("mode", Mode == LlmMode.Calls ? "calls" : "totals");
        Put("list", List);
        Put("model", Model);
        Put("input", Input);
        Put("output", Output);
        Put("cacheRead", CacheRead);
        Put("cacheWrite", CacheWrite);
        Put("cacheWrite1h", CacheWrite1h);
        Put("requests", Requests);
        Put("cost", Cost);
        if (Cost is not null)
        {
            Put("costCurrency", CostCurrency);
            if (CostScale != 1)
                Put("costScale", CostScale);
        }
        Put("id", Id);
        Put("at", At);
        if (Price is { } price)
            o["price"] = new JsonObject { ["input"] = price.Input, ["output"] = price.Output, ["cacheRead"] = price.CacheRead, ["currency"] = price.Currency };
        Put("dailyBudget", DailyBudget);
        Put("monthlyBudget", MonthlyBudget);
        Put("usdToEur", UsdToEur);
        if (CountFirst)
            Put("countFirst", true);
        return o;
    }

    private static string? Currency(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        "EUR" or "€" => Eur,
        "USD" or "$" => Usd,
        _ => null,
    };

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s.Trim() : null;

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(v.GetString()?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        } : null;
}

/// <summary>Un giorno di un connettore: chiamate, token e costo. Il costo in dollari si converte quando si mostra.</summary>
public sealed class LlmDay
{
    public long Calls { get; set; }

    public long Input { get; set; }

    public long Output { get; set; }

    public long CacheRead { get; set; }

    public long CacheWrite { get; set; }

    /// <summary>Costo in dollari (listino di Anthropic o costo del sito in dollari).</summary>
    public double Usd { get; set; }

    /// <summary>Costo già in euro (costo del sito in euro, prezzi propri in euro).</summary>
    public double Eur { get; set; }

    /// <summary>Token di un modello senza prezzo: il costo è parziale.</summary>
    public long UnpricedTokens { get; set; }

    public void Add(LlmDay other)
    {
        Calls += other.Calls;
        Input += other.Input;
        Output += other.Output;
        CacheRead += other.CacheRead;
        CacheWrite += other.CacheWrite;
        Usd += other.Usd;
        Eur += other.Eur;
        UnpricedTokens += other.UnpricedTokens;
    }

    /// <summary>Tutto in euro; null se ci sono dollari ma il cambio non si conosce ancora.</summary>
    public double? InEuro(double? usdToEur) => Usd == 0 ? Eur : usdToEur is { } rate ? Eur + Usd * rate : null;
}

/// <summary>Una chiamata (o, nei totali, quanto è cambiato in un giro), per l'elenco delle ultime.</summary>
public sealed record LlmCall(DateTimeOffset At, string Model, long Input, long Output, long Calls, double Usd, double Eur, bool Priced);

/// <summary>Il registro di un connettore.</summary>
public sealed class LlmBook
{
    public const int SeenCapacity = 2000;
    public const int RecentCapacity = 30;

    /// <summary>Per giorno (<c>yyyy-MM-dd</c>, ora locale).</summary>
    public Dictionary<string, LlmDay> Days { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Ultimi totali letti, per modello (<see cref="LlmMode.Totals"/>).</summary>
    public Dictionary<string, double[]> Last { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Id delle chiamate già contate (<see cref="LlmMode.Calls"/>), dal più vecchio.</summary>
    public List<string> Seen { get; set; } = [];

    /// <summary>Dalla più recente.</summary>
    public List<LlmCall> Recent { get; set; } = [];
}

/// <summary>Spesa di oggi e del mese di un connettore, in euro.</summary>
public sealed record LlmSummary(LlmDay Today, LlmDay Month, double? TodayEur, double? MonthEur);

/// <summary>
/// Registro dei costi LLM dei connettori (<c>llm-costs.json</c>): per ogni connettore i giorni con chiamate, token e
/// costo, gli ultimi totali letti e le chiamate già contate, così un riavvio non conta due volte.
/// </summary>
public sealed class LlmLedger
{
    public const int KeepDays = 400;

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

    public Dictionary<string, LlmBook> Books { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Ultimo cambio dollari → euro della BCE.</summary>
    public double? UsdToEur { get; set; }

    public DateOnly? RateDate { get; set; }

    public static string DefaultPath { get; } = System.IO.Path.Combine(AtomicFile.DataFolder, "llm-costs.json");

    public static LlmLedger Load(string path)
    {
        try
        {
            return AtomicFile.TryReadAllText(path) is { } json ? JsonSerializer.Deserialize<LlmLedger>(json, Options) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void Save(string path) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, Options));

    public static string Key(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Togli il registro di un connettore rimosso.</summary>
    public bool Forget(string connectorId) => Books.Remove(connectorId);

    /// <summary>
    /// Legge l'uso dalla risposta del sito e lo aggiunge al registro. True se qualcosa è cambiato (da salvare).
    /// </summary>
    public bool Apply(string connectorId, LlmSpec spec, JsonElement root, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!Books.TryGetValue(connectorId, out var book))
            Books[connectorId] = book = new LlmBook();
        var changed = false;
        foreach (var item in Items(spec, root))
        {
            var model = Resolve(spec.Model, item) ?? string.Empty;
            var current = new[]
            {
                Count(spec.Input, item), Count(spec.Output, item), Count(spec.CacheRead, item), Count(spec.CacheWrite, item),
                Count(spec.CacheWrite1h, item), Count(spec.Requests, item), Count(spec.Cost, item),
            };
            double[] delta;
            var at = now;
            if (spec.Mode == LlmMode.Calls)
            {
                var id = Resolve(spec.Id, item) ?? Fingerprint(item);
                if (book.Seen.Contains(id, StringComparer.Ordinal))
                    continue;
                book.Seen.Add(id);
                if (book.Seen.Count > LlmBook.SeenCapacity)
                    book.Seen.RemoveRange(0, book.Seen.Count - LlmBook.SeenCapacity);
                changed = true;
                delta = current;
                if (spec.Requests is null)
                    delta[5] = 1;
                if (Resolve(spec.At, item) is { } when && DateTimeOffset.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                    at = parsed;
            }
            else
            {
                var had = book.Last.TryGetValue(model, out var previous);
                book.Last[model] = current;
                changed |= !had || !previous!.SequenceEqual(current);
                if (!had && !spec.CountFirst)
                    continue; // punto di partenza
                delta = had ? current.Zip(previous!, (c, p) => c - p).ToArray() : current;
                if (delta.Any(d => d < 0))
                    delta = current; // il contatore è ripartito (nuovo giorno, sito riavviato)
            }
            if (delta.All(d => d == 0))
                continue;
            Add(book, spec, model, delta, at);
            changed = true;
        }
        var oldest = Key(DateOnly.FromDateTime(now.LocalDateTime).AddDays(-KeepDays));
        foreach (var old in book.Days.Keys.Where(k => string.CompareOrdinal(k, oldest) < 0).ToArray())
            book.Days.Remove(old);
        return changed;
    }

    public LlmSummary Summary(string connectorId, DateOnly today, double? usdToEur)
    {
        var day = new LlmDay();
        var month = new LlmDay();
        if (Books.TryGetValue(connectorId, out var book))
        {
            if (book.Days.TryGetValue(Key(today), out var d))
                day.Add(d);
            var prefix = today.ToString("yyyy-MM-", CultureInfo.InvariantCulture);
            foreach (var (key, value) in book.Days)
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                    month.Add(value);
        }
        return new LlmSummary(day, month, day.InEuro(usdToEur), month.InEuro(usdToEur));
    }

    /// <summary>
    /// Valori del connettore: spesa di oggi e del mese in euro (avviso oltre il budget), chiamate e token di oggi.
    /// </summary>
    public static IReadOnlyList<ConnectorReading> Readings(LlmSpec spec, LlmSummary summary)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(summary);
        var partial = summary.Today.UnpricedTokens > 0 ? "*" : string.Empty;
        var list = new List<ConnectorReading>
        {
            Euro("LLM oggi" + partial, summary.TodayEur, spec.DailyBudget),
            Euro("LLM mese", summary.MonthEur, spec.MonthlyBudget),
        };
        if (spec.Mode == LlmMode.Calls || spec.Requests is not null)
            list.Add(new ConnectorReading("Chiamate oggi", summary.Today.Calls.ToString(CultureInfo.InvariantCulture), null, ReadingLevel.Normal) { Number = summary.Today.Calls });
        var tokens = summary.Today.Input + summary.Today.CacheRead + summary.Today.CacheWrite + summary.Today.Output;
        list.Add(new ConnectorReading("Token oggi", AgentFormat.Tokens(tokens), null, ReadingLevel.Normal) { Number = tokens });
        return list;
    }

    public static string FormatEuro(double? euro) => euro switch
    {
        null => "--",
        < 100 => euro.Value.ToString("0.00", CultureInfo.InvariantCulture),
        _ => euro.Value.ToString("0", CultureInfo.InvariantCulture),
    };

    /// <summary>Esportazione per foglio di calcolo: un giorno per riga e per connettore, euro col cambio di adesso.</summary>
    public string Csv(Func<string, string> nameOf, double? usdToEur)
    {
        ArgumentNullException.ThrowIfNull(nameOf);
        var csv = new StringBuilder("data;connettore;chiamate;token_in;token_out;cache_letta;cache_scritta;costo_eur;costo_usd;token_senza_prezzo\r\n");
        foreach (var (id, book) in Books.OrderBy(b => b.Key, StringComparer.Ordinal))
            foreach (var (day, d) in book.Days.OrderBy(d => d.Key, StringComparer.Ordinal))
            {
                var eur = d.InEuro(usdToEur);
                csv.Append(CultureInfo.InvariantCulture,
                    $"{day};{nameOf(id).Replace(';', ',')};{d.Calls};{d.Input};{d.Output};{d.CacheRead};{d.CacheWrite};" +
                    $"{(eur is { } e ? e.ToString("0.0000", CultureInfo.InvariantCulture) : string.Empty)};{d.Usd.ToString("0.0000", CultureInfo.InvariantCulture)};{d.UnpricedTokens}\r\n");
            }
        return csv.ToString();
    }

    /// <summary>Costo di un pezzo d'uso: quello del sito, i prezzi propri o il listino di Anthropic. Null = senza prezzo.</summary>
    public static (double Usd, double Eur)? CostOf(LlmSpec spec, string model, double[] delta)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(delta);
        var (input, output, cacheRead, cacheWrite, cacheWrite1h) = (delta[0], delta[1], delta[2], delta[3], delta[4]);
        if (spec.Cost is not null)
        {
            var amount = delta[6] * spec.CostScale;
            return spec.CostCurrency == LlmSpec.Eur ? (0, amount) : (amount, 0);
        }
        if (spec.Price is { } p)
        {
            var amount = (input * p.Input + cacheWrite * p.Input * 1.25 + cacheWrite1h * p.Input * 2 + cacheRead * p.CacheRead + output * p.Output) / 1_000_000;
            return p.Currency == LlmSpec.Eur ? (0, amount) : (amount, 0);
        }
        // la tariffa per i prompt lunghi (Haiku 5.5) vale per richiesta: sui totali di più chiamate non si può sapere
        return AgentPricing.For(model) is { } listed && (spec.Mode == LlmMode.Calls ? listed : listed with { LongPrompt = null }) is var price
            ? (price.Cost(new TokenUsage((long)input, (long)output, (long)cacheWrite, (long)cacheWrite1h, (long)cacheRead)), 0)
            : null;
    }

    private static void Add(LlmBook book, LlmSpec spec, string model, double[] delta, DateTimeOffset at)
    {
        var key = Key(DateOnly.FromDateTime(at.LocalDateTime));
        if (!book.Days.TryGetValue(key, out var day))
            book.Days[key] = day = new LlmDay();
        var cost = CostOf(spec, model, delta);
        var piece = new LlmDay
        {
            Calls = (long)delta[5],
            Input = (long)delta[0],
            Output = (long)delta[1],
            CacheRead = (long)delta[2],
            CacheWrite = (long)(delta[3] + delta[4]),
            Usd = cost?.Usd ?? 0,
            Eur = cost?.Eur ?? 0,
            UnpricedTokens = cost is null ? (long)(delta[0] + delta[1] + delta[2] + delta[3] + delta[4]) : 0,
        };
        day.Add(piece);
        book.Recent.Insert(0, new LlmCall(at, model, piece.Input + piece.CacheRead + piece.CacheWrite, piece.Output, piece.Calls, piece.Usd, piece.Eur, cost is not null));
        if (book.Recent.Count > LlmBook.RecentCapacity)
            book.Recent.RemoveRange(LlmBook.RecentCapacity, book.Recent.Count - LlmBook.RecentCapacity);
    }

    private static JsonElement[] Items(LlmSpec spec, JsonElement root)
    {
        if (spec.List is null)
            return [root];
        var path = spec.List.Trim('{', '}', ' ');
        return JsonTemplate.TryResolve(root, path, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).ToArray()
            : [];
    }

    /// <summary><c>"{percorso}"</c> → il valore nella voce; un testo senza graffe è un valore fisso (il nome del modello).</summary>
    private static string? Resolve(string? template, JsonElement item)
    {
        if (template is null)
            return null;
        if (!(template.StartsWith('{') && template.EndsWith('}')))
            return template;
        return JsonTemplate.TryResolve(item, template[1..^1].Trim(), out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;
    }

    private static double Count(string? template, JsonElement item) =>
        Resolve(template, item) is { } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 0;

    private static ConnectorReading Euro(string label, double? euro, double? budget)
    {
        var over = euro is { } e && budget is { } b && e > b;
        return new ConnectorReading(label, FormatEuro(euro), "€", over ? ReadingLevel.Warning : ReadingLevel.Normal) { Number = euro };
    }

    private static string Fingerprint(JsonElement item) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.GetRawText())))[..16];
}
