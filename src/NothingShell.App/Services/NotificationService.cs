using System.Runtime.InteropServices;
using System.Windows.Threading;
using NothingShell.Core.Notch;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace NothingShell.App.Services;

/// <summary>
/// Legge le notifiche di Windows e ne estrae le mail (docs/08-notch-mail-pages.md) e, da Collegamento al
/// telefono, chiamate e notifiche del telefono (docs/13-telefono.md).
/// Il contenuto resta solo in memoria. Evento <c>NotificationChanged</c> se disponibile,
/// e comunque un controllo ogni 3 s.
/// </summary>
public sealed class NotificationService : IDisposable
{
    public const int RecentCapacity = 10;
    private const int RecentAppsCapacity = 8;

    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly List<MailItem> _recent = [];
    private readonly List<string> _apps = [];
    private HashSet<uint> _seen = [];
    private UserNotificationListener? _listener;
    private bool _primed;
    private bool _busy;
    private bool _subscribed;

    public NotificationService()
    {
        _poll.Tick += (_, _) => _ = PollAsync();
    }

    /// <summary>App di posta aggiunte dall'utente (impostazione <c>extraMailApps</c>).</summary>
    public Func<IEnumerable<string>> ExtraMailApps { get; set; } = () => [];

    public UserNotificationListenerAccessStatus Access { get; private set; } = UserNotificationListenerAccessStatus.Unspecified;

    public IReadOnlyList<MailItem> Recent => _recent;

    /// <summary>App che hanno mandato notifiche da quando la shell è avviata, la più recente per prima.</summary>
    public IReadOnlyList<string> NotifyingApps => _apps;

    /// <summary>Mail nuova arrivata dopo l'avvio (quelle già presenti non generano avvisi).</summary>
    public event Action<MailItem>? MailArrived;

    public event Action? RecentChanged;

    /// <summary>Chiamata o notifica del telefono arrivata dopo l'avvio.</summary>
    public event Action<PhoneEvent>? PhoneArrived;

    public void Start()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            Access = _listener.GetAccessStatus();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            Access = UserNotificationListenerAccessStatus.Denied;
            return;
        }
        if (Access == UserNotificationListenerAccessStatus.Allowed)
            Begin();
    }

    /// <summary>Chiede a Windows il permesso di leggere le notifiche (mostra la richiesta all'utente).</summary>
    public async Task RequestAccessAsync()
    {
        if (_listener is null)
            return;
        Access = await _listener.RequestAccessAsync();
        if (Access == UserNotificationListenerAccessStatus.Allowed)
            Begin();
    }

    public void Stop() => _poll.Stop();

    public void Dispose() => Stop();

    private void Begin()
    {
        if (_poll.IsEnabled)
            return; // già avviato (interruttore riacceso)
        if (!_subscribed)
        {
            try
            {
                _listener!.NotificationChanged += (_, _) => _dispatcher.BeginInvoke(() => _ = PollAsync());
                _subscribed = true;
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
            {
                // senza evento basta il controllo periodico
            }
        }
        _poll.Start();
        _ = PollAsync();
    }

    private async Task PollAsync()
    {
        if (_busy || _listener is null)
            return;
        _busy = true;
        try
        {
            var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var current = new HashSet<uint>();
            var added = false;
            foreach (var n in notifications.OrderBy(n => n.CreationTime))
            {
                current.Add(n.Id);
                if (_seen.Contains(n.Id))
                    continue;
                var (app, appId, texts) = Read(n);
                RememberApp(app);
                if (PhoneLink.IsPhoneLink(appId))
                {
                    if (_primed && PhoneLink.Parse(texts, n.CreationTime) is { } phone)
                        PhoneArrived?.Invoke(phone);
                    continue;
                }
                if (!MailDetector.IsMail(app, texts, ExtraMailApps()))
                    continue;
                if (MailDetector.Extract(app, texts, n.CreationTime) is not { } mail)
                    continue;

                mail = mail with { AppId = appId }; // per aprire l'app dal notch
                _recent.Insert(0, mail);
                if (_recent.Count > RecentCapacity)
                    _recent.RemoveAt(_recent.Count - 1);
                added = true;
                if (_primed)
                    MailArrived?.Invoke(mail);
            }
            // gli Id delle notifiche rimosse non tornano: basta ricordare quelle presenti
            _seen = current;
            _primed = true;
            if (added)
                RecentChanged?.Invoke();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            // permesso revocato o servizio non disponibile: si riprova al prossimo giro
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Solo il nome dell'app, mai il contenuto: serve alle impostazioni per scegliere le app di posta.</summary>
    private void RememberApp(string app)
    {
        if (string.IsNullOrWhiteSpace(app))
            return;
        _apps.RemoveAll(a => a.Equals(app, StringComparison.OrdinalIgnoreCase));
        _apps.Insert(0, app);
        if (_apps.Count > RecentAppsCapacity)
            _apps.RemoveAt(_apps.Count - 1);
    }

    private static (string App, string? AppId, IReadOnlyList<string> Texts) Read(UserNotification notification)
    {
        string app;
        string? appId = null;
        try
        {
            appId = notification.AppInfo.AppUserModelId;
            app = notification.AppInfo.DisplayInfo.DisplayName;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            app = string.Empty; // alcune app non espongono il nome
        }

        var visual = notification.Notification.Visual;
        var binding = visual?.GetBinding(KnownNotificationBindings.ToastGeneric) ?? visual?.Bindings.FirstOrDefault();
        var texts = binding?.GetTextElements().Select(t => t.Text ?? string.Empty).ToArray() ?? [];
        return (app, appId, texts);
    }
}
