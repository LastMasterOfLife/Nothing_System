using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NothingShell.Core.PortalDebug;

/// <summary>Risposta di <c>/_nothing/pair</c>: il token da tenere nel Gestore credenziali e chi è il portale.</summary>
public sealed record PairResult(string Token, PortalInfo Info);

/// <summary>
/// Il protocollo fra la shell e il portale (docs/24-debug-potenziato.md): solo HTTP e JSON, la shell chiede e il portale
/// risponde, quindi funziona anche con un portale su un altro PC o in una macchina virtuale senza aprire porte sul PC.
/// Le letture sono tolleranti: un campo che manca o è di un altro tipo non fa perdere l'evento.
/// </summary>
public static class DebugProtocol
{
    /// <summary>Prefisso degli indirizzi che l'app Django aggiunge al portale.</summary>
    public const string Prefix = "/_nothing";

    /// <summary>Versione del protocollo che la shell parla; il portale la riceve in <c>X-Nothing-Debug</c>.</summary>
    public const int Version = 1;

    /// <summary>Al massimo tanti eventi per pagina (il portale ne tiene 2000 in memoria).</summary>
    public const int PageSize = 500;

    /// <summary>Indirizzo di un endpoint del portale; <paramref name="address"/> è già normalizzato.</summary>
    public static Uri Endpoint(string address, string path)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(path);
        return new Uri($"{address.TrimEnd('/')}{Prefix}/{path.TrimStart('/')}", UriKind.Absolute);
    }

    /// <summary>Gli eventi dopo <paramref name="after"/> (0 = dall'inizio di ciò che il portale ha in memoria).</summary>
    public static Uri EventsEndpoint(string address, long after) =>
        Endpoint(address, $"events?after={after.ToString(CultureInfo.InvariantCulture)}&limit={PageSize.ToString(CultureInfo.InvariantCulture)}");

    public static string PairBody(string code, string client) =>
        JsonSerializer.Serialize(new Dictionary<string, object> { ["code"] = code, ["client"] = client, ["protocol"] = Version });

    public static PairResult? ParsePair(string json)
    {
        using var doc = TryParse(json);
        if (doc?.RootElement is not { ValueKind: JsonValueKind.Object } root)
            return null;
        var token = Str(root, "token");
        if (string.IsNullOrWhiteSpace(token))
            return null;
        return new PairResult(token, ParseInfo(root.TryGetProperty("portal", out var p) ? p : root));
    }

    public static PortalStatus? ParseStatus(string json)
    {
        using var doc = TryParse(json);
        if (doc?.RootElement is not { ValueKind: JsonValueKind.Object } root)
            return null;
        var checks = new List<PortalCheck>();
        if (root.TryGetProperty("checks", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || Str(item, "name") is not { Length: > 0 } name)
                    continue;
                var state = (Str(item, "state") ?? "ok").ToUpperInvariant() switch
                {
                    "WARN" or "WARNING" => CheckState.Warning,
                    "ERROR" or "FAIL" or "FAILING" or "DOWN" => CheckState.Failing,
                    _ => CheckState.Ok,
                };
                checks.Add(new PortalCheck(name, Text(item, "value") ?? string.Empty, state, Str(item, "detail")));
            }
        }
        return new PortalStatus(ParseInfo(root.TryGetProperty("portal", out var p) ? p : root), checks);
    }

    public static PortalInfo ParseInfo(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return new PortalInfo("Portale");
        IReadOnlyList<string>? connectors = null;
        if (element.TryGetProperty("connectors", out var c) && c.ValueKind == JsonValueKind.Array)
            connectors = [.. c.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)];
        return new PortalInfo(
            Str(element, "name") is { Length: > 0 } name ? name : "Portale",
            Str(element, "framework"),
            Str(element, "version"),
            connectors,
            element.TryGetProperty("debug", out var d) && d.ValueKind is JsonValueKind.True or JsonValueKind.False ? d.GetBoolean() : null,
            Num(element, "uptime"),
            Num(element, "pid") is { } pid ? (int)pid : null,
            Str(element, "root"));
    }

    /// <summary>Una pagina di <c>/_nothing/events</c>; null se il JSON non è una pagina.</summary>
    public static DebugPage? ParseEvents(string json)
    {
        using var doc = TryParse(json);
        if (doc?.RootElement is not { ValueKind: JsonValueKind.Object } root)
            return null;
        var events = new List<DebugEvent>();
        if (root.TryGetProperty("events", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (ParseEvent(item) is { } e)
                    events.Add(e);
            }
        }
        var seq = Num(root, "seq") is { } s ? (long)s : events.Count > 0 ? events.Max(e => e.Seq) : 0;
        return new DebugPage(Str(root, "boot") ?? string.Empty, seq, events,
            root.TryGetProperty("more", out var more) && more.ValueKind == JsonValueKind.True);
    }

    public static DebugEvent? ParseEvent(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
            return null;
        var seq = Num(e, "seq") is { } s ? (long)s : 0;
        var time = Time(e);
        var rid = Str(e, "rid");
        switch (Str(e, "kind")?.ToUpperInvariant())
        {
            case "REQUEST":
                var sql = e.TryGetProperty("sql", out var q) && q.ValueKind == JsonValueKind.Object ? q : default;
                return new DebugRequest(seq, time, rid,
                    (Str(e, "method") ?? "GET").ToUpperInvariant(),
                    Str(e, "path") ?? "/",
                    Num(e, "status") is { } status ? (int)status : 0,
                    Num(e, "ms") ?? 0,
                    Num(e, "size") is { } size ? (long)size : null,
                    Str(e, "query"),
                    Str(e, "view"),
                    Str(e, "user"),
                    Str(e, "ip"),
                    sql.ValueKind == JsonValueKind.Object && Num(sql, "count") is { } count ? (int)count : 0,
                    sql.ValueKind == JsonValueKind.Object ? Num(sql, "ms") ?? 0 : 0,
                    Map(e, "headers"),
                    Map(e, "response_headers"),
                    Str(e, "body"));
            case "LOG":
                return new DebugLogLine(seq, time, rid, (Str(e, "level") ?? "INFO").ToUpperInvariant(), Str(e, "logger") ?? string.Empty,
                    Str(e, "msg") ?? Str(e, "message") ?? string.Empty);
            case "ERROR":
                var frames = new List<DebugFrame>();
                if (e.TryGetProperty("frames", out var fs) && fs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in fs.EnumerateArray())
                    {
                        if (f.ValueKind != JsonValueKind.Object)
                            continue;
                        frames.Add(new DebugFrame(Str(f, "file") ?? "?", Num(f, "line") is { } line ? (int)line : 0,
                            Str(f, "func") ?? "?", Str(f, "code"), f.TryGetProperty("app", out var app) && app.ValueKind == JsonValueKind.True));
                    }
                }
                return new DebugCrash(seq, time, rid, Str(e, "type") ?? "Exception", Str(e, "msg") ?? Str(e, "message") ?? string.Empty,
                    frames, Str(e, "method")?.ToUpperInvariant(), Str(e, "path"));
            case "SQL":
                return new DebugSql(seq, time, rid, Str(e, "sql") ?? string.Empty, Num(e, "ms") ?? 0, Str(e, "db"));
            case "CUSTOM":
                return new DebugCustom(seq, time, rid, Str(e, "name") ?? "evento", Map(e, "data") ?? new Dictionary<string, string>());
            default:
                return null;
        }
    }

    private static JsonDocument? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Ora dell'evento: secondi Unix (<c>t</c>, anche con decimali) o testo ISO 8601 (<c>time</c>).</summary>
    private static DateTimeOffset Time(JsonElement e)
    {
        if (Num(e, "t") is { } t && t > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(t * 1000));
        if (Str(e, "time") is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed;
        return DateTimeOffset.UnixEpoch;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        } : null;

    /// <summary>Come <see cref="Str"/>, ma anche un oggetto o un elenco diventano testo (il valore di un controllo).</summary>
    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? v.GetRawText() : Str(e, name);

    private static double? Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
            return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            return d;
        return null;
    }

    private static Dictionary<string, string>? Map(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object)
            return null;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in v.EnumerateObject())
            map[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? string.Empty : p.Value.GetRawText();
        return map;
    }
}

