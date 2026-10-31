using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NothingShell.App.Rendering;
using NothingShell.Core.Connectors;
using NothingShell.Core.Widgets;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Notch;
using NothingShell.Core.Plugins;

namespace NothingShell.App.Settings;

/// <summary>Operazioni che la finestra delle impostazioni chiede alla shell (implementate da ShellController).</summary>
public interface IShellSettingsActions
{
    bool NotchEnabled { get; set; }

    bool BarEnabled { get; set; }

    bool UseWinKey { get; set; }

    bool WidgetsLocked { get; set; }

    bool MailInNotch { get; set; }

    bool AccessAnimations { get; set; }

    string NotificationAccessText { get; }

    bool CanRequestNotificationAccess { get; }

    bool NotificationAccessAllowed { get; }

    Task RequestNotificationAccessAsync();

    /// <summary>App proposte come "app di posta" e se lo sono già (<c>extraMailApps</c>).</summary>
    IReadOnlyList<(string App, bool IsMail)> MailAppCandidates { get; }

    void SetMailApp(string app, bool isMail);

    /// <summary>Ultima mail rilevata dalle notifiche; null se nessuna finora.</summary>
    MailItem? LastMail { get; }

    /// <summary>Avviso mail di prova nel notch, per verificare che si veda.</summary>
    void ShowTestMailAlert();

    /// <summary>Casella IMAP collegata (null se nessuna); la password è nel Gestore credenziali.</summary>
    MailAccount? MailAccount { get; }

    string MailAccountStatus { get; }

    bool MailAccountConnected { get; }

    event Action? MailAccountChanged;

    /// <summary>Prova l'accesso e, se riesce, collega la casella; altrimenti restituisce il motivo.</summary>
    Task<string?> ConnectMailAccountAsync(MailAccount account, string password);

    void DisconnectMailAccount();

    void ReconnectMailAccount();

    /// <summary>Webmail aperta dal lettore di posta: quella indicata o quella proposta dalla casella.</summary>
    string? WebmailAddress { get; }

    void SetWebmail(string? url);

    /// <summary>Attività dal vivo del notch (volume, download…), accese o spente.</summary>
    bool IsActivityEnabled(LiveActivity activity);

    void SetActivityEnabled(LiveActivity activity, bool enabled);

    /// <summary>Link iCal (.ics) dei calendari, oltre al calendario di Windows (docs/11-agenda-pannello-rapido.md).</summary>
    IReadOnlyList<string> CalendarFeeds { get; }

    /// <summary>Salva i link; restituisce l'errore se un indirizzo non è valido.</summary>
    string? SetCalendarFeeds(IReadOnlyList<string> feeds);

    string CalendarStatus { get; }

    event Action? CalendarChanged;

    void RefreshCalendar();

    void ShowMorningBriefNow();

    IEnumerable<(string Id, string Name)> Widgets { get; }

    IReadOnlyList<PluginLoadResult> PluginResults { get; }

    string DataFolder { get; }

    string PluginFolder { get; }

    bool IsWidgetEnabled(string id);

    void SetWidgetEnabled(string id, bool enabled);

    void ResetWidgetLayout();

    void ReloadPlugins();

    /// <summary>Collegamento diretto col telefono (KDE Connect sulla rete locale), spento di serie.</summary>
    bool PhoneDirect { get; set; }

    NothingShell.App.Services.IPhoneActions Phone { get; }

    /// <summary>Connettori (docs/14-agenti-connettori.md): i file letti, con l'errore se non sono validi.</summary>
    IReadOnlyList<ConnectorLoadResult> ConnectorResults { get; }

    string ConnectorFolder { get; }

    /// <summary>Permesso di eseguire comandi e azioni, legato al contenuto del file.</summary>
    bool IsConnectorGranted(ConnectorManifest manifest);

    void SetConnectorGranted(ConnectorManifest manifest, bool granted);

    bool HasConnectorSecret(ConnectorManifest manifest);

    /// <summary>Token o password nel Gestore credenziali; vuoto lo cancella.</summary>
    void SetConnectorSecret(ConnectorManifest manifest, string secret);

    /// <summary>
    /// Connettore dalla libreria (<see cref="ConnectorCatalog"/>): null se attivato, altrimenti il motivo. Con
    /// <paramref name="replace"/> riscrive quel connettore invece di aggiungerne uno.
    /// </summary>
    string? AddConnector(ConnectorTemplate connector, string? name, IReadOnlyDictionary<string, string> values, bool grant, ConnectorManifest? replace = null);

    /// <summary>Cambio dollari → euro della BCE in uso per i costi LLM; null finché non arriva.</summary>
    double? UsdToEur { get; }

    LlmSummary ConnectorCosts(ConnectorManifest manifest);

    /// <summary>Ultime chiamate (o giri, con i totali) registrate, dalla più recente.</summary>
    IReadOnlyList<LlmCall> ConnectorCalls(ConnectorManifest manifest);

    /// <summary>Costi LLM del connettore (null li spegne): null se fatto, altrimenti il motivo.</summary>
    string? SetConnectorLlm(ConnectorManifest manifest, LlmSpec? llm);

    void ResetConnectorCosts(ConnectorManifest manifest);

    void ExportConnectorCosts();

    void RemoveConnector(ConnectorManifest manifest);

    void ReloadConnectors();

    void CopyConnectorExamples();

    /// <summary>Cervello dei comandi (docs/15-comandi-cervello.md): stato del modello locale.</summary>
    string BrainStatus { get; }

    /// <summary>Scorciatoie imparate dall'uso.</summary>
    int BrainLearned { get; }

    bool BrainModelInstalled { get; }

    bool BrainInstalling { get; }

    event Action? BrainChanged;

    void InstallBrainModel();

    void CancelBrainInstall();

    void ClearBrainMemory();

    void OpenTerminal();
}

/// <summary>Pagine del menù laterale, nell'ordine in cui compaiono.</summary>
public enum SettingsPage
{
    General,
    Notch,
    Phone,
    Mail,
    Agenda,
    Dev,
    Agents,
    Connectors,
    Access,
    Widgets,
    Plugins,
    Info,
}

/// <summary>
/// Impostazioni in stile Nothing (docs/07-settings-plugins.md): menù laterale con le pagine, ogni pagina fatta di
/// schede. Ogni modifica si applica subito.
/// </summary>
public sealed class SettingsWindow : Window
{
    private const double PanelWidth = 1000;
    private const double PanelHeight = 720;
    private const double SidebarWidth = 236;
    private const string RowTag = "row";

    private static readonly (SettingsPage Page, string Title, string Subtitle)[] Pages =
    [
        (SettingsPage.General, "Generale", "Avvio e componenti della shell"),
        (SettingsPage.Notch, "Notch", "Pagine e attività dal vivo"),
        (SettingsPage.Phone, "Telefono", "Chiamate, notifiche e collegamento diretto"),
        (SettingsPage.Mail, "Posta", "Mail nel notch e casella collegata"),
        (SettingsPage.Agenda, "Agenda", "Appuntamenti, calendari e riepilogo"),
        (SettingsPage.Dev, "Sviluppo", "Build, appunti e server locali"),
        (SettingsPage.Agents, "Agenti", "Claude Code e altri agenti al lavoro"),
        (SettingsPage.Connectors, "Connettori", "Dispositivi, servizi e automazioni"),
        (SettingsPage.Access, "Accesso", "Blocco e sblocco"),
        (SettingsPage.Widgets, "Widget", "Widget sul desktop"),
        (SettingsPage.Plugins, "Plugin", "Widget aggiuntivi"),
        (SettingsPage.Info, "Info", "Versione e dati"),
    ];

    private readonly IShellSettingsActions _shell;
    private readonly StackPanel _nav = new();
    private readonly StackPanel _page = new() { Margin = new Thickness(0, 0, 4, 24) };
    private readonly DotMatrixView _pageTitle = new() { Pitch = 3, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _pageSubtitle = Label(string.Empty, 12, Theme.GreyBrush);
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
    private Panel _current;
    private SettingsPage _selected = SettingsPage.General;
    private ConnectorTemplate? _draft;

    /// <summary>Connettore da cui viene il modulo: Modifica lo riscrive, Duplica ne parte per uno nuovo.</summary>
    private ConnectorManifest? _source;
    private bool _duplicate;

    /// <summary>Connettore di cui si stanno impostando i costi LLM.</summary>
    private ConnectorManifest? _llmDraft;
    private string? _libraryCategory;
    private TextBlock? _mailStatus;
    private TextBlock? _calendarStatus;
    private TextBlock? _phoneStatus;

    public SettingsWindow(IShellSettingsActions shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;
        _current = _page;

        var area = SystemParameters.WorkArea;
        Width = Math.Min(PanelWidth, area.Width - 40);
        Height = Math.Min(PanelHeight, area.Height - 40);
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = "Impostazioni Nothing Shell";
        Content = BuildVisualTree();
        Rebuild();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                if (_draft is not null || _llmDraft is not null)
                {
                    CloseForms();
                    Rebuild();
                }
                else
                {
                    Close();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
                var index = Array.FindIndex(Pages, p => p.Page == _selected);
                Show(Pages[(index + step + Pages.Length) % Pages.Length].Page);
                e.Handled = true;
            }
        };
        // Lo stato della casella cambia da solo (connessione, errori): si aggiorna la riga senza rifare il modulo.
        _shell.MailAccountChanged += OnMailAccountChanged;
        _shell.CalendarChanged += OnCalendarChanged;
        _shell.Phone.Changed += OnPhoneChanged;
        _shell.BrainChanged += OnBrainChanged;
        Closed += (_, _) =>
        {
            _shell.BrainChanged -= OnBrainChanged;
            _shell.Phone.Changed -= OnPhoneChanged;
            _shell.MailAccountChanged -= OnMailAccountChanged;
            _shell.CalendarChanged -= OnCalendarChanged;
        };
    }

