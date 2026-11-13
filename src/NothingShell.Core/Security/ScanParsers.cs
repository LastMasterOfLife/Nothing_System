using System.Text;
using System.Text.Json;

namespace NothingShell.Core.Security;

/// <summary>
/// Trasforma l'uscita degli scanner esterni in <see cref="Finding"/> (docs/26-sicurezza.md). I parser sono puri e testati
/// con campioni; l'App si limita a lanciare lo strumento e passare qui il testo. Righe o campi mancanti non fanno saltare
/// nulla: si prende quello che c'è.
/// </summary>
public static class NucleiParser
{
    /// <summary>L'uscita di <c>nuclei -jsonl</c>: un oggetto JSON per riga. Righe vuote o non valide vengono ignorate.</summary>
    public static IReadOnlyList<Finding> Parse(string? jsonl)
    {
        var findings = new List<Finding>();
        if (string.IsNullOrWhiteSpace(jsonl))
            return findings;
        foreach (var line in jsonl.Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0 || text[0] != '{')
                continue;
            Finding? finding = null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                finding = FromElement(doc.RootElement);
            }
            catch (JsonException)
            {
                // riga non JSON: capita con gli avvisi su stderr mescolati, si salta
            }
            if (finding is not null)
                findings.Add(finding);
        }
        return findings;
    }

    private static Finding? FromElement(JsonElement root)
    {
        var info = root.TryGetProperty("info", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
        var name = Str(info, "name") ?? Str(root, "template-id") ?? "Rilevazione nuclei";
        var severity = Severities.FromText(Str(info, "severity"));
        var where = Str(root, "matched-at") ?? Str(root, "host") ?? Str(root, "matched") ?? string.Empty;
        var templateId = Str(root, "template-id");
        var description = Str(info, "description");
        var remediation = Str(info, "remediation") ?? "Vedi il riferimento del template e correggi la configurazione o aggiorna il componente.";
        var reference = FirstString(info, "reference") ?? templateId;
        var evidence = Str(root, "extracted-results") is { Length: > 0 } ex ? ex
            : description is { Length: > 0 } ? description
            : $"Template «{templateId}» ha dato riscontro.";
        return new Finding(severity, name, where, Trim(evidence, 300), Trim(remediation, 400), reference, Source: "nuclei");
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? FirstString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Array when v.GetArrayLength() > 0 && v[0].ValueKind == JsonValueKind.String => v[0].GetString(),
            _ => null,
        };
    }

    internal static string Trim(string text, int max)
    {
        var s = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}

/// <summary>Il report JSON di OWASP ZAP (<c>-J</c>/baseline): <c>site[].alerts[]</c> con rischio, descrizione, soluzione e istanze.</summary>
public static class ZapParser
{
    public static IReadOnlyList<Finding> Parse(string? json)
    {
        var findings = new List<Finding>();
        if (string.IsNullOrWhiteSpace(json))
            return findings;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return findings;
        }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("site", out var sites) || sites.ValueKind != JsonValueKind.Array)
                return findings;
            foreach (var site in sites.EnumerateArray())
            {
                if (!site.TryGetProperty("alerts", out var alerts) || alerts.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var alert in alerts.EnumerateArray())
                    findings.Add(FromAlert(alert, Str(site, "@name") ?? Str(site, "name") ?? string.Empty));
            }
        }
        return findings;
    }

    private static Finding FromAlert(JsonElement alert, string siteName)
    {
        var title = Str(alert, "alert") ?? Str(alert, "name") ?? "Avviso ZAP";
        var severity = Severities.FromZapRisk(Int(alert, "riskcode"));
        var where = siteName;
        var evidence = NoHtml(Str(alert, "desc") ?? string.Empty);
        if (alert.TryGetProperty("instances", out var instances) && instances.ValueKind == JsonValueKind.Array && instances.GetArrayLength() > 0)
        {
            var first = instances[0];
            where = Str(first, "uri") ?? siteName;
            if (Str(first, "evidence") is { Length: > 0 } ev)
                evidence = NoHtml(ev);
        }
        var remediation = NoHtml(Str(alert, "solution") ?? "Vedi la soluzione consigliata da ZAP.");
        var cwe = Str(alert, "cweid");
        var reference = cwe is { Length: > 0 } && cwe != "-1" ? $"CWE-{cwe}" : NoHtml(Str(alert, "reference") ?? string.Empty) is { Length: > 0 } r ? NucleiParser.Trim(r, 120) : null;
        return new Finding(severity, title, where, NucleiParser.Trim(evidence, 300), NucleiParser.Trim(remediation, 400), reference, Source: "zap");
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int Int(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var v))
            return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
            _ => 0,
        };
    }

    /// <summary>Via i tag HTML dai testi di ZAP (desc, solution): restano leggibili nel report in testo.</summary>
    internal static string NoHtml(string html)
    {
        if (html.IndexOf('<') < 0)
            return html.Trim();
        var sb = new StringBuilder(html.Length);
        var inside = false;
        foreach (var c in html)
        {
            if (c == '<')
                inside = true;
            else if (c == '>')
                inside = false;
            else if (!inside)
                sb.Append(c);
        }
        return sb.ToString().Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&").Trim();
    }
}
