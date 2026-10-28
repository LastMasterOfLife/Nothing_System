using System.IO;
using System.Net.Sockets;
using System.Windows.Threading;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
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

    private const MessageSummaryItems SummaryItems =
        MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate | MessageSummaryItems.Flags;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly List<MailItem> _recent = [];
    private CancellationTokenSource? _stop;
    private MailAccount? _account;

    public IReadOnlyList<MailItem> Recent => _recent;

    /// <summary>Mail non lette nella Posta in arrivo (anche oltre le ultime 10).</summary>
    public int UnreadCount { get; private set; }

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
        _account = account;
        SetStatus($"Connessione a {account.Server}…", connected: false);
        var token = _stop.Token;
        _ = Task.Run(() => RunAsync(account, password, token), token);
    }

    public void Stop()
    {
        _stop?.Cancel();
        _stop?.Dispose();
        _stop = null;
        _account = null;
        _recent.Clear();
        UnreadCount = 0;
        SetStatus("Nessuna casella collegata", connected: false);
    }

    /// <summary>
    /// Scarica la mail intera con una connessione a parte (quella principale è in IDLE). La casella è aperta in
    /// sola lettura e MailKit chiede <c>BODY.PEEK</c>: aprirla qui non la segna come letta.
    /// </summary>
    public async Task<(MailContent? Content, string? Error)> LoadAsync(ImapMessageRef message, CancellationToken cancellation)
    {
        if (Credentials() is not ({ } account, { } password))
            return (null, "La casella non è collegata.");
        using var client = new ImapClient { Timeout = 30_000 };
        try
        {
            await ConnectAsync(client, account, password, cancellation);
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, cancellation);
            if (client.Inbox.UidValidity != message.UidValidity)
                return (null, "La casella è stata riorganizzata dal server: la mail non si trova più.");
            var mime = await client.Inbox.GetMessageAsync(new UniqueId(message.UidValidity, message.Uid), cancellation);
            await client.DisconnectAsync(true, cancellation);
            return (ToContent(mime), null);
        }
        catch (MessageNotFoundException)
        {
            return (null, "La mail non c'è più: forse è stata spostata o eliminata.");
        }
        catch (Exception ex) when (Describe(ex) is { } reason)
        {
            return (null, reason);
        }
    }

    /// <summary>Segna la mail come letta sul server: l'unica modifica che Nothing Shell fa, e solo se richiesta.</summary>
    public async Task<string?> MarkReadAsync(ImapMessageRef message, CancellationToken cancellation)
    {
        if (Credentials() is not ({ } account, { } password))
            return "La casella non è collegata.";
        using var client = new ImapClient { Timeout = 30_000 };
        try
        {
            await ConnectAsync(client, account, password, cancellation);
            await client.Inbox.OpenAsync(FolderAccess.ReadWrite, cancellation);
            if (client.Inbox.UidValidity != message.UidValidity)
                return "La casella è stata riorganizzata dal server: la mail non si trova più.";
            await client.Inbox.AddFlagsAsync(new UniqueId(message.UidValidity, message.Uid), MessageFlags.Seen, true, cancellation);
            await client.DisconnectAsync(true, cancellation);
        }
        catch (Exception ex) when (Describe(ex) is { } reason)
        {
            return reason;
        }
        // La connessione principale riceverà il cambio dal server; intanto si aggiorna subito.
        var index = _recent.FindIndex(m => m.Imap == message);
        if (index >= 0 && _recent[index].IsUnread)
        {
            _recent[index] = _recent[index] with { IsUnread = false };
            UnreadCount = Math.Max(0, UnreadCount - 1);
            Changed?.Invoke();
        }
        return null;
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

    /// <summary>
    /// Con IDLE il server avvisa appena arriva una mail o cambia lo stato di una (letta altrove, eliminata);
    /// l'attesa si rinnova ogni 9 minuti.
    /// </summary>
    private static async Task WaitForChangeAsync(ImapClient client, IMailFolder inbox, CancellationToken stop)
    {
        if (!client.Capabilities.HasFlag(ImapCapabilities.Idle))
        {
            await Task.Delay(PollInterval, stop);
            await client.NoOpAsync(stop);
            return;
        }
        using var done = new CancellationTokenSource(IdleRestart);
        void OnChanged(object? sender, EventArgs e) => done.Cancel();
        inbox.CountChanged += OnChanged;
        inbox.MessageFlagsChanged += OnChanged;
        try
        {
            await client.IdleAsync(done.Token, stop);
        }
        finally
        {
            inbox.CountChanged -= OnChanged;
            inbox.MessageFlagsChanged -= OnChanged;
        }
    }

    /// <summary>
    /// Rilegge le ultime mail (pochi KB: busta e flag) e il numero di non lette; avvisa solo per quelle con UID
    /// nuovo. Al primo controllo prende solo il punto di partenza.
    /// </summary>
    private async Task CheckAsync(IMailFolder inbox, ImapCursor cursor, CancellationToken stop)
    {
        var latest = inbox.Count == 0 ? [] : await inbox.FetchAsync(Math.Max(0, inbox.Count - RecentCount), -1, SummaryItems, stop);
        var mails = latest.OrderByDescending(m => m.UniqueId.Id).Select(ToMail).ToArray();
        var unread = (await inbox.SearchAsync(SearchQuery.NotSeen, stop)).Count;

        MailItem[] arrived = [];
        if (!cursor.IsPrimed || cursor.UidValidity != inbox.UidValidity)
        {
            var lastUid = latest.Count > 0 ? latest.Max(m => m.UniqueId.Id) : (inbox.UidNext?.Id ?? 1) - 1;
            cursor.Prime(inbox.UidValidity, lastUid);
        }
        else
        {
            var range = new UniqueIdRange(new UniqueId(inbox.UidValidity, cursor.LastUid + 1), UniqueId.MaxValue);
            var found = await inbox.SearchAsync(SearchQuery.Uids(range), stop);
            var fresh = cursor.Advance(inbox.UidValidity, found.Select(u => u.Id)).ToHashSet();
            arrived = mails.Where(m => m.Imap is { } r && fresh.Contains(r.Uid)).Reverse().ToArray(); // dalla più vecchia
        }

        Post(() =>
        {
            _recent.Clear();
            _recent.AddRange(mails);
            UnreadCount = unread;
            foreach (var mail in arrived)
                MailArrived?.Invoke(mail);
            Changed?.Invoke();
        }, stop);
    }

    private static MailItem ToMail(IMessageSummary summary)
    {
        var from = summary.Envelope?.From.Mailboxes.FirstOrDefault();
        var sender = string.IsNullOrWhiteSpace(from?.Name) ? from?.Address ?? string.Empty : from.Name;
        var subject = string.IsNullOrWhiteSpace(summary.Envelope?.Subject) ? "(senza oggetto)" : summary.Envelope.Subject;
        var received = summary.InternalDate ?? summary.Envelope?.Date ?? DateTimeOffset.Now;
        return new MailItem(sender, subject, "Casella collegata", received)
        {
            Imap = new ImapMessageRef(summary.UniqueId.Validity, summary.UniqueId.Id),
            SenderAddress = from?.Address,
            IsUnread = summary.Flags is { } flags && !flags.HasFlag(MessageFlags.Seen),
        };
    }

    /// <summary>Testo semplice se c'è, altrimenti l'HTML ripulito; degli allegati solo il nome.</summary>
    private static MailContent ToContent(MimeMessage mime)
    {
        var from = mime.From.Mailboxes.FirstOrDefault();
        var body = mime.TextBody is { Length: > 0 } text ? MailText.Normalize(text)
            : mime.HtmlBody is { } html ? MailText.FromHtml(html)
            : string.Empty;
        return new MailContent(
            from is null ? string.Empty : string.IsNullOrWhiteSpace(from.Name) ? from.Address : $"{from.Name} <{from.Address}>",
            string.Join(", ", mime.To.Mailboxes.Select(m => string.IsNullOrWhiteSpace(m.Name) ? m.Address : m.Name)),
            mime.Date,
            string.IsNullOrWhiteSpace(mime.Subject) ? "(senza oggetto)" : mime.Subject,
            body,
            mime.Attachments.Select(a => a.ContentDisposition?.FileName ?? a.ContentType.Name ?? "allegato").ToArray(),
            mime.ReplyTo.Mailboxes.FirstOrDefault()?.Address ?? from?.Address);
    }

    /// <summary>Casella collegata e password dal Gestore credenziali; null se scollegata.</summary>
    private (MailAccount Account, string Password)? Credentials() =>
        _account is { } account && CredentialStore.Read(account.CredentialTarget) is { Length: > 0 } password
            ? (account, password)
            : null;

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
