using System.Text.RegularExpressions;

namespace NothingShell.Core.Connectors;

/// <summary>
/// Le query dei connettori di database sono controlli: una sola istruzione, solo lettura (docs/18-connettori-diagnostica.md).
/// Non sostituisce un utente con permessi di sola lettura, ma ferma gli errori e le query copiate male.
/// </summary>
public static partial class SqlGuard
{
    /// <summary>Il motivo per cui la query non va bene, null se è una lettura accettabile.</summary>
    public static string? Check(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var text = query.Trim();
        if (text.Length == 0)
            return "la query è vuota";
        if (text.Contains("--", StringComparison.Ordinal) || text.Contains("/*", StringComparison.Ordinal))
            return "niente commenti nella query";

        // le parole dentro i testi tra apici ('delete') non contano
        var code = Literals().Replace(text, "''").TrimEnd(';', ' ', '\t', '\r', '\n');
        if (code.Contains(';', StringComparison.Ordinal))
            return "una sola istruzione";
        if (!StartsWithRead().IsMatch(code))
            return "deve cominciare con SELECT o WITH";
        if (Forbidden().Match(code) is { Success: true } hit)
            return $"«{hit.Value.ToUpperInvariant()}» non è ammesso: i controlli leggono soltanto";
        return null;
    }

    [GeneratedRegex("'(?:[^']|'')*'")]
    private static partial Regex Literals();

    [GeneratedRegex(@"^\s*\(*\s*(select|with)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StartsWithRead();

    [GeneratedRegex(@"\b(into|insert|update|delete|merge|drop|alter|create|truncate|grant|revoke|deny|exec|execute|openrowset|opendatasource|openquery|bulk|backup|restore|shutdown|waitfor|xp_\w*|sp_\w*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Forbidden();
}
