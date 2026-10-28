using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using NothingShell.Core.Notch;
using Windows.ApplicationModel.Appointments;

namespace NothingShell.App.Services;

/// <summary>
/// Appuntamenti per l'agenda del notch (docs/11-agenda-pannello-rapido.md): il calendario di Windows (account
/// aggiunti a Windows, se Windows lo concede alle app desktop) e i link iCal (.ics) di Outlook, Google, iCloud.
/// Si rilegge ogni 15 minuti e su richiesta; se un link non risponde restano gli eventi letti l'ultima volta.
/// </summary>
public sealed class CalendarService : IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    /// <summary>Da oggi a fra una settimana: basta per "domani 9:00" e "ven 9:00".</summary>
    private static readonly TimeSpan Window = TimeSpan.FromDays(8);

    private static readonly HttpClient Http = CreateClient();

    private readonly DispatcherTimer _timer = new() { Interval = RefreshInterval };
    private readonly Dictionary<string, IReadOnlyList<CalendarEvent>> _feedCache = new(StringComparer.Ordinal);
    private IReadOnlyList<CalendarEvent> _windows = [];
    private IReadOnlyList<string> _feeds = [];
    private Task? _refreshing;
    private bool _running;

    public CalendarService()
    {
        _timer.Tick += (_, _) => _ = RefreshAsync();
    }

    /// <summary>Appuntamenti da oggi a una settimana, senza doppioni, in ordine.</summary>
    public IReadOnlyList<CalendarEvent> Events { get; private set; } = [];

    /// <summary>Esito dell'ultima lettura, per le impostazioni: "Calendario di Windows: 4 eventi · Link 1: 12 eventi".</summary>
    public string Status { get; private set; } = "Calendario non ancora letto";

    /// <summary>Almeno una fonte ha dato eventi o un link è configurato: senza, l'agenda invita a collegarne uno.</summary>
    public bool HasSource => _windows.Count > 0 || _feeds.Count > 0;

    /// <summary>Eventi cambiati, sul thread UI.</summary>
    public event Action? Changed;

    public void Start(IReadOnlyList<string> feeds)
    {
        ArgumentNullException.ThrowIfNull(feeds);
        var changed = !feeds.SequenceEqual(_feeds);
        _feeds = feeds;
        if (_running && !changed)
            return;
        _running = true;
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Stop()
    {
        _running = false;
        _timer.Stop();
        if (Events.Count == 0)
            return;
        Events = [];
        Changed?.Invoke();
    }

    public void Dispose() => Stop();

    /// <summary>Rilegge tutte le fonti; chiamate ravvicinate aspettano la stessa lettura.</summary>
    public Task RefreshAsync()
    {
        if (!_running)
            return Task.CompletedTask;
        return _refreshing ??= RunAsync();

        async Task RunAsync()
        {
            try
            {
                await ReadAllAsync();
            }
            finally
            {
                _refreshing = null;
            }
        }
    }

    private async Task ReadAllAsync()
    {
        var from = new DateTimeOffset(DateTime.Today);
        var to = from + Window;
        var status = new List<string>();

        var (windows, windowsStatus) = await ReadWindowsAsync(from, Window);
        _windows = windows;
        status.Add(windowsStatus);

        var feeds = _feeds;
        for (var i = 0; i < feeds.Count; i++)
        {
            var (events, error) = await ReadFeedAsync(feeds[i], from, to);
            var label = feeds.Count == 1 ? "Link iCal" : $"Link {i + 1}";
            if (events is not null)
            {
                _feedCache[feeds[i]] = events;
                status.Add($"{label}: {Count(events.Count)}");
            }
            else
            {
                status.Add($"{label}: {error}");
            }
        }
        foreach (var stale in _feedCache.Keys.Except(feeds).ToArray())
            _feedCache.Remove(stale);

        if (!_running)
            return;
        Events = Agenda.Merge(_windows.Concat(_feedCache.Values.SelectMany(e => e)));
        Status = string.Join(" · ", status);
        Changed?.Invoke();
    }

    /// <summary>Il calendario di Windows: per le app desktop serve "Consenti alle app desktop di accedere al calendario".</summary>
    private static async Task<(IReadOnlyList<CalendarEvent> Events, string Status)> ReadWindowsAsync(DateTimeOffset from, TimeSpan span)
    {
        try
        {
            var store = await AppointmentManager.RequestStoreAsync(AppointmentStoreAccessType.AllCalendarsReadOnly);
            if (store is null)
                return ([], "Calendario di Windows non disponibile");
            var options = new FindAppointmentsOptions { MaxCount = 300 };
            foreach (var property in new[]
                     {
                         AppointmentProperties.Subject, AppointmentProperties.Location, AppointmentProperties.StartTime,
                         AppointmentProperties.Duration, AppointmentProperties.AllDay, AppointmentProperties.Details,
                         AppointmentProperties.OnlineMeetingLink, AppointmentProperties.IsCanceledMeeting,
                     })
                options.FetchProperties.Add(property);
            var found = await store.FindAppointmentsAsync(from, span, options);
            var events = found
                .Where(a => !a.IsCanceledMeeting)
                .Select(a => new CalendarEvent(
                    string.IsNullOrWhiteSpace(a.Subject) ? "(senza titolo)" : a.Subject.Trim(),
                    a.StartTime,
                    a.StartTime + a.Duration,
                    a.AllDay,
                    string.IsNullOrWhiteSpace(a.Location) ? null : a.Location.Trim(),
                    MeetingLinks.Find(a.OnlineMeetingLink, a.Location, a.Details)))
                .ToArray();
            return (events, $"Calendario di Windows: {Count(events.Length)}");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            return ([], "Calendario di Windows non accessibile");
        }
    }

    private static async Task<(IReadOnlyList<CalendarEvent>? Events, string Error)> ReadFeedAsync(string url, DateTimeOffset from, DateTimeOffset to)
    {
        if (Normalize(url) is not { } uri)
            return (null, "indirizzo non valido");
        try
        {
            var ics = await Http.GetStringAsync(uri);
            if (!ics.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
                return (null, "non è un calendario iCal");
            var events = await Task.Run(() => IcsCalendar.Parse(ics, from, to));
            return (events, string.Empty);
        }
        catch (HttpRequestException ex)
        {
            return (null, ex.StatusCode is { } code ? $"errore {(int)code}" : "non raggiungibile");
        }
        catch (TaskCanceledException)
        {
            return (null, "non risponde");
        }
    }

    /// <summary>I link <c>webcal://</c> di Outlook e iCloud sono normali indirizzi https.</summary>
    public static Uri? Normalize(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        url = url.Trim();
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url["webcal://".Length..];
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;
    }

    private static string Count(int n) => n == 1 ? "1 evento" : $"{n} eventi";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NothingShell/1.0");
        return client;
    }
}
