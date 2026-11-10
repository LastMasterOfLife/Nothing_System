using System.Globalization;
using System.Windows;
using NothingShell.App.Rendering;
using NothingShell.Core.PortalDebug;

namespace NothingShell.App.Settings;

/// <summary>
/// Debug potenziato (docs/24-debug-potenziato.md): i portali collegati con un codice, la dashboard di ognuno e quando il
/// notch avvisa da solo.
/// </summary>
public sealed partial class SettingsWindow
{
    private void DebugPage()
    {
        var settings = _shell.DebugSettings;
        Section("DEBUG POTENZIATO");
        ToggleRow("Guarda i portali in sviluppo", "Richieste, log, errori, query e controlli dal vivo; avvisi nel notch quando qualcosa si rompe",
            settings.Enabled, v =>
            {
                _shell.SetDebugEnabled(v);
                Rebuild();
            });
        if (!settings.Enabled)
            return;

        var links = _shell.DebugLinks;
        Section("PORTALI COLLEGATI", links.Count > 0 ? links.Count.ToString(CultureInfo.InvariantCulture) : null);
        if (links.Count == 0)
            Note("Nessun portale collegato. Prepara il portale e collegalo con il codice qui sotto.", Theme.MutedBrush);
        foreach (var link in links)
        {
            var portal = link.Portal;
            ActionRow(portal.Name, $"{portal.Address} · {DebugReach(link)}",
            [
                ("DASHBOARD", () => _shell.OpenDebugDashboard(portal.Id)),
                ("SCOLLEGA", async () =>
                {
                    await _shell.UnpairDebugPortalAsync(portal.Id);
                    Rebuild();
                }),
            ]);
            ToggleRow("Avvisi nel notch", $"Per {portal.Name}: spento, la dashboard continua a raccogliere", portal.Watch,
                v => _shell.SetDebugPortalWatch(portal.Id, v));
        }

        Section("COLLEGA UN PORTALE");
        Step(1, "Aggiungi nothing_debug al portale (una volta sola)", done: null);
        Note("È un'app Django che raccoglie richieste, log, errori e query e li dà solo alla shell collegata. Si accende " +
             "da sola con DEBUG = True; in produzione resta spenta. Installala nel venv del portale:", Theme.MutedBrush);
        var install = $"pip install -e \"{_shell.DebugSdkFolder}\"";
        Code(install);
        Note("Poi in settings.py (il middleware va per primo, così misura anche gli altri):", Theme.MutedBrush);
        const string snippet = "INSTALLED_APPS += [\"nothing_debug\"]\nMIDDLEWARE = [\"nothing_debug.middleware.NothingDebugMiddleware\", *MIDDLEWARE]";
        Code(snippet);
        ButtonRow(("COPIA INSTALLAZIONE", () => Clipboard.SetDataObject(install, copy: true)),
            ("COPIA SETTINGS", () => Clipboard.SetDataObject(snippet, copy: true)),
            ("APRI CARTELLA", () => OpenFolder(_shell.DebugSdkFolder)));

        Step(2, "Avvia il portale e prendi il codice", done: null);
        Note("python manage.py runserver stampa all'avvio «Nothing Debug · codice di collegamento K7Q2-9XMP». Vale 10 " +
             "minuti e una volta sola. Se non lo vedi: apri http://127.0.0.1:8000/_nothing/ dal PC del portale, oppure " +
             "python manage.py nothing_code.", Theme.MutedBrush);

        Step(3, "Collega", done: links.Count > 0 ? true : null);
        var address = Field("Indirizzo del portale (anche su un altro PC: http://192.168.1.20:8000)", "http://127.0.0.1:8000");
        var code = Field("Codice di collegamento", string.Empty);
        var result = Note(string.Empty, Theme.MutedBrush);
        var busy = false;
        ButtonRow(primary: true, ("COLLEGA", async () =>
        {
            if (busy)
                return;
            busy = true;
            result.Foreground = Theme.MutedBrush;
            result.Text = "Collegamento in corso…";
            var error = await _shell.PairDebugPortalAsync(address.Text, code.Text);
            busy = false;
            if (error is null)
            {
                Rebuild();
                return;
            }
            result.Foreground = Theme.CriticalBrush;
            result.Text = error;
        }));

        DebugRulesSection(settings.AlertRules);

        Section("I TUOI DATI");
        Note("La shell chiede al portale, il portale non chiama mai la shell: nessuna porta aperta sul PC e nessun servizio " +
             "esterno. Il token sta nel Gestore credenziali di Windows; nel portale solo il suo hash, in ~/.nothing_debug. " +
             "Cookie, intestazioni Authorization e parametri come password e token arrivano già nascosti; il corpo delle " +
             "richieste solo con NOTHING_DEBUG = {\"BODY\": True}.", Theme.MutedBrush);
    }

