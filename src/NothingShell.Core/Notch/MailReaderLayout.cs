using System.Globalization;
using System.Text;

namespace NothingShell.Core.Notch;

/// <summary>
/// Posizione e misure del lettore di posta, ricordate tra un'apertura e l'altra (docs/08-notch-mail-pages.md).
/// Coordinate in DIP; <see cref="Left"/> e <see cref="Top"/> null = al centro dello schermo.
/// </summary>
public sealed record MailReaderLayout(double Width, double Height, double ListWidth, double? Left = null, double? Top = null, double BodySize = 13)
{
    public const double MinWidth = 560;
    public const double MinHeight = 420;
    public const double MinList = 220;
    public const double MaxList = 440;
    public const double MinBody = 11;
    public const double MaxBody = 22;

    /// <summary>Sotto questa larghezza l'elenco si nasconde e la mail occupa tutto (si riapre col pulsante ELENCO).</summary>
    public const double CompactWidth = 700;

    public static MailReaderLayout Default { get; } = new(960, 640, 300);

    /// <summary>
    /// Riporta le misure dentro i limiti e la finestra dentro l'area di lavoro (un monitor staccato non la perde
    /// fuori dallo schermo). L'elenco lascia almeno 320 DIP alla mail.
    /// </summary>
    public MailReaderLayout Fit(double workLeft, double workTop, double workWidth, double workHeight)
    {
        var width = Math.Clamp(Width, MinWidth, Math.Max(MinWidth, workWidth));
        var height = Math.Clamp(Height, MinHeight, Math.Max(MinHeight, workHeight));
        var list = Math.Clamp(ListWidth, MinList, Math.Max(MinList, Math.Min(MaxList, width - 320)));
        double? left = Left is { } l ? Math.Clamp(l, workLeft, Math.Max(workLeft, workLeft + workWidth - width)) : null;
        double? top = Top is { } t ? Math.Clamp(t, workTop, Math.Max(workTop, workTop + workHeight - height)) : null;
        return this with { Width = width, Height = height, ListWidth = list, Left = left, Top = top, BodySize = Math.Clamp(BodySize, MinBody, MaxBody) };
    }
}

/// <summary>Mail divise per giorno nell'elenco del lettore: OGGI, IERI, QUESTA SETTIMANA, PRIMA.</summary>
public static class MailGroups
{
    public static IReadOnlyList<(string Label, IReadOnlyList<MailItem> Mails)> Of(IEnumerable<MailItem> mails, DateTime today)
    {
        ArgumentNullException.ThrowIfNull(mails);
        return mails
            .GroupBy(m => Label(m.Received.ToLocalTime().Date, today.Date))
            .Select(g => (g.Key, (IReadOnlyList<MailItem>)g.ToList()))
            .OrderBy(g => Array.IndexOf(Order, g.Key))
            .ToList();
    }

    private static readonly string[] Order = ["OGGI", "IERI", "QUESTA SETTIMANA", "PRIMA"];

    private static string Label(DateTime day, DateTime today) =>
        day >= today ? "OGGI"
        : day == today.AddDays(-1) ? "IERI"
        : day > today.AddDays(-7) ? "QUESTA SETTIMANA"
        : "PRIMA";
}

/// <summary>Ricerca e iniziali delle mail del lettore.</summary>
public static class MailSearch
{
    /// <summary>Tutte le parole cercate compaiono in mittente, indirizzo, oggetto o app (senza badare a maiuscole e accenti).</summary>
    public static bool Matches(MailItem mail, string? query)
    {
        ArgumentNullException.ThrowIfNull(mail);
        if (string.IsNullOrWhiteSpace(query))
            return true;
        var haystack = Fold($"{mail.Sender} {mail.SenderAddress} {mail.Subject} {mail.App}");
        return Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).All(haystack.Contains);
    }

    /// <summary>"Mario Rossi" → "MR", "amazon.it" → "A", "&lt;info@x.it&gt;" → "I"; vuoto → "?".</summary>
    public static string Initials(string? sender)
    {
        var text = (sender ?? string.Empty).Trim();
        var angle = text.IndexOf('<', StringComparison.Ordinal);
        if (angle > 0)
            text = text[..angle].Trim(); // "Mario Rossi <mario@x.it>": basta il nome
        var words = text
            .Split([' ', '_', '-', '"', '\'', '<', '>', '@'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetterOrDigit(w[0]))
            .ToList();
        if (words.Count == 0)
            return "?";
        // per un indirizzo conta solo la parte prima della chiocciola
        if (text.Contains('@', StringComparison.Ordinal) && !text.Contains(' ', StringComparison.Ordinal))
            return char.ToUpperInvariant(words[0][0]).ToString();
        var first = char.ToUpperInvariant(words[0][0]);
        return words.Count > 1 && char.IsLetter(words[^1][0]) ? $"{first}{char.ToUpperInvariant(words[^1][0])}" : first.ToString();
    }

    private static string Fold(string text)
    {
        var plain = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                plain.Append(char.ToLowerInvariant(c));
        }
        return plain.ToString();
    }
}
