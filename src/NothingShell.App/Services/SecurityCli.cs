using System.IO;
using NothingShell.Core.Security;

namespace NothingShell.App.Services;

/// <summary>
/// I due comandi headless della Modalità Sicurezza (docs/26-sicurezza.md), per provarla senza aprire la shell:
/// <c>--security-tools</c> dice quali scanner sono installati, <c>--security-scan &lt;url&gt; &lt;report&gt; --confirm</c> esegue una
/// sessione contro un sito <b>proprio</b> e scrive il resoconto. Il cancello resta esplicito anche da riga di comando: senza
/// <c>--confirm</c> (cioè senza dichiarare di essere autorizzati su quell'host) non parte nulla.
/// </summary>
public static class SecurityCli
{
    public static async Task<IReadOnlyList<string>> ToolsAsync()
    {
        var tools = await SecurityScanService.DetectToolsAsync(CancellationToken.None);
        var lines = new List<string> { "Strumenti della Modalità Sicurezza:" };
        foreach (var t in tools)
            lines.Add($"- {t.Name}: {(t.Installed ? "presente" + (t.Version is { Length: > 0 } v ? $" ({v})" : string.Empty) : "NON installato")} — {t.Why}");
        lines.Add(string.Empty);
        lines.Add("La ricognizione passiva è sempre disponibile (non serve installare nulla).");
        return lines;
    }

    /// <returns>0 se la sessione è andata, 1 in caso di errore della sessione, 2 URL non valido, 3 autorizzazione mancante.</returns>
    public static async Task<int> ScanAsync(string url, string reportPath, bool confirm)
    {
        var target = ScopeGuard.Parse(url);
        if (target is null)
        {
            File.WriteAllText(reportPath, "# Modalità Sicurezza\n\nIndirizzo non valido: serve un URL http/https (es. https://miosito.it).\n");
            return 2;
        }
        if (!confirm)
        {
            File.WriteAllText(reportPath,
                $"# Modalità Sicurezza — autorizzazione mancante\n\nIl bersaglio «{target.Display}» non è stato autorizzato.\n\n" +
                "Questa modalità esegue richieste d'attacco (non distruttive) e va usata solo su un sito tuo o per cui hai il permesso scritto.\n" +
                $"Per confermare ed eseguire:\n\n    NothingShell.exe --security-scan {target.Display} <report.md> --confirm\n");
            return 3;
        }

        var session = new SecuritySession();
        session.SetTarget(target);
        session.Authorize(new ScanAuthorization(target.Host, DateTimeOffset.Now));

        var folder = Path.GetDirectoryName(Path.GetFullPath(reportPath)) is { Length: > 0 } d ? d : SecurityScanService.DefaultFolder;
        var service = new SecurityScanService();
        var tools = await SecurityScanService.DetectToolsAsync(CancellationToken.None);
        var options = new ScanOptions { OutputFolder = folder };
        var snapshot = await service.RunAsync(session, options, _ => { }, CancellationToken.None);
        File.WriteAllText(reportPath, SecurityReport.ToMarkdown(snapshot, tools));
        return snapshot.Status == SessionStatus.Failed ? 1 : 0;
    }
}
