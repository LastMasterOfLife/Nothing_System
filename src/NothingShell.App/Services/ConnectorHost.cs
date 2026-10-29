using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using NothingShell.App.Interop;
using NothingShell.Core.Connectors;
using NothingShell.Core.Storage;

namespace NothingShell.App.Services;

/// <summary>
/// Connettori verso dispositivi e sistemi (docs/14-agenti-connettori.md): un file JSON per connettore in
/// <c>~/.nothing-shell/connectors</c>. Le letture girano sul pool con timeout; i risultati arrivano sul thread UI.
/// Leggere non cambia nulla. Comandi e azioni (scrivere, accendere, riavviare) partono solo se l'utente ha dato il
/// permesso a quel connettore, e il permesso vale per il contenuto esatto del file: modificarlo lo toglie.
/// </summary>
public sealed class ConnectorHost : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MqttKeepAlive = TimeSpan.FromSeconds(30);

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly HttpClient _http = new() { Timeout = Timeout };
    private readonly Dictionary<string, ConnectorState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MqttLink> _mqtt = new(StringComparer.Ordinal);
    private readonly ConnectorAlerts _alerts = new();
    private readonly LlmLedger _ledger;
    private readonly string? _ledgerPath;
    private CancellationTokenSource? _stop;
    private int _rateFetching;

    public ConnectorHost()
        : this(LlmLedger.DefaultPath)
    {
    }

    /// <param name="ledgerPath">Dove salvare il registro dei costi LLM; null = solo in memoria (diagnostica).</param>
    public ConnectorHost(string? ledgerPath)
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NothingShell/0.12");
        _ledgerPath = ledgerPath;
        _ledger = ledgerPath is null ? new LlmLedger() : LlmLedger.Load(ledgerPath);
    }

    public static string Folder { get; } = Path.Combine(AtomicFile.DataFolder, "connectors");

    /// <summary>Il permesso dato dall'utente: id del connettore → hash del file approvato.</summary>
    public Func<ConnectorManifest, bool> IsGranted { get; set; } = _ => false;

    public IReadOnlyList<ConnectorLoadResult> Results { get; private set; } = [];

    /// <summary>Nell'ordine dei file.</summary>
    public IReadOnlyList<ConnectorState> States =>
        Results.Where(r => r.Manifest is not null && _states.ContainsKey(r.FileName)).Select(r => _states[r.FileName]).ToArray();

    public event Action? Changed;

    /// <summary>Un valore è appena passato oltre soglia.</summary>
    public event Action<ConnectorManifest, ConnectorReading>? Alert;

    /// <summary>Un'azione è finita: null se riuscita, altrimenti il motivo.</summary>
    public event Action<ConnectorManifest, ConnectorAction, string?>? ActionFinished;

    public bool Running => _stop is not null;

    public void Start()
    {
        StopRunning(); // senza Changed: i widget dei connettori non spariscono per un attimo a ogni riavvio
        _stop = new CancellationTokenSource();
        Directory.CreateDirectory(Folder);
        var results = new List<ConnectorLoadResult>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            results.Add(AtomicFile.TryReadAllText(file) is { } json
                ? ConnectorParser.Parse(id, json)
                : new ConnectorLoadResult(id, null, "file non leggibile"));
        }
        Results = results;
        var token = _stop.Token;
        foreach (var manifest in results.Select(r => r.Manifest).OfType<ConnectorManifest>())
        {
            var granted = IsGranted(manifest);
            if (manifest.Kind == ConnectorKind.Command && !granted)
            {
                _states[manifest.Id] = new ConnectorState(manifest, ConnectorStatus.NeedsGrant, [], null, null);
                continue;
            }
            _states[manifest.Id] = new ConnectorState(manifest, ConnectorStatus.Waiting, [], null, null) { Granted = granted };
            _ = manifest.Kind == ConnectorKind.Mqtt
                ? Task.Run(() => RunMqttAsync(manifest, granted, token), token)
                : Task.Run(() => PollAsync(manifest, granted, token), token);
        }
        Changed?.Invoke();
    }

    public void Stop()
    {
        StopRunning();
        Changed?.Invoke();
    }

    private void StopRunning()
    {
        _stop?.Cancel();
        _stop?.Dispose();
        _stop = null;
        lock (_mqtt)
        {
            foreach (var link in _mqtt.Values)
                link.Dispose();
            _mqtt.Clear();
        }
        _states.Clear();
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }

    /// <summary>Salva il token o la password nel Gestore credenziali (vuoto = cancella) e riparte.</summary>
    public void SetSecret(ConnectorManifest manifest, string secret)
    {
        StoreSecret(manifest, secret);
        if (Running)
            Start();
    }

    /// <summary>Solo il Gestore credenziali, senza ripartire (chi chiama riparte dopo aver finito).</summary>
    public static void StoreSecret(ConnectorManifest manifest, string secret)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrEmpty(secret))
            CredentialStore.Delete(manifest.CredentialTarget);
        else
            CredentialStore.Write(manifest.CredentialTarget, manifest.UserName ?? manifest.Id, secret, "Nothing Shell: connettore");
    }

    public static bool HasSecret(ConnectorManifest manifest) => CredentialStore.Read(manifest.CredentialTarget) is { Length: > 0 };

    /// <summary>Esegue un'azione del connettore, solo con il permesso per questo contenuto del file.</summary>
    public void Run(ConnectorManifest manifest, ConnectorAction action)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(action);
        if (!IsGranted(manifest) || !manifest.Actions.Contains(action))
        {
            ActionFinished?.Invoke(manifest, action, "permesso non dato");
            return;
        }
        _ = Task.Run(async () =>
        {
            string? error;
            try
            {
                error = await RunActionAsync(manifest, action);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or Win32Exception
                                           or TaskCanceledException or TimeoutException or InvalidOperationException or UriFormatException)
            {
                error = Describe(ex);
            }
            _ = _dispatcher.BeginInvoke(() =>
            {
                ActionFinished?.Invoke(manifest, action, error);
                if (error is null && manifest.Kind != ConnectorKind.Mqtt && _stop is { } stop)
                    _ = Task.Run(() => RefreshOnceAsync(manifest, stop.Token)); // si vede subito l'effetto
            });
        });
    }

    /// <summary>
    /// Per <c>--connectors-check</c>: un giro dei connettori di <paramref name="folder"/>, una riga ciascuno. I comandi
    /// partono solo con <paramref name="runCommands"/>; MQTT resta fuori (è un collegamento continuo).
    /// </summary>
    public static async Task<IReadOnlyList<string>> CheckAsync(string folder, bool runCommands)
    {
        using var host = new ConnectorHost(ledgerPath: null); // la diagnostica non tocca il registro dei costi
        var lines = new List<string> { $"Cartella: {folder}" };
        foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            var result = ConnectorParser.Parse(Path.GetFileNameWithoutExtension(file), await File.ReadAllTextAsync(file));
            if (result.Manifest is not { } m)
            {
                lines.Add($"{result.FileName}: NON VALIDO · {result.Error}");
                continue;
            }
            if (m.Kind == ConnectorKind.Mqtt || (m.Kind == ConnectorKind.Command && !runCommands))
            {
                lines.Add($"{m.Name} ({m.Kind}): saltato{(m.Kind == ConnectorKind.Command ? ", serve --esegui" : string.Empty)}");
                continue;
            }
            try
            {
                using var timeout = new CancellationTokenSource(Timeout * 2);
                var readings = await host.ReadAsync(m, timeout.Token);
                lines.Add($"{m.Name} ({m.Kind}): " + string.Join(" · ", readings.Select(r =>
                    $"{r.Label} = {r.Text}{(r.Unit is { } u ? " " + u : string.Empty)}{(r.Level == ReadingLevel.Warning ? " [AVVISO]" : string.Empty)}")));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or Win32Exception or JsonException
                                           or TaskCanceledException or TimeoutException or UnauthorizedAccessException or PingException or InvalidOperationException)
            {
                lines.Add($"{m.Name} ({m.Kind}): ERRORE · {Describe(ex)}");
            }
        }
        return lines;
    }

    private async Task PollAsync(ConnectorManifest manifest, bool granted, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await RefreshOnceAsync(manifest, token, granted);
            try
            {
                await Task.Delay(manifest.Refresh, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RefreshOnceAsync(ConnectorManifest manifest, CancellationToken token, bool? granted = null)
    {
        ConnectorState state;
        var isGranted = granted ?? _states.GetValueOrDefault(manifest.Id)?.Granted ?? false;
        try
        {
            var readings = await ReadAsync(manifest, token);
            state = new ConnectorState(manifest, ConnectorStatus.Ok, readings, null, DateTimeOffset.Now) { Granted = isGranted };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or Win32Exception or JsonException
                                       or TaskCanceledException or TimeoutException or UnauthorizedAccessException or PingException or InvalidOperationException)
        {
            state = new ConnectorState(manifest, ConnectorStatus.Error, [], Describe(ex), DateTimeOffset.Now) { Granted = isGranted };
        }
        _ = _dispatcher.BeginInvoke(() => Publish(state));
    }

    private void Publish(ConnectorState state)
    {
        if (_stop is null || !_states.TryGetValue(state.Manifest.Id, out var current))
            return;
        // un errore passeggero non cancella i valori di prima: restano, con il motivo accanto
        if (state.Status == ConnectorStatus.Error && current.Readings is { Count: > 0 } previous)
            state = state with { Readings = previous };
        _states[state.Manifest.Id] = state;
        Changed?.Invoke();
        if (state.Status == ConnectorStatus.Ok)
            foreach (var reading in _alerts.Update(state.Manifest.Id, state.Readings))
                Alert?.Invoke(state.Manifest, reading);
    }

    private async Task<IReadOnlyList<ConnectorReading>> ReadAsync(ConnectorManifest m, CancellationToken token)
    {
        switch (m.Kind)
        {
            case ConnectorKind.Http:
            {
                using var doc = ConnectorReadings.Document(await GetAsync(m, new Uri(ExpandUrl(m.Url!, DateTimeOffset.UtcNow)), token));
                return WithLlm(m, doc.RootElement, m.Readings.Select(r => ConnectorReadings.Evaluate(r, doc.RootElement, m.Name)));
            }
            case ConnectorKind.HomeAssistant:
            {
                var list = new List<ConnectorReading>();
                foreach (var spec in m.Readings)
                {
                    var url = new Uri(new Uri(m.Url!.TrimEnd('/') + "/"), "api/states/" + Uri.EscapeDataString(spec.Source!));
                    using var doc = JsonDocument.Parse(await GetAsync(m, url, token));
                    var root = doc.RootElement;
                    var attributes = root.TryGetProperty("attributes", out var a) ? a : default;
                    var name = Text(attributes, "friendly_name") ?? spec.Source!;
                    var unit = spec.Unit ?? Text(attributes, "unit_of_measurement");
                    var s = spec with { Unit = unit };
                    list.Add(spec.Value is not null
                        ? ConnectorReadings.Evaluate(s, root, name)
                        : ConnectorReadings.FromText(s, Translate(Text(root, "state") ?? "--"), name));
                }
                return list;
            }
            case ConnectorKind.Prometheus:
            {
                var list = new List<ConnectorReading>();
                foreach (var spec in m.Readings)
                {
                    var url = new Uri(new Uri(m.Url!.TrimEnd('/') + "/"), "api/v1/query?query=" + Uri.EscapeDataString(spec.Source!));
                    using var doc = JsonDocument.Parse(await GetAsync(m, url, token));
                    var value = doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("result", out var result)
                                && result.ValueKind == JsonValueKind.Array && result.GetArrayLength() > 0
                                && result[0].TryGetProperty("value", out var pair) && pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2
                        ? pair[1].GetString() ?? "--"
                        : "--";
                    list.Add(ConnectorReadings.FromText(spec, value, spec.Source!));
                }
                return list;
            }
            case ConnectorKind.Command:
            {
                using var doc = ConnectorReadings.Document(await RunProcessAsync(m.Command!, m.Arguments, token));
                return WithLlm(m, doc.RootElement, Specs(m).Select(r => ConnectorReadings.Evaluate(r, doc.RootElement, "Uscita")));
            }
            case ConnectorKind.File:
            {
                var text = await File.ReadAllTextAsync(Environment.ExpandEnvironmentVariables(m.Path!), token).WaitAsync(Timeout, token);
                using var doc = ConnectorReadings.Document(text);
                return WithLlm(m, doc.RootElement, Specs(m).Select(r => ConnectorReadings.Evaluate(r, doc.RootElement, "Contenuto")));
            }
            case ConnectorKind.Ping:
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(m.Host!, TimeSpan.FromSeconds(3), cancellationToken: token);
                var online = reply.Status == IPStatus.Success;
                return
                [
                    ConnectorReadings.FromText(new ReadingSpec("Stato", null) { AlertOn = "OFFLINE" }, online ? "ONLINE" : "OFFLINE", "Stato"),
                    ConnectorReadings.FromText(new ReadingSpec("Latenza", null) { Unit = online ? "ms" : null }, online ? reply.RoundtripTime.ToString(System.Globalization.CultureInfo.InvariantCulture) : "--", "Latenza"),
                ];
            }
            default:
                return [];
        }
    }

    /// <summary>Senza "readings" un comando o un file mostrano la prima riga dell'uscita (se non tengono solo i costi LLM).</summary>
    private static IReadOnlyList<ReadingSpec> Specs(ConnectorManifest m) =>
        m.Readings.Count > 0 ? m.Readings : m.Llm is not null ? [] : [new ReadingSpec(null, "{lines[0]}")];

    /// <summary>
    /// <c>{today}</c> e <c>{monthStart}</c> negli indirizzi: mezzanotte UTC di oggi e del primo del mese, in ISO 8601
    /// (es. <c>starting_at={today}</c> dell'Admin API di Anthropic).
    /// </summary>
    public static string ExpandUrl(string url, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(url);
        const string iso = "yyyy-MM-dd'T'HH:mm:ss'Z'";
        var day = nowUtc.UtcDateTime.Date;
        return url.Replace("{today}", day.ToString(iso, System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{monthStart}", new DateTime(day.Year, day.Month, 1).ToString(iso, System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    // ── Costi LLM ──────────────────────────────────────────────────────────────

    /// <summary>Cambio dollari → euro in uso (BCE del giorno), null finché non arriva.</summary>
    public double? UsdToEur
    {
        get
        {
            lock (_ledger)
                return _ledger.UsdToEur;
        }
    }

    public LlmSummary LlmSummaryOf(ConnectorManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        lock (_ledger)
            return _ledger.Summary(manifest.Id, DateOnly.FromDateTime(DateTime.Now), manifest.Llm?.UsdToEur ?? _ledger.UsdToEur);
    }

    public IReadOnlyList<LlmCall> LlmRecent(string connectorId)
    {
        lock (_ledger)
            return _ledger.Books.TryGetValue(connectorId, out var book) ? [.. book.Recent] : [];
    }

    public string LlmCsv(Func<string, string> nameOf)
    {
        lock (_ledger)
            return _ledger.Csv(nameOf, _ledger.UsdToEur);
    }

    /// <summary>Dimentica i costi di un connettore (rimosso, o azzerato dall'utente).</summary>
    public void ForgetLlm(string connectorId)
    {
        lock (_ledger)
        {
            if (_ledger.Forget(connectorId))
                SaveLedger();
        }
    }

    /// <summary>Aggiunge al registro quello che il sito ha speso dall'ultimo giro e mette i costi fra i valori.</summary>
    private List<ConnectorReading> WithLlm(ConnectorManifest m, JsonElement root, IEnumerable<ConnectorReading> readings)
    {
        var list = readings.ToList();
        if (m.Llm is not { } spec)
            return list;
        LlmSummary summary;
        lock (_ledger)
        {
            if (_ledger.Apply(m.Id, spec, root, DateTimeOffset.Now))
                SaveLedger();
            summary = _ledger.Summary(m.Id, DateOnly.FromDateTime(DateTime.Now), spec.UsdToEur ?? _ledger.UsdToEur);
            if (spec.UsdToEur is null && _ledger.RateDate != DateOnly.FromDateTime(DateTime.Now))
                _ = RefreshRateAsync();
        }
        list.AddRange(LlmLedger.Readings(spec, summary));
        return list;
    }

    private void SaveLedger()
    {
        if (_ledgerPath is null)
            return;
        try
        {
            _ledger.Save(_ledgerPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // si riprova al prossimo giro: il registro resta in memoria
        }
    }

    /// <summary>Cambio USD → EUR della BCE (Frankfurter), una volta al giorno; se non arriva resta quello di prima.</summary>
    private async Task RefreshRateAsync()
    {
        if (Interlocked.Exchange(ref _rateFetching, 1) == 1)
            return;
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(new Uri("https://api.frankfurter.app/latest?from=USD&to=EUR")));
            if (doc.RootElement.TryGetProperty("rates", out var rates) && rates.TryGetProperty("EUR", out var eur) && eur.TryGetDouble(out var rate) && rate > 0)
            {
                lock (_ledger)
                {
                    _ledger.UsdToEur = rate;
                    _ledger.RateDate = DateOnly.FromDateTime(DateTime.Now);
                    SaveLedger();
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // senza rete il cambio resta quello dell'ultima volta
        }
        finally
        {
            Volatile.Write(ref _rateFetching, 0);
        }
    }

    /// <summary>Gli stati di Home Assistant più comuni in italiano: "on" → "ACCESO".</summary>
    private static string Translate(string state) => state switch
    {
        "on" => "ACCESO",
        "off" => "SPENTO",
        "open" => "APERTO",
        "closed" => "CHIUSO",
        "unavailable" => "NON DISP.",
        "unknown" => "--",
        "home" => "A CASA",
        "not_home" => "FUORI",
        _ => state,
    };

    private async Task<string> GetAsync(ConnectorManifest m, Uri url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        Authorize(m, request);
        using var response = await _http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(token);
    }

    private static void Authorize(ConnectorManifest m, HttpRequestMessage request)
    {
        foreach (var (name, value) in m.Headers)
            request.Headers.TryAddWithoutValidation(name, value);
        if (m.Secret && CredentialStore.Read(m.CredentialTarget) is { Length: > 0 } secret)
        {
            var header = m.Kind == ConnectorKind.HomeAssistant ? "Authorization" : m.SecretHeader;
            var format = m.Kind == ConnectorKind.HomeAssistant ? "Bearer {secret}" : m.SecretFormat;
            request.Headers.Remove(header);
            request.Headers.TryAddWithoutValidation(header, format.Replace("{secret}", secret, StringComparison.Ordinal));
        }
    }

    private async Task<string?> RunActionAsync(ConnectorManifest m, ConnectorAction action)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        switch (action.Kind)
        {
            case ActionKind.Command:
                await RunProcessAsync(action.Command!, action.Arguments, timeout.Token);
                return null;
            case ActionKind.Http or ActionKind.HomeAssistant:
            {
                var baseUri = m.Url is { } u ? new Uri(u.TrimEnd('/') + "/") : null;
                var url = action.Kind == ActionKind.HomeAssistant
                    ? new Uri(baseUri ?? throw new InvalidOperationException("manca \"url\""), "api/services/" + action.Service!.Replace('.', '/'))
                    : baseUri is not null ? new Uri(baseUri, action.Url!) : new Uri(action.Url!);
                using var request = new HttpRequestMessage(action.Kind == ActionKind.HomeAssistant ? HttpMethod.Post : new HttpMethod(action.Method), url);
                Authorize(m, request);
                if (action.Body is { } body)
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await _http.SendAsync(request, timeout.Token);
                return response.IsSuccessStatusCode ? null : $"risposta {(int)response.StatusCode}";
            }
            case ActionKind.Mqtt:
            {
                MqttLink? link;
                lock (_mqtt)
                    _mqtt.TryGetValue(m.Id, out link);
                if (link is null)
                    return "broker non collegato";
                await link.SendAsync(Mqtt.PublishPacket(action.Topic!, action.Payload ?? string.Empty), timeout.Token);
                return null;
            }
            default:
                return "azione sconosciuta";
        }
    }

    /// <summary>Collegamento continuo al broker: si riprova dopo 5 s, raddoppiando fino a 5 minuti.</summary>
    private async Task RunMqttAsync(ConnectorManifest m, bool granted, CancellationToken token)
    {
        var delay = TimeSpan.FromSeconds(5);
        var values = new Dictionary<ReadingSpec, ConnectorReading>();
        while (!token.IsCancellationRequested)
        {
            try
            {
                await MqttSessionAsync(m, granted, values, token);
                delay = TimeSpan.FromSeconds(5);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException or OperationCanceledException
                                           or System.Security.Authentication.AuthenticationException or InvalidOperationException)
            {
                var state = new ConnectorState(m, ConnectorStatus.Error, [], Describe(ex), DateTimeOffset.Now) { Granted = granted };
                _ = _dispatcher.BeginInvoke(() => Publish(state));
            }
            try
            {
                await Task.Delay(delay, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromMinutes(5).Ticks));
        }
    }

    private async Task MqttSessionAsync(ConnectorManifest m, bool granted, Dictionary<ReadingSpec, ConnectorReading> values, CancellationToken token)
    {
        using var client = new TcpClient();
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            connect.CancelAfter(Timeout);
            await client.ConnectAsync(m.Host!, m.Port > 0 ? m.Port : m.Tls ? 8883 : 1883, connect.Token);
        }
        Stream stream = client.GetStream();
        if (m.Tls)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = m.Host }, token);
            stream = ssl;
        }
        using var link = new MqttLink(stream);
        var password = m.Secret ? CredentialStore.Read(m.CredentialTarget) : null;
        await link.SendAsync(Mqtt.Connect($"nothing-shell-{Environment.MachineName}-{m.Id}".ToLowerInvariant(), m.UserName, password,
            (ushort)(MqttKeepAlive.TotalSeconds * 2)), token);

        var buffer = new byte[16 * 1024];
        var pending = new MemoryStream();
        var lastPing = DateTimeOffset.Now;
        var connected = false;
        while (!token.IsCancellationRequested)
        {
            using var read = CancellationTokenSource.CreateLinkedTokenSource(token);
            read.CancelAfter(MqttKeepAlive);
            int count;
            try
            {
                count = await stream.ReadAsync(buffer, read.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                count = -1; // nessun messaggio per un po': si manda il ping
            }
            if (count == 0)
                throw new IOException("il broker ha chiuso la connessione");
            if (count > 0)
                pending.Write(buffer, 0, count);
            if (DateTimeOffset.Now - lastPing >= MqttKeepAlive)
            {
                await link.SendAsync(Mqtt.PingReq(), token);
                lastPing = DateTimeOffset.Now;
            }

            var data = pending.ToArray();
            var offset = 0;
            var changed = false;
            while (Mqtt.TryRead(data.AsSpan(offset), out var consumed) is { } packet)
            {
                offset += consumed;
                if (packet.Type == Mqtt.Connack)
                {
                    var code = Mqtt.ConnackCode(packet);
                    if (code != 0)
                        throw new InvalidOperationException(Mqtt.ConnackReason(code));
                    connected = true;
                    lock (_mqtt)
                        _mqtt[m.Id] = link;
                    await link.SendAsync(Mqtt.Subscribe(1, m.Readings.Select(r => r.Source!)), token);
                    var waiting = new ConnectorState(m, ConnectorStatus.Ok, Order(m, values), null, DateTimeOffset.Now) { Granted = granted };
                    _ = _dispatcher.BeginInvoke(() => Publish(waiting));
                }
                else if (Mqtt.ReadPublish(packet) is { } message && message is var (topic, payload))
                {
                    foreach (var spec in m.Readings.Where(r => Mqtt.Matches(r.Source!, topic)))
                    {
                        if (spec.Value is not null)
                        {
                            using var doc = ConnectorReadings.Document(payload);
                            values[spec] = ConnectorReadings.Evaluate(spec, doc.RootElement, topic);
                        }
                        else
                        {
                            values[spec] = ConnectorReadings.FromText(spec, payload, topic);
                        }
                        changed = true;
                    }
                }
            }
            pending = new MemoryStream();
            pending.Write(data, offset, data.Length - offset);
            if (changed && connected)
            {
                var state = new ConnectorState(m, ConnectorStatus.Ok, Order(m, values), null, DateTimeOffset.Now) { Granted = granted };
                _ = _dispatcher.BeginInvoke(() => Publish(state));
            }
        }
        lock (_mqtt)
            _mqtt.Remove(m.Id);
    }

    /// <summary>I valori nell'ordine del file; quelli non ancora arrivati non compaiono.</summary>
    private static ConnectorReading[] Order(ConnectorManifest m, Dictionary<ReadingSpec, ConnectorReading> values) =>
        m.Readings.Where(values.ContainsKey).Select(r => values[r]).ToArray();

    private static async Task<string> RunProcessAsync(string command, IReadOnlyList<string> arguments, CancellationToken token)
    {
        var local = Path.Combine(Folder, command);
        ProcessStartInfo start;
        if (command.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            start = new ProcessStartInfo("powershell.exe");
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", File.Exists(local) ? local : command })
                start.ArgumentList.Add(a);
        }
        else
        {
            start = new ProcessStartInfo(File.Exists(local) ? local : Environment.ExpandEnvironmentVariables(command));
        }
        foreach (var arg in arguments)
            start.ArgumentList.Add(arg);
        Directory.CreateDirectory(Folder); // i comandi partono dalla cartella dei connettori (script accanto ai file)
        start.WorkingDirectory = Folder;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        using var process = Process.Start(start) ?? throw new Win32Exception("processo non avviato");
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException();
        }
        var output = await stdout;
        if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
        {
            var reason = (await stderr).Split('\n', 2)[0].Trim();
            throw new IOException($"uscita {process.ExitCode}{(reason.Length > 0 ? $": {reason}" : string.Empty)}");
        }
        return output;
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } code } => $"risposta {(int)code}" + (code is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden ? ": token mancante o errato" : string.Empty),
        HttpRequestException or SocketException => "non raggiungibile",
        TaskCanceledException or TimeoutException => "tempo scaduto",
        JsonException => "risposta non in JSON",
        Win32Exception => "comando non trovato",
        FileNotFoundException or DirectoryNotFoundException => "file non trovato",
        UnauthorizedAccessException => "accesso negato",
        PingException => "host sconosciuto",
        System.Security.Authentication.AuthenticationException => "TLS non riuscito",
        _ => ex.Message,
    };

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Connessione al broker: le scritture (ping, azioni) passano una alla volta.</summary>
    private sealed class MqttLink(Stream stream) : IDisposable
    {
        private readonly SemaphoreSlim _write = new(1, 1);

        public async Task SendAsync(byte[] packet, CancellationToken token)
        {
            await _write.WaitAsync(token);
            try
            {
                await stream.WriteAsync(packet, token);
                await stream.FlushAsync(token);
            }
            finally
            {
                _write.Release();
            }
        }

        public void Dispose()
        {
            stream.Dispose();
            _write.Dispose();
        }
    }
}
