using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NothingShell.Core.Plugins;

namespace NothingShell.Core.Connectors;

/// <summary>Da dove legge un connettore (docs/14-agenti-connettori.md).</summary>
public enum ConnectorKind
{
    /// <summary>GET di una URL che risponde in JSON (API di NAS, router, stampanti, OctoPrint…).</summary>
    Http,

    /// <summary>Stati delle entità di Home Assistant (<c>/api/states/&lt;entità&gt;</c>).</summary>
    HomeAssistant,

    /// <summary>Query istantanee di Prometheus (<c>/api/v1/query</c>).</summary>
    Prometheus,

    /// <summary>Comando qualsiasi (PowerShell, ssh, python…) che stampa JSON o testo: serve il permesso.</summary>
    Command,

    /// <summary>File JSON o di testo, anche su una cartella di rete.</summary>
    File,

    /// <summary>Raggiungibilità di un host (ping) con la latenza.</summary>
    Ping,

    /// <summary>Messaggi di un broker MQTT (sensori, Zigbee2MQTT, Tasmota, ESPHome…).</summary>
    Mqtt,
}

/// <summary>Un valore da mostrare: dove prenderlo, come chiamarlo e quando diventa un avviso.</summary>
public sealed record ReadingSpec(string? Label, string? Value)
{
    /// <summary>Entità di Home Assistant, query di Prometheus o topic MQTT, secondo il tipo.</summary>
    public string? Source { get; init; }

    public string? Unit { get; init; }

    public double Scale { get; init; } = 1;

    public int? Decimals { get; init; }

    public double? Above { get; init; }

    public double? Below { get; init; }

    /// <summary>Testo che fa scattare l'avviso (es. "on" per una porta aperta), senza distinguere maiuscole.</summary>
    public string? AlertOn { get; init; }
}

public enum ActionKind
{
    Command,
    Http,
    Mqtt,
    HomeAssistant,
}

/// <summary>Un'azione del connettore: pulsante nella pagina Dispositivi, solo con il permesso.</summary>
public sealed record ConnectorAction(string Label, ActionKind Kind)
{
    public string? Command { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string Method { get; init; } = "POST";

    public string? Url { get; init; }

    public string? Body { get; init; }

    public string? Topic { get; init; }

    public string? Payload { get; init; }

    /// <summary>Servizio di Home Assistant come <c>light.turn_off</c>.</summary>
    public string? Service { get; init; }

    /// <summary>Le azioni pericolose (riavvia, spegni) chiedono un secondo clic.</summary>
    public bool Confirm { get; init; }
}

public sealed record ConnectorManifest(string Id, string Name, ConnectorKind Kind, TimeSpan Refresh, IReadOnlyList<ReadingSpec> Readings)
{
    public const int MinRefreshSeconds = 5;
    public const int DefaultRefreshSeconds = 60;

