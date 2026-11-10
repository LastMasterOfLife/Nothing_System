using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using NothingShell.App.Interop;
using NothingShell.Core.PortalDebug;

namespace NothingShell.App.Services;

/// <summary>
/// Un portale collegato: lo interroga di continuo (eventi ogni secondo, controlli ogni 15 s) e tiene in memoria quello che
/// arriva. Tutto gira sul thread della UI tranne l'attesa della rete, quindi registro e attenzione non hanno bisogno di
/// lucchetti.
/// </summary>
public sealed class PortalLink : IDisposable
{
    private static readonly TimeSpan EventsEvery = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan OfflineEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StatusEvery = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly Func<DebugAlertRules> _rules;
    private readonly CancellationTokenSource _stop = new();
    private DateTimeOffset _nextStatus = DateTimeOffset.MinValue;
    private bool _learned;

    internal PortalLink(DebugPortal portal, HttpClient http, Func<DebugAlertRules> rules)
    {
        Portal = portal;
        _http = http;
        _rules = rules;
    }

    public DebugPortal Portal { get; internal set; }

    public DebugLog Log { get; } = new();

    public DebugWatch Watch { get; } = new();

    public PortalReach Reach => Watch.Reach;

    public PortalStatus? Status { get; internal set; }

    /// <summary>Perché l'ultima richiesta non è andata (null se è andata).</summary>
    public string? Problem { get; private set; }

    public DateTimeOffset? LastContact { get; private set; }

    /// <summary>In pausa: non si chiede niente al portale (la dashboard resta com'è).</summary>
    public bool Paused { get; set; }

    /// <summary>Eventi nuovi, raggiungibilità o controlli cambiati.</summary>
    public event Action? Changed;

    public event Action<DebugAlert>? Alert;

    internal void Start() => _ = RunAsync(_stop.Token);

    private async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            if (!Paused)
                await PollAsync(stop);
            try
            {
                await Task.Delay(Reach == PortalReach.Online ? EventsEvery : OfflineEvery, stop);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Un giro: gli eventi nuovi e, ogni tanto, lo stato coi controlli. Si può chiamare a mano (Aggiorna).</summary>
    public async Task PollAsync(CancellationToken stop = default)
    {
        // il giro automatico e Aggiorna non si sovrappongono: lo stesso cursore chiesto due volte non serve
        if (_polling)
            return;
        _polling = true;
        try
        {
            await PollOnceAsync(stop);
        }
        finally
        {
            _polling = false;
        }
    }

    private bool _polling;

    private async Task PollOnceAsync(CancellationToken stop)
    {
        var token = CredentialStore.Read(Portal.CredentialTarget);
        if (token is null)
        {
            SetReach(PortalReach.Unauthorized, "Token mancante nel Gestore credenziali: ricollega il portale");
            return;
        }
        var more = true;
        for (var pages = 0; more && pages < 10; pages++)
        {
            var (body, reach, problem) = await GetAsync(DebugProtocol.EventsEndpoint(Portal.Address, Log.Cursor), token, stop);
            SetReach(reach, problem);
            if (body is null || DebugProtocol.ParseEvents(body) is not { } page)
                return;
            // un riavvio del runserver (codice ricaricato) non è un avviso: la dashboard conta i riavvii
            var added = Log.Add(page);
            if (added.Count > 0)
            {
                foreach (var alert in Watch.OnEvents(added, _rules(), DateTimeOffset.Now, initial: !_learned))
                    Raise(alert);
                Changed?.Invoke();
            }
            _learned = true;
            more = page.More;
        }

        if (DateTimeOffset.Now >= _nextStatus)
        {
            _nextStatus = DateTimeOffset.Now + StatusEvery;
            var (body, reach, problem) = await GetAsync(DebugProtocol.Endpoint(Portal.Address, "status"), token, stop);
            SetReach(reach, problem);
            if (body is not null && DebugProtocol.ParseStatus(body) is { } status)
            {
                Status = status;
                foreach (var alert in Watch.OnChecks(status.Checks, _rules()))
                    Raise(alert);
                Changed?.Invoke();
            }
        }
    }

    /// <summary>Lo stato dei controlli subito, senza aspettare i 15 s.</summary>
    public void RefreshStatusSoon() => _nextStatus = DateTimeOffset.MinValue;

    private async Task<(string? Body, PortalReach Reach, string? Problem)> GetAsync(Uri uri, string token, CancellationToken stop)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, stop);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (null, PortalReach.Unauthorized, "Il portale non riconosce più la shell: ricollegalo con un codice nuovo");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (null, PortalReach.Offline, "Il portale risponde ma senza nothing_debug (manca il middleware o DEBUG è spento)");
            if (!response.IsSuccessStatusCode)
                return (null, PortalReach.Offline, $"Il portale risponde {(int)response.StatusCode}");
            return (await response.Content.ReadAsStringAsync(stop), PortalReach.Online, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return (null, PortalReach.Offline, stop.IsCancellationRequested ? null : "Non risponde");
        }
    }

    private void SetReach(PortalReach reach, string? problem)
    {
        var before = (Reach, Problem);
        if (reach == PortalReach.Online)
            LastContact = DateTimeOffset.Now;
        Problem = problem;
        if (Watch.OnReach(reach, _rules(), DateTimeOffset.Now) is { } alert)
            Raise(alert);
        if (before != (Reach, Problem))
            Changed?.Invoke();
    }

    private void Raise(DebugAlert alert)
    {
        if (Portal.Watch)
            Alert?.Invoke(alert);
    }

    /// <summary>Dice al portale di dimenticare il token (se risponde); il token si cancella comunque.</summary>
    internal async Task UnpairAsync()
    {
        if (CredentialStore.Read(Portal.CredentialTarget) is { } token)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, DebugProtocol.Endpoint(Portal.Address, "unpair"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var _ = await _http.SendAsync(request, cts.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // portale spento: il token resta nel suo file finché non si ricollega o usa nothing_code --scollega-tutti
            }
        }
        CredentialStore.Delete(Portal.CredentialTarget);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}

