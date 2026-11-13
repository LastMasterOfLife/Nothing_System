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
}
