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

    private void OpenSecurityReport()
    {
        // Niente report scritto: apri almeno la cartella dei report.
        if (_securityReportPath is not { Length: > 0 } path || !File.Exists(path))
        {
            DevTools.OpenPath(SecurityScanService.DefaultFolder);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            // Nessuna app associata ai .md: mostra il file in Esplora risorse, così lo si apre da lì.
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