/// <summary>I portali del Debug potenziato (docs/24-debug-potenziato.md): collegamento col codice e un <see cref="PortalLink"/> per portale.</summary>
public sealed class PortalDebugService : IDisposable
{
    private const string CredentialComment = "Nothing Shell: token del Debug potenziato";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly List<PortalLink> _links = [];
    private DebugAlertRules _rules = DebugAlertRules.Default;

    public PortalDebugService() =>
        _http.DefaultRequestHeaders.Add("X-Nothing-Debug", DebugProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public IReadOnlyList<PortalLink> Links => _links;

    /// <summary>Qualcosa è cambiato in un portale (eventi, stato).</summary>
    public event Action<PortalLink>? Changed;

    public event Action<PortalLink, DebugAlert>? Alert;

    public PortalLink? Find(string id) => _links.FirstOrDefault(l => l.Portal.Id == id);

    /// <summary>Allinea i collegamenti alle impostazioni: nuovi portali partono, quelli tolti si fermano.</summary>
    public void Apply(DebugSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _rules = settings.AlertRules;
        var wanted = settings.Enabled ? settings.PortalList : [];
        foreach (var link in _links.Where(l => wanted.All(p => p.Id != l.Portal.Id)).ToList())
        {
            link.Dispose();
            _links.Remove(link);
        }
        foreach (var portal in wanted)
        {
            if (Find(portal.Id) is { } existing)
            {
                existing.Portal = portal;
                continue;
            }
            var link = new PortalLink(portal, _http, () => _rules);
            link.Changed += () => Changed?.Invoke(link);
            link.Alert += alert => Alert?.Invoke(link, alert);
            _links.Add(link);
            link.Start();
        }
    }

    /// <summary>
    /// Scambia il codice col token e lo mette nel Gestore credenziali. Restituisce il portale da salvare, o il motivo
    /// per cui non si è collegato.
    /// </summary>
    public async Task<(DebugPortal? Portal, string? Error)> PairAsync(string addressText, string codeText, DebugSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var (address, inlineCode) = PortalAddress.Split(addressText);
        if (address is null)
            return (null, "Scrivi l'indirizzo del portale, ad esempio http://127.0.0.1:8000");
        var code = PairingCode.Normalize(codeText) ?? inlineCode;
        if (code is null)
            return (null, $"Il codice è di {PairingCode.Length} caratteri, come K7Q2-9XMP: lo stampa il runserver all'avvio o lo trovi su {address}{DebugProtocol.Prefix}/");

        string body;
        HttpStatusCode status;
        try
        {
            using var content = new StringContent(DebugProtocol.PairBody(code, Environment.MachineName), Encoding.UTF8, "application/json");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var response = await _http.PostAsync(DebugProtocol.Endpoint(address, "pair"), content, cts.Token);
            status = response.StatusCode;
            body = await response.Content.ReadAsStringAsync(cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, $"{address} non risponde: il portale è avviato? (python manage.py runserver)");
        }
        if (status == HttpStatusCode.NotFound)
            return (null, "Il portale risponde ma non ha nothing_debug: aggiungilo a INSTALLED_APPS e MIDDLEWARE (vedi sotto) e controlla che DEBUG sia True");
        if (status == HttpStatusCode.Forbidden)
            return (null, "Codice sbagliato o scaduto (vale 10 minuti, una volta sola): prendine uno nuovo con python manage.py nothing_code");
        if (DebugProtocol.ParsePair(body) is not { } paired)
            return (null, $"Risposta inattesa dal portale ({(int)status})");

        var existing = settings.PortalList.FirstOrDefault(p => string.Equals(p.Address, address, StringComparison.OrdinalIgnoreCase));
        var portal = existing is not null
            ? existing with { Name = paired.Info.Name }
            : new DebugPortal(settings.NewId(paired.Info.Name), paired.Info.Name, address);
        if (!CredentialStore.Write(portal.CredentialTarget, portal.Name, paired.Token, CredentialComment))
            return (null, "Non riesco a salvare il token nel Gestore credenziali di Windows");
        if (Find(portal.Id) is { } link)
        {
            link.Portal = portal;
            _ = link.PollAsync();
        }
        return (portal, null);
    }

    public async Task UnpairAsync(string id)
    {
        if (Find(id) is { } link)
        {
            await link.UnpairAsync();
            link.Dispose();
            _links.Remove(link);
        }
        else
        {
            CredentialStore.Delete(new DebugPortal(id, id, string.Empty).CredentialTarget);
        }
    }

    /// <summary>Svuota il registro anche nel portale (POST /clear), così dopo un Svuota non torna niente.</summary>
    public async Task ClearAsync(PortalLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        link.Log.Clear();
        if (CredentialStore.Read(link.Portal.CredentialTarget) is not { } token)
            return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, DebugProtocol.Endpoint(link.Portal.Address, "clear"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var _ = await _http.SendAsync(request, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // il portale terrà i suoi: torneranno solo se il cursore riparte
        }
    }

    public void Dispose()
    {
        foreach (var link in _links)
            link.Dispose();
        _links.Clear();
        _http.Dispose();
    }
}
