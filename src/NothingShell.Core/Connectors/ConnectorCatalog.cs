using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NothingShell.Core.Connectors;

public enum FieldKind
{
    Text,
    Number,

    /// <summary>Token o password: va nel Gestore credenziali, mai nel file.</summary>
    Secret,

    /// <summary>Più valori separati da virgole (entità, topic).</summary>
    List,
}

/// <summary>Un dato che l'utente scrive per attivare un connettore della libreria.</summary>
public sealed record ConnectorField(string Key, string Label, FieldKind Kind = FieldKind.Text)
{
    /// <summary>Valore proposto nel modulo e usato se il campo resta vuoto.</summary>
    public string? Default { get; init; }

    /// <summary>Esempio mostrato accanto all'etichetta.</summary>
    public string? Hint { get; init; }

    public bool Optional { get; init; }

    /// <summary>Forma che il valore deve avere: i valori entrano in comandi e indirizzi.</summary>
    public string? Pattern { get; init; }
}

/// <summary>Valori del modulo, già controllati: vuoti per i campi facoltativi lasciati in bianco.</summary>
public sealed class TemplateInput(IReadOnlyDictionary<string, string> values)
{
    public string this[string key] => values.TryGetValue(key, out var v) ? v : string.Empty;

    public bool Has(string key) => this[key].Length > 0;

    public double? Number(string key) =>
        double.TryParse(this[key].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    public string[] List(string key) => ConnectorCatalog.SplitList(this[key]);
}

/// <summary>Connettore pronto: pochi campi diventano il file JSON (docs/14-agenti-connettori.md).</summary>
public sealed record ConnectorTemplate(string Id, string Name, string Category, string Description, IReadOnlyList<ConnectorField> Fields, Func<TemplateInput, JsonObject> Build)
{
    /// <summary>Cosa fa sul PC o sul dispositivo, se esegue comandi o ha azioni: si mostra accanto al permesso.</summary>
    public string? Permission { get; init; }
}

/// <summary>Esito di <see cref="ConnectorCatalog.Create"/>: il file da scrivere o il motivo per cui non si può.</summary>
public sealed record TemplateResult(string? Id, string? Json, string? Secret, string? Error);

/// <summary>Libreria di connettori pronti, divisi per categoria; il file generato passa da <see cref="ConnectorParser"/>.</summary>
public static partial class ConnectorCatalog
{
    private const string HttpUrl = @"^https?://\S+$";
    private const string HostName = @"^[\w.\-:\[\]]+$";
    private const string SshTarget = @"^([\w.\-]+@)?[\w.\-]+$";
    private const string JsonPath = @"^[\w.\[\]\-]+$";
    private const string Coordinate = @"^-?\d{1,3}([.,]\d+)?$";
    private const double Gigabyte = 1024d * 1024 * 1024;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static IReadOnlyList<string> Categories { get; } = ["Questo PC", "Rete e web", "Casa", "Server", "Sicurezza", "Sviluppo e AI", "Informazioni", "Personalizzati"];

