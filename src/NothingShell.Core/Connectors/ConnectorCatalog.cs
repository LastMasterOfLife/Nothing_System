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

    /// <summary>Controllo in più sul valore (una query di sola lettura): il motivo dell'errore, null se va bene.</summary>
    public Func<string, string?>? Validate { get; init; }
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

        // Questi quattro prendono spunto dai plugin di un file manager (Ski): cartelle, cestino, unità, duplicati.
        new("cartella", "Dimensione di una cartella", "Questo PC", "Quanto occupa una cartella (Download, progetti, backup) e quanti file ha; Apri.",
            [
                new("path", "Cartella") { Hint = @"C:\Users\nome\Downloads" },
                new("max", "Avvisa sopra (GB)", FieldKind.Number) { Optional = true },
            ],
            v =>
            {
                var path = Quote(v["path"]);
                var o = PowerShell(
                    $"$f = Get-ChildItem -LiteralPath {path} -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum; @{{ gb = [math]::Round($f.Sum / 1GB, 2); file = $f.Count }} | ConvertTo-Json",
                    1800,
                    Reading("Occupa", "{gb}", "GB").With("above", v.Number("max")),
                    Reading("File", "{file}"));
                o["actions"] = new JsonArray(PowerShellAction("Apri", $"Invoke-Item -LiteralPath {path}", confirm: false));
                return o;
            })
        {
            Permission = "Esegue PowerShell per sommare i file della cartella (Get-ChildItem) e, col pulsante, la apre in Esplora file.",
        },
        new("cestino", "Cestino", "Questo PC", "Quanti elementi ci sono nel cestino e quanto spazio occupano; Svuota.",
            [new("max", "Avvisa sopra (MB)", FieldKind.Number) { Optional = true }],
            v =>
            {
                var o = PowerShell(
                    "$i = @((New-Object -ComObject Shell.Application).Namespace(10).Items()); " +
                    "@{ elementi = $i.Count; mb = [math]::Round((($i | ForEach-Object { $_.Size }) | Measure-Object -Sum).Sum / 1MB) } | ConvertTo-Json",
                    600,
                    Reading("Elementi", "{elementi}"),
                    Reading("Occupa", "{mb}", "MB").With("above", v.Number("max")));
                o["actions"] = new JsonArray(PowerShellAction("Svuota", "Clear-RecycleBin -Force -ErrorAction SilentlyContinue", confirm: true));
                return o;
            })
        {
            Permission = "Esegue PowerShell per leggere il cestino e, col pulsante, svuotarlo (Clear-RecycleBin): i file non si recuperano più.",
        },
        new("unita-rimovibile", "Unità rimovibile", "Questo PC", "Chiavetta USB o disco esterno: se è collegato, nome e spazio libero; Espelli.",
            [new("drive", "Lettera dell'unità") { Default = "E", Hint = "E", Pattern = "^[A-Za-z]:?$" }],
            v =>
            {
                var letter = v["drive"].TrimEnd(':').ToUpperInvariant();
                var o = PowerShell(
                    $"$v = Get-Volume -DriveLetter {letter} -ErrorAction SilentlyContinue; " +
                    "$(if ($v) { @{ stato = 'COLLEGATA'; nome = \"$($v.FileSystemLabel)\"; libero = [math]::Round($v.SizeRemaining / 1GB, 1) } } " +
                    "else { @{ stato = 'ASSENTE'; nome = '-'; libero = 0 } }) | ConvertTo-Json",
                    30,
                    Reading("Stato", "{stato}"),
                    Reading("Nome", "{nome}"),
                    Reading("Libero", "{libero}", "GB"));
                o["actions"] = new JsonArray(PowerShellAction("Espelli",
                    $"(New-Object -ComObject Shell.Application).Namespace(17).ParseName('{letter}:').InvokeVerb('Eject')", confirm: false));
                return o;
            })
        {
            Permission = "Esegue PowerShell per leggere l'unità (Get-Volume) e, col pulsante, espellerla come da Esplora file.",
        },
        new("duplicati", "File duplicati", "Questo PC", "Copie identiche dentro una cartella (stesso contenuto, SHA-256) e lo spazio che sprecano.",
            [
                new("path", "Cartella") { Hint = @"C:\Users\nome\Downloads" },
                new("max", "Avvisa sopra (MB sprecati)", FieldKind.Number) { Optional = true },
            ],
            v => PowerShell(
                // prima per dimensione (veloce), l'impronta solo per i file della stessa dimensione
                $"$g = @(Get-ChildItem -LiteralPath {Quote(v["path"])} -Recurse -File -ErrorAction SilentlyContinue | Where-Object Length -gt 0 | Group-Object Length | Where-Object Count -gt 1 | " +
                "ForEach-Object { $n = $_.Name; $_.Group | Get-FileHash -Algorithm SHA256 -ErrorAction SilentlyContinue | Add-Member -NotePropertyName Size -NotePropertyValue ([long]$n) -PassThru } | " +
                "Group-Object Hash | Where-Object Count -gt 1); " +
                "@{ copie = [int](($g | ForEach-Object { $_.Count - 1 }) | Measure-Object -Sum).Sum; mb = [math]::Round((($g | ForEach-Object { $_.Group[0].Size * ($_.Count - 1) }) | Measure-Object -Sum).Sum / 1MB, 1) } | ConvertTo-Json",
                21600,
                Reading("Copie in più", "{copie}"),
                Reading("Sprecati", "{mb}", "MB").With("above", v.Number("max"))))
        {
            Permission = "Esegue PowerShell per leggere i file della cartella e calcolarne l'impronta (Get-FileHash); non cancella niente.",
        },

        // ── Questo PC: programmi, code e attività (docs/18-connettori-diagnostica.md) ──
        new("processo-dettaglio", "Programma: salute", "Questo PC", "Un programma in esecuzione: CPU, memoria, thread, handle e da quanto è partito (un riavvio improvviso si vede).",
            [
                new("process", "Nome del processo") { Hint = "w3wp", Pattern = @"^[\w.\-]+$" },
                new("cpu", "Avvisa sopra (% CPU)", FieldKind.Number) { Optional = true },
                new("mem", "Avvisa sopra (MB di memoria)", FieldKind.Number) { Optional = true },
                new("handles", "Avvisa sopra (handle)", FieldKind.Number) { Optional = true },
                new("restart", "Avvisa se è partito da meno di (minuti)", FieldKind.Number) { Optional = true },
            ],
            v =>
            {
                var name = Quote(Regex.Replace(v["process"], @"\.exe$", string.Empty, RegexOptions.IgnoreCase));
                return PowerShell(
                    $"$p = @(Get-Process -Name {name} -ErrorAction SilentlyContinue); " +
                    "if ($p.Count -eq 0) { $o = @{ stato = 'FERMO'; istanze = 0; cpu = 0; memoria = 0; thread = 0; handle = 0; minuti = 'n.d.' } } else { " +
                    "$t1 = ($p | Measure-Object CPU -Sum).Sum; Start-Sleep -Seconds 1; " +
                    $"$q = @(Get-Process -Name {name} -ErrorAction SilentlyContinue); if ($q.Count -eq 0) {{ $q = $p }}; " +
                    "$t2 = ($q | Measure-Object CPU -Sum).Sum; " +
                    "$st = @($q | ForEach-Object { try { $_.StartTime } catch { $null } } | Where-Object { $_ } | Sort-Object -Descending); " +
                    "$o = @{ stato = 'ATTIVO'; istanze = $q.Count; cpu = [math]::Round(([double]$t2 - [double]$t1) / [Environment]::ProcessorCount * 100); " +
                    "memoria = [math]::Round(($q | Measure-Object WorkingSet64 -Sum).Sum / 1MB); thread = [int](($q | ForEach-Object { $_.Threads.Count } | Measure-Object -Sum).Sum); " +
                    "handle = [int](($q | Measure-Object HandleCount -Sum).Sum); minuti = $(if ($st.Count) { [math]::Round(((Get-Date) - $st[0]).TotalMinutes) } else { 'n.d.' }) } }; $o | ConvertTo-Json",
                    30,
                    Reading("Stato", "{stato}").With("alertOn", "FERMO"),
                    Reading("Istanze", "{istanze}"),
                    Reading("CPU", "{cpu}", "%").With("above", v.Number("cpu")),
                    Reading("Memoria", "{memoria}", "MB").With("above", v.Number("mem")),
                    Reading("Thread", "{thread}"),
                    Reading("Handle", "{handle}").With("above", v.Number("handles")),
                    Reading("In esecuzione da", "{minuti}", "min").With("below", v.Number("restart")));
            })
        {
            Permission = "Esegue PowerShell per leggere i contatori del processo (Get-Process): non lo tocca.",
        },
        new("log-errori", "Log: errori e attività", "Questo PC", "Quante delle ultime righe di un log contengono errori, l'ultima di queste e da quanto il file non cambia (programma fermo).",
            [
                new("path", "Percorso del file") { Hint = @"C:\app\logs\app.log" },
                new("pattern", "Espressione da cercare") { Default = "error|errore|exception|fail|fatal", Hint = "espressione regolare, senza distinguere maiuscole", Pattern = @"^[^\r\n]+$" },
                new("lines", "Righe da guardare", FieldKind.Number) { Default = "1000" },
                new("max", "Avvisa sopra (righe trovate)", FieldKind.Number) { Optional = true },
                new("stale", "Avvisa se non cambia da (minuti)", FieldKind.Number) { Optional = true },
            ],
            v => PowerShell(
                $"$f = Get-Item -LiteralPath {Quote(v["path"])} -ErrorAction Stop; $l = @(Get-Content -LiteralPath {Quote(v["path"])} -Tail {(int)Math.Clamp(v.Number("lines") ?? 1000, 1, 20000)} -ErrorAction Stop); " +
                $"$m = @($l | Where-Object {{ $_ -match {Quote(v["pattern"])} }}); $u = $(if ($m.Count) {{ \"$($m[-1])\".Trim() }} else {{ '' }}); if ($u.Length -gt 160) {{ $u = $u.Substring(0, 160) }}; " +
                "@{ trovate = $m.Count; ultima = $u; fermo = [math]::Round(((Get-Date) - $f.LastWriteTime).TotalMinutes) } | ConvertTo-Json",
                60,
                Reading("Righe con errori", "{trovate}").With("above", v.Number("max")),
                Reading("Ultima", "{ultima}"),
                Reading("Non cambia da", "{fermo}", "min").With("above", v.Number("stale"))))
        {
            Permission = "Esegue PowerShell per leggere le ultime righe del file (Get-Content): non lo modifica.",
        },
        new("cartella-coda", "Cartella di coda", "Questo PC", "File in attesa in una cartella (spool, importazioni, posta in uscita) e da quanto aspetta il più vecchio: una coda ferma diventa un avviso.",
            [
                new("path", "Cartella") { Hint = @"C:\spool\in o \\server\scambio\in" },
                new("filter", "Solo i file") { Default = "*", Hint = "*.xml", Pattern = @"^[\w.*?\-]+$" },
                new("max", "Avvisa sopra (file)", FieldKind.Number) { Optional = true },
                new("age", "Avvisa se il più vecchio aspetta da (minuti)", FieldKind.Number) { Optional = true },
            ],
            v => PowerShell(
                $"$f = @(Get-ChildItem -LiteralPath {Quote(v["path"])} -File -Force -Filter {Quote(v["filter"])} -ErrorAction Stop); $old = $f | Sort-Object LastWriteTime | Select-Object -First 1; " +
                "@{ file = $f.Count; minuti = $(if ($old) { [math]::Round(((Get-Date) - $old.LastWriteTime).TotalMinutes) } else { 0 }); nome = $(if ($old) { $old.Name } else { '' }) } | ConvertTo-Json",
                60,
                Reading("In coda", "{file}", "file").With("above", v.Number("max")),
                Reading("Il più vecchio aspetta", "{minuti}", "min").With("above", v.Number("age")),
                Reading("Nome", "{nome}")))
        {
            Permission = "Esegue PowerShell per elencare i file della cartella (Get-ChildItem): non li apre, non li sposta e non li cancella.",
        },
        new("file-recente", "File più recente", "Questo PC", "Quanto è vecchio l'ultimo file di una cartella (backup, esportazioni): \"l'ultimo backup ha 3 giorni\" diventa un avviso.",
            [
                new("path", "Cartella") { Hint = @"D:\backup" },
                new("filter", "Solo i file") { Default = "*", Hint = "*.bak", Pattern = @"^[\w.*?\-]+$" },
                new("max", "Avvisa oltre (ore)", FieldKind.Number) { Default = "26" },
                new("minmb", "Avvisa sotto (MB)", FieldKind.Number) { Optional = true },
            ],
            v => PowerShell(
                $"$f = Get-ChildItem -LiteralPath {Quote(v["path"])} -File -Force -Filter {Quote(v["filter"])} -ErrorAction Stop | Sort-Object LastWriteTime -Descending | Select-Object -First 1; " +
                "$o = if ($f) { @{ stato = 'OK'; nome = $f.Name; ore = [math]::Round(((Get-Date) - $f.LastWriteTime).TotalHours, 1); mb = [math]::Round($f.Length / 1MB, 1) } } else { @{ stato = 'NESSUN FILE'; nome = ''; ore = 'n.d.'; mb = 0 } }; $o | ConvertTo-Json",
                300,
                Reading("Stato", "{stato}").With("alertOn", "NESSUN FILE"),
                Reading("Ultimo file", "{nome}"),
                Reading("Età", "{ore}", "ore").With("above", v.Number("max")),
                Reading("Dimensione", "{mb}", "MB").With("below", v.Number("minmb"))))
        {
            Permission = "Esegue PowerShell per elencare i file della cartella (Get-ChildItem): non li apre, non li sposta e non li cancella.",
        },
        new("attivita-pianificata", "Attività pianificata", "Questo PC", "Una attività di Windows (Utilità di pianificazione): stato, ultimo esito, da quanto non gira e quando riparte.",
            [
                new("task", "Nome dell'attività") { Hint = "Backup notturno", Pattern = @"^[\w.\- ]+$" },
                new("hours", "Avvisa se non gira da (ore)", FieldKind.Number) { Optional = true },
            ],
            v => PowerShell(
                $"$t = Get-ScheduledTask -TaskName {Quote(v["task"])} -ErrorAction Stop | Select-Object -First 1; $i = $t | Get-ScheduledTaskInfo; $r = [int64]$i.LastTaskResult; " +
                // 0 = riuscita, 267009 = in corso, 267011 = mai partita: gli altri codici sono errori
                "$e = if ($r -eq 0 -or $r -eq 267009 -or $r -eq 267011) { 'OK' } else { 'ERRORE' }; " +
                "$ore = if ($i.LastRunTime -and $i.LastRunTime.Year -gt 2000) { [math]::Round(((Get-Date) - $i.LastRunTime).TotalHours, 1) } else { 'mai' }; " +
                "$next = if ($i.NextRunTime -and $i.NextRunTime.Year -gt 2000) { $i.NextRunTime.ToString('dd/MM HH:mm') } else { 'nessuna' }; " +
                "@{ stato = \"$($t.State)\"; esito = $e; codice = ('0x{0:X}' -f $r); ore = $ore; prossima = $next } | ConvertTo-Json",
                120,
                Reading("Stato", "{stato}").With("alertOn", "Disabled"),
                Reading("Ultimo esito", "{esito}").With("alertOn", "ERRORE"),
                Reading("Codice", "{codice}"),
                Reading("Ultima esecuzione", "{ore}", "ore fa").With("above", v.Number("hours")),
                Reading("Prossima", "{prossima}")))
        {
            Permission = "Esegue PowerShell per leggere l'attività (Get-ScheduledTask): non la avvia e non la modifica.",
        },
        new("registro-eventi", "Registro eventi di Windows", "Questo PC", "Errori e arresti anomali delle applicazioni nelle ultime ore, con l'ultimo messaggio.",
            [
                new("log", "Registro") { Default = "Application", Hint = "Application o System", Pattern = "^(Application|System)$" },
                new("source", "Sorgente (vuoto = tutte)") { Hint = ".NET Runtime, Application Error, MSSQLSERVER", Optional = true, Pattern = @"^[\w.\- /]+$" },
                new("hours", "Ore da guardare", FieldKind.Number) { Default = "24" },
                new("max", "Avvisa sopra (errori)", FieldKind.Number) { Default = "0" },
            ],
            v => PowerShell(
                $"$q = @{{ LogName = {Quote(v["log"])}; Level = 1, 2; StartTime = (Get-Date).AddHours(-{(int)Math.Clamp(v.Number("hours") ?? 24, 1, 720)}) }}; " +
                (v.Has("source") ? $"$q['ProviderName'] = {Quote(v["source"])}; " : string.Empty) +
                "$e = @(Get-WinEvent -FilterHashtable $q -MaxEvents 500 -ErrorAction SilentlyContinue); $u = $e | Select-Object -First 1; " +
                "$msg = if ($u) { (\"$($u.Message)\" -split '\\r?\\n')[0] } else { '' }; if ($msg.Length -gt 140) { $msg = $msg.Substring(0, 140) }; " +
                "@{ errori = $e.Count; ultimo = $msg; quando = $(if ($u) { $u.TimeCreated.ToString('dd/MM HH:mm') } else { '—' }); fonte = $(if ($u) { $u.ProviderName } else { '' }) } | ConvertTo-Json",
                120,
                Reading("Errori", "{errori}").With("above", v.Number("max")),
                Reading("Ultimo", "{ultimo}"),
                Reading("Quando", "{quando}"),
                Reading("Sorgente", "{fonte}")))
        {
            Permission = "Esegue PowerShell per leggere il registro eventi (Get-WinEvent): solo errori e critici, non lo modifica.",
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
        new("sito-contenuto", "Contenuto di una pagina", "Rete e web", "Scarica una pagina e controlla il codice, il tempo e che contenga (o non contenga) un certo testo: prende le pagine che rispondono ma sono sbagliate.",
            [
                new("url", "Indirizzo") { Hint = "https://miosito.it/login", Pattern = HttpUrl },
                new("contains", "Deve contenere") { Hint = "Accedi", Optional = true, Pattern = @"^[^\r\n]+$" },
                new("absent", "Non deve contenere") { Hint = "Fatal error", Optional = true, Pattern = @"^[^\r\n]+$" },
                new("max", "Avvisa sopra (ms)", FieldKind.Number) { Optional = true },
                new("token", "Token (Authorization: Bearer)", FieldKind.Secret) { Optional = true },
            ],
            v => PowerShell(
                "$ErrorActionPreference = 'Stop'; $h = @{}; if ($env:NOTHING_SECRET) { $h['Authorization'] = 'Bearer ' + $env:NOTHING_SECRET }; $code = 0; $body = ''; $sw = [Diagnostics.Stopwatch]::StartNew(); " +
                $"try {{ $r = Invoke-WebRequest -UseBasicParsing -Uri {Quote(v["url"])} -Headers $h -TimeoutSec 15 -MaximumRedirection 5; $code = [int]$r.StatusCode; $body = [string]$r.Content }} " +
                "catch { if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode } else { throw } }; $sw.Stop(); " +
                (v.Has("contains") ? $"$c = if ($body.IndexOf({Quote(v["contains"])}, [StringComparison]::OrdinalIgnoreCase) -ge 0) {{ 'TROVATO' }} else {{ 'MANCANTE' }}; " : "$c = ''; ") +
                (v.Has("absent") ? $"$a = if ($body.IndexOf({Quote(v["absent"])}, [StringComparison]::OrdinalIgnoreCase) -ge 0) {{ 'PRESENTE' }} else {{ 'ASSENTE' }}; " : "$a = ''; ") +
                "@{ codice = $code; ms = $sw.ElapsedMilliseconds; kb = [math]::Round($body.Length / 1KB, 1); contiene = $c; vietato = $a } | ConvertTo-Json",
                60,
                new[]
                {
                    Reading("Codice", "{codice}").With("above", 399),
                    Reading("Risposta", "{ms}", "ms").With("above", v.Number("max")),
                    Reading("Dimensione", "{kb}", "KB"),
                    v.Has("contains") ? Reading("Contiene", "{contiene}").With("alertOn", "MANCANTE") : null,
                    v.Has("absent") ? Reading("Testo vietato", "{vietato}").With("alertOn", "PRESENTE") : null,
                }.OfType<JsonObject>().ToArray()))
        {
            Permission = "Esegue PowerShell per scaricare la pagina (Invoke-WebRequest, solo lettura) e cercarci il testo indicato.",
        },
        new("salute-api", "Salute di un'API", "Rete e web", "Legge l'indirizzo di salute di un servizio (/health, /actuator/health, /healthz): stato complessivo e componenti in errore, anche quando risponde 503.",
            [
                new("url", "Indirizzo di salute") { Hint = "https://miosito.it/actuator/health", Pattern = HttpUrl },
                new("max", "Avvisa sopra (ms)", FieldKind.Number) { Optional = true },
                new("token", "Token (Authorization: Bearer)", FieldKind.Secret) { Optional = true },
            ],
            v => PowerShell(
                "$ErrorActionPreference = 'Stop'; $h = @{}; if ($env:NOTHING_SECRET) { $h['Authorization'] = 'Bearer ' + $env:NOTHING_SECRET }; $code = 0; $body = ''; $sw = [Diagnostics.Stopwatch]::StartNew(); " +
                $"try {{ $r = Invoke-WebRequest -UseBasicParsing -Uri {Quote(v["url"])} -Headers $h -TimeoutSec 15 -MaximumRedirection 5; $code = [int]$r.StatusCode; $body = [string]$r.Content }} " +
                // un servizio malato risponde 503 con il dettaglio nel corpo: si legge anche quello
                "catch { if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode; $sr = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream()); $body = $sr.ReadToEnd(); $sr.Dispose() } else { throw } }; $sw.Stop(); " +
                "$j = $null; try { $j = $body | ConvertFrom-Json } catch { }; " +
                "$risposta = if ($j -and $j.status) { \"$($j.status)\" } elseif ($body.Length -gt 0 -and $body.Length -lt 40) { $body.Trim() } else { '' }; " +
                "$comp = $null; if ($j) { $comp = $j.components; if (-not $comp) { $comp = $j.entries } }; $bad = @(); $all = @(); " +
                "if ($comp -and $comp -isnot [array]) { foreach ($p in $comp.PSObject.Properties) { $cs = \"$($p.Value.status)\"; $all += \"$($p.Name)=$cs\"; if ($cs -match 'down|unhealthy|fail|error|critical|out_of_service') { $bad += $p.Name } } }; " +
                "$ko = ($code -ge 400) -or ($risposta -match 'down|unhealthy|fail|error|critical|out_of_service'); " +
                "$stato = if ($ko) { 'KO' } elseif ($risposta -match 'degraded|warn') { 'DEGRADATO' } else { 'OK' }; " +
                "@{ stato = $stato; codice = $code; ms = $sw.ElapsedMilliseconds; risposta = $risposta; guasti = $bad.Count; dettaglio = ($bad -join ', '); componenti = ($all -join ', ') } | ConvertTo-Json",
                30,
                Reading("Stato", "{stato}").With("alertOn", "KO"),
                Reading("Risposta", "{risposta}"),
                Reading("Codice", "{codice}"),
                Reading("Tempo", "{ms}", "ms").With("above", v.Number("max")),
                Reading("Componenti in errore", "{guasti}").With("above", 0),
                Reading("In errore", "{dettaglio}"),
                Reading("Componenti", "{componenti}")))
        {
            Permission = "Esegue PowerShell per leggere l'indirizzo di salute (Invoke-WebRequest, solo lettura).",
        },

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

        new("ftp", "Server FTP", "Server", "Si collega a un server FTP o FTPS, elenca una cartella e dice quanti elementi ci sono e quanto ci mette a rispondere.",
            [
                new("host", "Server") { Hint = "ftp.miosito.it", Pattern = HostName },
                new("port", "Porta", FieldKind.Number) { Default = "21" },
                new("username", "Utente") { Hint = "vuoto = accesso anonimo", Optional = true, Pattern = @"^[^\s'""`$]+$" },
                new("password", "Password", FieldKind.Secret) { Optional = true },
                new("folder", "Cartella da elencare") { Default = "/", Pattern = @"^/\S*$" },
                new("tls", "Connessione cifrata (FTPS)") { Default = "no", Hint = "si o no", Pattern = "^(si|no)$" },
                new("min", "Avvisa sotto (elementi)", FieldKind.Number) { Optional = true },
            ],
            v => PowerShell(
                "$ErrorActionPreference = 'Stop'; " +
                $"$r = [Net.FtpWebRequest]::Create('ftp://' + {Quote(v["host"])} + ':{(int)(v.Number("port") ?? 21)}' + {Quote(v["folder"])}); " +
                "$r.Method = [Net.WebRequestMethods+Ftp]::ListDirectory; $r.UsePassive = $true; $r.Timeout = 10000; $r.ReadWriteTimeout = 10000; " +
                $"$r.EnableSsl = ${(v["tls"] == "si" ? "true" : "false")}; " +
                (v.Has("username") ? $"$r.Credentials = New-Object Net.NetworkCredential({Quote(v["username"])}, $env:NOTHING_SECRET); " : string.Empty) +
                "$sw = [Diagnostics.Stopwatch]::StartNew(); $resp = $r.GetResponse(); $rd = New-Object IO.StreamReader($resp.GetResponseStream()); " +
                "$n = @($rd.ReadToEnd() -split '\\r?\\n' | Where-Object { $_ }).Count; $sw.Stop(); $msg = \"$($resp.StatusDescription)\".Trim(); $rd.Dispose(); $resp.Close(); " +
                "@{ stato = 'ONLINE'; ms = $sw.ElapsedMilliseconds; elementi = $n; risposta = $msg } | ConvertTo-Json",
                120,
                Reading("Stato", "{stato}"),
                Reading("Risposta", "{ms}", "ms"),
                Reading("Elementi", "{elementi}").With("below", v.Number("min")),
                Reading("Esito", "{risposta}")))
        {
            Permission = "Esegue PowerShell per collegarsi al server FTP con l'utente e la password indicati ed elencare la cartella: non carica, non scarica e non cancella nulla.",
        },
        new("database", "Database", "Server", "SQL Server: apre la connessione, misura i tempi, legge la versione ed esegue una query di controllo. PostgreSQL e MySQL: raggiungibilità della porta.",
            [
                new("engine", "Tipo") { Default = "sqlserver", Hint = "sqlserver, postgres, mysql o mariadb", Pattern = "^(sqlserver|postgres|mysql|mariadb)$" },
                new("host", "Server") { Hint = "db.miosito.it", Pattern = HostName },
                new("port", "Porta (vuota = quella del tipo)", FieldKind.Number) { Optional = true },
                new("database", "Database") { Hint = "solo SQL Server", Optional = true, Pattern = @"^[\w.\-]+$" },
                new("username", "Utente") { Hint = "vuoto = accesso integrato di Windows (SQL Server)", Optional = true, Pattern = @"^[^\s'""`$;]+$" },
                new("password", "Password", FieldKind.Secret) { Optional = true },
                new("query", "Query di controllo") { Default = "SELECT 1", Hint = "SELECT COUNT(*) FROM ordini (solo SQL Server, un valore solo)", Optional = true, Validate = SqlGuard.Check },
                new("max", "Avvisa sopra (valore della query)", FieldKind.Number) { Optional = true },
            ],
            v =>
            {
                var engine = v["engine"];
                var host = Quote(v["host"]);
                var port = (int)(v.Number("port") ?? engine switch { "sqlserver" => 1433, "postgres" => 5432, _ => 3306 });
                if (engine != "sqlserver")
                {
                    return PowerShell(
                        "$ErrorActionPreference = 'Stop'; $sw = [Diagnostics.Stopwatch]::StartNew(); $c = New-Object Net.Sockets.TcpClient; " +
                        $"$ar = $c.BeginConnect({host}, {port}, $null, $null); if (-not $ar.AsyncWaitHandle.WaitOne(8000)) {{ $c.Close(); throw 'porta non raggiungibile' }}; $c.EndConnect($ar); $c.Close(); $sw.Stop(); " +
                        "@{ stato = 'RAGGIUNGIBILE'; ms = $sw.ElapsedMilliseconds } | ConvertTo-Json",
                        60,
                        Reading("Stato", "{stato}"),
                        Reading("Risposta", "{ms}", "ms"));
                }
                var query = v.Has("query") ? v["query"] : "SELECT 1";
                return PowerShell(
                    SqlOpen(v, port) +
                    $"$m = $c.CreateCommand(); $m.CommandTimeout = 10; $m.CommandText = {Quote(query)}; $sw.Restart(); $x = $m.ExecuteScalar(); $sw.Stop(); $ver = $c.ServerVersion; $c.Close(); " +
                    "@{ stato = 'ONLINE'; connessione = $ms; query = $sw.ElapsedMilliseconds; versione = $ver; valore = \"$x\" } | ConvertTo-Json",
                    60,
                    Reading("Stato", "{stato}"),
                    Reading("Connessione", "{connessione}", "ms"),
                    Reading("Query", "{query}", "ms"),
                    Reading("Versione", "{versione}"),
                    Reading("Valore", "{valore}").With("above", v.Number("max")));
            })
        {
            Permission = "Esegue PowerShell per collegarsi al database con l'utente e la password indicati ed eseguire la query scritta nel modulo a ogni aggiornamento: scegli una query di sola lettura.",
        },
        new("db-conteggio", "Database: conteggio con soglia", "Server", "SQL Server: una query che restituisce un numero (ordini bloccati, errori nuovi, code) con avviso sopra o sotto una soglia.",
            [
                .. SqlFields(),
                new("query", "Query (un numero)") { Hint = "SELECT COUNT(*) FROM ordini WHERE stato = 'bloccato'", Validate = SqlGuard.Check },
                new("label", "Nome del valore") { Default = "Valore", Hint = "Ordini bloccati" },
                new("above", "Avvisa sopra", FieldKind.Number) { Optional = true },
                new("below", "Avvisa sotto", FieldKind.Number) { Optional = true },
            ],
            v => PowerShell(
                SqlOpen(v, (int)(v.Number("port") ?? 1433)) +
                $"$m = $c.CreateCommand(); $m.CommandTimeout = 10; $m.CommandText = {Quote(v["query"])}; $sw.Restart(); $x = $m.ExecuteScalar(); $sw.Stop(); $c.Close(); " +
                "if ($null -eq $x -or $x -is [DBNull]) { throw 'la query non restituisce un valore' }; " +
                "@{ connessione = $ms; query = $sw.ElapsedMilliseconds; valore = [double]$x } | ConvertTo-Json",
                60,
                Reading(v["label"], "{valore}").With("above", v.Number("above")).With("below", v.Number("below")),
                Reading("Query", "{query}", "ms"),
                Reading("Connessione", "{connessione}", "ms")))
        {
            Permission = SqlPermission,
        },
        new("db-freschezza", "Database: freschezza dei dati", "Server", "SQL Server: una query che restituisce una data (l'ultimo ordine, l'ultima sincronizzazione): se è troppo vecchia il processo che scrive si è fermato.",
            [
                .. SqlFields(),
                new("query", "Query (una data)") { Hint = "SELECT MAX(aggiornato_il) FROM ordini", Validate = SqlGuard.Check },
                new("hours", "Avvisa oltre (ore)", FieldKind.Number) { Default = "24" },
                new("utc", "La data è in UTC") { Default = "no", Hint = "si o no (no = ora del PC)", Pattern = "^(si|no)$" },
            ],
            v => PowerShell(
                SqlOpen(v, (int)(v.Number("port") ?? 1433)) +
                $"$m = $c.CreateCommand(); $m.CommandTimeout = 10; $m.CommandText = {Quote(v["query"])}; $sw.Restart(); $x = $m.ExecuteScalar(); $sw.Stop(); $c.Close(); " +
                "$o = if ($null -eq $x -or $x -is [DBNull]) { @{ stato = 'NESSUN DATO'; ultimo = '—'; ore = 'n.d.' } } else { $d = [datetime]$x; " +
                $"$now = {(v["utc"] == "si" ? "[datetime]::UtcNow" : "(Get-Date)")}; " +
                "@{ stato = 'OK'; ultimo = $d.ToString('dd/MM/yyyy HH:mm'); ore = [math]::Round(($now - $d).TotalHours, 1) } }; " +
                "$o['connessione'] = $ms; $o['query'] = $sw.ElapsedMilliseconds; $o | ConvertTo-Json",
                120,
                Reading("Stato", "{stato}").With("alertOn", "NESSUN DATO"),
                Reading("Ultimo dato", "{ultimo}"),
                Reading("Età", "{ore}", "ore").With("above", v.Number("hours")),
                Reading("Query", "{query}", "ms"),
                Reading("Connessione", "{connessione}", "ms")))
        {
            Permission = SqlPermission,
        },

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
        new("git", "Repository Git", "Sviluppo e AI", "Ramo, file modificati e commit da inviare o da ricevere di un repository sul PC; Fetch e Apri.",
            [new("path", "Cartella del repository") { Hint = @"C:\dev\progetto" }],
            v =>
            {
                var path = Quote(v["path"]);
                var o = PowerShell(
                    $"$s = @(git -C {path} status --porcelain=v2 --branch 2>$null); if ($LASTEXITCODE) {{ throw 'non è un repository git' }}; " +
                    "$b = \"$($s -match '^# branch.head ')\" -replace '^# branch.head ', ''; $a = 0; $d = 0; " +
                    "if (\"$($s -match '^# branch.ab ')\" -match '\\+(\\d+) -(\\d+)') { $a = [int]$Matches[1]; $d = [int]$Matches[2] }; " +
                    "@{ ramo = $b; modifiche = @($s -notmatch '^#').Count; avanti = $a; indietro = $d } | ConvertTo-Json",
                    60,
                    Reading("Ramo", "{ramo}"),
                    Reading("Modificati", "{modifiche}", "file"),
                    Reading("Da inviare", "{avanti}"),
                    Reading("Da ricevere", "{indietro}").With("above", 0));
                o["actions"] = new JsonArray(
                    PowerShellAction("Fetch", $"git -C {path} fetch --quiet", confirm: false),
                    PowerShellAction("Apri", $"Invoke-Item -LiteralPath {path}", confirm: false));
                return o;
            })
        {
            Permission = "Esegue git sul PC (status e, col pulsante, fetch) e apre la cartella in Esplora file.",
        },
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
        new("integrita", "Integrità di un file", "Sicurezza",
            "Impronta SHA-256 di un file (hosts, script, eseguibili): diversa da quella attesa diventa un avviso. Vuota = mostra quella attuale.",
            [
                new("path", "File") { Default = @"C:\Windows\System32\drivers\etc\hosts" },
                new("hash", "Impronta attesa (SHA-256)") { Optional = true, Hint = "64 caratteri esadecimali", Pattern = "^[A-Fa-f0-9]{64}$" },
            ],
            v => PowerShell(
                $"$p = {Quote(v["path"])}; $h = (Get-FileHash -LiteralPath $p -Algorithm SHA256 -ErrorAction Stop).Hash; $e = {Quote(v["hash"].ToUpperInvariant())}; " +
                "@{ impronta = $h.Substring(0, 16); esito = $(if (-not $e) { 'DA FISSARE' } elseif ($h -eq $e) { 'INTEGRO' } else { 'CAMBIATO' }); " +
                "modificato = (Get-Item -LiteralPath $p).LastWriteTime.ToString('dd/MM/yyyy HH:mm') } | ConvertTo-Json",
                900,
                Reading("SHA-256", "{impronta}"),
                Reading("Esito", "{esito}").With("alertOn", "CAMBIATO"),
                Reading("Modificato", "{modificato}")))
        {
            Permission = "Esegue PowerShell per calcolare l'impronta del file (Get-FileHash); non lo modifica.",
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
            if (field.Validate?.Invoke(value) is { } problem)
                return Fail($"«{field.Label}»: {problem}");
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

    /// <summary>
    /// Le prime righe di uno script che apre una connessione a SQL Server (utente e password da <c>NOTHING_SECRET</c> o accesso
    /// integrato): lascia <c>$c</c> aperta, <c>$sw</c> fermo e <c>$ms</c> con il tempo di connessione.
    /// </summary>
    private static string SqlOpen(TemplateInput v, int port) =>
        "$ErrorActionPreference = 'Stop'; $b = New-Object Data.SqlClient.SqlConnectionStringBuilder; " +
        $"$b['Data Source'] = {Quote(v["host"] + "," + port.ToString(CultureInfo.InvariantCulture))}; $b['Connect Timeout'] = 8; $b['Application Name'] = 'Nothing Shell'; " +
        (v.Has("database") ? $"$b['Initial Catalog'] = {Quote(v["database"])}; " : string.Empty) +
        (v.Has("username") ? $"$b['User ID'] = {Quote(v["username"])}; $b['Password'] = $env:NOTHING_SECRET; " : "$b['Integrated Security'] = $true; ") +
        "$sw = [Diagnostics.Stopwatch]::StartNew(); $c = New-Object Data.SqlClient.SqlConnection($b.ConnectionString); $c.Open(); $sw.Stop(); $ms = $sw.ElapsedMilliseconds; ";

    /// <summary>I campi di connessione a SQL Server dei connettori che eseguono una query.</summary>
    private static ConnectorField[] SqlFields() =>
    [
        new("host", "Server") { Hint = "db.miosito.it", Pattern = HostName },
        new("port", "Porta", FieldKind.Number) { Default = "1433", Optional = true },
        new("database", "Database") { Hint = "gestionale", Pattern = @"^[\w.\-]+$" },
        new("username", "Utente") { Hint = "vuoto = accesso integrato di Windows; meglio un utente di sola lettura", Optional = true, Pattern = @"^[^\s'""`$;]+$" },
        new("password", "Password", FieldKind.Secret) { Optional = true },
    ];

    private const string SqlPermission = "Esegue PowerShell per collegarsi a SQL Server con l'utente e la password indicati ed eseguire la query scritta nel modulo a ogni aggiornamento (solo SELECT: una sola istruzione, niente scritture).";

    private static string Coord(string text) => text.Replace(',', '.');

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();
}