/// <summary>Il codice di collegamento che il portale scrive nella console: 8 caratteri senza quelli che si confondono.</summary>
public static class PairingCode
{
    /// <summary>Niente 0/O, 1/I/L: si legge e si scrive senza errori.</summary>
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public const int Length = 8;

    /// <summary>Il codice come lo vuole il portale (maiuscolo, senza trattini e spazi); null se non è un codice.</summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var builder = new StringBuilder(Length);
        foreach (var ch in text.Trim().ToUpperInvariant())
        {
            if (ch is '-' or ' ' or '_' or '·')
                continue;
            if (!Alphabet.Contains(ch, StringComparison.Ordinal))
                return null;
            builder.Append(ch);
        }
        return builder.Length == Length ? builder.ToString() : null;
    }

    /// <summary>Come lo si mostra: <c>K7Q2-9XMP</c>.</summary>
    public static string Format(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return code.Length == Length ? $"{code[..4]}-{code[4..]}" : code;
    }
}

/// <summary>L'indirizzo del portale come lo scrive l'utente: <c>localhost:8000</c> diventa <c>http://localhost:8000</c>.</summary>
public static class PortalAddress
{
    /// <summary>Lo schema e l'host, con porta ed eventuale percorso di base, senza barra finale; null se non è un indirizzo.</summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var value = text.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
            value = "http://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
            return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        // chi incolla l'indirizzo di una pagina del portale o di /_nothing/ intende il portale
        var nothing = path.IndexOf(DebugProtocol.Prefix, StringComparison.OrdinalIgnoreCase);
        if (nothing >= 0)
            path = path[..nothing];
        return $"{uri.Scheme}://{uri.Authority}{path}";
    }

    /// <summary>Indirizzo e codice in un testo solo, come li stampa il portale (<c>http://127.0.0.1:8000 K7Q2-9XMP</c>).</summary>
    public static (string? Address, string? Code) Split(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, null);
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && PairingCode.Normalize(parts[^1]) is { } code)
            return (Normalize(string.Join(' ', parts[..^1])), code);
        // anche nothing://host:porta/CODICE
        if (parts.Length == 1 && parts[0].StartsWith("nothing://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = parts[0]["nothing://".Length..];
            var slash = rest.LastIndexOf('/');
            if (slash > 0 && PairingCode.Normalize(rest[(slash + 1)..]) is { } c)
                return (Normalize(rest[..slash]), c);
        }
        return (Normalize(text), null);
    }

    /// <summary>Il portale gira su questo PC: si possono aprire i file dello stack nell'editor.</summary>
    public static bool IsLocal(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
               (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
    }
}