    public static IReadOnlyList<ConnectorTemplate> All { get; } =
    [
        // ── Questo PC ─────────────────────────────────────────────────────────────
        new("disco", "Spazio su disco", "Questo PC", "Spazio libero e occupato di un'unità, con avviso quando sta finendo.",
            [
                new("drive", "Unità") { Default = "C", Hint = "C", Pattern = "^[A-Za-z]:?$" },
                new("min", "Avvisa sotto (GB liberi)", FieldKind.Number) { Default = "20", Optional = true },
            ],
            v => PowerShell(
                $"$d = Get-PSDrive {v["drive"].TrimEnd(':').ToUpperInvariant()}; @{{ libero = [math]::Round($d.Free / 1GB, 1); usato = [math]::Round($d.Used / ($d.Used + $d.Free) * 100) }} | ConvertTo-Json",
                300,
                Reading("Libero", "{libero}", "GB") .With("below", v.Number("min")),
                Reading("Occupato", "{usato}", "%")))
        {
            Permission = "Esegue PowerShell sul PC per leggere lo spazio (Get-PSDrive).",
        },
        new("servizio", "Servizio di Windows", "Questo PC", "Stato di un servizio (Docker, SQL Server, Spooler…) con Avvia e Riavvia.",
            [new("service", "Nome del servizio") { Hint = "Spooler", Pattern = @"^[\w.\-]+$" }],
            v =>
            {
                var name = Quote(v["service"]);
                var o = PowerShell($"$s = Get-Service -Name {name} -ErrorAction Stop; @{{ stato = \"$($s.Status)\"; avvio = \"$($s.StartType)\" }} | ConvertTo-Json", 60,
                    Reading("Stato", "{stato}").With("alertOn", "Stopped"),
                    Reading("Avvio", "{avvio}"));
                o["actions"] = new JsonArray(
                    PowerShellAction("Avvia", $"Start-Service -Name {name}", confirm: false),
                    PowerShellAction("Riavvia", $"Restart-Service -Name {name} -Force", confirm: true));
                return o;
            })
        {
            Permission = "Esegue PowerShell: legge lo stato e, coi pulsanti, avvia o riavvia il servizio (molti servizi vogliono i permessi di amministratore).",
        },
        new("processo", "App in esecuzione", "Questo PC", "Se un programma è aperto e quanta memoria usa; Termina con doppio clic.",
            [new("process", "Nome del processo") { Hint = "ollama", Pattern = @"^[\w.\-]+$" }],
            v =>
            {
                var name = Quote(Regex.Replace(v["process"], @"\.exe$", string.Empty, RegexOptions.IgnoreCase));
                var o = PowerShell(
                    $"$p = @(Get-Process -Name {name} -ErrorAction SilentlyContinue); @{{ stato = $(if ($p.Count) {{ 'ATTIVO' }} else {{ 'FERMO' }}); memoria = [math]::Round(($p | Measure-Object WorkingSet64 -Sum).Sum / 1MB) }} | ConvertTo-Json",
                    30,
                    Reading("Stato", "{stato}").With("alertOn", "FERMO"),
                    Reading("Memoria", "{memoria}", "MB"));
                o["actions"] = new JsonArray(PowerShellAction("Termina", $"Stop-Process -Name {name} -Force", confirm: true));
                return o;
            })
        {
            Permission = "Esegue PowerShell: legge i processi e, col pulsante, termina il programma.",
        },
        new("docker", "Docker", "Questo PC", "Container in esecuzione e fermi, immagini scaricate (Docker Desktop o Engine).",
            [],
            _ => Obj(("type", "command"), ("command", "docker"), ("args", Array("info", "--format", "{{json .}}")), ("refreshSeconds", 60),
                ("readings", new JsonArray(
                    Reading("In esecuzione", "{ContainersRunning}"),
                    Reading("Fermi", "{ContainersStopped}"),
                    Reading("Immagini", "{Images}")))))
        {
            Permission = "Esegue docker info sul PC.",
        },
        new("log", "File di log", "Questo PC", "Ultima riga di un log, in rosso quando parla di errori o eccezioni.",
            [new("path", "Percorso del file") { Hint = @"C:\app\logs\app.log" }],
            v => PowerShell(
                $"$l = Get-Content -LiteralPath {Quote(v["path"])} -Tail 1 -ErrorAction Stop; @{{ riga = \"$l\"; errore = $(if (\"$l\" -match 'error|errore|exception|fail') {{ 'SI' }} else {{ 'NO' }}) }} | ConvertTo-Json",
                30,
                Reading("Ultima riga", "{riga}"),
                Reading("Errore", "{errore}").With("alertOn", "SI")))
        {
            Permission = "Esegue PowerShell per leggere l'ultima riga del file.",
        },

        // ── Rete e web ────────────────────────────────────────────────────────────
        new("ping", "Dispositivo in rete", "Rete e web", "Online o offline e latenza di router, NAS, stampante, telecamera…",
            [new("host", "Indirizzo o nome") { Hint = "192.168.1.1", Pattern = HostName }],
            v => Obj(("type", "ping"), ("host", v["host"]), ("refreshSeconds", 20))),
        new("sito", "Sito o servizio web", "Rete e web", "Controlla che un sito o un'API risponda: offline diventa un errore nel notch.",
            [new("url", "Indirizzo") { Hint = "https://miosito.it", Pattern = HttpUrl }],
            v => Obj(("type", "http"), ("url", v["url"]), ("refreshSeconds", 60), ("readings", new JsonArray(Reading("Stato", "ONLINE"))))),
        new("ip-pubblico", "IP pubblico", "Rete e web", "L'indirizzo con cui esci su Internet (ipify), utile con VPN e IP dinamici.",
            [],
            _ => Obj(("type", "http"), ("url", "https://api.ipify.org?format=json"), ("refreshSeconds", 900), ("readings", new JsonArray(Reading("IP", "{ip}"))))),
        new("statuspage", "Stato di un servizio online", "Rete e web", "Pagine di stato Statuspage: Claude, GitHub, OpenAI, Discord, Cloudflare…",
            [new("url", "Pagina di stato") { Default = "https://status.claude.com", Hint = "https://www.githubstatus.com", Pattern = HttpUrl }],
            v => Obj(("type", "http"), ("url", v["url"].TrimEnd('/') + "/api/v2/status.json"), ("refreshSeconds", 300),
                ("readings", new JsonArray(
                    Reading("Stato", "{status.description}"),
                    Reading("Gravità", "{status.indicator}").With("alertOn", "major"))))),

        // ── Casa ──────────────────────────────────────────────────────────────────
        new("homeassistant", "Home Assistant", "Casa", "Sensori e interruttori di casa; un pulsante per accendere o spegnere un'entità.",
            [
                new("url", "Indirizzo") { Default = "http://homeassistant.local:8123", Pattern = HttpUrl },
                new("token", "Token di accesso a lunga durata", FieldKind.Secret) { Hint = "Profilo › Sicurezza" },
                new("entities", "Entità da mostrare", FieldKind.List) { Hint = "sensor.soggiorno, binary_sensor.porta", Pattern = @"^[a-z_]+\.\w+$" },
                new("toggle", "Entità da accendere e spegnere", FieldKind.Text) { Hint = "light.soggiorno", Pattern = @"^[a-z_]+\.\w+$", Optional = true },
            ],
            v =>
            {
                var o = Obj(("type", "homeassistant"), ("url", v["url"]), ("refreshSeconds", 30),
                    ("readings", new JsonArray(v.List("entities").Select(e => (JsonNode?)Obj(("entity", e))).ToArray())));
                if (v.Has("toggle"))
                    o["actions"] = new JsonArray(Obj(("label", "Accendi/spegni"), ("service", "homeassistant.toggle"), ("data", Obj(("entity_id", v["toggle"])))));
                return o;
            })
        {
            Permission = "Il pulsante Accendi/spegni cambia lo stato dell'entità in Home Assistant.",
        },
        new("mqtt", "Sensori MQTT", "Casa", "Ultimo messaggio dei topic: Zigbee2MQTT, Tasmota, ESPHome, Shelly…",
            [
                new("host", "Broker") { Hint = "broker.local", Pattern = HostName },
                new("port", "Porta", FieldKind.Number) { Default = "1883" },
                new("username", "Utente") { Optional = true },
                new("password", "Password", FieldKind.Secret) { Optional = true },
                new("topics", "Topic", FieldKind.List) { Hint = "zigbee2mqtt/camera, casa/caldaia" },
                new("field", "Campo del JSON") { Hint = "temperature", Optional = true, Pattern = @"^[\w.\[\]]+$" },
            ],
            v => Obj(("type", "mqtt"), ("host", v["host"]), ("port", (int)(v.Number("port") ?? 1883)), ("username", v.Has("username") ? v["username"] : null),
                ("readings", new JsonArray(v.List("topics").Select(t => (JsonNode?)Obj(
                    ("topic", t),
                    ("label", t.Split('/').LastOrDefault(s => s is not ("+" or "#" or "")) ?? t),
                    ("value", v.Has("field") ? "{" + v["field"] + "}" : null))).ToArray())))),
        new("shelly", "Presa Shelly", "Casa", "Presa o relè Shelly (Gen2/Plus): acceso, potenza, consumo; Accendi/spegni.",
            [new("host", "Indirizzo IP") { Hint = "192.168.1.50", Pattern = HostName }],
            v => Obj(("type", "http"), ("url", $"http://{v["host"]}/rpc/Switch.GetStatus?id=0"), ("refreshSeconds", 15),
                ("readings", new JsonArray(
                    Reading("Acceso", "{output}"),
                    Reading("Potenza", "{apower}", "W").With("decimals", 0),
                    Reading("Consumo", "{aenergy.total}", "kWh").With("scale", 0.001).With("decimals", 2))),
                ("actions", new JsonArray(Obj(("label", "Accendi/spegni"), ("method", "GET"), ("url", "/rpc/Switch.Toggle?id=0"))))))
        {
            Permission = "Il pulsante accende e spegne la presa.",
        },
        new("tasmota", "Presa Tasmota", "Casa", "Dispositivo Tasmota: stato e potenza; Accendi/spegni.",
            [new("host", "Indirizzo IP") { Hint = "192.168.1.51", Pattern = HostName }],
            v => Obj(("type", "http"), ("url", $"http://{v["host"]}/cm?cmnd=Status%200"), ("refreshSeconds", 15),
                ("readings", new JsonArray(
                    Reading("Stato", "{StatusSTS.POWER}"),
                    Reading("Potenza", "{StatusSNS.ENERGY.Power}", "W"))),
                ("actions", new JsonArray(Obj(("label", "Accendi/spegni"), ("method", "GET"), ("url", "/cm?cmnd=Power%20Toggle"))))))
        {
            Permission = "Il pulsante accende e spegne il dispositivo.",
        },
        new("octoprint", "Stampante 3D (OctoPrint)", "Casa", "Stato, avanzamento e tempo mancante della stampa; Pausa.",
            [
                new("url", "Indirizzo di OctoPrint") { Default = "http://octopi.local", Pattern = HttpUrl },
                new("token", "Chiave API", FieldKind.Secret) { Hint = "Impostazioni › API" },
            ],
            v => Obj(("type", "http"), ("url", v["url"].TrimEnd('/') + "/api/job"), ("secretHeader", "X-Api-Key"), ("secretFormat", "{secret}"), ("refreshSeconds", 15),
                ("readings", new JsonArray(
                    Reading("Stato", "{state}"),
                    Reading("Avanzamento", "{progress.completion}", "%").With("decimals", 0),
                    Reading("Mancano", "{progress.printTimeLeft}", "min").With("scale", 1 / 60d).With("decimals", 0))),
                ("actions", new JsonArray(Obj(("label", "Pausa"), ("url", "/api/job"), ("body", Obj(("command", "pause"), ("action", "toggle"))), ("confirm", true))))))
        {
            Permission = "Il pulsante mette in pausa o riprende la stampa.",
        },
        new("klipper", "Stampante 3D (Klipper)", "Casa", "Moonraker (Mainsail, Fluidd): stato, file e avanzamento; Pausa e Riprendi.",
            [new("url", "Indirizzo di Moonraker") { Default = "http://mainsailos.local", Pattern = HttpUrl }],
            v => Obj(("type", "http"), ("url", v["url"].TrimEnd('/') + "/printer/objects/query?print_stats&display_status"), ("refreshSeconds", 15),
                ("readings", new JsonArray(
                    Reading("Stato", "{result.status.print_stats.state}").With("alertOn", "error"),
                    Reading("File", "{result.status.print_stats.filename}"),
                    Reading("Avanzamento", "{result.status.display_status.progress}", "%").With("scale", 100).With("decimals", 0))),
                ("actions", new JsonArray(
                    Obj(("label", "Pausa"), ("url", "/printer/print/pause"), ("confirm", true)),
                    Obj(("label", "Riprendi"), ("url", "/printer/print/resume"))))))
        {
            Permission = "I pulsanti mettono in pausa e riprendono la stampa.",
        },

        // ── Server ────────────────────────────────────────────────────────────────
        new("server-ssh", "Server Linux (SSH)", "Server", "Carico, RAM, disco e da quanto è acceso; Riavvia. Serve l'accesso con chiave.",
            [new("target", "Utente e host") { Hint = "utente@server.local", Pattern = SshTarget }],
            v => Ssh(v["target"], "cut -d' ' -f1 /proc/loadavg; free | awk '/^Mem/{printf \"%.0f\\n\", $3/$2*100}'; df / | awk 'NR==2{print $5+0}'; uptime -p",
                Reading("Carico", "{lines[0]}"),
                Reading("RAM", "{lines[1]}", "%").With("above", 90),
                Reading("Disco /", "{lines[2]}", "%").With("above", 90),
                Reading("Acceso da", "{lines[3]}")))
        {
            Permission = "Esegue ssh verso il server: legge i valori e, col pulsante, lo riavvia (sudo -n reboot).",
        },
        new("raspberry", "Raspberry Pi", "Server", "Temperatura della CPU, disco e da quanto è acceso; Riavvia. Serve l'accesso con chiave.",
            [new("target", "Utente e host") { Default = "pi@raspberrypi.local", Pattern = SshTarget }],
            v => Ssh(v["target"], "cat /sys/class/thermal/thermal_zone0/temp; df / | awk 'NR==2{print $5+0}'; uptime -p",
                Reading("CPU", "{lines[0]}", "°C").With("scale", 0.001).With("decimals", 1).With("above", 75),
                Reading("Disco", "{lines[1]}", "%").With("above", 90),
                Reading("Acceso da", "{lines[2]}")))
        {
            Permission = "Esegue ssh verso il Raspberry: legge i valori e, col pulsante, lo riavvia (sudo -n reboot).",
        },
        new("prometheus", "Prometheus (node_exporter)", "Server", "CPU, RAM e disco di un server dalle metriche di Prometheus.",
            [
                new("url", "Indirizzo di Prometheus") { Default = "http://prometheus.local:9090", Pattern = HttpUrl },
                new("instance", "Istanza") { Hint = "server:9100", Optional = true, Pattern = @"^[\w.\-:]+$" },
            ],
            v =>
            {
                var filter = v.Has("instance") ? $",instance=\"{v["instance"]}\"" : string.Empty;
                var only = v.Has("instance") ? $"{{instance=\"{v["instance"]}\"}}" : string.Empty;
                return Obj(("type", "prometheus"), ("url", v["url"]), ("refreshSeconds", 30),
                    ("readings", new JsonArray(
                        Reading("CPU", null, "%").With("query", $"100 - avg(rate(node_cpu_seconds_total{{mode=\"idle\"{filter}}}[2m])) * 100").With("decimals", 0).With("above", 90),
                        Reading("RAM", null, "%").With("query", $"100 * (1 - sum(node_memory_MemAvailable_bytes{only}) / sum(node_memory_MemTotal_bytes{only}))").With("decimals", 0).With("above", 90),
                        Reading("Disco /", null, "%").With("query", $"100 * (1 - sum(node_filesystem_avail_bytes{{mountpoint=\"/\"{filter}}}) / sum(node_filesystem_size_bytes{{mountpoint=\"/\"{filter}}}))").With("decimals", 0).With("above", 90))));
            }),

        // ── Sviluppo e AI ─────────────────────────────────────────────────────────
        new("github-pr", "GitHub: PR da rivedere", "Sviluppo e AI", "Quante pull request aspettano la tua revisione, con avviso quando arriva la prima.",
            [
                new("user", "Utente GitHub") { Pattern = @"^[\w\-]+$" },
                new("token", "Token (per i repository privati)", FieldKind.Secret) { Optional = true, Hint = "github.com/settings/tokens" },
            ],
            v => GitHub($"https://api.github.com/search/issues?q=is:open+is:pr+review-requested:{v["user"]}", 300,
                Reading("Da rivedere", "{total_count}").With("above", 0))),
        new("github-actions", "GitHub: ultima CI", "Sviluppo e AI", "Nome, stato ed esito dell'ultima esecuzione delle Actions di un repository.",
            [
                new("repo", "Repository") { Hint = "utente/progetto", Pattern = @"^[\w.\-]+/[\w.\-]+$" },
                new("token", "Token (per i repository privati)", FieldKind.Secret) { Optional = true, Hint = "github.com/settings/tokens" },
            ],
            v => GitHub($"https://api.github.com/repos/{v["repo"]}/actions/runs?per_page=1", 120,
                Reading("Workflow", "{workflow_runs[0].name}"),
                Reading("Stato", "{workflow_runs[0].status}"),
                Reading("Esito", "{workflow_runs[0].conclusion}").With("alertOn", "failure"))),
        new("ollama", "Ollama", "Sviluppo e AI", "Modello locale caricato in memoria e quanta VRAM occupa.",
            [new("url", "Indirizzo") { Default = "http://localhost:11434", Pattern = HttpUrl }],
            v => Obj(("type", "http"), ("url", v["url"].TrimEnd('/') + "/api/ps"), ("refreshSeconds", 15),
                ("readings", new JsonArray(
                    Reading("Modello", "{models[0].name}"),
                    Reading("VRAM", "{models[0].size_vram}", "GB").With("scale", 1 / Gigabyte).With("decimals", 1))))),

        new("anthropic-costi", "Costi API Anthropic", "Sviluppo e AI",
            "Quanto spende in euro, oggi e nel mese, una chiave API (un sito, un'app) o tutta l'organizzazione, per modello.",
            [
                new("key", "Chiave Admin API", FieldKind.Secret) { Hint = "sk-ant-admin… (Console › Impostazioni › Admin keys)" },
                new("keyId", "Id della chiave API da seguire") { Hint = "apikey_01… (vuoto = tutta l'organizzazione)", Optional = true, Pattern = @"^[\w\-]+$" },
                new("daily", "Budget al giorno (€)", FieldKind.Number) { Optional = true },
                new("monthly", "Budget al mese (€)", FieldKind.Number) { Optional = true },
            ],
            v => Obj(("type", "http"), ("refreshSeconds", 300),
                ("url", "https://api.anthropic.com/v1/organizations/usage_report/messages?starting_at={today}&bucket_width=1d&group_by[]=model" +
                        (v.Has("keyId") ? "&api_key_ids[]=" + v["keyId"] : string.Empty)),
                ("headers", Obj(("anthropic-version", "2023-06-01"))),
                ("secretHeader", "x-api-key"), ("secretFormat", "{secret}"),
                ("llm", Obj(("list", "data[0].results"), ("model", "{model}"), ("input", "{uncached_input_tokens}"), ("output", "{output_tokens}"),
                    ("cacheRead", "{cache_read_input_tokens}"), ("cacheWrite", "{cache_creation.ephemeral_5m_input_tokens}"),
                    ("cacheWrite1h", "{cache_creation.ephemeral_1h_input_tokens}"), ("countFirst", true),
                    ("dailyBudget", v.Number("daily")), ("monthlyBudget", v.Number("monthly")))))),
        new("sito-llm", "Sito con LLM: costi", "Sviluppo e AI",
            "Costi in euro delle chiamate al modello fatte da un tuo sito, da un indirizzo che ne espone l'uso (chiamate o totali).",
            [
                new("url", "Indirizzo dell'uso") { Hint = "https://miosito.it/admin/llm-usage", Pattern = HttpUrl },
                new("token", "Token (Authorization: Bearer)", FieldKind.Secret) { Optional = true },
                new("mode", "Formato") { Default = "chiamate", Hint = "chiamate (un elenco) o totali (contatori)", Pattern = "^(chiamate|totali)$" },
                new("list", "Campo dell'elenco") { Default = "calls", Hint = "calls (vuoto = la risposta è una voce sola)", Optional = true, Pattern = JsonPath },
                new("model", "Campo del modello o nome fisso") { Default = "model", Hint = "model, oppure claude-sonnet-5-5", Pattern = JsonPath },
                new("input", "Campo dei token in entrata") { Default = "input_tokens", Pattern = JsonPath },
                new("output", "Campo dei token in uscita") { Default = "output_tokens", Pattern = JsonPath },
                new("cost", "Campo del costo, se lo calcola il sito") { Hint = "cost", Optional = true, Pattern = JsonPath },
                new("currency", "Valuta del costo") { Default = "USD", Pattern = "^(USD|EUR|usd|eur)$" },
                new("priceIn", "Prezzo in entrata (€ per milione), per modelli non Anthropic", FieldKind.Number) { Optional = true },
                new("priceOut", "Prezzo in uscita (€ per milione)", FieldKind.Number) { Optional = true },
                new("daily", "Budget al giorno (€)", FieldKind.Number) { Optional = true },
                new("monthly", "Budget al mese (€)", FieldKind.Number) { Optional = true },
            ],
            v =>
            {
                var calls = v["mode"] == "chiamate";
                // un nome con trattini o punti e senza campi annidati è il modello fisso, non un campo
                var model = v["model"].StartsWith("claude-", StringComparison.OrdinalIgnoreCase) || v["model"].StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)
                    ? v["model"] : "{" + v["model"] + "}";
                var llm = Obj(("mode", calls ? "calls" : "totals"), ("list", v.Has("list") ? v["list"] : null), ("model", model),
                    ("input", "{" + v["input"] + "}"), ("output", "{" + v["output"] + "}"),
                    ("cost", v.Has("cost") ? "{" + v["cost"] + "}" : null), ("costCurrency", v.Has("cost") ? v["currency"].ToUpperInvariant() : null),
                    ("id", calls ? "{id}" : null), ("at", calls ? "{at}" : null),
                    ("price", v.Number("priceIn") is { } pin && v.Number("priceOut") is { } pout
                        ? Obj(("input", pin), ("output", pout), ("currency", "EUR")) : null),
                    ("dailyBudget", v.Number("daily")), ("monthlyBudget", v.Number("monthly")));
                return Obj(("type", "http"), ("url", v["url"]), ("refreshSeconds", 60), ("secret", v.Has("token") ? true : null), ("llm", llm));
            }),

