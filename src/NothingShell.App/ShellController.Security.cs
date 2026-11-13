using System.Diagnostics;
using System.IO;
using NothingShell.App.Notch;
using NothingShell.App.Services;
using NothingShell.Core.Security;

namespace NothingShell.App;

/// <summary>
/// Modalità Sicurezza (docs/26-sicurezza.md): la pagina del notch chiede di provare un sito proprio, qui si guida la
/// sessione non distruttiva e si rimanda al notch l'istantanea dal vivo; alla fine la scheda di avviso e il report.
/// </summary>
public sealed partial class ShellController
{
    private readonly SecurityScanService _securityScan = new();
    private CancellationTokenSource? _securityStop;
    private string? _securityReportPath;

    private void StartSecurityScan(SecurityTarget target)
    {
        _securityStop?.Cancel();
        _securityStop?.Dispose();
        _securityStop = new CancellationTokenSource();
        var token = _securityStop.Token;

        // L'autorizzazione è la conferma esplicita fatta dall'utente nella pagina (il cancello della UI).
        var session = new SecuritySession();
        session.SetTarget(target);
        session.Authorize(new ScanAuthorization(target.Host, DateTimeOffset.Now));
        _securityReportPath = null;
        _notch.SetSecurity(session.Snapshot(), null);

        _ = Task.Run(async () =>
        {
            SecuritySnapshot snapshot;
            try
            {
                snapshot = await _securityScan.RunAsync(session, new ScanOptions(),
                    s => _notch.Dispatcher.BeginInvoke(() => _notch.SetSecurity(s, _securityScan.LastReportPath)), token);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                session.Fail(ex.Message, DateTimeOffset.Now);
                snapshot = session.Snapshot();
            }
            _securityReportPath = _securityScan.LastReportPath;
            _ = _notch.Dispatcher.BeginInvoke(() => OnSecurityFinished(snapshot));
        }, token);
    }

    private void StopSecurityScan() => _securityStop?.Cancel();

    private void OnSecurityFinished(SecuritySnapshot snapshot)
    {
        _notch.SetSecurity(snapshot, _securityReportPath); // istantanea finale: la pillola di sicurezza si spegne
        _notch.ShowCard(NotchCards.SecurityFinished(snapshot, OpenSecurityReport, _notch.DismissCard), CardDuration);
    }

    private void OpenSecurityReport() => OpenSecurityReportFile(_securityReportPath ?? string.Empty);

    // ── Impostazioni › SICUREZZA ───────────────────────────────────────────────

    /// <summary>La Modalità Sicurezza è accesa (la pagina compare nello swipe).</summary>
    public bool SecurityEnabled => _settings.Security;

    public void SetSecurityEnabled(bool enabled) => Update(_settings with { Security = enabled });

    /// <summary>Gli scanner installati (controllo sincrono per file, per la pagina impostazioni).</summary>
    public (bool Nuclei, bool Zap) SecurityToolsPresent() =>
        (SecurityScanService.QuickPresent("nuclei"), SecurityScanService.QuickPresent("zap"));

    /// <summary>Lo storico: i report nella cartella, dal più recente, con bersaglio e data letti dal nome.</summary>
    public IReadOnlyList<SecurityReportInfo> SecurityReports()
    {
        try
        {
            if (!Directory.Exists(SecurityScanService.DefaultFolder))
                return [];
            var list = new List<SecurityReportInfo>();
            foreach (var html in Directory.EnumerateFiles(SecurityScanService.DefaultFolder, "*.html"))
            {
                if (SecurityReportName.Parse(Path.GetFileNameWithoutExtension(html)) is not { } parsed)
                    continue;
                var md = Path.ChangeExtension(html, ".md");
                list.Add(new SecurityReportInfo(parsed.Host, parsed.At, html, File.Exists(md) ? md : null));
            }
            return list.OrderByDescending(r => r.At).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void OpenSecurityFolder()
    {
        try
        {
            Directory.CreateDirectory(SecurityScanService.DefaultFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        DevTools.OpenPath(SecurityScanService.DefaultFolder);
    }

    /// <summary>Cancella tutti i report dello storico (html e md).</summary>
    public void ClearSecurityHistory()
    {
        try
        {
            if (!Directory.Exists(SecurityScanService.DefaultFolder))
                return;
            foreach (var file in Directory.EnumerateFiles(SecurityScanService.DefaultFolder, "*.*")
                         .Where(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
                File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // qualche file in uso: pazienza, gli altri sono stati tolti
        }
    }

    /// <summary>Apri un report: il file (il browser apre gli .html); se non si può, mostralo in Esplora risorse.</summary>
    public void OpenSecurityReportFile(string path)
    {
        if (path is not { Length: > 0 } || !File.Exists(path))
        {
            OpenSecurityFolder();
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            try
            {
                DevTools.ShowInFolder(path);
            }
            catch (Exception ex2) when (ex2 is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                // pazienza: il file resta nella cartella dei report
            }
        }
    }
}

/// <summary>Una voce dello storico delle scansioni: bersaglio, quando, e i file del report.</summary>
public sealed record SecurityReportInfo(string Host, DateTime At, string HtmlPath, string? MarkdownPath);