    private void DebugRulesSection(DebugAlertRules rules)
    {
        Section("ATTENZIONE NEL NOTCH");
        Note("Il notch avvisa da solo, con la scheda che apre la dashboard sull'evento. Ogni avviso si ripete al massimo ogni " +
             $"{rules.Cooldown.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} minuti; gli errori nuovi hanno sempre il loro.", Theme.MutedBrush);
        ToggleRow("Errori nuovi", "Un'eccezione mai vista (stesso tipo nello stesso punto del codice conta una volta)", rules.NewErrors,
            v => _shell.SetDebugRules(_shell.DebugSettings.AlertRules with { NewErrors = v }));
        ToggleRow("Risposte 5xx", "Anche senza eccezione (un 502 del proxy, un 503 voluto)", rules.ServerErrors,
            v => _shell.SetDebugRules(_shell.DebugSettings.AlertRules with { ServerErrors = v }));
        ToggleRow("Richieste lente", $"Oltre {DebugWatch.FormatMs(rules.SlowMs)}", rules.SlowRequests,
            v => _shell.SetDebugRules(_shell.DebugSettings.AlertRules with { SlowRequests = v }));
        ToggleRow("Portale giù e di nuovo su", $"Dopo {DebugWatch.DownAfter.ToString(CultureInfo.InvariantCulture)} tentativi senza risposta", rules.Down,
            v => _shell.SetDebugRules(_shell.DebugSettings.AlertRules with { Down = v }));
        ToggleRow("Controlli del portale", "Database, migrazioni, cache e i controlli scritti con @nothing_debug.check, quando peggiorano", rules.Checks,
            v => _shell.SetDebugRules(_shell.DebugSettings.AlertRules with { Checks = v }));
        ToggleRow("Log ERROR", "Righe di log da ERROR in su che non vengono da un'eccezione", rules.ErrorLogs,
            v => _shell.SetDebugRules(_shell.DebugSettings.AlertRules with { ErrorLogs = v }));
        var slow = Field("Una richiesta è lenta oltre (millisecondi)", rules.SlowMs.ToString(CultureInfo.InvariantCulture));
        var cooldown = Field("Lo stesso avviso al massimo ogni (secondi)", rules.CooldownSeconds.ToString(CultureInfo.InvariantCulture));
        slow.LostFocus += (_, _) =>
        {
            if (int.TryParse(slow.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) && ms is >= 50 and <= 600000 &&
                ms != _shell.DebugSettings.AlertRules.SlowMs)
            {
                _shell.SetDebugRules(_shell.DebugSettings.AlertRules with { SlowMs = ms });
                Rebuild();
            }
        };
        cooldown.LostFocus += (_, _) =>
        {
            if (int.TryParse(cooldown.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s is >= 10 and <= 86400 &&
                s != _shell.DebugSettings.AlertRules.CooldownSeconds)
            {
                _shell.SetDebugRules(_shell.DebugSettings.AlertRules with { CooldownSeconds = s });
                Rebuild();
            }
        };
    }

    private static string DebugReach(NothingShell.App.Services.PortalLink link) => link.Paused ? "in pausa" : link.Reach switch
    {
        PortalReach.Online => $"collegato · {link.Log.Count.ToString(CultureInfo.InvariantCulture)} eventi",
        PortalReach.Unknown => "collegamento…",
        PortalReach.Unauthorized => link.Problem ?? "da ricollegare",
        _ => link.Problem ?? "non risponde",
    };

    private void OnDebugChanged() => Dispatcher.BeginInvoke(() =>
    {
        // si ridisegna solo se non si sta scrivendo (indirizzo, codice, soglie)
        if (_selected == SettingsPage.Debug && !IsKeyboardFocusWithin)
            Rebuild();
    });
}