        // ── Sicurezza ─────────────────────────────────────────────────────────────
        new("defender", "Microsoft Defender", "Sicurezza",
            "Antivirus e protezione in tempo reale accesi, età delle firme, minacce degli ultimi 7 giorni; Scansione rapida.",
            [],
            _ =>
            {
                var o = PowerShell(
                    "$s = Get-MpComputerStatus; $t = @(Get-MpThreatDetection -ErrorAction SilentlyContinue | Where-Object { $_.InitialDetectionTime -gt (Get-Date).AddDays(-7) }).Count; " +
                    "@{ antivirus = $(if ($s.AntivirusEnabled) { 'ATTIVO' } else { 'SPENTO' }); tempo = $(if ($s.RealTimeProtectionEnabled) { 'ATTIVA' } else { 'SPENTA' }); firme = $s.AntivirusSignatureAge; minacce = $t } | ConvertTo-Json",
                    300,
                    Reading("Antivirus", "{antivirus}").With("alertOn", "SPENTO"),
                    Reading("Tempo reale", "{tempo}").With("alertOn", "SPENTA"),
                    Reading("Firme", "{firme}", "giorni").With("above", 3),
                    Reading("Minacce 7 gg", "{minacce}").With("above", 0));
                o["actions"] = new JsonArray(PowerShellAction("Scansione rapida",
                    "Start-Process -WindowStyle Hidden \"$env:ProgramFiles\\Windows Defender\\MpCmdRun.exe\" -ArgumentList '-Scan','-ScanType','1'", confirm: false));
                return o;
            })
        {
            Permission = "Esegue PowerShell per leggere lo stato di Defender (Get-MpComputerStatus) e, col pulsante, avvia una scansione rapida.",
        },
        new("firewall", "Firewall di Windows", "Sicurezza", "Se il firewall è acceso per le reti di dominio, private e pubbliche: spento diventa un avviso.",
            [],
            _ => PowerShell(
                "$p = @{}; Get-NetFirewallProfile | ForEach-Object { $p[$_.Name] = $(if ($_.Enabled) { 'ATTIVO' } else { 'SPENTO' }) }; " +
                "@{ dominio = $p['Domain']; privata = $p['Private']; pubblica = $p['Public'] } | ConvertTo-Json",
                300,
                Reading("Dominio", "{dominio}").With("alertOn", "SPENTO"),
                Reading("Privata", "{privata}").With("alertOn", "SPENTO"),
                Reading("Pubblica", "{pubblica}").With("alertOn", "SPENTO")))
        {
            Permission = "Esegue PowerShell per leggere i profili del firewall (Get-NetFirewallProfile).",
        },
        new("aggiornamenti", "Aggiornamenti di Windows", "Sicurezza", "Da quanti giorni non si installano aggiornamenti e se Windows aspetta un riavvio.",
            [new("days", "Avvisa dopo (giorni)", FieldKind.Number) { Default = "35" }],
            v => PowerShell(
                "$h = Get-HotFix | Where-Object InstalledOn | Sort-Object InstalledOn -Descending | Select-Object -First 1; " +
                "@{ giorni = [math]::Floor(((Get-Date) - $h.InstalledOn).TotalDays); ultimo = \"$($h.HotFixID)\"; " +
                "riavvio = $(if (Test-Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\WindowsUpdate\\Auto Update\\RebootRequired') { 'SI' } else { 'NO' }) } | ConvertTo-Json",
                3600,
                Reading("Ultimo", "{giorni}", "giorni fa").With("above", v.Number("days")),
                Reading("Pacchetto", "{ultimo}"),
                Reading("Riavvio", "{riavvio}").With("alertOn", "SI")))
        {
            Permission = "Esegue PowerShell per leggere gli aggiornamenti installati (Get-HotFix) e il riavvio in sospeso.",
        },
        new("certificato", "Certificato SSL di un sito", "Sicurezza", "Quanti giorni mancano alla scadenza del certificato HTTPS, se è valido e chi lo emette.",
            [
                new("host", "Sito") { Hint = "miosito.it", Pattern = @"^[\w.\-]+$" },
                new("port", "Porta", FieldKind.Number) { Default = "443" },
                new("min", "Avvisa sotto (giorni)", FieldKind.Number) { Default = "14" },
            ],
            v =>
            {
                var host = Quote(v["host"]);
                return PowerShell(
                    $"$c = New-Object Net.Sockets.TcpClient({host}, {(int)(v.Number("port") ?? 443)}); " +
                    "$s = New-Object Net.Security.SslStream($c.GetStream(), $false, ({ $true } -as [Net.Security.RemoteCertificateValidationCallback])); " +
                    $"$s.AuthenticateAsClient({host}); $x = [Security.Cryptography.X509Certificates.X509Certificate2]$s.RemoteCertificate; $s.Dispose(); $c.Dispose(); " +
                    "@{ giorni = [math]::Floor(($x.NotAfter - (Get-Date)).TotalDays); valido = $(if ($x.Verify()) { 'SI' } else { 'NO' }); emittente = $x.GetNameInfo('SimpleName', $true) } | ConvertTo-Json",
                    3600,
                    Reading("Scade tra", "{giorni}", "giorni").With("below", v.Number("min")),
                    Reading("Valido", "{valido}").With("alertOn", "NO"),
                    Reading("Emittente", "{emittente}"));
            })
        {
            Permission = "Esegue PowerShell per aprire una connessione TLS al sito e leggere il certificato.",
        },
        new("intestazioni", "Intestazioni di sicurezza", "Sicurezza", "Quali difese HTTP manda un sito (HSTS, CSP, X-Frame-Options…) e quali mancano.",
            [new("url", "Indirizzo") { Hint = "https://miosito.it", Pattern = HttpUrl }],
            v => PowerShell(
                $"$h = (Invoke-WebRequest -Uri {Quote(v["url"])} -UseBasicParsing -MaximumRedirection 5 -TimeoutSec 10).Headers; " +
                "$need = 'Strict-Transport-Security','Content-Security-Policy','X-Content-Type-Options','X-Frame-Options','Referrer-Policy'; " +
                "$miss = @($need | Where-Object { -not $h[$_] }); " +
                "@{ presenti = \"$($need.Count - $miss.Count)/$($need.Count)\"; mancanti = $miss.Count; quali = $(if ($miss.Count) { $miss -join ', ' } else { 'nessuna' }) } | ConvertTo-Json",
                3600,
                Reading("Presenti", "{presenti}"),
                Reading("Mancanti", "{mancanti}").With("above", 0),
                Reading("Quali", "{quali}")))
        {
            Permission = "Esegue PowerShell per scaricare la pagina e leggerne le intestazioni (Invoke-WebRequest).",
        },
        new("porte", "Porte esposte", "Sicurezza", "Quante porte TCP del PC ascoltano sulla rete (non solo su localhost) e quali.",
            [new("max", "Avvisa sopra (porte)", FieldKind.Number) { Optional = true }],
            v => PowerShell(
                "$p = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.LocalAddress -notin '127.0.0.1','::1' } | Select-Object -ExpandProperty LocalPort | Sort-Object -Unique); " +
                "@{ esposte = $p.Count; quali = ($p | Select-Object -First 12) -join ', ' } | ConvertTo-Json",
                300,
                Reading("Esposte", "{esposte}").With("above", v.Number("max")),
                Reading("Quali", "{quali}")))
        {
            Permission = "Esegue PowerShell per elencare le porte in ascolto (Get-NetTCPConnection).",
        },
        new("amministratori", "Amministratori del PC", "Sicurezza", "Quanti e quali account sono amministratori: uno nuovo diventa un avviso.",
            [new("max", "Avvisa sopra (account)", FieldKind.Number) { Default = "2" }],
            v => PowerShell(
                "$m = @(Get-LocalGroupMember -SID 'S-1-5-32-544' -ErrorAction Stop | ForEach-Object { $_.Name.Split('\\')[-1] }); " +
                "@{ numero = $m.Count; chi = $m -join ', ' } | ConvertTo-Json",
                600,
                Reading("Amministratori", "{numero}").With("above", v.Number("max")),
                Reading("Chi", "{chi}")))
        {
            Permission = "Esegue PowerShell per leggere il gruppo Administrators (Get-LocalGroupMember).",
        },
        new("ssh-accessi", "Accessi SSH falliti", "Sicurezza", "Tentativi di accesso falliti a un server Linux nelle ultime 24 ore e sessioni aperte. Serve l'accesso con chiave.",
            [
                new("target", "Utente e host") { Hint = "utente@server.local", Pattern = SshTarget },
                new("max", "Avvisa sopra (tentativi)", FieldKind.Number) { Default = "20" },
            ],
            v => Obj(("type", "command"), ("command", "ssh"), ("refreshSeconds", 600),
                ("args", Array("-o", "BatchMode=yes", "-o", "ConnectTimeout=5", v["target"],
                    "journalctl -q _COMM=sshd --since '24 hours ago' 2>/dev/null | grep -c -E 'Failed password|Invalid user'; who | wc -l")),
                ("readings", new JsonArray(
                    Reading("Falliti 24 h", "{lines[0]}").With("above", v.Number("max")),
                    Reading("Sessioni", "{lines[1]}")))))
        {
            Permission = "Esegue ssh verso il server per leggere il registro di sshd (journalctl: l'utente deve poterlo leggere, gruppo adm o systemd-journal).",
        },
        new("kev", "Vulnerabilità sfruttate (CISA)", "Sicurezza", "Il catalogo CISA delle vulnerabilità sfruttate davvero: quante sono e quando è cambiato.",
            [],
            _ => Obj(("type", "http"), ("url", "https://www.cisa.gov/sites/default/files/feeds/known_exploited_vulnerabilities.json"), ("refreshSeconds", 21600),
                ("readings", new JsonArray(
                    Reading("Nel catalogo", "{count}"),
                    Reading("Ultima", "{vulnerabilities[0].cveID}").With("label", "Ultima aggiunta"),
                    Reading("Aggiornato", "{dateReleased}"))))),

