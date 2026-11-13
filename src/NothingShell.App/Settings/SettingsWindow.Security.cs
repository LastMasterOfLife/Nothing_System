using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using NothingShell.App.Rendering;
using NothingShell.App.Services;

namespace NothingShell.App.Settings;

/// <summary>Sicurezza (docs/26-sicurezza.md): accendere la modalità, vedere gli scanner installati e lo storico dei report.</summary>
public sealed partial class SettingsWindow
{
    private void SecurityPage()
    {
        Section("MODALITÀ SICUREZZA");
        ToggleRow("Pagina Sicurezza nel notch",
            "Prova in modo autorizzato un sito tuo: ricognizione passiva e, se installati, nuclei e OWASP ZAP in modo non distruttivo. Si sposta come le altre in Impostazioni › Notch.",
            _shell.SecurityEnabled, v =>
            {
                _shell.SetSecurityEnabled(v);
                Rebuild();
            });
        Note("Parte solo contro l'host che dichiari tuo e resta dentro quell'host. Niente DoS, brute-force o furto di dati: " +
             "le prove dimostrano che il buco esiste, non lo sfruttano.", Theme.MutedBrush);

        Section("SCANNER");
        var (nuclei, zap) = _shell.SecurityToolsPresent();
        ValueRow("nuclei", "Template di vulnerabilità note (CVE, misconfig)", nuclei ? "installato" : "assente", group: false);
        ValueRow("OWASP ZAP", "Scansione baseline dell'app web", zap ? "installato" : "assente", group: false);
        Note(nuclei && zap
            ? "Scanner pronti: le sessioni li usano in automatico."
            : "Senza scanner esterni gira comunque la ricognizione passiva (intestazioni, TLS, cookie). Gli scanner installati dalla shell stanno in " + SecurityScanService.ToolsFolder + ".",
            Theme.MutedBrush);

        Section("STORICO");
        var reports = _shell.SecurityReports();
        if (reports.Count == 0)
        {
            Note("Ancora nessuna scansione. Apri la pagina Sicurezza nel notch, scrivi un sito tuo e conferma.", Theme.MutedBrush);
            return;
        }
        foreach (var report in reports)
            ReportRow(report);
        ButtonRow(("APRI CARTELLA", _shell.OpenSecurityFolder));
        ConfirmButtonRow("AZZERA STORICO", "AZZERA DAVVERO", () =>
        {
            _shell.ClearSecurityHistory();
            Rebuild();
        });
    }

    private void ReportRow(SecurityReportInfo report)
    {
        var row = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Label(report.Host, 14, Theme.InkBrush));
        var when = Label(report.At.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture), 12, Theme.MutedBrush);
        when.Margin = new Thickness(0, 3, 0, 0);
        text.Children.Add(when);

        var open = Button("APRI", primary: false);
        open.VerticalAlignment = VerticalAlignment.Center;
        open.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _shell.OpenSecurityReportFile(report.HtmlPath);
        };
        Grid.SetColumn(open, 1);

        row.Children.Add(text);
        row.Children.Add(open);
        AddRow(row);
    }
}
