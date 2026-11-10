using System.IO;
using System.Net.Http;
using System.Windows;
using NothingShell.App.Notch;
using NothingShell.App.Services;
using NothingShell.App.Settings;
using NothingShell.Core.Notch;
using NothingShell.Core.PortalDebug;

namespace NothingShell.App;

/// <summary><c>--debug-snapshot</c>: la dashboard del Debug potenziato con un portale finto, una scheda per vista, e l'avviso nel notch.</summary>
public partial class App
{
    private static async Task RenderDebugSnapshots(string folder)
    {
        Directory.CreateDirectory(folder);
        using var http = new HttpClient();
        var portal = new DebugPortal("sinapsi-web", "sinapsi_web", "http://127.0.0.1:8000");
        using var link = new PortalLink(portal, http, () => DebugAlertRules.Default);
        var now = DateTimeOffset.Now;
        var t = (double secondsAgo) => now.AddSeconds(-secondsAgo);
        var seq = 0L;
        var events = new List<DebugEvent>();
        void Add(DebugEvent e) => events.Add(e);
        var headers = new Dictionary<string, string> { ["Accept"] = "text/html", ["Cookie"] = "[nascosto]", ["User-Agent"] = "Mozilla/5.0" };
        for (var i = 0; i < 40; i++)
            Add(new DebugRequest(++seq, t(280 - (i * 6)), $"r{i}", i % 5 == 0 ? "POST" : "GET", i % 3 == 0 ? $"/ordini/{100 + i}/" : i % 3 == 1 ? "/clienti/" : "/api/v1/listino/",
                200, 40 + (i * 7 % 90), 4200, null, "core.views.lista", "andrea", "127.0.0.1", 3 + (i % 4), 6.5, headers));
        Add(new DebugSql(++seq, t(40), "r-slow", "SELECT \"core_riga\".\"id\", \"core_riga\".\"ordine_id\", \"core_riga\".\"prezzo\" FROM \"core_riga\" WHERE \"core_riga\".\"ordine_id\" = 12", 312));
        Add(new DebugRequest(++seq, t(40), "r-slow", "GET", "/report/vendite/", 200, 3820, 98000, "anno=2026", "report.views.vendite", "andrea", "127.0.0.1", 148, 2900, headers));
        Add(new DebugLogLine(++seq, t(25), "r-err", "WARNING", "core.ordini", "Ordine 12 senza cliente: uso quello di default"));
        Add(new DebugCrash(++seq, t(25), "r-err", "KeyError", "'cliente_id'",
        [
            new DebugFrame(@"C:\dev\python_prg\sinapsi_web\venv\Lib\site-packages\django\core\handlers\exception.py", 55, "inner", "response = get_response(request)", false),
            new DebugFrame(@"C:\dev\python_prg\sinapsi_web\venv\Lib\site-packages\django\core\handlers\base.py", 197, "_get_response", "response = wrapped_callback(request, *callback_args, **callback_kwargs)", false),
            new DebugFrame(@"C:\dev\python_prg\sinapsi_web\sinapsi\core\views.py", 88, "ordine_dettaglio", "totale = calcola_totale(ordine, dati)", true),
            new DebugFrame(@"C:\dev\python_prg\sinapsi_web\sinapsi\core\prezzi.py", 42, "calcola_totale", "cliente = dati[\"cliente_id\"]", true),
        ], "GET", "/ordini/12/"));
        Add(new DebugRequest(++seq, t(25), "r-err", "GET", "/ordini/12/", 500, 182, 51200, null, "core.views.ordine_dettaglio", "andrea", "127.0.0.1", 12, 40, headers));
        Add(new DebugCustom(++seq, t(12), null, "ordine creato", new Dictionary<string, string> { ["id"] = "131", ["totale"] = "249.90" }));
        Add(new DebugRequest(++seq, t(8), "r-404", "GET", "/static/logo-vecchio.png", 404, 3, 2300));
        Add(new DebugLogLine(++seq, t(4), null, "INFO", "core.sync", "Sincronizzazione listino: 312 articoli aggiornati"));
        link.Log.Add(new DebugPage("demo", seq, events));
        link.Watch.OnReach(PortalReach.Online, DebugAlertRules.Default, now);
        link.Status = new PortalStatus(new PortalInfo("sinapsi_web", "Django 5.2.7 · Python 3.12.4", "1", null, true, 5400, 18244, @"C:\dev\python_prg\sinapsi_web\sinapsi"),
        [
            new PortalCheck("Database default", "4 ms", CheckState.Ok, "postgresql"),
            new PortalCheck("Migrazioni", "2 da applicare", CheckState.Warning, "core.0042_riga_sconto, core.0043_cliente_note"),
            new PortalCheck("Cache", "1 ms", CheckState.Ok),
            new PortalCheck("Coda email", "64", CheckState.Warning, "oltre 50 da inviare"),
        ]);

        using var service = new PortalDebugService();
        var crash = events.OfType<DebugCrash>().First();
        var window = new DebugDashboardWindow(link, service, null);
        window.Present(crash);
        window.Hide();
        SaveSnapshot(window.PrepareSnapshot(), Path.Combine(folder, "debug-dashboard-errore.png"), scale: 1);
        window.Present(events.OfType<DebugRequest>().First(r => r.RequestId == "r-slow"));
        window.Hide();
        SaveSnapshot(window.PrepareSnapshot(), Path.Combine(folder, "debug-dashboard-richiesta.png"), scale: 1);
        window.Close();

        using var media = new MediaService();
        var notch = new NotchWindow(media, DevPageActions.None, ConnectedPageActions.None);
        var alert = new DebugWatch().OnEvents([crash], DebugAlertRules.Default, now)[0];
        notch.ShowCard(NotchCards.DebugAlert(portal.Name, alert, () => { }, () => { }, () => { }), TimeSpan.FromSeconds(8));
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Card, NotchPage.Home), Path.Combine(folder, "debug-notch-errore.png"));

        // pagina Debug del notch: il portale con le cose da guardare, uno giù, poi senza portali
        notch.DismissCard();
        var rules = DebugAlertRules.Default;
        notch.SetDebug(
        [
            new DebugPortalView(portal.Id, portal.Name, PortalReach.Online, null, false,
                DebugHighlights.Summary(link.Log, PortalReach.Online, null, false, now), DebugHighlights.For(link.Log, link.Status, rules, now)),
            new DebugPortalView("elf", "py_elf_portal", PortalReach.Offline, "Non risponde", false, "Non risponde", []),
        ]);
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Expanded, NotchPage.Debug), Path.Combine(folder, "debug-notch-pagina.png"));
        notch.SetDebug([]);
        SaveSnapshot(notch.PrepareSnapshot(NotchState.Expanded, NotchPage.Debug), Path.Combine(folder, "debug-notch-vuota.png"));

        var settings = new SettingsWindow(new SnapshotSettings([]));
        var root = (FrameworkElement)settings.Content;
        root.Width = settings.Width;
        root.Height = 2400;
        settings.Show(SettingsPage.Debug);
        SaveSnapshot(root, Path.Combine(folder, "debug-settings.png"), scale: 1);
        await Task.CompletedTask;
    }

    /// <summary>
    /// <c>--debug-check indirizzo codice [file]</c>: collega il portale, legge eventi e controlli una volta, li scrive nel
    /// file (di serie debug-check.txt accanto all'eseguibile) e scollega. Per capire se il portale è pronto senza la shell avviata.
    /// </summary>
    private static async Task DebugCheck(string address, string code, string? output)
    {
        var report = new System.Text.StringBuilder();
        using var service = new PortalDebugService();
        var (portal, error) = await service.PairAsync(address, code, DebugSettings.Default);
        if (portal is null)
        {
            report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Collegamento non riuscito: {error}");
        }
        else
        {
            report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Collegato: {portal.Name} ({portal.Address})");
            service.Apply(DebugSettings.Default.WithPortal(portal));
            var link = service.Find(portal.Id)!;
            link.RefreshStatusSoon();
            for (var i = 0; i < 40 && (link.Status is null || link.Reach == PortalReach.Unknown); i++)
            {
                await link.PollAsync();
                await Task.Delay(250);
            }
            report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Stato: {link.Reach} {link.Problem}");
            report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Eventi: {link.Log.Count} · riavvii {link.Log.Restarts}");
            foreach (var e in link.Log.Events)
                report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  {e.Kind,-8} {e.RequestId,-12} {e.Summary.Split('\n')[0]}");
            foreach (var g in link.Log.ErrorGroups)
                report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Errore ×{g.Count}: {g.Fingerprint}");
            if (link.Status is { } status)
            {
                report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Portale: {status.Info.Framework} · pid {status.Info.Pid} · debug {status.Info.DebugMode}");
                foreach (var c in status.Checks)
                    report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  Controllo {c.Name}: {c.Value} ({c.State}) {c.Detail}");
            }
            var stats = link.Log.Stats(DateTimeOffset.Now, TimeSpan.FromMinutes(5));
            report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Statistiche: {stats.Requests} richieste · 5xx {stats.ServerErrors} · p95 {DebugWatch.FormatMs(stats.P95Ms)}");
            await service.UnpairAsync(portal.Id);
            report.AppendLine("Scollegato.");
        }
        File.WriteAllText(output ?? Path.Combine(AppContext.BaseDirectory, "debug-check.txt"), report.ToString());
    }
}
