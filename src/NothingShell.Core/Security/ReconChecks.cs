namespace NothingShell.Core.Security;

/// <summary>
/// Ricognizione passiva (docs/26-sicurezza.md): guarda <b>solo</b> la risposta a una normale richiesta — intestazioni,
/// versione TLS, cookie, software dichiarato — e ne ricava le debolezze, senza mandare nessun payload d'attacco. Puro e
/// testato. È la fase che c'è sempre, anche quando non è installato nessuno scanner esterno, così la modalità dà comunque
/// un risultato. I controlli ricalcano le regole del connettore «Intestazioni di sicurezza», ma qui diventano debolezze
/// con rimedio.
/// </summary>
public static class ReconChecks
{
    /// <param name="url">L'URL effettivamente richiesto (per il campo «dove»).</param>
    /// <param name="status">Codice HTTP della risposta.</param>
    /// <param name="tlsProtocol">Nome del protocollo TLS negoziato (es. «Tls13»), null se la richiesta era in chiaro.</param>
    /// <param name="headers">Intestazioni della risposta, con i nomi in minuscolo (come le dà <c>HttpProbe</c>).</param>
    public static IReadOnlyList<Finding> FromResponse(Uri url, int status, string? tlsProtocol, IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(headers);
        var found = new List<Finding>();
        var where = url.AbsoluteUri;
        var https = url.Scheme == "https";
        bool Has(string name) => headers.ContainsKey(name) && headers[name].Trim().Length > 0;
        string Get(string name) => headers.TryGetValue(name, out var v) ? v : string.Empty;

        // Traffico in chiaro: una pagina servita in HTTP (contenuto, non un redirect verso HTTPS) espone tutto a chi è in mezzo.
        if (!https && status is >= 200 and < 300)
            found.Add(new Finding(Severity.High, "Sito raggiungibile in HTTP (non cifrato)", where,
                $"Risposta {status} su http:// senza TLS.",
                "Servi tutto in HTTPS e reindirizza l'HTTP all'HTTPS (301), poi attiva HSTS.", "CWE-319"));

        // TLS vecchio: protocolli sotto il TLS 1.2 sono rotti.
        if (tlsProtocol is { Length: > 0 } tls && (tls.Contains("Tls11", StringComparison.OrdinalIgnoreCase)
                                                    || tls.Contains("Tls10", StringComparison.OrdinalIgnoreCase)
                                                    || (tls.Contains("Tls1", StringComparison.OrdinalIgnoreCase) && !tls.Contains("Tls12", StringComparison.OrdinalIgnoreCase) && !tls.Contains("Tls13", StringComparison.OrdinalIgnoreCase))
                                                    || tls.Contains("Ssl", StringComparison.OrdinalIgnoreCase)))
            found.Add(new Finding(Severity.High, "Versione TLS obsoleta accettata", where,
                $"Handshake completato con {tls}.",
                "Accetta solo TLS 1.2 e 1.3; disattiva TLS 1.0/1.1 e SSLv3.", "CWE-326"));

        // HSTS: senza, un downgrade a HTTP resta possibile.
        if (https && !Has("strict-transport-security"))
            found.Add(new Finding(Severity.Medium, "Manca Strict-Transport-Security (HSTS)", where,
                "Nessuna intestazione HSTS nella risposta HTTPS.",
                "Aggiungi «Strict-Transport-Security: max-age=31536000; includeSubDomains».", "CWE-319"));

        // Content-Security-Policy: la difesa principale contro l'XSS.
        if (!Has("content-security-policy"))
            found.Add(new Finding(Severity.Medium, "Manca Content-Security-Policy", where,
                "Nessuna CSP: una XSS non trova argini.",
                "Definisci una CSP restrittiva (default-src 'self', niente 'unsafe-inline').", "CWE-693"));

        // Clickjacking: X-Frame-Options o frame-ancestors nella CSP.
        var csp = Get("content-security-policy");
        if (!Has("x-frame-options") && !csp.Contains("frame-ancestors", StringComparison.OrdinalIgnoreCase))
            found.Add(new Finding(Severity.Medium, "Nessuna protezione dal clickjacking", where,
                "Mancano sia X-Frame-Options sia «frame-ancestors» nella CSP.",
                "Aggiungi «X-Frame-Options: DENY» o «frame-ancestors 'none'» nella CSP.", "CWE-1021"));

        // Sniffing del tipo di contenuto.
        if (!Get("x-content-type-options").Contains("nosniff", StringComparison.OrdinalIgnoreCase))
            found.Add(new Finding(Severity.Low, "Manca X-Content-Type-Options: nosniff", where,
                "Il browser può indovinare il tipo dei contenuti.",
                "Aggiungi «X-Content-Type-Options: nosniff».", "CWE-16"));

        // Referrer-Policy: evita di perdere URL sensibili.
        if (!Has("referrer-policy"))
            found.Add(new Finding(Severity.Info, "Manca Referrer-Policy", where,
                "Nessuna Referrer-Policy dichiarata.",
                "Aggiungi «Referrer-Policy: strict-origin-when-cross-origin».", "CWE-200"));

        // Software esposto: versioni nel banner aiutano chi cerca una CVE pronta.
        foreach (var name in new[] { "server", "x-powered-by", "x-aspnet-version", "x-aspnetmvc-version" })
            if (Get(name) is { Length: > 0 } banner && banner.Any(char.IsDigit))
                found.Add(new Finding(Severity.Low, "Versione del software esposta nelle intestazioni", where,
                    $"{name}: {banner}",
                    "Togli o accorcia le intestazioni che rivelano prodotto e versione.", "CWE-200"));

        // Cookie senza protezioni.
        if (Get("set-cookie") is { Length: > 0 } cookie)
        {
            if (!cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase))
                found.Add(new Finding(Severity.Medium, "Cookie senza HttpOnly", where,
                    "Un cookie è impostato senza il flag HttpOnly.",
                    "Aggiungi HttpOnly ai cookie di sessione, così JavaScript non li legge.", "CWE-1004"));
            if (https && !cookie.Contains("secure", StringComparison.OrdinalIgnoreCase))
                found.Add(new Finding(Severity.Medium, "Cookie senza Secure", where,
                    "Un cookie è impostato senza il flag Secure su HTTPS.",
                    "Aggiungi Secure ai cookie, così non partono su connessioni HTTP.", "CWE-614"));
            if (!cookie.Contains("samesite", StringComparison.OrdinalIgnoreCase))
                found.Add(new Finding(Severity.Low, "Cookie senza SameSite", where,
                    "Un cookie è impostato senza l'attributo SameSite.",
                    "Imposta SameSite=Lax o Strict per ridurre il rischio di CSRF.", "CWE-352"));
        }

        // CORS spalancato.
        if (Get("access-control-allow-origin").Trim() == "*" && Get("access-control-allow-credentials").Contains("true", StringComparison.OrdinalIgnoreCase))
            found.Add(new Finding(Severity.Medium, "CORS permissivo con credenziali", where,
                "Access-Control-Allow-Origin: * insieme ad Allow-Credentials: true.",
                "Non usare «*» quando mandi credenziali: elenca le origini fidate.", "CWE-942"));

        return found;
    }
}