    public string? Url { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>Token o password nel Gestore credenziali (<see cref="CredentialTarget"/>), mai nel file.</summary>
    public bool Secret { get; init; }

    public string SecretHeader { get; init; } = "Authorization";

    public string SecretFormat { get; init; } = "Bearer {secret}";

    public string? Command { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? Path { get; init; }

    public string? Host { get; init; }

    public int Port { get; init; }

    public bool Tls { get; init; }

    public string? UserName { get; init; }

    public IReadOnlyList<ConnectorAction> Actions { get; init; } = [];

    /// <summary>Connettore della libreria da cui è nato il file (<see cref="ConnectorCatalog"/>), se c'è.</summary>
    public string? Template { get; init; }

    /// <summary>Hash del file: il permesso dato vale solo per questo contenuto.</summary>
    public string Hash { get; init; } = string.Empty;

    public string CredentialTarget => $"NothingShell:connector:{Id}";

    /// <summary>Esegue programmi o cambia qualcosa: senza permesso non parte.</summary>
    public bool NeedsGrant => Kind == ConnectorKind.Command || Actions.Count > 0;
}

public sealed record ConnectorLoadResult(string FileName, ConnectorManifest? Manifest, string? Error);

/// <summary>Lettura e validazione dei file <c>connectors/&lt;id&gt;.json</c>.</summary>
public static partial class ConnectorParser
{
    public static ConnectorLoadResult Parse(string id, string json)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(json);
        if (!IdPattern().IsMatch(id))
            return Fail(id, "nome del file non valido (solo minuscole, cifre e trattini)");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            return Fail(id, $"JSON non valido: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Fail(id, "il connettore deve essere un oggetto JSON");
            if (String(root, "name") is not { Length: > 0 } name)
                return Fail(id, "manca \"name\"");
            var kind = String(root, "type")?.ToLowerInvariant() switch
            {
                "http" => ConnectorKind.Http,
                "homeassistant" or "home-assistant" => ConnectorKind.HomeAssistant,
                "prometheus" => ConnectorKind.Prometheus,
                "command" => ConnectorKind.Command,
                "file" => ConnectorKind.File,
                "ping" => ConnectorKind.Ping,
                "mqtt" => ConnectorKind.Mqtt,
                _ => (ConnectorKind?)null,
            };
            if (kind is not { } k)
                return Fail(id, "\"type\" deve essere http, homeassistant, prometheus, command, file, ping o mqtt");
            var refresh = root.TryGetProperty("refreshSeconds", out var r) && r.TryGetInt32(out var s) ? s : ConnectorManifest.DefaultRefreshSeconds;
            if (refresh < ConnectorManifest.MinRefreshSeconds)
                return Fail(id, $"\"refreshSeconds\" deve essere almeno {ConnectorManifest.MinRefreshSeconds}");

            var readings = new List<ReadingSpec>();
            if (root.TryGetProperty("readings", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
                {
                    readings.Add(new ReadingSpec(String(item, "label"), String(item, "value"))
                    {
                        Source = String(item, "entity") ?? String(item, "query") ?? String(item, "topic"),
                        Unit = String(item, "unit"),
                        Scale = Double(item, "scale") ?? 1,
                        Decimals = item.TryGetProperty("decimals", out var d) && d.TryGetInt32(out var dd) ? Math.Clamp(dd, 0, 6) : null,
                        Above = Double(item, "above"),
                        Below = Double(item, "below"),
                        AlertOn = String(item, "alertOn"),
                    });
                }
            }

            var actions = new List<ConnectorAction>();
            if (root.TryGetProperty("actions", out var acts) && acts.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in acts.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
                {
                    if (ParseAction(item, k) is not { } action)
                        return Fail(id, "ogni azione vuole \"label\" e ciò che serve al suo tipo (command, url, topic o service)");
                    actions.Add(action);
                }
            }

            var manifest = new ConnectorManifest(id, name.Trim(), k, TimeSpan.FromSeconds(refresh), readings)
            {
                Url = String(root, "url"),
                Headers = root.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object
                    ? h.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!)
                    : new Dictionary<string, string>(),
                Secret = root.TryGetProperty("secret", out var secret) && secret.ValueKind == JsonValueKind.True,
                SecretHeader = String(root, "secretHeader") ?? "Authorization",
                SecretFormat = String(root, "secretFormat") ?? "Bearer {secret}",
                Command = String(root, "command"),
                Arguments = Strings(root, "args"),
                Path = String(root, "path"),
                Host = String(root, "host"),
                Port = root.TryGetProperty("port", out var p) && p.TryGetInt32(out var port) && port is > 0 and < 65536 ? port : 0,
                Tls = root.TryGetProperty("tls", out var tls) && tls.ValueKind == JsonValueKind.True,
                UserName = String(root, "username"),
                Actions = actions,
                Template = String(root, "template"),
                Hash = HashOf(json),
            };
            return Validate(manifest) is { } error ? Fail(id, error) : new ConnectorLoadResult(id, manifest, null);
        }
    }

    /// <summary>SHA-256 del file: cambiare il connettore toglie il permesso dato.</summary>
    public static string HashOf(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16];

    private static string? Validate(ConnectorManifest m)
    {
        static bool IsHttp(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
        switch (m.Kind)
        {
            case ConnectorKind.Http or ConnectorKind.HomeAssistant or ConnectorKind.Prometheus when !IsHttp(m.Url):
                return "\"url\" deve essere un indirizzo http(s) completo";
            case ConnectorKind.Http when m.Readings.Any(r => r.Value is null):
            case ConnectorKind.Command when m.Readings.Any(r => r.Value is null):
            case ConnectorKind.File when m.Readings.Any(r => r.Value is null):
                return "ogni lettura vuole \"value\" (es. \"{temperatura}\")";
            case ConnectorKind.HomeAssistant when m.Readings.Count == 0 || m.Readings.Any(r => r.Source is null):
                return "ogni lettura vuole \"entity\" (es. \"sensor.soggiorno\")";
            case ConnectorKind.Prometheus when m.Readings.Count == 0 || m.Readings.Any(r => r.Source is null):
                return "ogni lettura vuole \"query\"";
            case ConnectorKind.Command when string.IsNullOrWhiteSpace(m.Command):
                return "manca \"command\"";
            case ConnectorKind.File when string.IsNullOrWhiteSpace(m.Path):
                return "manca \"path\"";
            case ConnectorKind.Ping or ConnectorKind.Mqtt when string.IsNullOrWhiteSpace(m.Host):
                return "manca \"host\"";
            case ConnectorKind.Mqtt when m.Readings.Count == 0 || m.Readings.Any(r => r.Source is null):
                return "ogni lettura vuole \"topic\"";
        }
        // ping si descrive da solo; comandi e file senza letture mostrano il testo dell'uscita
        return m.Readings.Count == 0 && m.Kind is ConnectorKind.Http ? "manca \"readings\"" : null;
    }

    private static ConnectorAction? ParseAction(JsonElement item, ConnectorKind connector)
    {
        if (String(item, "label") is not { Length: > 0 } label)
            return null;
        var kind = String(item, "type")?.ToLowerInvariant() switch
        {
            "command" => ActionKind.Command,
            "http" => ActionKind.Http,
            "mqtt" => ActionKind.Mqtt,
            "homeassistant" or "home-assistant" => ActionKind.HomeAssistant,
            null => connector switch
            {
                ConnectorKind.Mqtt => ActionKind.Mqtt,
                ConnectorKind.HomeAssistant => ActionKind.HomeAssistant,
                ConnectorKind.Http => ActionKind.Http,
                _ => ActionKind.Command,
            },
            _ => (ActionKind?)null,
        };
        var action = kind is { } k ? new ConnectorAction(label.Trim().ToUpperInvariant(), k)
        {
            Command = String(item, "command"),
            Arguments = Strings(item, "args"),
            Method = String(item, "method")?.ToUpperInvariant() ?? "POST",
            Url = String(item, "url"),
            // per Home Assistant il corpo è "data" (es. {"entity_id": "light.soggiorno"})
            Body = item.TryGetProperty("body", out var b) ? b.ValueKind == JsonValueKind.String ? b.GetString() : b.GetRawText()
                : item.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data.GetRawText() : null,
            Topic = String(item, "topic"),
            Payload = item.TryGetProperty("payload", out var pl) ? pl.ValueKind == JsonValueKind.String ? pl.GetString() : pl.GetRawText() : null,
            Service = String(item, "service"),
            Confirm = item.TryGetProperty("confirm", out var c) && c.ValueKind == JsonValueKind.True,
        } : null;
        return action switch
        {
            null => null,
            { Kind: ActionKind.Command, Command: null or "" } => null,
            { Kind: ActionKind.Http, Url: null or "" } => null,
            { Kind: ActionKind.Http, Method: not ("POST" or "PUT" or "PATCH" or "DELETE" or "GET") } => null,
            { Kind: ActionKind.Mqtt, Topic: null or "" } => null,
            { Kind: ActionKind.HomeAssistant } a when a.Service?.Split('.') is not [{ Length: > 0 }, { Length: > 0 }] => null,
            _ => action,
        };
    }

    private static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Double(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) ? d : null;

    private static string[] Strings(JsonElement element, string property) =>
        element.TryGetProperty(property, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.ToString()).ToArray() : [];

    private static ConnectorLoadResult Fail(string id, string error) => new(id, null, error);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex IdPattern();
}

public enum ReadingLevel
{
    Normal,
    Warning,
}

public sealed record ConnectorReading(string Label, string Text, string? Unit, ReadingLevel Level)
{
    public double? Number { get; init; }
}

public enum ConnectorStatus
{
    Waiting,
    Ok,
    Error,

    /// <summary>Serve il permesso dalle impostazioni (comandi e azioni).</summary>
    NeedsGrant,
}

/// <summary>Ultimo giro di un connettore, per la pagina Dispositivi.</summary>
public sealed record ConnectorState(ConnectorManifest Manifest, ConnectorStatus Status, IReadOnlyList<ConnectorReading> Readings, string? Error, DateTimeOffset? UpdatedAt)
{
    public bool Granted { get; init; }
}

/// <summary>Valori e soglie, indipendenti dalla sorgente.</summary>
public static class ConnectorReadings
{
    /// <summary>Un'uscita che non è JSON diventa <c>{"text": "…", "lines": […]}</c>, così vale anche <c>{lines[0]}</c>.</summary>
    public static JsonDocument Document(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var trimmed = output.Trim().TrimStart('﻿');
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                return JsonDocument.Parse(trimmed);
            }
            catch (JsonException)
            {
                // non era JSON: testo
            }
        }
        var lines = trimmed.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
        return JsonDocument.Parse(JsonSerializer.Serialize(new { text = trimmed, lines }));
    }