    /// <summary>Apre una pagina del menù laterale.</summary>
    public void Show(SettingsPage page)
    {
        if (page != _selected)
            CloseForms();
        _selected = page;
        Rebuild();
        _scroll.ScrollToTop();
    }

    /// <summary>Apre il modulo di un connettore della libreria (anche per le istantanee).</summary>
    public void ShowConnectorTemplate(string id)
    {
        _selected = SettingsPage.Connectors;
        CloseForms();
        _draft = ConnectorCatalog.Find(id);
        Rebuild();
        _scroll.ScrollToTop();
    }

    /// <summary>Modulo della libreria già compilato coi valori di un connettore attivo: per modificarlo o duplicarlo.</summary>
    public void EditConnector(ConnectorManifest manifest, bool duplicate)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Template is not { } template || ConnectorCatalog.Find(template) is not { } found)
            return;
        _selected = SettingsPage.Connectors;
        CloseForms();
        _draft = found;
        _source = manifest;
        _duplicate = duplicate;
        Rebuild();
        _scroll.ScrollToTop();
    }

    /// <summary>Modulo dei costi LLM di un connettore (anche per le istantanee).</summary>
    public void EditConnectorCosts(ConnectorManifest manifest)
    {
        _selected = SettingsPage.Connectors;
        CloseForms();
        _llmDraft = manifest ?? throw new ArgumentNullException(nameof(manifest));
        Rebuild();
        _scroll.ScrollToTop();
    }

    private void CloseForms()
    {
        _draft = null;
        _source = null;
        _duplicate = false;
        _llmDraft = null;
    }

    /// <summary>Ridisegna il menù e la pagina aperta (dopo una ricarica dei plugin cambiano i widget e gli errori).</summary>
    public void Rebuild()
    {
        BuildNav();
        _page.Children.Clear();
        _current = _page;
        _mailStatus = null;
        _calendarStatus = null;
        _phoneStatus = null;

        var (_, title, subtitle) = Pages.First(p => p.Page == _selected);
        _pageTitle.Dots = DotText.Render(title.ToUpperInvariant());
        _pageSubtitle.Text = subtitle;

        switch (_selected)
        {
            case SettingsPage.General:
                GeneralPage();
                break;
            case SettingsPage.Notch:
                NotchPage();
                break;
            case SettingsPage.Phone:
                PhonePage();
                break;
            case SettingsPage.Mail:
                MailPage();
                break;
            case SettingsPage.Agenda:
                AgendaPage();
                break;
            case SettingsPage.Dev:
                DevPage();
                break;
            case SettingsPage.Agents:
                AgentsPage();
                break;
            case SettingsPage.Connectors:
                if (_llmDraft is { } costs)
                    LlmForm(costs);
                else if (_draft is { } template)
                    ConnectorForm(template);
                else
                    ConnectorsPage();
                break;
            case SettingsPage.Access:
                AccessPage();
                break;
            case SettingsPage.Widgets:
                WidgetsPage();
                break;
            case SettingsPage.Plugins:
                PluginsPage();
                break;
            case SettingsPage.Info:
                InfoPage();
                break;
        }
    }

    private void GeneralPage()
    {
        Section("AVVIO");
        ToggleRow("Avvia con Windows", "Solo per questo utente", Autostart.IsEnabled, Autostart.Set);

        Section("COMPONENTI");
        ToggleRow("Notch", "Orologio e musica in alto al centro", _shell.NotchEnabled, v => _shell.NotchEnabled = v);
        ToggleRow("Barra al posto della taskbar", "Spenta: torna la taskbar di Windows", _shell.BarEnabled, v => _shell.BarEnabled = v);
        ToggleRow("Tasto Win apre il launcher", "Le combinazioni Win+… restano a Windows", _shell.UseWinKey, v => _shell.UseWinKey = v);

        Section("SCORCIATOIE");
        Note("Ctrl+Win spegne e riaccende la shell. Ctrl+Tab passa alla pagina successiva delle impostazioni, Esc chiude.", Theme.GreyBrush);
    }

    private void NotchPage()
    {
        Section("PAGINE");
        Note("Swipe sul notch aperto (touchpad, rotellina o trascinamento) per Agenda, Posta, Telefono, Lavoro, Agenti, Server, " +
             "Dispositivi, Appunti, Timer e Sistema. Le pagine si accendono e spengono con le loro funzioni.", Theme.GreyBrush);

        Section("ATTIVITÀ DAL VIVO");
        ActivityRow(LiveActivity.Volume, "Volume", "Con i tasti del volume, al posto dell'indicatore di Windows");
        ActivityRow(LiveActivity.Brightness, "Luminosità", "Schermo del portatile (i monitor esterni non la comunicano)");
        ActivityRow(LiveActivity.Privacy, "Microfono e webcam in uso", "Icone rosse nel notch, le app nella pagina Sistema");
        ActivityRow(LiveActivity.Downloads, "Download", "In corso dalla cartella Download, poi Apri e Cartella");
        ActivityRow(LiveActivity.Screenshots, "Screenshot rapido", "Strumento di cattura (Win+Maiusc+S, Stamp) e Greenshot: anteprima da trascinare");
        ActivityRow(LiveActivity.Bluetooth, "Cuffie Bluetooth", "Collegamento e batteria");
    }

    /// <summary>Lavoro nel notch, appunti intelligenti e server locali (docs/12-sviluppo.md).</summary>
    private void OnBrainChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (_selected == SettingsPage.Dev)
            Rebuild();
    });

    /// <summary>Terminale PowerShell con il cervello dei comandi (docs/15-comandi-cervello.md).</summary>
    private void BrainSection()
    {
        Section("COMANDI E CERVELLO");
        Note("Il terminale Nothing è PowerShell vero: ogni comando parte com'è. Le frasi che PowerShell non capisce passano dalle " +
             "scorciatoie imparate e poi dal modello locale; nel launcher si scrive > e il comando o la frase.", Theme.GreyBrush);
        Note($"{_shell.BrainStatus} · {_shell.BrainLearned} scorciatoie imparate", Theme.WhiteBrush);
        ButtonRow(("APRI TERMINALE", _shell.OpenTerminal),
            _shell.BrainInstalling ? ("ANNULLA DOWNLOAD", _shell.CancelBrainInstall)
            : _shell.BrainModelInstalled ? ("DIMENTICA LE SCORCIATOIE", _shell.ClearBrainMemory)
            : ("SCARICA IL MODELLO", _shell.InstallBrainModel));
    }

    private void DevPage()
    {
        BrainSection();

        Section("NEL NOTCH");
        ActivityRow(LiveActivity.Tasks, "Comandi e build", "Tempo nella pillola, scheda alla fine con il primo errore, pagina Lavoro");
        ActivityRow(LiveActivity.Ci, "CI di GitHub dopo il push", "Con GitHub CLI (gh): esito delle Actions e Rilancia");
        ActivityRow(LiveActivity.Servers, "Server locali", "Porte in ascolto: avviso, Apri nel browser, Termina");

        Section("APPUNTI");
        ActivityRow(LiveActivity.Clipboard, "Cronologia degli appunti", "Pagina Appunti: ultime copie con Formatta, Apri, Copia HEX… (solo in memoria)");
        ActivityRow(LiveActivity.ClipboardHints, "Avviso alla copia", "JSON, errori, colori e timestamp appena copiati");
        ActivityRow(LiveActivity.ClipboardSecrets, "Proteggi chiavi e token", "Chiavi API, JWT e password copiati spariscono dagli appunti dopo 30 s");

        Section("TERMINALE");
        var module = System.IO.Path.Combine(AppContext.BaseDirectory, "tools", "NothingShell.psm1");
        var profileLine = $"Import-Module '{module}'; Enable-NothingShellPrompt";
        Note("Nel terminale: ns seguito dal comando (ns dotnet build, ns npm test) manda inizio e fine al notch. " +
             "Con l'hook del prompt arrivano anche i comandi lunghi (30 s o più) e i git push. Aggiungi al profilo " +
             "di PowerShell ($PROFILE):", Theme.GreyBrush);
        Code(profileLine);
        Note("Altre shell: NothingShell.exe --task '{\"event\":\"done\",\"title\":\"make\",\"seconds\":40,\"exitCode\":0}'. " +
             "Editor per gli errori: \"editor\" in settings.json, altrimenti VS Code o Cursor dal PATH.", Theme.GreyBrush);
        ButtonRow(("COPIA RIGA DEL PROFILO", () => Clipboard.SetDataObject(profileLine, copy: true)));
    }

    /// <summary>Agenti al lavoro: Claude Code da solo, gli altri dalla pipe (docs/14-agenti-connettori.md).</summary>
    private void AgentsPage()
    {
        Section("NEL NOTCH");
        ActivityRow(LiveActivity.Agents, "Agenti al lavoro", "Token e costo dal vivo, scheda quando l'agente ha finito");

        Section("CLAUDE CODE");
        Note("Compare da solo: Nothing Shell legge solo token, modello e stato dai transcript in ~/.claude/projects, mai il " +
             "testo dei messaggi. Il costo è quello del listino dell'API: con un abbonamento è una stima, non quanto paghi.", Theme.GreyBrush);

        Section("ALTRI AGENTI");
        Note("Mandano i loro totali dalla riga di comando, con \"state\":\"done\" alla fine:", Theme.GreyBrush);
        Code("NothingShell.exe --task '{\"event\":\"agent\",\"id\":\"mio-agente\",\"title\":\"Report\",\"model\":\"claude-sonnet-5-5\"," +
             "\"input\":12000,\"output\":800,\"state\":\"working\"}'");
        Note("Per dare agli agenti occhi su casa, server e servizi usa i connettori: valori e avvisi finiscono nella pagina " +
             "Dispositivi del notch.", Theme.GreyBrush);
        ButtonRow(("APRI CONNETTORI", () => Show(SettingsPage.Connectors)));
    }

    /// <summary>
    /// Connettori: quelli attivi (stato, token nel Gestore credenziali, permesso per comandi e azioni, rimozione) e la
    /// libreria di connettori pronti, che si attivano scrivendo pochi dati. Il permesso vale per il file com'è adesso.
    /// </summary>
    private void ConnectorsPage()
    {
        Section("NEL NOTCH");
        ActivityRow(LiveActivity.Connectors, "Dispositivi e sistemi", "Pagina Dispositivi e avvisi quando un valore supera la soglia");

        var results = _shell.ConnectorResults;
        var tracked = results.Select(r => r.Manifest).OfType<ConnectorManifest>().Where(m => m.Llm is not null).ToArray();
        if (tracked.Length > 0)
        {
            Heading("COSTI LLM", tracked.Length == 1 ? "1 SITO" : $"{tracked.Length} SITI");
            Card(() => CostsSummary(tracked));
        }
        Heading(results.Count == 0 ? "ATTIVI" : $"ATTIVI · {results.Count}");
        if (results.Count == 0)
            Card(() => Note("Nessun connettore: scegline uno dalla libreria qui sotto.", Theme.GreyBrush));
        foreach (var result in results)
            Card(() => ConnectorCard(result));

        Heading("LIBRERIA");
        Note("Connettori pronti: scrivi pochi dati e partono subito. Leggere non cambia nulla; quelli che eseguono comandi o hanno " +
             "pulsanti (accendi, riavvia) chiedono il tuo permesso.", Theme.GreyBrush);
        var chips = new WrapPanel { Margin = new Thickness(0, 10, 0, 6) };
        foreach (var category in new string?[] { null }.Concat(ConnectorCatalog.Categories))
        {
            var selected = category == _libraryCategory;
            var chip = Chip(category?.ToUpperInvariant() ?? "TUTTI", selected);
            chip.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _libraryCategory = category;
                Rebuild();
            };
            chips.Children.Add(chip);
        }
        _page.Children.Add(chips);

        var active = results.Select(r => r.Manifest?.Template).OfType<string>().ToArray();
        var grid = new UniformGrid { Columns = 2 };
        foreach (var template in ConnectorCatalog.All.Where(t => _libraryCategory is null || t.Category == _libraryCategory))
            grid.Children.Add(TemplateTile(template, active.Count(a => a == template.Id)));
        _page.Children.Add(grid);

        Heading("AVANZATE");
        Card(() =>
        {
            Note("Ogni connettore è un file .json nella cartella dei connettori: si può scrivere a mano per casi particolari " +
                 "(formato in docs/14-agenti-connettori.md). Gli esempi copiati vanno in connectors\\esempi, dove non partono.", Theme.GreyBrush);
            ButtonRow(
                ("APRI CARTELLA", () => OpenFolder(_shell.ConnectorFolder)),
                ("COPIA GLI ESEMPI", _shell.CopyConnectorExamples),
                ("RICARICA", () =>
                {
                    _shell.ReloadConnectors();
                    Rebuild();
                }));
        });
    }

    /// <summary>Un connettore attivo: tipo e valori, permesso, token e rimozione con conferma.</summary>
    private void ConnectorCard(ConnectorLoadResult result)
    {
        if (result.Manifest is not { } m)
        {
            CardTitle(result.FileName, "NON VALIDO", Theme.RedBrush);
            Note(result.Error ?? string.Empty, Theme.RedBrush);
            ButtonRow(("APRI CARTELLA", () => OpenFolder(_shell.ConnectorFolder)));
            return;
        }
        var kind = m.Kind switch
        {
            ConnectorKind.HomeAssistant => "HOME ASSISTANT",
            ConnectorKind.Prometheus => "PROMETHEUS",
            ConnectorKind.Mqtt => "MQTT",
            ConnectorKind.Ping => "PING",
            ConnectorKind.Command => "COMANDO",
            ConnectorKind.File => "FILE",
            _ => "HTTP",
        };
        var granted = _shell.IsConnectorGranted(m);
        CardTitle(m.Name, kind, Theme.GreyBrush);
        var values = m.Readings.Count == 0 ? null : string.Join(", ", m.Readings.Select(r => r.Label ?? r.Source ?? r.Value).Where(l => l is not null).Take(5));
        var origin = m.Template is { } t && ConnectorCatalog.Find(t) is { } template ? template.Name : null;
        Note(string.Join(" · ", new[] { origin, values, $"ogni {FormatRefresh(m.Refresh)}" }.Where(p => p is not null)), Theme.GreyBrush);
        if (m.Actions.Count > 0)
            Note($"Pulsanti: {string.Join(", ", m.Actions.Select(a => a.Label))}", Theme.GreyBrush);

        if (m.Kind is ConnectorKind.Http or ConnectorKind.Command or ConnectorKind.File)
            LlmCard(m);

        var widget = ConnectorWidgetLayout.WidgetId(m.Id);
        ToggleRow("Widget sul desktop", "I valori in un riquadro sul desktop; si sposta trascinandolo", _shell.IsWidgetEnabled(widget),
            v => _shell.SetWidgetEnabled(widget, v));

        if (m.NeedsGrant)
        {
            var what = m.Kind == ConnectorKind.Command
                ? $"Esegue {m.Command} {string.Join(' ', m.Arguments)}"
                : "Può cambiare lo stato del dispositivo";
            if (what.Length > 160)
                what = what[..157] + "…";
            ToggleRow("Consenti comandi e azioni", what, granted, v =>
            {
                _shell.SetConnectorGranted(m, v);
                Rebuild();
            });
        }
        if (m.Secret)
        {
            var label = m.Kind == ConnectorKind.Mqtt ? "Password del broker" : "Token";
            var box = new PasswordBox();
            AddField($"{label} ({(_shell.HasConnectorSecret(m) ? "salvato nel Gestore credenziali" : "manca")})", box);
            ButtonRow(("SALVA", () =>
            {
                _shell.SetConnectorSecret(m, box.Password);
                Rebuild();
            }));
        }
        if (m.Template is { } from && ConnectorCatalog.Find(from) is not null)
            ButtonRow(("MODIFICA", () => EditConnector(m, duplicate: false)), ("DUPLICA", () => EditConnector(m, duplicate: true)));
        ConfirmButtonRow("RIMUOVI", "CONFERMA RIMOZIONE", () =>
        {
            _shell.RemoveConnector(m);
            Rebuild();
        });
    }

    /// <summary>Spesa di oggi e del mese di ogni sito che tiene i costi LLM, il totale, il cambio e l'esportazione.</summary>
    private void CostsSummary(ConnectorManifest[] tracked)
    {
        double today = 0, month = 0;
        var complete = true;
        foreach (var m in tracked)
        {
            var c = _shell.ConnectorCosts(m);
            today += c.TodayEur ?? 0;
            month += c.MonthEur ?? 0;
            complete &= c.TodayEur is not null && c.MonthEur is not null;
            var over = (m.Llm!.DailyBudget is { } d && c.TodayEur > d) || (m.Llm.MonthlyBudget is { } mb && c.MonthEur > mb);
            var row = Note($"{m.Name}   ·   oggi € {LlmLedger.FormatEuro(c.TodayEur)}{Budget(m.Llm.DailyBudget)}   ·   mese € {LlmLedger.FormatEuro(c.MonthEur)}{Budget(m.Llm.MonthlyBudget)}" +
                           $"   ·   {c.Today.Calls} chiamate oggi", over ? Theme.RedBrush : Theme.WhiteBrush);
            row.FontSize = 13;
        }
        if (tracked.Length > 1)
        {
            var total = Note($"In tutto   ·   oggi € {LlmLedger.FormatEuro(today)}   ·   mese € {LlmLedger.FormatEuro(month)}{(complete ? string.Empty : "  (manca il cambio)")}", Theme.WhiteBrush);
            total.FontWeight = FontWeights.SemiBold;
        }
        Note(_shell.UsdToEur is { } rate
                ? $"Dollari convertiti col cambio BCE: 1 $ = {rate.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)} €. I valori compaiono anche nella pagina Dispositivi e nei widget."
                : "Il cambio della BCE arriva al primo giro con costi in dollari; fino ad allora quei costi restano in sospeso.",
            Theme.GreyBrush);
        ButtonRow(("ESPORTA CSV", _shell.ExportConnectorCosts));
    }

    private static string Budget(double? budget) =>
        budget is { } b ? $" / {LlmLedger.FormatEuro(b)}" : string.Empty;

    /// <summary>Costi LLM nella scheda del connettore: riepilogo e ultime chiamate, oppure l'invito ad accenderli.</summary>
    private void LlmCard(ConnectorManifest m)
    {
        if (m.Llm is not { } llm)
        {
            ToggleRow("Costi LLM", "Il sito usa un modello (Claude, GPT…)? Tieni traccia in euro delle chiamate che fa, con budget e avvisi", false, v =>
            {
                if (v)
                    EditConnectorCosts(m);
            });
            return;
        }
        var c = _shell.ConnectorCosts(m);
        var over = (llm.DailyBudget is { } d && c.TodayEur > d) || (llm.MonthlyBudget is { } mb && c.MonthEur > mb);
        Note($"Costi LLM · oggi € {LlmLedger.FormatEuro(c.TodayEur)}{Budget(llm.DailyBudget)} · mese € {LlmLedger.FormatEuro(c.MonthEur)}{Budget(llm.MonthlyBudget)}" +
             $" · {c.Today.Calls} chiamate, {NothingShell.Core.Agents.AgentFormat.Tokens(c.Today.Input + c.Today.CacheRead + c.Today.CacheWrite)} in, " +
             $"{NothingShell.Core.Agents.AgentFormat.Tokens(c.Today.Output)} out oggi" +
             (c.Today.UnpricedTokens > 0 ? $" · {NothingShell.Core.Agents.AgentFormat.Tokens(c.Today.UnpricedTokens)} token senza prezzo (aggiungi i prezzi)" : string.Empty),
            over ? Theme.RedBrush : Theme.WhiteBrush);
        foreach (var call in _shell.ConnectorCalls(m).Take(4))
        {
            var euro = call.Priced ? (call.Usd == 0 ? call.Eur : _shell.UsdToEur is { } rate ? call.Eur + call.Usd * rate : (double?)null) : null;
            Note($"{call.At.ToLocalTime():dd/MM HH:mm} · {(call.Model.Length > 0 ? NothingShell.Core.Agents.AgentPricing.ShortName(call.Model) is { Length: > 0 } s ? s : call.Model : "modello?")}" +
                 $"{(call.Calls > 1 ? $" · {call.Calls} chiamate" : string.Empty)} · IN {NothingShell.Core.Agents.AgentFormat.Tokens(call.Input)} · OUT {NothingShell.Core.Agents.AgentFormat.Tokens(call.Output)}" +
                 $" · € {(call.Priced ? LlmLedger.FormatEuro(euro) : "senza prezzo")}", Theme.GreyBrush);
        }
        ButtonRow(("COSTI LLM", () => EditConnectorCosts(m)));
        ConfirmButtonRow("AZZERA I COSTI", "CONFERMA AZZERAMENTO", () =>
        {
            _shell.ResetConnectorCosts(m);
            Rebuild();
        });
    }

    /// <summary>
    /// Modulo dei costi LLM: dove sono modello, token e costo nella risposta del sito, prezzi per i modelli fuori
    /// listino, budget e cambio. Scrive il blocco "llm" del file del connettore.
    /// </summary>
    private void LlmForm(ConnectorManifest m)
    {
        var back = Label("‹  CONNETTORI", 11, Theme.GreyBrush);
        back.Cursor = Cursors.Hand;
        back.Margin = new Thickness(0, 0, 0, 14);
        back.MouseLeftButtonDown += (_, e) => e.Handled = true;
        back.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            CloseForms();
            Rebuild();
        };
        _page.Children.Add(back);

        var spec = m.Llm ?? new LlmSpec { Model = "{model}", Input = "{usage.input_tokens}", Output = "{usage.output_tokens}" };
        Section("COSTI LLM");
        CardTitle(m.Name, "EURO", Theme.GreyBrush);
        Note("Il sito deve rispondere (o il comando stampare) un JSON con l'uso del modello. Scrivi dove sono i valori come nelle " +
             "letture: {usage.input_tokens}. Con le singole chiamate ognuna si conta una volta (per id); con i totali si conta " +
             "quanto sono cresciuti da un giro all'altro. Il costo viene dal sito, dai prezzi qui sotto o dal listino di Anthropic " +
             "per i modelli Claude, in dollari convertiti col cambio BCE del giorno.", Theme.GreyBrush);

        var calls = spec.Mode == LlmMode.Calls;
        ToggleRow("Singole chiamate", "Il sito dà un elenco di chiamate, ognuna con un id. Spento: contatori totali", calls, v => calls = v);
        var list = Field("Elenco  ·  facoltativo  ·  es. calls o data[0].results", spec.List ?? string.Empty);
        var model = Field("Modello  ·  {model} o un nome fisso come claude-sonnet-5-5", spec.Model ?? string.Empty);
        var input = Field("Token in entrata  ·  es. {usage.input_tokens}", spec.Input ?? string.Empty);
        var output = Field("Token in uscita  ·  es. {usage.output_tokens}", spec.Output ?? string.Empty);
        var cacheRead = Field("Token letti dalla cache  ·  facoltativo", spec.CacheRead ?? string.Empty);
        var cacheWrite = Field("Token scritti in cache (5 minuti)  ·  facoltativo", spec.CacheWrite ?? string.Empty);
        var cacheWrite1h = Field("Token scritti in cache (1 ora)  ·  facoltativo", spec.CacheWrite1h ?? string.Empty);
        var requests = Field("Numero di chiamate (con i totali)  ·  facoltativo", spec.Requests ?? string.Empty);
        var id = Field("Id della chiamata  ·  facoltativo  ·  es. {id}", spec.Id ?? string.Empty);
        var at = Field("Ora della chiamata  ·  facoltativo  ·  es. {created_at}", spec.At ?? string.Empty);

        Section("COSTO");
        var cost = Field("Costo calcolato dal sito  ·  facoltativo  ·  vince sul listino", spec.Cost ?? string.Empty);
        var costCurrency = Field("Valuta del costo  ·  USD o EUR", spec.CostCurrency);
        var costScale = Field("Scala del costo  ·  0.01 se è in centesimi", Number(spec.CostScale));
        var priceIn = Field("Prezzo in entrata per milione di token  ·  per modelli fuori listino", spec.Price is { } p1 ? Number(p1.Input) : string.Empty);
        var priceOut = Field("Prezzo in uscita per milione di token", spec.Price is { } p2 ? Number(p2.Output) : string.Empty);
        var priceCurrency = Field("Valuta dei prezzi  ·  EUR o USD", spec.Price?.Currency ?? LlmSpec.Eur);

        Section("MONITORAGGIO");
        var daily = Field("Budget al giorno (€)  ·  oltre diventa un avviso nel notch", spec.DailyBudget is { } d ? Number(d) : string.Empty);
        var monthly = Field("Budget al mese (€)", spec.MonthlyBudget is { } mo ? Number(mo) : string.Empty);
        var rate = Field("Cambio fisso 1 $ = … €  ·  vuoto = BCE del giorno", spec.UsdToEur is { } r ? Number(r) : string.Empty);
        var countFirst = spec.CountFirst;
        ToggleRow("Conta anche la prima lettura", "Per contatori che ripartono ogni giorno (Admin API di Anthropic). Spento: la prima lettura fa da punto di partenza",
            countFirst, v => countFirst = v);

        var result = Note(string.Empty, Theme.RedBrush);
        result.Visibility = Visibility.Collapsed;
        ButtonRow(primary: true,
            ("SALVA", () =>
            {
                static string? T(TextBox box) => box.Text.Trim() is { Length: > 0 } t ? t : null;
                static double? N(TextBox box) =>
                    double.TryParse(box.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
                var currency = T(costCurrency)?.ToUpperInvariant() == LlmSpec.Eur ? LlmSpec.Eur : LlmSpec.Usd;
                var next = new LlmSpec
                {
                    Mode = calls ? LlmMode.Calls : LlmMode.Totals,
                    List = T(list), Model = T(model), Input = T(input), Output = T(output),
                    CacheRead = T(cacheRead), CacheWrite = T(cacheWrite), CacheWrite1h = T(cacheWrite1h),
                    Requests = T(requests), Id = T(id), At = T(at),
                    Cost = T(cost), CostCurrency = currency, CostScale = N(costScale) ?? 1,
                    Price = N(priceIn) is { } pin && N(priceOut) is { } pout
                        ? new LlmPrice(pin, pout, pin / 10, T(priceCurrency)?.ToUpperInvariant() == LlmSpec.Usd ? LlmSpec.Usd : LlmSpec.Eur)
                        : null,
                    DailyBudget = N(daily), MonthlyBudget = N(monthly), UsdToEur = N(rate) is > 0 and var fixedRate ? fixedRate : null,
                    CountFirst = countFirst,
                };
                if (_shell.SetConnectorLlm(m, next) is { } error)
                {
                    result.Text = error;
                    result.Visibility = Visibility.Visible;
                    return;
                }
                CloseForms();
                Rebuild();
            }),
            ("ANNULLA", () =>
            {
                CloseForms();
                Rebuild();
            }));
        if (m.Llm is not null)
            ConfirmButtonRow("SMETTI DI TENERE TRACCIA", "CONFERMA: TOGLI I COSTI", () =>
            {
                _shell.SetConnectorLlm(m, null);
                CloseForms();
                Rebuild();
            });
    }

    private static string Number(double value) => value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Tessera della libreria: categoria, nome, descrizione; un clic apre il modulo.</summary>
    private Border TemplateTile(ConnectorTemplate template, int active)
    {
        var stack = new StackPanel();
        var top = new Grid();
        top.Children.Add(Label(template.Category.ToUpperInvariant(), 10, Theme.GreyBrush));
        if (template.Permission is not null)
        {
            var tag = Label("PERMESSO", 10, Theme.GreyBrush);
            tag.HorizontalAlignment = HorizontalAlignment.Right;
            tag.ToolTip = template.Permission;
            top.Children.Add(tag);
        }
        stack.Children.Add(top);
        var name = Label(template.Name, 15, Theme.WhiteBrush);
        name.FontWeight = FontWeights.SemiBold;
        name.Margin = new Thickness(0, 6, 0, 4);
        stack.Children.Add(name);
        var description = Label(template.Description, 12, Theme.GreyBrush);
        description.TextWrapping = TextWrapping.Wrap;
        description.Height = 34;
        stack.Children.Add(description);
        var footer = Label(active > 0 ? $"+ AGGIUNGI ANCORA · {active} ATTIVO" : "+ AGGIUNGI", 11, active > 0 ? Theme.GreyBrush : Theme.RedBrush);
        footer.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(footer);

        var tile = new Border
        {
            Background = Theme.SurfaceBrush,
            BorderBrush = Theme.OutlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, 10, 10),
            Cursor = Cursors.Hand,
            Child = stack,
        };
        tile.MouseEnter += (_, _) => tile.BorderBrush = Theme.GreyBrush;
        tile.MouseLeave += (_, _) => tile.BorderBrush = Theme.OutlineBrush;
        tile.MouseLeftButtonDown += (_, e) => e.Handled = true;
        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowConnectorTemplate(template.Id);
        };
        return tile;
    }

    /// <summary>Modulo di un connettore della libreria: nome, i pochi campi che servono, permesso e Attiva.</summary>
    private void ConnectorForm(ConnectorTemplate template)
    {
        var back = Label("‹  LIBRERIA", 11, Theme.GreyBrush);
        back.Cursor = Cursors.Hand;
        back.Margin = new Thickness(0, 0, 0, 14);
        back.MouseLeftButtonDown += (_, e) => e.Handled = true;
        back.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _draft = null;
            Rebuild();
        };
        _page.Children.Add(back);

        var source = _source;
        var editing = source is not null && !_duplicate;
        Section(template.Category.ToUpperInvariant());
        CardTitle(template.Name, editing ? "MODIFICA" : _duplicate ? "DUPLICA" : null, Theme.GreyBrush);
        Note(template.Description, Theme.GreyBrush);
        if (_duplicate)
            Note("Una copia indipendente: cambia indirizzo, server o processo e ottieni un altro connettore, con il suo widget e i suoi costi.", Theme.GreyBrush);
        var name = Field("Nome nella pagina Dispositivi", source is null ? template.Name : editing ? source.Name : source.Name + " 2");
        var inputs = new List<(ConnectorField Field, Func<string> Read)>();
        foreach (var field in template.Fields)
        {
            var caption = field.Label + (field.Optional ? "  ·  facoltativo" : string.Empty) +
                          (field.Hint is { } hint ? $"  ·  es. {hint}" : string.Empty) +
                          (field.Kind == FieldKind.List ? "  ·  separati da virgole" : string.Empty);
            if (field.Kind == FieldKind.Secret)
            {
                var box = new PasswordBox();
                AddField(caption + (editing && source!.Secret ? "  ·  vuoto = resta quello salvato" : "  ·  va nel Gestore credenziali"), box);
                inputs.Add((field, () => box.Password));
            }
            else
            {
                var box = Field(caption, source?.TemplateValues.GetValueOrDefault(field.Key) ?? field.Default ?? string.Empty);
                inputs.Add((field, () => box.Text));
            }
        }

        var grant = template.Permission is not null;
        if (template.Permission is { } permission)
        {
            Section("PERMESSO");
            ToggleRow("Consenti comandi e azioni", permission, grant, v => grant = v);
            Note("Il permesso vale per il connettore com'è adesso: se il file cambia va dato di nuovo. I pulsanti delicati " +
                 "(riavvia, termina) chiedono un secondo clic.", Theme.GreyBrush);
        }

        Section(editing ? "SALVA" : "ATTIVA");
        Note(editing ? "Il connettore riparte con i nuovi valori; widget, costi LLM e token restano."
            : "Il connettore compare nella pagina Dispositivi del notch; i valori arrivano al primo aggiornamento. " +
              "Puoi attivare lo stesso connettore quante volte vuoi: un sito, un server o un processo per volta.", Theme.GreyBrush);
        var result = Note(string.Empty, Theme.RedBrush);
        result.Visibility = Visibility.Collapsed;
        ButtonRow(primary: true,
            (editing ? "SALVA" : "ATTIVA", () =>
            {
                var values = inputs.ToDictionary(i => i.Field.Key, i => i.Read(), StringComparer.Ordinal);
                if (_shell.AddConnector(template, name.Text, values, grant, editing ? source : null) is { } error)
                {
                    result.Text = error;
                    result.Visibility = Visibility.Visible;
                    return;
                }
                CloseForms();
                Rebuild();
                _scroll.ScrollToTop();
            }),
            ("ANNULLA", () =>
            {
                CloseForms();
                Rebuild();
            }));
    }

    private void AccessPage()
    {
        Section("ANIMAZIONI");
        ToggleRow("Animazioni di blocco e sblocco", "Card con il tuo account prima e dopo il blocco di Windows",
            _shell.AccessAnimations, v => _shell.AccessAnimations = v);
        Note("Password, PIN e Windows Hello restano a Windows: Nothing Shell anima solo il prima e il dopo. " +
             "Blocca dal launcher o con NothingShell.exe --lock.", Theme.GreyBrush);
    }

    private void WidgetsPage()
    {
        Section("SUL DESKTOP");
        foreach (var (id, name) in _shell.Widgets)
            ToggleRow(name, null, _shell.IsWidgetEnabled(id), v => _shell.SetWidgetEnabled(id, v));

        Section("DISPOSIZIONE");
        ToggleRow("Blocca posizione", "I widget non si trascinano", _shell.WidgetsLocked, v => _shell.WidgetsLocked = v);
        ButtonRow(("RIPRISTINA DISPOSIZIONE", _shell.ResetWidgetLayout));
    }

    private void PluginsPage()
    {
        Section("INSTALLATI");
        var valid = _shell.PluginResults.Where(r => r.Manifest is not null).ToArray();
        if (valid.Length == 0)
            Note("Nessun plugin installato.", Theme.GreyBrush);
        foreach (var plugin in valid)
            ToggleRow(plugin.Manifest!.Name, plugin.FolderName, _shell.IsWidgetEnabled(plugin.Manifest.WidgetId),
                v => _shell.SetWidgetEnabled(plugin.Manifest.WidgetId, v));
        foreach (var bad in _shell.PluginResults.Where(r => r.Error is not null))
            Note($"{bad.FolderName}: {bad.Error}", Theme.RedBrush);

        Section("CARTELLA");
        Note("Un plugin è una cartella con plugin.json: i dati arrivano da una URL JSON o da un comando che stampa JSON " +
             "(formato in docs/07-settings-plugins.md).", Theme.GreyBrush);
        ButtonRow(
            ("APRI CARTELLA", () => OpenFolder(_shell.PluginFolder)),
            ("RICARICA", () =>
            {
                _shell.ReloadPlugins();
                Rebuild();
            }));
    }

    private void InfoPage()
    {
        Section("VERSIONE");
        CardTitle($"Nothing Shell {typeof(SettingsWindow).Assembly.GetName().Version?.ToString(2)}", null, Theme.GreyBrush);
        Note("Strato grafico in stile Nothing OS sopra Windows 11. Non sostituisce explorer.exe.", Theme.GreyBrush);

        Section("DATI");
        Note(_shell.DataFolder, Theme.WhiteBrush);
        Note("Impostazioni, posizioni dei widget, plugin e connettori. Password e token sono nel Gestore credenziali di Windows.", Theme.GreyBrush);
        ButtonRow(("APRI CARTELLA DEI DATI", () => OpenFolder(_shell.DataFolder)));
    }

    /// <summary>Collegamento al telefono di Windows e collegamento diretto (docs/13-telefono.md).</summary>
    private void PhonePage()
    {
        Section("COLLEGAMENTO AL TELEFONO");
        ActivityRow(LiveActivity.Phone, "Chiamate e notifiche", "Da Collegamento al telefono di Windows (serve l'accesso alle notifiche)");

        Section("COLLEGAMENTO DIRETTO");
        ToggleRow("Collegamento diretto (KDE Connect)",
            "Batteria, notifiche, chiamate, appunti, file e Trova il telefono, sulla rete di casa",
            _shell.PhoneDirect, v =>
            {
                _shell.PhoneDirect = v;
                Rebuild();
            });
        if (!_shell.PhoneDirect)
        {
            Note("Senza collegamento diretto chiamate e notifiche arrivano da Collegamento al telefono di Windows.", Theme.GreyBrush);
            return;
        }
        Note("Sul telefono installa KDE Connect (Play Store o F-Droid), sulla stessa rete Wi-Fi del PC; il telefono " +
             "compare qui sotto. Windows deve consentire le porte 1714–1764 sulle reti private.", Theme.GreyBrush);
        _phoneStatus = Note(_shell.Phone.Status, Theme.GreyBrush);

        Section("TELEFONI");
        if (_shell.Phone.Devices.Count == 0)
            Note("Nessun telefono in rete per ora.", Theme.GreyBrush);
        foreach (var device in _shell.Phone.Devices)
        {
            var detail = device switch
            {
                { PairPending: true, VerificationKey: { } key } => $"Codice {key}: confermalo sul telefono",
                { PairPending: true } => "Associazione in corso…",
                { Paired: true, Connected: true, Battery: { } b } => $"Collegato · batteria {b.Charge}%{(b.Charging ? " in carica" : string.Empty)}",
                { Paired: true, Connected: true } => "Collegato",
                { Paired: true } => "Associato, non raggiungibile",
                _ => "In rete, non associato",
            };
            var id = device.DeviceId;
            (string, Action)[] actions = device switch
            {
                { PairPending: true, PairRequestedByPhone: true } => [("ACCETTA", () => _shell.Phone.AcceptPair(id)), ("RIFIUTA", () => _shell.Phone.RejectPair(id))],
                { PairPending: true } => [],
                { Paired: true } => [("DISSOCIA", () => _shell.Phone.Unpair(id))],
                _ => [("ASSOCIA", () => _shell.Phone.RequestPair(id))],
            };
            ActionRow(device.Name, detail, actions);
        }
        var address = Field("Indirizzo IP del telefono (se non compare da solo)", "");
        ButtonRow(
            ("CERCA", () => _shell.Phone.Discover(System.Net.IPAddress.TryParse(address.Text.Trim(), out var ip) ? ip : null)),
            ("CONSENTI NEL FIREWALL", _shell.Phone.AllowFirewall));
    }

    /// <summary>Il telefono cambia stato da solo (collegato, batteria, associazione): si ridisegna la pagina.</summary>
    private void OnPhoneChanged()
    {
        if (_phoneStatus is not null && IsVisible)
            Rebuild();
    }

    /// <summary>Prossimo appuntamento e riepilogo del mattino, con i calendari da cui leggere gli appuntamenti.</summary>
    private void AgendaPage()
    {
        Section("NEL NOTCH");
        ActivityRow(LiveActivity.Agenda, "Prossimo appuntamento", "Sotto l'ora, nella pagina Agenda e 10 minuti prima, con Partecipa");
        ActivityRow(LiveActivity.MorningBrief, "Riepilogo del mattino", "Al primo accesso della giornata: appuntamenti e mail da leggere");

        Section("CALENDARI");
        Note("Nothing Shell legge il calendario di Windows (se Windows lo consente alle app desktop) e i link iCal " +
             "(.ics) che aggiungi qui. Outlook: Impostazioni › Calendario › Calendari condivisi › Pubblica un " +
             "calendario, link ICS. Google Calendar: impostazioni del calendario › Indirizzo segreto in formato iCal. " +
             "I link si leggono soltanto.", Theme.GreyBrush);

        var feeds = _shell.CalendarFeeds;
        foreach (var feed in feeds)
            ActionRow(feed, null, [("RIMUOVI", () =>
            {
                _shell.SetCalendarFeeds(feeds.Where(f => f != feed).ToArray());
                Rebuild();
            })]);

        var link = Field("Aggiungi un link iCal (https:// o webcal://)", "");
        var result = Note(string.Empty, Theme.RedBrush);
        result.Visibility = Visibility.Collapsed;
        ButtonRow(
            ("AGGIUNGI", () =>
            {
                if (_shell.SetCalendarFeeds([.. feeds, link.Text]) is { } error)
                {
                    result.Text = error;
                    result.Visibility = Visibility.Visible;
                    return;
                }
                Rebuild();
            }),
            ("AGGIORNA", _shell.RefreshCalendar),
            ("PROVA RIEPILOGO", _shell.ShowMorningBriefNow));
        _calendarStatus = Note(_shell.CalendarStatus, Theme.GreyBrush);
    }

    private void OnCalendarChanged()
    {
        if (_calendarStatus is not null)
            _calendarStatus.Text = _shell.CalendarStatus;
    }

    /// <summary>
    /// Come collegare la posta al notch (docs/08-notch-mail-pages.md). Due strade: la casella collegata
    /// direttamente (IMAP, consigliata) oppure le notifiche che un'app di posta mostra in Windows.
    /// </summary>
    private void MailPage()
    {
        Section("NEL NOTCH");
        ToggleRow("Mail nel notch", "Busta e oggetto per qualche secondo, elenco nella pagina Posta",
            _shell.MailInNotch, v =>
            {
                _shell.MailInNotch = v;
                Rebuild();
            });
        if (!_shell.MailInNotch)
            return;

        Section("CASELLA COLLEGATA", "consigliato");
        Note("Nothing Shell si collega direttamente alla casella (IMAP) e avvisa appena arriva una mail, senza " +
             "altre app aperte. Un clic su una mail nel notch la apre nel lettore di Nothing: la mail resta non " +
             "letta finché non premi Segna come letta, l'unica modifica che Nothing Shell fa alla casella. " +
             "La password resta cifrata nel Gestore credenziali di Windows.", Theme.GreyBrush);
        if (_shell.MailAccount is { } account)
        {
            _mailStatus = Note(_shell.MailAccountStatus, _shell.MailAccountConnected ? Theme.WhiteBrush : Theme.RedBrush);
            Note($"Utente: {account.Login} · server {account.Server}:{account.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                Theme.GreyBrush);
            var webmail = Field("Webmail (per «Apri in…» e gli allegati; se è installata come app di Edge si apre l'app)",
                _shell.WebmailAddress ?? string.Empty);
            webmail.LostFocus += (_, _) =>
            {
                if (webmail.Text.Trim() != (_shell.WebmailAddress ?? string.Empty))
                    _shell.SetWebmail(webmail.Text);
            };
            ButtonRow(
                ("RICOLLEGA", _shell.ReconnectMailAccount),
                ("SCOLLEGA", () =>
                {
                    _shell.DisconnectMailAccount();
                    Rebuild();
                }));
        }
        else
            MailAccountForm();

        Section("OPPURE: NOTIFICHE DI UN'APP DI POSTA");
        Note("Per Gmail, Outlook.com o un programma di posta già configurato sul PC: Nothing Shell legge le " +
             "notifiche che mostrano in Windows. Le webmail aziendali (Exchange) di solito non ne mandano: per " +
             "quelle usa la casella collegata qui sopra.", Theme.GreyBrush);

        var allowed = _shell.NotificationAccessAllowed;
        Step(1, "Consenti l'accesso alle notifiche", done: allowed);
        Note(_shell.NotificationAccessText, allowed ? Theme.GreyBrush : Theme.RedBrush);
        if (_shell.CanRequestNotificationAccess)
            ButtonRow(("CONSENTI ACCESSO", async () =>
            {
                await _shell.RequestNotificationAccessAsync();
                Rebuild();
            }));
        else if (!allowed)
            ButtonRow(("APRI PRIVACY › NOTIFICHE", () => OpenUri("ms-settings:privacy-notifications")));

        Step(2, "Usa un'app di posta con le notifiche attive", done: null);
        Note("Riconosciute da sole: Outlook (classico e nuovo), Posta, Thunderbird, eM Client, Mailbird, Spark.",
            Theme.GreyBrush);
        Note("Webmail nel browser (Gmail, Outlook.com, Yahoo, Proton): consenti le notifiche al sito " +
             "(lucchetto accanto all'indirizzo › Notifiche › Consenti) e lascia aperta una scheda.", Theme.GreyBrush);
        Note("In Windows l'app deve avere le notifiche attive e Non disturbare deve essere spento.", Theme.GreyBrush);
        ButtonRow(("APRI NOTIFICHE DI WINDOWS", () => OpenUri("ms-settings:notifications")));

        Step(3, "La tua app non è riconosciuta?", done: null);
        var candidates = _shell.MailAppCandidates;
        if (candidates.Count == 0)
            Note("Qui compaiono le app che hanno mandato notifiche da quando Nothing Shell è avviato. " +
                 "Mandati una mail di prova, poi premi Aggiorna.", Theme.GreyBrush);
        else
        {
            Note("Accendi l'app che ti avvisa delle mail: le sue notifiche diventano mail nel notch.", Theme.GreyBrush);
            foreach (var (app, isMail) in candidates)
                ToggleRow(app, null, isMail, v => _shell.SetMailApp(app, v));
        }

        var last = _shell.LastMail;
        Section("VERIFICA");
        Note(last is null
                ? "Nessuna mail rilevata finora."
                : $"Ultima mail rilevata alle {last.Received.ToLocalTime():HH:mm} da {last.App}: «{last.Subject}»",
            Theme.GreyBrush);
        ButtonRow(("PROVA L'AVVISO", _shell.ShowTestMailAlert), ("AGGIORNA", Rebuild));
    }

    /// <summary>Modulo per collegare la casella: si salva solo se l'accesso di prova riesce.</summary>
    private void MailAccountForm()
    {
        var address = Field("Indirizzo email", "");
        var server = Field("Server IMAP (lo trovi nelle istruzioni del provider, es. mail.dominio.it)", "");
        var port = Field("Porta", MailAccount.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var user = Field(@"Utente: lascia vuoto se è l'indirizzo email (alcuni server Exchange vogliono DOMINIO\nome)", "");
        var password = new PasswordBox();
        AddField("Password", password);

        // Il server si propone dall'indirizzo, finché l'utente non ne scrive uno.
        address.LostFocus += (_, _) =>
        {
            if (server.Text.Length == 0 && ImapSetup.SuggestServer(address.Text) is { } suggested)
                server.Text = suggested;
        };

        var result = Note(string.Empty, Theme.GreyBrush);
        var busy = false;
        ButtonRow(primary: true, ("COLLEGA", async () =>
        {
            if (busy)
                return;
            busy = true;
            result.Foreground = Theme.GreyBrush;
            result.Text = "Prova di accesso in corso…";
            var account = new MailAccount(
                address.Text.Trim(),
                server.Text.Trim(),
                int.TryParse(port.Text.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 0,
                string.IsNullOrWhiteSpace(user.Text) ? null : user.Text.Trim());
            var error = await _shell.ConnectMailAccountAsync(account, password.Password);
            busy = false;
            if (error is null)
            {
                Rebuild();
                return;
            }
            result.Foreground = Theme.RedBrush;
            result.Text = error;
        }));
    }

    private void OnMailAccountChanged()
    {
        if (_mailStatus is null)
            return;
        _mailStatus.Text = _shell.MailAccountStatus;
        _mailStatus.Foreground = _shell.MailAccountConnected ? Theme.WhiteBrush : Theme.RedBrush;
    }

    // ── Struttura della finestra ────────────────────────────────────────────────

    private Border BuildVisualTree()
    {
        var sidebar = new DockPanel { Background = Brushes.Transparent };
        var brand = new StackPanel { Margin = new Thickness(10, 4, 0, 26) };
        brand.Children.Add(new DotMatrixView { Pitch = 2, Dots = DotText.Render("NOTHING"), DotColor = Theme.Red, HorizontalAlignment = HorizontalAlignment.Left });
        var shell = Label("SHELL · IMPOSTAZIONI", 10, Theme.GreyBrush);
        shell.Margin = new Thickness(0, 8, 0, 0);
        brand.Children.Add(shell);
        DockPanel.SetDock(brand, Dock.Top);
        sidebar.Children.Add(brand);
        var version = Label($"v{typeof(SettingsWindow).Assembly.GetName().Version?.ToString(2)}", 10, Theme.GreyBrush);
        version.Margin = new Thickness(12, 12, 0, 0);
        DockPanel.SetDock(version, Dock.Bottom);
        sidebar.Children.Add(version);
        sidebar.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _nav });
        sidebar.MouseLeftButtonDown += DragWindow;

        var sidebarBorder = new Border
        {
            Width = SidebarWidth,
            Background = Theme.SurfaceBrush,
            BorderBrush = Theme.OutlineBrush,
            BorderThickness = new Thickness(0, 0, 1, 0),
            CornerRadius = new CornerRadius(27, 0, 0, 27),
            Padding = new Thickness(14, 24, 14, 18),
            Child = sidebar,
        };

        var header = new Grid { Margin = new Thickness(0, 0, 0, 22), Background = Brushes.Transparent };
        var titles = new StackPanel();
        titles.Children.Add(_pageTitle);
        _pageSubtitle.Margin = new Thickness(0, 10, 0, 0);
        titles.Children.Add(_pageSubtitle);
        header.Children.Add(titles);
        var close = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Background = Brushes.Transparent,
            BorderBrush = Theme.OutlineBrush,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.Hand,
            ToolTip = "Chiudi (Esc)",
            Child = new DotMatrixView
            {
                Pitch = 2,
                Dots = DotText.Render("X"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        close.MouseEnter += (_, _) => close.Background = Theme.SurfaceBrush;
        close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
        // La pressione va fermata qui: arrivando all'intestazione avvierebbe DragMove, che cattura il mouse
        // fino al rilascio, e il clic sulla X non arriverebbe mai.
        close.MouseLeftButtonDown += (_, e) => e.Handled = true;
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Close();
        };
        header.Children.Add(close);
        header.MouseLeftButtonDown += DragWindow;

        _scroll.Content = _page;
        var content = new DockPanel { Margin = new Thickness(36, 28, 32, 0) };
        DockPanel.SetDock(header, Dock.Top);
        content.Children.Add(header);
        content.Children.Add(_scroll);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(sidebarBorder);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);

        return new Border
        {
            Background = Theme.BlackBrush,
            BorderBrush = Theme.OutlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(28),
            Child = grid,
        };
    }

    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    /// <summary>Voci del menù: la pagina aperta su fondo chiaro con il punto rosso; un numero dove serve.</summary>
    private void BuildNav()
    {
        _nav.Children.Clear();
        foreach (var (page, title, _) in Pages)
        {
            var selected = page == _selected;
            var row = new Grid();
            var dot = new Ellipse { Width = 6, Height = 6, Fill = selected ? Theme.RedBrush : Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(dot);
            var label = Label(title, 13, selected ? Theme.WhiteBrush : Theme.GreyBrush);
            label.Margin = new Thickness(16, 0, 0, 0);
            label.VerticalAlignment = VerticalAlignment.Center;
            if (selected)
                label.FontWeight = FontWeights.SemiBold;
            row.Children.Add(label);
            var count = page switch
            {
                SettingsPage.Connectors => _shell.ConnectorResults.Count,
                SettingsPage.Plugins => _shell.PluginResults.Count(r => r.Manifest is not null),
                _ => 0,
            };
            if (count > 0)
            {
                var badge = Label(count.ToString(System.Globalization.CultureInfo.InvariantCulture), 11, Theme.GreyBrush);
                badge.HorizontalAlignment = HorizontalAlignment.Right;
                badge.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(badge);
            }
            var item = new Border
            {
                Height = 38,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 0, 2),
                Background = selected ? Theme.OutlineBrush : Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = row,
            };
            if (!selected)
            {
                item.MouseEnter += (_, _) => label.Foreground = Theme.WhiteBrush;
                item.MouseLeave += (_, _) => label.Foreground = Theme.GreyBrush;
            }
            item.MouseLeftButtonDown += (_, e) => e.Handled = true;
            item.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                Show(page);
            };
            _nav.Children.Add(item);
        }
    }

    // ── Mattoni delle pagine ────────────────────────────────────────────────────

    /// <summary>Titolo grigio e una nuova scheda: le righe che seguono finiscono dentro.</summary>
    private void Section(string title, string? tag = null)
    {
        Heading(title, tag);
        _current = NewCard();
    }

    /// <summary>Titolo grigio fuori dalle schede (le righe successive vanno sulla pagina).</summary>
    private void Heading(string title, string? tag = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, _page.Children.Count == 0 ? 0 : 26, 0, 10) };
        row.Children.Add(Label(title, 11, Theme.GreyBrush));
        if (tag is not null)
        {
            var badge = Label(tag.ToUpperInvariant(), 10, Theme.RedBrush);
            badge.VerticalAlignment = VerticalAlignment.Center;
            badge.Margin = new Thickness(10, 0, 0, 0);
            row.Children.Add(badge);
        }
        _page.Children.Add(row);
        _current = _page;
    }

    /// <summary>Scheda a sé (un connettore): il contenuto si costruisce dentro, poi si torna alla pagina.</summary>
    private void Card(Action build)
    {
        _current = NewCard();
        build();
        _current = _page;
    }

    private StackPanel NewCard()
    {
        var stack = new StackPanel();
        _page.Children.Add(new Border
        {
            Background = Theme.SurfaceBrush,
            BorderBrush = Theme.OutlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(20, 12, 20, 14),
            Margin = new Thickness(0, 0, 0, 10),
            Child = stack,
        });
        return stack;
    }

    /// <summary>Riga di una scheda; tra due righe di fila una linea sottile.</summary>
    private void AddRow(FrameworkElement row)
    {
        if (_current.Children.Count > 0 && _current.Children[^1] is FrameworkElement { Tag: RowTag })
            _current.Children.Add(new Border { Height = 1, Background = Theme.OutlineBrush, Margin = new Thickness(0, 2, 0, 2) });
        row.Tag = RowTag;
        _current.Children.Add(row);
    }

    private void CardTitle(string text, string? tag, Brush tagBrush)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        var label = Label(text, 16, Theme.WhiteBrush);
        label.FontWeight = FontWeights.SemiBold;
        label.Margin = new Thickness(0, 0, 110, 0);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        row.Children.Add(label);
        if (tag is not null)
        {
            var badge = Label(tag, 10, tagBrush);
            badge.HorizontalAlignment = HorizontalAlignment.Right;
            badge.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(badge);
        }
        _current.Children.Add(row);
    }

    private void ActivityRow(LiveActivity activity, string title, string description) =>
        ToggleRow(title, description, _shell.IsActivityEnabled(activity), v => _shell.SetActivityEnabled(activity, v));

    private void ToggleRow(string title, string? description, bool isOn, Action<bool> changed)
    {
        var row = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 64, 0) };
        text.Children.Add(Label(title, 14, Theme.WhiteBrush));
        if (description is not null)
        {
            var d = Label(description, 12, Theme.GreyBrush);
            d.Margin = new Thickness(0, 3, 0, 0);
            d.TextWrapping = TextWrapping.Wrap;
            text.Children.Add(d);
        }
        var toggle = new DotToggle(isOn) { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        toggle.Toggled += changed;
        row.Children.Add(text);
        row.Children.Add(toggle);
        AddRow(row);
    }

    /// <summary>Riga con titolo, dettaglio e pulsanti di testo rossi a destra (telefoni, calendari).</summary>
    private void ActionRow(string title, string? detail, (string Text, Action Click)[] actions)
    {
        var row = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 150, 0) };
        var t = Label(title, 14, Theme.WhiteBrush);
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        t.ToolTip = title;
        text.Children.Add(t);
        if (detail is not null)
        {
            var d = Label(detail, 12, Theme.GreyBrush);
            d.Margin = new Thickness(0, 3, 0, 0);
            text.Children.Add(d);
        }
        row.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (label, click) in actions)
        {
            var button = Label(label, 11, Theme.RedBrush);
            button.Margin = new Thickness(14, 0, 0, 0);
            button.Cursor = Cursors.Hand;
            button.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                click();
            };
            buttons.Children.Add(button);
        }
        row.Children.Add(buttons);
        AddRow(row);
    }

    /// <summary>Passo numerato: numero a punti rosso finché c'è da fare, grigio quando è a posto.</summary>
    private void Step(int number, string title, bool? done)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 4) };
        row.Children.Add(new DotMatrixView
        {
            Pitch = 2,
            Dots = DotText.Render(number.ToString("00", System.Globalization.CultureInfo.InvariantCulture)),
            DotColor = done == true ? Theme.Grey : Theme.Red,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });
        var label = Label(done == true ? $"{title}  ·  fatto" : title, 14, Theme.WhiteBrush);
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        _current.Children.Add(row);
    }

    private TextBox Field(string label, string text)
    {
        var box = new TextBox { Text = text };
        AddField(label, box);
        return box;
    }

    /// <summary>Campo in stile Nothing: etichetta grigia, riquadro nero con bordo Outline e angoli arrotondati.</summary>
    private void AddField(string label, Control input)
    {
        var caption = Label(label, 11, Theme.GreyBrush);
        caption.TextWrapping = TextWrapping.Wrap;
        caption.Margin = new Thickness(0, 12, 0, 5);
        _current.Children.Add(caption);

        input.Background = Brushes.Transparent;
        input.BorderThickness = new Thickness(0);
        input.Foreground = Theme.WhiteBrush;
        input.FontFamily = Theme.BodyFont;
        input.FontSize = 13;
        input.VerticalContentAlignment = VerticalAlignment.Center;
        switch (input)
        {
            case TextBox t:
                t.CaretBrush = Theme.WhiteBrush;
                break;
            case PasswordBox p:
                p.CaretBrush = Theme.WhiteBrush;
                break;
        }
        var frame = new Border
        {
            Background = Theme.BlackBrush,
            BorderBrush = Theme.OutlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8, 12, 8),
            Child = input,
        };
        input.GotKeyboardFocus += (_, _) => frame.BorderBrush = Theme.GreyBrush;
        input.LostKeyboardFocus += (_, _) => frame.BorderBrush = Theme.OutlineBrush;
        _current.Children.Add(frame);
    }

    private void ButtonRow(params (string Text, Action Click)[] buttons) => ButtonRow(false, buttons);

    /// <summary>Pulsanti a pillola; con <paramref name="primary"/> il primo è bianco pieno (l'azione del modulo).</summary>
    private void ButtonRow(bool primary, params (string Text, Action Click)[] buttons) =>
        ButtonRow(primary, buttons.Select(b => (b.Text, (Func<Task>)(() =>
        {
            b.Click();
            return Task.CompletedTask;
        }))).ToArray());

    private void ButtonRow(params (string Text, Func<Task> Click)[] buttons) => ButtonRow(false, buttons);

    private void ButtonRow(bool primary, params (string Text, Func<Task> Click)[] buttons)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 4) };
        for (var i = 0; i < buttons.Length; i++)
        {
            var (text, click) = buttons[i];
            var button = Button(text, primary && i == 0);
            button.MouseLeftButtonUp += async (_, e) =>
            {
                e.Handled = true;
                await click();
            };
            row.Children.Add(button);
        }
        _current.Children.Add(row);
    }

    /// <summary>Pulsante che chiede un secondo clic entro 3 s (rimuovi): il testo diventa rosso con la conferma.</summary>
    private void ConfirmButtonRow(string text, string confirmText, Action confirmed)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 4) };
        var button = Button(text, primary: false);
        var label = (TextBlock)button.Child;
        var armed = false;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            armed = false;
            label.Text = text;
            label.Foreground = Theme.WhiteBrush;
        };
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (armed)
            {
                timer.Stop();
                confirmed();
                return;
            }
            armed = true;
            label.Text = confirmText;
            label.Foreground = Theme.RedBrush;
            timer.Start();
        };
        row.Children.Add(button);
        _current.Children.Add(row);
    }

    private static Border Button(string text, bool primary)
    {
        var button = new Border
        {
            BorderBrush = primary ? Theme.WhiteBrush : Theme.GreyBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(17),
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 8, 0),
            Background = primary ? Theme.WhiteBrush : Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = Label(text, 11, primary ? Theme.BlackBrush : Theme.WhiteBrush),
        };
        if (!primary)
        {
            button.MouseEnter += (_, _) => button.Background = Theme.OutlineBrush;
            button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        }
        return button;
    }

    /// <summary>Filtro a pillola della libreria: quello scelto è pieno.</summary>
    private static Border Chip(string text, bool selected) => new()
    {
        BorderBrush = selected ? Theme.WhiteBrush : Theme.OutlineBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(14),
        Padding = new Thickness(12, 6, 12, 6),
        Margin = new Thickness(0, 0, 6, 6),
        Background = selected ? Theme.WhiteBrush : Brushes.Transparent,
        Cursor = Cursors.Hand,
        Child = Label(text, 10, selected ? Theme.BlackBrush : Theme.GreyBrush),
    };

    private TextBlock Note(string text, Brush brush)
    {
        var note = Label(text, 12, brush);
        note.TextWrapping = TextWrapping.Wrap;
        note.LineHeight = 18;
        note.Margin = new Thickness(0, 3, 0, 3);
        _current.Children.Add(note);
        return note;
    }

    /// <summary>Riga da copiare (comandi): carattere a spaziatura fissa su fondo nero, selezionabile.</summary>
    private void Code(string text)
    {
        _current.Children.Add(new Border
        {
            Background = Theme.BlackBrush,
            BorderBrush = Theme.OutlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 6, 0, 6),
            Child = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Theme.WhiteBrush,
                CaretBrush = Theme.WhiteBrush,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
            },
        });
    }

    private static string FormatRefresh(TimeSpan refresh) => refresh.TotalSeconds switch
    {
        < 60 => $"{refresh.TotalSeconds:0} s",
        < 3600 => $"{refresh.TotalMinutes:0} min",
        _ => $"{refresh.TotalHours:0.#} h",
    };

    private static void OpenFolder(string path)
    {
        System.IO.Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
    }

    private static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // pagina delle impostazioni non disponibile in questa versione di Windows
        }
    }

    private static TextBlock Label(string text, double size, Brush foreground) => new()
    {
        Text = text,
        FontFamily = Theme.BodyFont,
        FontSize = size,
        Foreground = foreground,
    };
}
