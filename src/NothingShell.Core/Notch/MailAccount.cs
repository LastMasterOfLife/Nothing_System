namespace NothingShell.Core.Notch;

/// <summary>
/// Casella IMAP collegata al notch (docs/08-notch-mail-pages.md). La password non è qui: sta nel Gestore
/// credenziali di Windows, sotto <see cref="CredentialTarget"/>.
/// </summary>
public sealed record MailAccount(
    string Address,
    string Server,
    int Port = MailAccount.DefaultPort,
    string? UserName = null,
    string? Webmail = null)
{
    public const int DefaultPort = 993;

    /// <summary>Nome utente per l'accesso: l'indirizzo, se non ne è indicato un altro.</summary>
    public string Login => string.IsNullOrWhiteSpace(UserName) ? Address : UserName;

    public string CredentialTarget => $"NothingShell:imap:{Login}@{Server}";
}

/// <summary>Configurazione guidata della casella IMAP.</summary>
public static class ImapSetup
{
    private static readonly Dictionary<string, string> KnownServers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gmail.com"] = "imap.gmail.com",
        ["googlemail.com"] = "imap.gmail.com",
        ["outlook.com"] = "outlook.office365.com",
        ["outlook.it"] = "outlook.office365.com",
        ["hotmail.com"] = "outlook.office365.com",
        ["hotmail.it"] = "outlook.office365.com",
        ["live.com"] = "outlook.office365.com",
        ["live.it"] = "outlook.office365.com",
        ["msn.com"] = "outlook.office365.com",
        ["yahoo.com"] = "imap.mail.yahoo.com",
        ["yahoo.it"] = "imap.mail.yahoo.com",
        ["icloud.com"] = "imap.mail.me.com",
        ["me.com"] = "imap.mail.me.com",
        ["libero.it"] = "imapmail.libero.it",
        ["virgilio.it"] = "in.virgilio.it",
        ["tiscali.it"] = "imap.tiscali.it",
    };

    /// <summary>Server IMAP probabile per l'indirizzo (i provider noti, altrimenti <c>imap.dominio</c>); null se non è un indirizzo.</summary>
    public static string? SuggestServer(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var at = address.Trim().LastIndexOf('@');
        if (at <= 0 || at == address.Trim().Length - 1)
            return null;
        var domain = address.Trim()[(at + 1)..];
        return KnownServers.TryGetValue(domain, out var server) ? server : $"imap.{domain.ToLowerInvariant()}";
    }

    /// <summary>Primo errore da mostrare all'utente, null se i dati sono completi.</summary>
    public static string? Validate(MailAccount account, string password)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(password);
        var at = account.Address.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at == account.Address.Length - 1 || account.Address.Any(char.IsWhiteSpace))
            return "Indirizzo email non valido.";
        if (string.IsNullOrWhiteSpace(account.Server) || account.Server.Any(char.IsWhiteSpace))
            return "Indica il server IMAP (es. mail.tuodominio.it).";
        if (account.Port is < 1 or > 65535)
            return "Porta non valida (di solito 993).";
        if (password.Length == 0)
            return "Inserisci la password.";
        return null;
    }
}

/// <summary>
/// Quali messaggi IMAP sono nuovi, per UID. Al primo controllo (e se il server cambia <c>UIDVALIDITY</c>,
/// cioè rinumera la casella) si prende solo il punto di partenza: nessun avviso per le mail già presenti.
/// </summary>
public sealed class ImapCursor
{
    public bool IsPrimed { get; private set; }

    public uint UidValidity { get; private set; }

    public uint LastUid { get; private set; }

    public void Prime(uint uidValidity, uint lastUid)
    {
        UidValidity = uidValidity;
        LastUid = lastUid;
        IsPrimed = true;
    }

    /// <summary>UID nuovi in ordine crescente (il server può restituire anche l'ultimo già visto: <c>n:*</c>).</summary>
    public IReadOnlyList<uint> Advance(uint uidValidity, IEnumerable<uint> uids)
    {
        ArgumentNullException.ThrowIfNull(uids);
        var all = uids.ToArray();
        if (!IsPrimed || uidValidity != UidValidity)
        {
            Prime(uidValidity, all.Length > 0 ? all.Max() : LastUid);
            return [];
        }
        var fresh = all.Where(u => u > LastUid).Distinct().Order().ToArray();
        if (fresh.Length > 0)
            LastUid = fresh[^1];
        return fresh;
    }
}

/// <summary>Mail da più fonti (casella IMAP, notifiche di Windows) in un unico elenco.</summary>
public static class MailFeed
{
    private static readonly TimeSpan SameMailWindow = TimeSpan.FromMinutes(3);

    /// <summary>
    /// La stessa mail vista da due fonti (es. casella collegata e Outlook aperto): stesso oggetto, stesso
    /// mittente (o uno dei due vuoto) e ricevuta a pochi minuti di distanza.
    /// </summary>
    public static bool IsSame(MailItem a, MailItem b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Subject.Equals(b.Subject, StringComparison.OrdinalIgnoreCase)
               && (a.Sender.Length == 0 || b.Sender.Length == 0 || a.Sender.Equals(b.Sender, StringComparison.OrdinalIgnoreCase))
               && (a.Received - b.Received).Duration() <= SameMailWindow;
    }

    /// <summary>
    /// Le più recenti per prime, senza doppioni, al massimo <paramref name="max"/>. Dei doppioni resta quella
    /// della casella collegata: si può riaprire dal server.
    /// </summary>
    public static IReadOnlyList<MailItem> Merge(IEnumerable<MailItem> first, IEnumerable<MailItem> second, int max)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        var merged = new List<MailItem>();
        foreach (var mail in first.Concat(second).OrderByDescending(m => m.Received))
        {
            var same = merged.FindIndex(m => IsSame(m, mail));
            if (same < 0)
                merged.Add(mail);
            else if (merged[same].Imap is null && mail.Imap is not null)
                merged[same] = mail;
        }
        return merged.Take(max).ToArray();
    }
}
