namespace NothingShell.Core.Security;

/// <summary>Gravità di una debolezza, dal più lieve al più grave.</summary>
public enum Severity
{
    Info,
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>
/// Una debolezza di sicurezza trovata sul bersaglio autorizzato (docs/26-sicurezza.md): cosa è, dove, la prova (breve e
/// non sensibile), come si risolve e un riferimento (CWE, OWASP, id del template). È una constatazione da sistemare, non
/// un bottino: la prova dimostra che il buco esiste, non espone i dati che ci sono dietro.
/// </summary>
public sealed record Finding(
    Severity Severity,
    string Title,
    string Where,
    string Evidence,
    string Remediation,
    string? Reference = null,
    string Source = "recon");

/// <summary>Etichette e conversioni per la gravità.</summary>
public static class Severities
{
    /// <summary>In italiano, per il notch e il report.</summary>
    public static string Label(Severity severity) => severity switch
    {
        Severity.Critical => "CRITICA",
        Severity.High => "ALTA",
        Severity.Medium => "MEDIA",
        Severity.Low => "BASSA",
        _ => "INFO",
    };

    /// <summary>Dalla parola usata da nuclei/ZAP (o altri) alla gravità; sconosciuta = Info.</summary>
    public static Severity FromText(string? text) => (text ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "critical" or "critico" or "critica" => Severity.Critical,
        "high" or "alto" or "alta" => Severity.High,
        "medium" or "moderate" or "medio" or "media" => Severity.Medium,
        "low" or "basso" or "bassa" => Severity.Low,
        _ => Severity.Info,
    };

    /// <summary>Il codice di rischio di ZAP (0 info … 3 alto) in gravità.</summary>
    public static Severity FromZapRisk(int riskCode) => riskCode switch
    {
        >= 3 => Severity.High,
        2 => Severity.Medium,
        1 => Severity.Low,
        _ => Severity.Info,
    };
}