        // ── Informazioni ──────────────────────────────────────────────────────────
        new("meteo", "Meteo", "Informazioni", "Temperatura, umidità, vento e pioggia di adesso (Open-Meteo, senza chiave).",
            [
                new("lat", "Latitudine") { Default = "45.46", Pattern = Coordinate },
                new("lon", "Longitudine") { Default = "9.19", Pattern = Coordinate },
                new("above", "Avvisa sopra (°C)", FieldKind.Number) { Optional = true },
            ],
            v => Obj(("type", "http"), ("refreshSeconds", 900),
                ("url", $"https://api.open-meteo.com/v1/forecast?latitude={Coord(v["lat"])}&longitude={Coord(v["lon"])}&current=temperature_2m,relative_humidity_2m,wind_speed_10m,precipitation"),
                ("readings", new JsonArray(
                    Reading("Temperatura", "{current.temperature_2m}", "°C").With("above", v.Number("above")),
                    Reading("Umidità", "{current.relative_humidity_2m}", "%"),
                    Reading("Vento", "{current.wind_speed_10m}", "km/h"),
                    Reading("Pioggia", "{current.precipitation}", "mm"))))),
        new("aria", "Qualità dell'aria", "Informazioni", "Indice europeo e polveri sottili (Open-Meteo, senza chiave).",
            [
                new("lat", "Latitudine") { Default = "45.46", Pattern = Coordinate },
                new("lon", "Longitudine") { Default = "9.19", Pattern = Coordinate },
                new("above", "Avvisa sopra (indice)", FieldKind.Number) { Default = "60", Optional = true },
            ],
            v => Obj(("type", "http"), ("refreshSeconds", 1800),
                ("url", $"https://air-quality-api.open-meteo.com/v1/air-quality?latitude={Coord(v["lat"])}&longitude={Coord(v["lon"])}&current=european_aqi,pm2_5,pm10"),
                ("readings", new JsonArray(
                    Reading("Indice", "{current.european_aqi}").With("above", v.Number("above")),
                    Reading("PM2.5", "{current.pm2_5}", "µg/m³"),
                    Reading("PM10", "{current.pm10}", "µg/m³"))))),
        new("cambio", "Cambio valuta", "Informazioni", "Tasso di cambio della BCE (Frankfurter), con soglie facoltative.",
            [
                new("from", "Da") { Default = "EUR", Pattern = "^[A-Za-z]{3}$" },
                new("to", "A") { Default = "USD", Pattern = "^[A-Za-z]{3}$" },
                new("above", "Avvisa sopra", FieldKind.Number) { Optional = true },
                new("below", "Avvisa sotto", FieldKind.Number) { Optional = true },
            ],
            v =>
            {
                var (from, to) = (v["from"].ToUpperInvariant(), v["to"].ToUpperInvariant());
                return Obj(("type", "http"), ("url", $"https://api.frankfurter.app/latest?from={from}&to={to}"), ("refreshSeconds", 3600),
                    ("readings", new JsonArray(Reading($"{from} → {to}", $"{{rates.{to}}}").With("decimals", 4).With("above", v.Number("above")).With("below", v.Number("below")))));
            }),
        new("crypto", "Criptovaluta", "Informazioni", "Prezzo di una moneta (CoinGecko), con soglie facoltative.",
            [
                new("coin", "Moneta (id di CoinGecko)") { Default = "bitcoin", Hint = "ethereum", Pattern = @"^[a-z0-9\-]+$" },
                new("currency", "Valuta") { Default = "eur", Pattern = "^[A-Za-z]{3}$" },
                new("above", "Avvisa sopra", FieldKind.Number) { Optional = true },
                new("below", "Avvisa sotto", FieldKind.Number) { Optional = true },
            ],
            v =>
            {
                var currency = v["currency"].ToLowerInvariant();
                return Obj(("type", "http"), ("url", $"https://api.coingecko.com/api/v3/simple/price?ids={v["coin"]}&vs_currencies={currency}"), ("refreshSeconds", 300),
                    ("readings", new JsonArray(Reading(v["coin"], $"{{{v["coin"]}.{currency}}}", currency.ToUpperInvariant()).With("above", v.Number("above")).With("below", v.Number("below")))));
            }),

