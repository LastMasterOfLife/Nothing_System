using System.Globalization;
using System.Text;

namespace NothingShell.Core.Security;

/// <summary>
/// Il resoconto di fine sessione (docs/26-sicurezza.md), in Markdown: cosa è stato provato, azione per azione, e ogni
/// debolezza con dove, prova e come si risolve. Puro e testato. È pensato per essere letto e per sistemare i problemi,
/// non per rifare l'attacco: niente credenziali o dati estratti, solo la prova che il buco c'è.
/// </summary>
public static class SecurityReport
{
    public static string ToMarkdown(SecuritySnapshot snapshot, IReadOnlyList<ScanTool>? tools = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var sb = new StringBuilder();
        var target = snapshot.Target?.Display ?? "(nessun bersaglio)";
        sb.Append("# Resoconto sicurezza — ").AppendLine(target);
        sb.AppendLine();
        sb.Append("- **Bersaglio**: ").AppendLine(target);
        sb.Append("- **Esito sessione**: ").AppendLine(StatusText(snapshot.Status));
        if (snapshot.StartedAt is { } start)
            sb.Append("- **Inizio**: ").AppendLine(start.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        if (snapshot.Duration is { } d)
            sb.Append("- **Durata**: ").AppendLine(FormatElapsed(d));
        if (snapshot.Error is { Length: > 0 } err)
            sb.Append("- **Errore**: ").AppendLine(err);
        sb.AppendLine("- **Autorizzazione**: eseguito solo contro l'host dichiarato dall'utente, in modalità non distruttiva.");
        sb.AppendLine();

        // Riepilogo per gravità.
        sb.AppendLine("## Riepilogo");
        sb.AppendLine();
        sb.Append("Debolezze trovate: **").Append(snapshot.Findings.Count).AppendLine("**.");
        sb.AppendLine();
        sb.AppendLine("| Gravità | Numero |");
        sb.AppendLine("|---|---|");
        foreach (var severity in new[] { Severity.Critical, Severity.High, Severity.Medium, Severity.Low, Severity.Info })
            sb.Append("| ").Append(Severities.Label(severity)).Append(" | ").Append(snapshot.Count(severity)).AppendLine(" |");
        sb.AppendLine();

        // Strumenti usati.
        if (tools is { Count: > 0 })
        {
            sb.AppendLine("## Strumenti");
            sb.AppendLine();
            foreach (var tool in tools)
                sb.Append("- **").Append(tool.Name).Append("**: ")
                    .Append(tool.Installed ? "presente" : "non installato")
                    .Append(tool.Version is { Length: > 0 } v ? $" ({v})" : string.Empty)
                    .Append(" — ").AppendLine(tool.Why);
            sb.AppendLine();
        }

        // Azione per azione.
        sb.AppendLine("## Azione per azione");
        sb.AppendLine();
        if (snapshot.Actions.Count == 0)
        {
            sb.AppendLine("Nessuna azione registrata.");
        }
        else
        {
            var n = 1;
            foreach (var action in snapshot.Actions)
            {
                sb.Append(n++).Append(". **").Append(action.Label).Append("** — ").Append(StepText(action.Status));
                TimeSpan? span = action.Started is { } s && action.Ended is { } e ? e - s : null;
                if (span is { } sp)
                    sb.Append(" · ").Append(FormatElapsed(sp));
                if (action.Findings > 0)
                    sb.Append(" · ").Append(action.Findings).Append(action.Findings == 1 ? " debolezza" : " debolezze");
                if (action.Detail is { Length: > 0 } detail)
                    sb.Append(" · ").Append(detail);
                sb.AppendLine();
            }
        }
        sb.AppendLine();

        // Debolezze in dettaglio.
        sb.AppendLine("## Debolezze trovate");
        sb.AppendLine();
        if (snapshot.Findings.Count == 0)
        {
            sb.AppendLine("Nessuna debolezza trovata dalle fasi eseguite.");
        }
        else
        {
            foreach (var finding in snapshot.Ordered)
            {
                sb.Append("### [").Append(Severities.Label(finding.Severity)).Append("] ").AppendLine(finding.Title);
                sb.AppendLine();
                if (finding.Where is { Length: > 0 })
                    sb.Append("- **Dove**: ").AppendLine(finding.Where);
                if (finding.Evidence is { Length: > 0 })
                    sb.Append("- **Prova**: ").AppendLine(finding.Evidence);
                sb.Append("- **Come si risolve**: ").AppendLine(finding.Remediation);
                if (finding.Reference is { Length: > 0 })
                    sb.Append("- **Riferimento**: ").AppendLine(finding.Reference);
                sb.Append("- **Fonte**: ").AppendLine(finding.Source);
                sb.AppendLine();
            }
        }

        sb.AppendLine("---");
        sb.AppendLine("Prodotto dalla Modalità Sicurezza di Nothing Shell. Scansione autorizzata e non distruttiva: nessun dato è stato estratto o modificato; le prove dimostrano solo l'esistenza del problema.");
        return sb.ToString();
    }

    /// <summary>mm:ss, oppure h:mm:ss oltre l'ora.</summary>
    public static string FormatElapsed(TimeSpan span)
    {
        var total = (int)Math.Max(0, Math.Round(span.TotalSeconds));
        var hours = total / 3600;
        var minutes = total / 60 % 60;
        var seconds = total % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{seconds:00}" : $"{minutes:00}:{seconds:00}";
    }

    private static string StatusText(SessionStatus status) => status switch
    {
        SessionStatus.Completed => "completata",
        SessionStatus.Aborted => "interrotta dall'utente",
        SessionStatus.Failed => "interrotta da un errore",
        SessionStatus.Running => "in corso",
        SessionStatus.NeedsAuthorization => "in attesa di autorizzazione",
        _ => "inattiva",
    };

    private static string StepText(StepStatus status) => status switch
    {
        StepStatus.Done => "fatta",
        StepStatus.Skipped => "saltata",
        StepStatus.Error => "errore",
        StepStatus.Running => "in corso",
        _ => "in attesa",
    };

    /// <summary>
    /// Lo stesso resoconto in HTML autosufficiente (tema scuro, colori per gravità): si apre nel browser, che su Windows è
    /// sempre associato — a differenza dei .md. È anche la pagina che lo storico apre.
    /// </summary>
    public static string ToHtml(SecuritySnapshot snapshot, IReadOnlyList<ScanTool>? tools = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var target = snapshot.Target?.Display ?? "(nessun bersaglio)";
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html lang=\"it\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>Sicurezza — ").Append(Esc(target)).AppendLine("</title>");
        sb.AppendLine("<style>" +
            ":root{color-scheme:dark}" +
            "body{margin:0;background:#0b0b0d;color:#e8e8ea;font:15px/1.55 ui-sans-serif,system-ui,Segoe UI,Roboto,sans-serif;padding:32px}" +
            ".wrap{max-width:860px;margin:0 auto}" +
            "h1{font-size:22px;margin:0 0 4px}h2{font-size:17px;margin:28px 0 10px;border-bottom:1px solid #26262b;padding-bottom:6px}" +
            "a{color:#9db7ff}.muted{color:#9a9aa2}" +
            "table{border-collapse:collapse;margin:8px 0}td,th{padding:4px 14px 4px 0;text-align:left}" +
            ".pill{display:inline-block;border-radius:4px;padding:1px 8px;font-size:12px;font-weight:600;color:#0b0b0d}" +
            ".f{border:1px solid #26262b;border-radius:8px;padding:12px 14px;margin:10px 0}" +
            ".f .t{font-weight:600;font-size:16px}.f .k{color:#9a9aa2;margin-right:6px}" +
            ".crit{background:#ff5a5f}.high{background:#ff7a45}.med{background:#e6a23c}.low{background:#5aa9ff}.info{background:#8a8a92}" +
            "code{background:#1a1a1f;border-radius:4px;padding:1px 5px}" +
            "</style></head><body><div class=\"wrap\">");

        sb.Append("<h1>Resoconto sicurezza</h1>");
        sb.Append("<div class=\"muted\">").Append(Esc(target)).AppendLine("</div>");
        sb.Append("<p>Esito: <b>").Append(Esc(StatusText(snapshot.Status))).Append("</b>");
        if (snapshot.StartedAt is { } start)
            sb.Append(" · ").Append(Esc(start.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
        if (snapshot.Duration is { } d)
            sb.Append(" · durata ").Append(Esc(FormatElapsed(d)));
        sb.AppendLine("</p>");
        if (snapshot.Error is { Length: > 0 } err)
            sb.Append("<p class=\"muted\">Errore: ").Append(Esc(err)).AppendLine("</p>");

        sb.AppendLine("<h2>Riepilogo</h2>");
        sb.Append("<p>Debolezze trovate: <b>").Append(snapshot.Findings.Count).AppendLine("</b></p><p>");
        foreach (var severity in new[] { Severity.Critical, Severity.High, Severity.Medium, Severity.Low, Severity.Info })
            sb.Append("<span class=\"pill ").Append(CssClass(severity)).Append("\">").Append(Severities.Label(severity))
                .Append(' ').Append(snapshot.Count(severity)).Append("</span> ");
        sb.AppendLine("</p>");

        if (tools is { Count: > 0 })
        {
            sb.AppendLine("<h2>Strumenti</h2><table>");
            foreach (var tool in tools)
                sb.Append("<tr><td><b>").Append(Esc(tool.Name)).Append("</b></td><td>")
                    .Append(tool.Installed ? "presente" : "non installato")
                    .Append(tool.Version is { Length: > 0 } v ? " (" + Esc(v) + ")" : string.Empty)
                    .Append("</td><td class=\"muted\">").Append(Esc(tool.Why)).AppendLine("</td></tr>");
            sb.AppendLine("</table>");
        }

        sb.AppendLine("<h2>Azione per azione</h2><ol>");
        foreach (var action in snapshot.Actions)
        {
            sb.Append("<li><b>").Append(Esc(action.Label)).Append("</b> — ").Append(Esc(StepText(action.Status)));
            if (action.Started is { } s && action.Ended is { } e)
                sb.Append(" · ").Append(Esc(FormatElapsed(e - s)));
            if (action.Findings > 0)
                sb.Append(" · ").Append(action.Findings).Append(action.Findings == 1 ? " debolezza" : " debolezze");
            if (action.Detail is { Length: > 0 } detail)
                sb.Append(" · <span class=\"muted\">").Append(Esc(detail)).Append("</span>");
            sb.AppendLine("</li>");
        }
        sb.AppendLine("</ol>");

        sb.AppendLine("<h2>Debolezze trovate</h2>");
        if (snapshot.Findings.Count == 0)
        {
            sb.AppendLine("<p class=\"muted\">Nessuna debolezza trovata dalle fasi eseguite.</p>");
        }
        else
        {
            foreach (var f in snapshot.Ordered)
            {
                sb.Append("<div class=\"f\"><div class=\"t\"><span class=\"pill ").Append(CssClass(f.Severity)).Append("\">")
                    .Append(Severities.Label(f.Severity)).Append("</span> ").Append(Esc(f.Title)).AppendLine("</div>");
                if (f.Where is { Length: > 0 })
                    sb.Append("<div><span class=\"k\">Dove</span><code>").Append(Esc(f.Where)).AppendLine("</code></div>");
                if (f.Evidence is { Length: > 0 })
                    sb.Append("<div><span class=\"k\">Prova</span>").Append(Esc(f.Evidence)).AppendLine("</div>");
                sb.Append("<div><span class=\"k\">Come si risolve</span>").Append(Esc(f.Remediation)).AppendLine("</div>");
                if (f.Reference is { Length: > 0 })
                    sb.Append("<div class=\"muted\"><span class=\"k\">Riferimento</span>").Append(Esc(f.Reference)).Append(" · ").Append(Esc(f.Source)).AppendLine("</div>");
                sb.AppendLine("</div>");
            }
        }

        sb.AppendLine("<p class=\"muted\">Modalità Sicurezza di Nothing Shell · scansione autorizzata e non distruttiva: nessun dato estratto o modificato.</p>");
        sb.AppendLine("</div></body></html>");
        return sb.ToString();
    }

    private static string CssClass(Severity severity) => severity switch
    {
        Severity.Critical => "crit",
        Severity.High => "high",
        Severity.Medium => "med",
        Severity.Low => "low",
        _ => "info",
    };

    private static string Esc(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}

/// <summary>
/// Nome del file di un report: <c>host-yyyyMMdd-HHmmss</c>. Puro e testato, così lo storico può elencare i report dalla
/// cartella e ricavarne bersaglio e data dal nome, senza un indice a parte.
/// </summary>
public static class SecurityReportName
{
    public static string Stem(string host, DateTimeOffset at) =>
        $"{Safe(host)}-{at.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";

    public static string Safe(string host) =>
        new((host ?? string.Empty).Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray());

    /// <summary>Da un nome file senza estensione: host e istante, o null se non è nel formato giusto.</summary>
    public static (string Host, DateTime At)? Parse(string? fileNameWithoutExtension)
    {
        var parts = (fileNameWithoutExtension ?? string.Empty).Split('-');
        if (parts.Length < 3)
            return null;
        var date = parts[^2];
        var time = parts[^1];
        if (date.Length != 8 || time.Length != 6
            || !DateTime.TryParseExact(date + time, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            return null;
        return (string.Join('-', parts[..^2]), at);
    }
}
