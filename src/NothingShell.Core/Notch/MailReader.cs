using System.Net;
using System.Text.RegularExpressions;
using NothingShell.Core.Launcher;

namespace NothingShell.Core.Notch;

/// <summary>Messaggio nella casella collegata: UID valido finché il server non cambia <c>UIDVALIDITY</c>.</summary>
public readonly record struct ImapMessageRef(uint UidValidity, uint Uid);

/// <summary>Mail aperta nel lettore (docs/08-notch-mail-pages.md). Solo in memoria.</summary>
public sealed record MailContent(
    string From,
    string To,
    DateTimeOffset Date,
    string Subject,
    string Body,
    IReadOnlyList<string> Attachments,
    string? ReplyAddress);

/// <summary>Testo leggibile del corpo di una mail.</summary>
public static partial class MailText
{
    /// <summary>HTML in testo semplice: paragrafi separati da una riga vuota, niente stili né script.</summary>
    public static string FromHtml(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var text = HiddenBlocks().Replace(html, string.Empty);
        text = Paragraphs().Replace(text, "\n\n");
        text = LineBreaks().Replace(text, "\n");
        text = ListItems().Replace(text, "\n• ");
        text = Tags().Replace(text, string.Empty);
        return Normalize(WebUtility.HtmlDecode(text));
    }

    /// <summary>Spazi in eccesso tolti, al massimo una riga vuota di fila.</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace(' ', ' ')
            .Split('\n')
            .Select(l => Spaces().Replace(l, " ").Trim());
        var result = new List<string>();
        foreach (var line in lines)
            if (line.Length > 0 || (result.Count > 0 && result[^1].Length > 0))
                result.Add(line);
        return string.Join('\n', result).Trim();
    }

    [GeneratedRegex(@"<(head|style|script|title)\b[^>]*>.*?</\1\s*>|<!--.*?-->", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HiddenBlocks();

    [GeneratedRegex(@"</(p|h[1-6]|table|blockquote|ul|ol)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex Paragraphs();

    [GeneratedRegex(@"<br\s*/?>|</(div|tr)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItems();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();
}

/// <summary>
/// Codici di verifica (OTP) nelle mail: un numero di 4–8 cifre (anche "123 456") vicino a parole come
/// "codice" o "verification". Gli anni e gli importi non contano.
/// </summary>
public static partial class MailCodes
{
    private static readonly string[] Keywords =
    [
        "codice", "code", "otp", "verifica", "verification", "pin", "passcode", "token", "monouso", "one-time",
        "conferma", "confirm", "2fa", "autenticazione", "authentication",
    ];

    public static string? Find(params string?[] texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;
            var lower = text.ToLowerInvariant();
            var keywords = Keywords.SelectMany(k => Occurrences(lower, k)).ToArray();
            if (keywords.Length == 0)
                continue;
            var best = Candidates().Matches(text)
                .Where(m => !IsYear(m.Value))
                .MinBy(m => keywords.Min(k => Math.Abs(k - m.Index)));
            if (best is not null)
                return best.Value.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        }
        return null;
    }

    private static IEnumerable<int> Occurrences(string text, string word)
    {
        for (var i = text.IndexOf(word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(word, i + 1, StringComparison.Ordinal))
            yield return i;
    }

    private static bool IsYear(string value) =>
        value.Length == 4 && int.TryParse(value, out var n) && n is >= 1900 and <= 2099;

    [GeneratedRegex(@"(?<![\d.,:/])(\d{3}[ -]\d{3}|\d{4,8})(?!\d|[.,:/]\d|\s?(€|%|eur))", RegexOptions.IgnoreCase)]
    private static partial Regex Candidates();
}

/// <summary>Azioni sulla mail aperta: rispondere e aprire la webmail.</summary>
public static class MailLinks
{
    private static readonly Dictionary<string, string> KnownWebmail = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gmail.com"] = "https://mail.google.com/",
        ["googlemail.com"] = "https://mail.google.com/",
        ["outlook.com"] = "https://outlook.live.com/mail/",
        ["outlook.it"] = "https://outlook.live.com/mail/",
        ["hotmail.com"] = "https://outlook.live.com/mail/",
        ["hotmail.it"] = "https://outlook.live.com/mail/",
        ["live.com"] = "https://outlook.live.com/mail/",
        ["live.it"] = "https://outlook.live.com/mail/",
        ["msn.com"] = "https://outlook.live.com/mail/",
        ["yahoo.com"] = "https://mail.yahoo.com/",
        ["yahoo.it"] = "https://mail.yahoo.com/",
        ["icloud.com"] = "https://www.icloud.com/mail",
        ["me.com"] = "https://www.icloud.com/mail",
        ["libero.it"] = "https://mail.libero.it/",
        ["virgilio.it"] = "https://mail.virgilio.it/",
        ["tiscali.it"] = "https://webmail.tiscali.it/",
    };

    /// <summary>
    /// Webmail della casella: quella indicata nelle impostazioni, quella dei provider noti, altrimenti la
    /// pagina del server (molti server, come Exchange, la portano alla webmail).
    /// </summary>
    public static Uri? Webmail(MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!string.IsNullOrWhiteSpace(account.Webmail))
            return ToHttpUri(account.Webmail.Trim());
        var domain = account.Address[(account.Address.LastIndexOf('@') + 1)..];
        return KnownWebmail.TryGetValue(domain, out var known) ? new Uri(known) : ToHttpUri(account.Server);
    }

    /// <summary>
    /// Web app installata per quel sito (le app di Edge hanno l'AppUserModelID che inizia con
    /// <c>host-</c>, es. <c>mail.contoso.it-1A2B3C4D_…!App</c>): si apre lì invece che in una scheda del browser.
    /// </summary>
    public static AppEntry? InstalledWebApp(Uri site, IEnumerable<AppEntry> apps)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(apps);
        return apps.FirstOrDefault(a => a.Id.StartsWith($"{site.Host}-", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Link <c>mailto:</c> per rispondere, con "Re: " davanti all'oggetto se manca.</summary>
    public static string? Reply(string? address, string subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (string.IsNullOrWhiteSpace(address) || !address.Contains('@', StringComparison.Ordinal))
            return null;
        var re = subject.StartsWith("re:", StringComparison.OrdinalIgnoreCase) ? subject : $"Re: {subject}";
        return $"mailto:{address.Trim()}?subject={Uri.EscapeDataString(re)}";
    }

    private static Uri? ToHttpUri(string text)
    {
        var withScheme = text.Contains("://", StringComparison.Ordinal) ? text : $"https://{text}";
        return Uri.TryCreate(withScheme, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri : null;
    }
}