        // ── Personalizzati ────────────────────────────────────────────────────────
        new("api", "API JSON", "Personalizzati", "Un valore da qualsiasi URL che risponde in JSON, con token facoltativo.",
            [
                new("url", "Indirizzo") { Hint = "https://api.esempio.it/stato", Pattern = HttpUrl },
                new("value", "Percorso del valore") { Hint = "dati.temperatura", Pattern = @"^[\w.\[\]\-]+$" },
                new("label", "Etichetta") { Default = "Valore" },
                new("unit", "Unità") { Optional = true },
                new("token", "Token (Authorization: Bearer)", FieldKind.Secret) { Optional = true },
            ],
            v => Obj(("type", "http"), ("url", v["url"]), ("refreshSeconds", 60),
                ("readings", new JsonArray(Reading(v["label"], "{" + v["value"] + "}", v.Has("unit") ? v["unit"] : null))))),
        new("file", "File di stato", "Personalizzati", "Un file JSON o di testo scritto da uno script, anche su una cartella di rete.",
            [
                new("path", "Percorso del file") { Hint = @"\\nas\backup\ultimo.json" },
                new("value", "Percorso del valore nel JSON") { Hint = "esito", Optional = true, Pattern = @"^[\w.\[\]\-]+$" },
            ],
            v => Obj(("type", "file"), ("path", v["path"]), ("refreshSeconds", 300),
                ("readings", new JsonArray(Reading("Valore", v.Has("value") ? "{" + v["value"] + "}" : "{lines[0]}"))))),
        new("comando", "Comando PowerShell", "Personalizzati", "Qualsiasi comando: si mostra la prima riga dell'uscita (o un JSON).",
            [new("script", "Comando") { Hint = "(Get-Date).ToString('HH:mm')" }],
            v => Obj(("type", "command"), ("command", "powershell.exe"), ("args", Array("-NoProfile", "-NonInteractive", "-Command", v["script"])), ("refreshSeconds", 60)))
        {
            Permission = "Esegue il comando che hai scritto, a ogni aggiornamento.",
        },
    ];

