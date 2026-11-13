namespace NothingShell.Core.Security;

/// <summary>Il bersaglio autorizzato: l'URL di partenza e l'host (minuscolo, senza porta) a cui tutto resta limitato.</summary>
public sealed record SecurityTarget(Uri Url, string Host)
{
    /// <summary>Schema, host (con porta se non standard) e percorso, senza la barra finale: per i titoli e i log.</summary>
    public string Display =>
        Url.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped).TrimEnd('/');
}

/// <summary>
/// Il cancello di autorizzazione della Modalità Sicurezza (docs/26-sicurezza.md). Una sessione può partire solo contro un
/// host che l'utente ha dichiarato suo o autorizzato, e ogni richiesta resta dentro quell'host: così lo stesso strumento
/// non si può puntare, per sbaglio o di proposito, su un sito di terzi. Puro e testato; nessuna rete qui.
/// </summary>
public static class ScopeGuard
{
    /// <summary>Legge l'indirizzo scritto dall'utente (aggiunge https:// se manca); null se non è un http/https con un host vero.</summary>
    public static SecurityTarget? Parse(string? input)
    {
        var s = (input ?? string.Empty).Trim();
        if (s.Length == 0)
            return null;
        if (!s.Contains("://", StringComparison.Ordinal))
            s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            return null;
        var host = NormalizeHost(url.DnsSafeHost);
        return host.Length == 0 ? null : new SecurityTarget(url, host);
    }

    /// <summary>Host confrontabile: senza spazi, senza punto finale, minuscolo.</summary>
    public static string NormalizeHost(string? host) =>
        (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>
    /// true se <paramref name="candidate"/> è una richiesta che resta dentro il bersaglio: stesso host, oppure (con
    /// <paramref name="includeSubdomains"/>) un suo sottodominio. Schemi diversi da http/https, o host diversi, sono fuori.
    /// Serve a tenere in riga un redirect o un link che porterebbe lo scanner altrove.
    /// </summary>
    public static bool IsInScope(SecurityTarget target, string? candidate, bool includeSubdomains = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!Uri.TryCreate((candidate ?? string.Empty).Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            return false;
        var host = NormalizeHost(url.DnsSafeHost);
        var allowed = NormalizeHost(target.Host);
        if (allowed.Length == 0)
            return false;
        return host == allowed || (includeSubdomains && host.EndsWith("." + allowed, StringComparison.Ordinal));
    }
}
