namespace NothingShell.Core.Notch;

/// <summary>Mail ricevuta, ricavata da una notifica. Resta solo in memoria.</summary>
public sealed record MailItem(string Sender, string Subject, string App, DateTimeOffset Received);

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