    public static ConnectorTemplate? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>
    /// Controlla i campi e costruisce il file: nome del connettore, tipo e letture. Il segreto torna a parte (va nel
    /// Gestore credenziali). L'id viene dal nome e non ripete quelli già usati; con <paramref name="replaceId"/> si
    /// riscrive quel connettore (Modifica). I valori del modulo, senza segreti, restano nel file (<c>"values"</c>):
    /// servono a Modifica e Duplica. Lo stesso connettore si può attivare quante volte serve (più siti, più server).
    /// </summary>
    public static TemplateResult Create(ConnectorTemplate template, string? name, IReadOnlyDictionary<string, string> values, IReadOnlyCollection<string> existingIds,
        string? replaceId = null, bool keepSecret = false)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(existingIds);
        var clean = new Dictionary<string, string>(StringComparer.Ordinal);
        string? secret = null;
        var keptSecret = false;
        foreach (var field in template.Fields)
        {
            var value = values.TryGetValue(field.Key, out var raw) ? raw.Trim() : string.Empty;
            if (value.Length == 0 && field.Kind == FieldKind.Secret && keepSecret)
            {
                keptSecret = true; // Modifica: resta il token già nel Gestore credenziali
                continue;
            }
            if (value.Length == 0 && field.Kind != FieldKind.Secret)
                value = field.Default ?? string.Empty;
            if (value.Length == 0)
            {
                if (field.Optional)
                    continue;
                return Fail($"manca «{field.Label}»");
            }
            if (field.Kind == FieldKind.Number && !double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                return Fail($"«{field.Label}» deve essere un numero");
            var parts = field.Kind == FieldKind.List ? SplitList(value) : [value];
            if (parts.Length == 0)
                return Fail($"manca «{field.Label}»");
            if (field.Pattern is { } pattern && parts.FirstOrDefault(p => !Regex.IsMatch(p, pattern)) is { } bad)
                return Fail($"«{bad}» non va bene come {field.Label.ToLowerInvariant()}{(field.Hint is { } h ? $" (es. {h})" : string.Empty)}");
            if (field.Kind == FieldKind.Secret)
                secret = value;
            clean[field.Key] = value;
        }

        var display = string.IsNullOrWhiteSpace(name) ? template.Name : name.Trim();
        var body = template.Build(new TemplateInput(clean));
        var root = new JsonObject { ["name"] = display, ["template"] = template.Id };
        foreach (var (key, node) in body.ToArray())
        {
            body.Remove(key);
            root[key] = node;
        }
        if (secret is not null || keptSecret)
            root["secret"] = true;
        var remembered = new JsonObject();
        foreach (var field in template.Fields.Where(f => f.Kind != FieldKind.Secret && clean.ContainsKey(f.Key)))
            remembered[field.Key] = clean[field.Key];
        if (remembered.Count > 0)
            root["values"] = remembered;

        var id = replaceId ?? UniqueId(Slug(display) ?? template.Id, existingIds);
        var json = root.ToJsonString(Indented);
        var parsed = ConnectorParser.Parse(id, json);
        return parsed.Error is { } error ? Fail(error) : new TemplateResult(id, json, secret, null);
    }

