using System.IO;
using System.Net.Sockets;
using System.Windows.Threading;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using NothingShell.App.Interop;
using NothingShell.Core.Notch;

namespace NothingShell.App.Services;

/// <summary>
/// Casella IMAP collegata direttamente (docs/08-notch-mail-pages.md): resta connessa alla Posta in arrivo in
/// sola lettura e con IDLE riceve subito le mail nuove, senza controlli periodici e senza altre app aperte.
/// Riconnessione automatica con attesa crescente; le mail restano solo in memoria.
/// </summary>
public sealed class ImapMailService : IDisposable
{
    private const int RecentCount = 10;
    private static readonly TimeSpan IdleRestart = TimeSpan.FromMinutes(9); // i server chiudono IDLE dopo ~30 min
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1); // solo se il server non ha IDLE
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(5);

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly List<MailItem> _recent = [];
    private CancellationTokenSource? _stop;

    public IReadOnlyList<MailItem> Recent => _recent;

    public string Status { get; private set; } = "Nessuna casella collegata";

    public bool IsConnected { get; private set; }

    /// <summary>Mail nuova arrivata dopo il collegamento (quelle già presenti non generano avvisi).</summary>
    public event Action<MailItem>? MailArrived;

    /// <summary>Stato o elenco delle mail cambiati.</summary>
    public event Action? Changed;

    public void Start(MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        Stop();
        if (CredentialStore.Read(account.CredentialTarget) is not { Length: > 0 } password)
        {
            SetStatus("Password non trovata nel Gestore credenziali: ricollega la casella", connected: false);
            return;
        }
        _stop = new CancellationTokenSource();
        SetStatus($"Connessione a {account.Server}…", connected: false);
        var token = _stop.Token;
        _ = Task.Run(() => RunAsync(account, password, token), token);
    }

    public void Stop()
    {
        _stop?.Cancel();
        _stop?.Dispose();
        _stop = null;
        _recent.Clear();
        SetStatus("Nessuna casella collegata", connected: false);
    }

    /// <summary>Prova accesso e apertura della Posta in arrivo; null se riesce, altrimenti il motivo in italiano.</summary>
    public static async Task<string?> TestAsync(MailAccount account, string password, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(account);
        using var client = new ImapClient { Timeout = 20_000 };
        try
        {
            await ConnectAsync(client, account, password, cancellation);
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, cancellation);
            await client.DisconnectAsync(true, cancellation);
            return null;
        }
        catch (Exception ex) when (Describe(ex) is { } reason)
        {
            return reason;
        }
    }

    public void Dispose()
    {
        _stop?.Cancel();
        _stop?.Dispose();
    }

    private static async Task ConnectAsync(ImapClient client, MailAccount account, string password, CancellationToken cancellation)
    {
        // 993: TLS dall'inizio; altre porte: STARTTLS se il server lo offre.
        var security = account.Port == MailAccount.DefaultPort ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto;
        await client.ConnectAsync(account.Server, account.Port, security, cancellation);
        await client.AuthenticateAsync(account.Login, password, cancellation);
    }

    private async Task RunAsync(MailAccount account, string password, CancellationToken stop)
    {
        var cursor = new ImapCursor();
        var retry = FirstRetry;
        while (!stop.IsCancellationRequested)
        {
            using var client = new ImapClient { Timeout = 60_000 };
            try
            {
                await ConnectAsync(client, account, password, stop);
                var inbox = client.Inbox;
                await inbox.OpenAsync(FolderAccess.ReadOnly, stop);
                Post(() => SetStatus($"Collegata: {account.Address} ({account.Server})", connected: true), stop);
                retry = FirstRetry;

                await CheckAsync(inbox, cursor, stop);
                while (client.IsConnected && !stop.IsCancellationRequested)
                {
                    await WaitForChangeAsync(client, inbox, stop);
                    await CheckAsync(inbox, cursor, stop);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (AuthenticationException)
            {
                // Riprovare con la stessa password non serve e rischia il blocco dell'account.
                Post(() => SetStatus("Accesso rifiutato dal server: ricollega la casella con utente e password giusti", connected: false), stop);
                return;
            }
            catch (Exception ex) when (Describe(ex) is { } reason)
            {
                var wait = retry;
                Post(() => SetStatus($"{reason} Nuovo tentativo tra {Math.Round(wait.TotalSeconds)} s.", connected: false), stop);
                retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxRetry.Ticks));
                try
                {
                    await Task.Delay(wait, stop);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Con IDLE il server avvisa appena arriva qualcosa; l'attesa si rinnova ogni 9 minuti.</summary>
    private static async Task WaitForChangeAsync(ImapClient client, IMailFolder inbox, CancellationToken stop)
    {
        if (!client.Capabilities.HasFlag(ImapCapabilities.Idle))
        {
            await Task.Delay(PollInterval, stop);
            await client.NoOpAsync(stop);
            return;
        }
        using var done = new CancellationTokenSource(IdleRestart);
        void OnCountChanged(object? sender, EventArgs e) => done.Cancel();
        inbox.CountChanged += OnCountChanged;
        try
        {
            await client.IdleAsync(done.Token, stop);
        }
        finally
        {
            inbox.CountChanged -= OnCountChanged;
        }
    }

    private async Task CheckAsync(IMailFolder inbox, ImapCursor cursor, CancellationToken stop)
    {
        const MessageSummaryItems items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate;
        if (!cursor.IsPrimed || cursor.UidValidity != inbox.UidValidity)
        {
            // Primo controllo: le ultime mail riempiono la pagina Posta, senza avvisi.
            var latest = inbox.Count == 0 ? [] : await inbox.FetchAsync(Math.Max(0, inbox.Count - RecentCount), -1, items, stop);
            var lastUid = latest.Count > 0 ? latest.Max(m => m.UniqueId.Id) : (inbox.UidNext?.Id ?? 1) - 1;
            cursor.Prime(inbox.UidValidity, lastUid);
            var mails = latest.OrderByDescending(m => m.UniqueId.Id).Select(ToMail).ToArray();
            Post(() =>
            {
                _recent.Clear();
                _recent.AddRange(mails);
                Changed?.Invoke();
            }, stop);
            return;
        }

        var range = new UniqueIdRange(new UniqueId(inbox.UidValidity, cursor.LastUid + 1), UniqueId.MaxValue);
        var found = await inbox.SearchAsync(SearchQuery.Uids(range), stop);
        var fresh = cursor.Advance(inbox.UidValidity, found.Select(u => u.Id));
        if (fresh.Count == 0)
            return;
        var summaries = await inbox.FetchAsync(fresh.Select(u => new UniqueId(inbox.UidValidity, u)).ToList(), items, stop);
        var arrived = summaries.OrderBy(m => m.UniqueId.Id).Select(ToMail).ToArray();
        Post(() =>
        {
            foreach (var mail in arrived)
            {
                _recent.Insert(0, mail);
                MailArrived?.Invoke(mail);
            }
            if (_recent.Count > RecentCount)
                _recent.RemoveRange(RecentCount, _recent.Count - RecentCount);
            Changed?.Invoke();
        }, stop);
    }

    private static MailItem ToMail(IMessageSummary summary)
    {
        var from = summary.Envelope?.From.Mailboxes.FirstOrDefault();
        var sender = string.IsNullOrWhiteSpace(from?.Name) ? from?.Address ?? string.Empty : from.Name;
        var subject = string.IsNullOrWhiteSpace(summary.Envelope?.Subject) ? "(senza oggetto)" : summary.Envelope.Subject;
        var received = summary.InternalDate ?? summary.Envelope?.Date ?? DateTimeOffset.Now;
        return new MailItem(sender, subject, "Casella collegata", received);
    }

    /// <summary>Errori di rete e di protocollo in parole semplici; null per gli errori che non conosciamo.</summary>
    private static string? Describe(Exception ex) => ex switch
    {
        AuthenticationException => "Accesso rifiutato: controlla utente e password (per alcuni server l'utente è DOMINIO\\nome).",
        SslHandshakeException => "Connessione cifrata non riuscita: controlla server e porta (di solito 993).",
        SocketException => "Server non raggiungibile: controlla il nome del server e la connessione a Internet.",
        ImapProtocolException or ImapCommandException => "Il server ha risposto in modo inatteso.",
        IOException => "Connessione interrotta.",
        TimeoutException => "Il server non risponde.",
        OperationCanceledException => "Operazione annullata.",
        _ => null,
    };

    /// <summary>Sul thread UI, solo se la casella non è stata scollegata nel frattempo.</summary>
    private void Post(Action action, CancellationToken stop) =>
        _dispatcher.BeginInvoke(() =>
        {
            if (!stop.IsCancellationRequested)
                action();
        });

    private void SetStatus(string status, bool connected)
    {
        Status = status;
        IsConnected = connected;
        Changed?.Invoke();
    }
}
