namespace NothingShell.Core.Notch;

/// <summary>Mail ricevuta, dalla casella collegata o da una notifica. Resta solo in memoria.</summary>
public sealed record MailItem(string Sender, string Subject, string App, DateTimeOffset Received)
{
    /// <summary>Posizione nella casella collegata, per riaprirla dal server; null per le mail dalle notifiche.</summary>
    public ImapMessageRef? Imap { get; init; }

    /// <summary>AppUserModelID dell'app che ha mandato la notifica, per aprirla; null se non noto.</summary>
    public string? AppId { get; init; }

    public string? SenderAddress { get; init; }

    /// <summary>Non ancora letta (solo casella collegata: le notifiche non lo dicono).</summary>
    public bool IsUnread { get; init; }
}

/// <summary>Riconosce le notifiche di posta e ne estrae mittente e oggetto (docs/08-notch-mail-pages.md).</summary>
public static class MailDetector
{
    private static readonly string[] MailApps =
        ["Outlook", "Posta", "Mail", "Thunderbird", "eM Client", "Mailbird", "Spark", "Gmail"];

    private static readonly string[] Browsers =
        ["Brave", "Chrome", "Edge", "Firefox", "Opera", "Vivaldi"];

    private static readonly string[] MailSites =
        ["mail.google.com", "outlook.live.com", "outlook.office.com", "mail.yahoo.com", "mail.proton.me"];

    /// <param name="appName">Nome visualizzato dell'app che ha inviato la notifica.</param>
    /// <param name="texts">Testi della notifica in ordine (titolo, corpo, attribuzione).</param>
    /// <param name="extraApps">App aggiunte dall'utente in <c>extraMailApps</c>.</param>
    public static bool IsMail(string appName, IReadOnlyList<string> texts, IEnumerable<string>? extraApps = null)
    {
        ArgumentNullException.ThrowIfNull(appName);
        ArgumentNullException.ThrowIfNull(texts);
        if (Matches(appName, MailApps) || (extraApps is not null && Matches(appName, extraApps)))
            return true;
        return Matches(appName, Browsers)
               && texts.Any(t => MailSites.Any(site => t.Contains(site, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Client di posta riconosciuto senza configurazione (Outlook, Posta, Thunderbird…).</summary>
    public static bool IsKnownMailApp(string appName)
    {
        ArgumentNullException.ThrowIfNull(appName);
        return Matches(appName, MailApps);
    }

    /// <summary>
    /// App da proporre nelle impostazioni come "app di posta": prima quelle già aggiunte dall'utente,
    /// poi quelle che hanno mandato notifiche di recente, esclusi i client già riconosciuti.
    /// </summary>
    public static IReadOnlyList<(string App, bool IsMail)> SetupCandidates(IEnumerable<string> notifyingApps, IEnumerable<string> extraApps)
    {
        ArgumentNullException.ThrowIfNull(notifyingApps);
        ArgumentNullException.ThrowIfNull(extraApps);
        var extras = extraApps.Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();
        return extras.Select(a => (a, true))
            .Concat(notifyingApps
                .Where(a => !string.IsNullOrWhiteSpace(a) && !IsKnownMailApp(a) && !Matches(a, extras))
                .Select(a => (a, false)))
            .DistinctBy(c => c.a, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Elenco <c>extraMailApps</c> con <paramref name="app"/> aggiunta o tolta.</summary>
    public static IReadOnlyList<string> WithExtraApp(IEnumerable<string> extraApps, string app, bool isMail)
    {
        ArgumentNullException.ThrowIfNull(extraApps);
        ArgumentNullException.ThrowIfNull(app);
        var without = extraApps.Where(a => !a.Equals(app, StringComparison.OrdinalIgnoreCase));
        return (isMail ? without.Append(app) : without).ToArray();
    }

    /// <summary>Riga 1 = mittente, prima riga del testo successivo = oggetto; con una sola riga è l'oggetto.</summary>
    public static MailItem? Extract(string appName, IReadOnlyList<string> texts, DateTimeOffset received)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var lines = texts.Select(t => t.Trim()).Where(t => t.Length > 0 && !IsSiteAttribution(t)).ToArray();
        return lines.Length switch
        {
            0 => null,
            1 => new MailItem(string.Empty, FirstLine(lines[0]), appName, received),
            _ => new MailItem(lines[0], FirstLine(lines[1]), appName, received),
        };
    }

    private static bool Matches(string appName, IEnumerable<string> names) =>
        names.Any(n => appName.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static bool IsSiteAttribution(string text) =>
        MailSites.Any(site => text.Equals(site, StringComparison.OrdinalIgnoreCase));

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();
}