    /// <summary>Applica <c>value</c> (segnaposto <c>{percorso}</c>) al documento; un solo segnaposto numerico usa scala e decimali.</summary>
    public static ConnectorReading Evaluate(ReadingSpec spec, JsonElement root, string fallbackLabel)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var template = spec.Value ?? "{text}";
        var path = template.StartsWith('{') && template.EndsWith('}') && template.IndexOf('{', 1) < 0 ? template[1..^1].Trim() : null;
        if (path is not null && JsonTemplate.TryResolve(root, path, out var value))
            return FromValue(spec, value, fallbackLabel);
        return Make(spec, JsonTemplate.Render(template, root), null, fallbackLabel);
    }

    /// <summary>Valore già estratto (stato di Home Assistant, risultato di Prometheus, payload MQTT).</summary>
    public static ConnectorReading FromText(ReadingSpec spec, string text, string fallbackLabel)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(text);
        var number = double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : (double?)null;
        return number is { } x ? Number(spec, x, fallbackLabel) : Make(spec, text.Trim(), null, fallbackLabel);
    }

    private static ConnectorReading FromValue(ReadingSpec spec, JsonElement value, string fallbackLabel) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDouble(out var d) => Number(spec, d, fallbackLabel),
        JsonValueKind.String => FromText(spec, value.GetString() ?? string.Empty, fallbackLabel),
        JsonValueKind.True => Make(spec, "SI", null, fallbackLabel),
        JsonValueKind.False => Make(spec, "NO", null, fallbackLabel),
        JsonValueKind.Null or JsonValueKind.Undefined => Make(spec, JsonTemplate.Missing, null, fallbackLabel),
        _ => Make(spec, value.GetRawText(), null, fallbackLabel),
    };

    private static ConnectorReading Number(ReadingSpec spec, double raw, string fallbackLabel)
    {
        var x = raw * spec.Scale;
        var text = spec.Decimals is { } d
            ? x.ToString("F" + d.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
            : Math.Round(x, 1).ToString("0.#", CultureInfo.InvariantCulture);
        return Make(spec, text, x, fallbackLabel);
    }

    private static ConnectorReading Make(ReadingSpec spec, string text, double? number, string fallbackLabel)
    {
        var warning = (number is { } n && ((spec.Above is { } above && n > above) || (spec.Below is { } below && n < below)))
                      || (spec.AlertOn is { } on && string.Equals(text, on, StringComparison.OrdinalIgnoreCase));
        return new ConnectorReading(spec.Label ?? fallbackLabel, text, spec.Unit, warning ? ReadingLevel.Warning : ReadingLevel.Normal) { Number = number };
    }
}

/// <summary>Avvisi solo quando un valore entra in allarme, non a ogni giro in cui ci resta.</summary>
public sealed class ConnectorAlerts
{
    private readonly HashSet<string> _warning = new(StringComparer.Ordinal);

    /// <summary>Le letture appena passate in allarme; quelle tornate normali si dimenticano.</summary>
    public IReadOnlyList<ConnectorReading> Update(string connectorId, IReadOnlyList<ConnectorReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        var fresh = new List<ConnectorReading>();
        foreach (var reading in readings)
        {
            var key = connectorId + "\n" + reading.Label;
            if (reading.Level == ReadingLevel.Warning)
            {
                if (_warning.Add(key))
                    fresh.Add(reading);
            }
            else
            {
                _warning.Remove(key);
            }
        }
        return fresh;
    }
}