    /// <summary>Mette (o toglie, con null) il blocco <c>"llm"</c> nel file di un connettore.</summary>
    public static string WithLlm(string json, LlmSpec? llm)
    {
        ArgumentNullException.ThrowIfNull(json);
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
        if (llm is null)
            root.Remove("llm");
        else
            root["llm"] = llm.ToJson();
        return root.ToJsonString(Indented);
    }

    /// <summary>"Stampante 3D (Klipper)" → "stampante-3d-klipper": minuscole, cifre e trattini, al massimo 40.</summary>
    public static string? Slug(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var plain = new StringBuilder();
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                plain.Append(char.ToLowerInvariant(c));
        }
        var slug = NotSlug().Replace(plain.ToString(), "-").Trim('-');
        if (slug.Length > 40)
            slug = slug[..40].TrimEnd('-');
        return slug.Length > 0 ? slug : null;
    }

    internal static string[] SplitList(string value) =>
        value.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string UniqueId(string id, IReadOnlyCollection<string> existing)
    {
        if (!existing.Contains(id, StringComparer.OrdinalIgnoreCase))
            return id;
        for (var n = 2; ; n++)
        {
            var suffix = "-" + n.ToString(CultureInfo.InvariantCulture);
            var candidate = (id.Length + suffix.Length > 40 ? id[..(40 - suffix.Length)] : id) + suffix;
            if (!existing.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                return candidate;
        }
    }

    private static TemplateResult Fail(string error) => new(null, null, null, error);

    private static JsonObject Obj(params (string Key, JsonNode? Value)[] properties)
    {
        var o = new JsonObject();
        foreach (var (key, value) in properties)
        {
            if (value is not null)
                o[key] = value;
        }
        return o;
    }

    private static JsonArray Array(params string[] items) => new(items.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());

    private static JsonObject Reading(string label, string? value, string? unit = null) => Obj(("label", label), ("value", value), ("unit", unit));

    /// <summary>Aggiunge una proprietà se ha un valore (le soglie facoltative restano fuori).</summary>
    private static JsonObject With(this JsonObject o, string key, JsonNode? value)
    {
        if (value is not null)
            o[key] = value;
        return o;
    }

    private static JsonObject PowerShell(string script, int refreshSeconds, params JsonObject[] readings) =>
        Obj(("type", "command"), ("command", "powershell.exe"), ("args", Array("-NoProfile", "-NonInteractive", "-Command", script)),
            ("refreshSeconds", refreshSeconds), ("readings", new JsonArray(readings.Select(r => (JsonNode?)r).ToArray())));

    private static JsonObject PowerShellAction(string label, string script, bool confirm) =>
        Obj(("label", label), ("command", "powershell.exe"), ("args", Array("-NoProfile", "-NonInteractive", "-Command", script)), ("confirm", confirm ? true : null));

    private static JsonObject Ssh(string target, string remote, params JsonObject[] readings) =>
        Obj(("type", "command"), ("command", "ssh"), ("args", Array("-o", "BatchMode=yes", "-o", "ConnectTimeout=5", target, remote)), ("refreshSeconds", 60),
            ("readings", new JsonArray(readings.Select(r => (JsonNode?)r).ToArray())),
            ("actions", new JsonArray(Obj(("label", "Riavvia"), ("command", "ssh"), ("args", Array("-o", "BatchMode=yes", target, "sudo -n reboot")), ("confirm", true)))));

    private static JsonObject GitHub(string url, int refreshSeconds, params JsonObject[] readings) =>
        Obj(("type", "http"), ("url", url), ("refreshSeconds", refreshSeconds),
            ("headers", Obj(("Accept", "application/vnd.github+json"), ("X-GitHub-Api-Version", "2022-11-28"))),
            ("readings", new JsonArray(readings.Select(r => (JsonNode?)r).ToArray())));

    /// <summary>Stringa PowerShell tra apici singoli: un apice nel testo si raddoppia.</summary>
    private static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Coord(string text) => text.Replace(',', '.');

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();
}
