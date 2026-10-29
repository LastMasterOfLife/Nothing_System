using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NothingShell.App.Rendering;
using NothingShell.Core.Agents;
using NothingShell.Core.Connectors;
using NothingShell.Core.Design;
using NothingShell.Core.Dev;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Notch;
using NothingShell.Core.Phone;

namespace NothingShell.App.Notch;

/// <summary>Cosa fanno i clic nelle pagine Telefono e Dispositivi: li decide il ShellController.</summary>
public sealed record ConnectedPageActions(
    Action<string> OpenFile,
    Action<string> ShowFile,
    Action FindPhone,
    Action<ConnectorManifest, ConnectorAction> RunAction)
{
    /// <summary>Sceglie un file e lo manda al telefono collegato.</summary>
    public Action SendFile { get; init; } = () => { };

    /// <summary>Manda al telefono il testo copiato (solo con un clic).</summary>
    public Action SendClipboard { get; init; } = () => { };

    /// <summary>Nessuna azione (anteprime di <c>--snapshot</c>).</summary>
    public static ConnectedPageActions None { get; } = new(_ => { }, _ => { }, () => { }, (_, _) => { });
}

/// <summary>Quello che la pagina Telefono mostra (docs/13-telefono.md).</summary>
public sealed record PhonePageData(
    bool Direct,
    PhoneDevice? Device,
    string Status,
    IncomingFile? Incoming,
    IReadOnlyList<ReceivedFile> Files,
    IReadOnlyList<PhoneEvent> Notifications)
{
    public static PhonePageData Empty { get; } = new(false, null, string.Empty, null, [], []);
}

/// <summary>Righe e titoletti in comune fra le pagine alte.</summary>
internal static class TallRows
{
    /// <summary>Titoletto grigio fra le sezioni di una pagina ("FILE RICEVUTI").</summary>
    public static TextBlock Caption(string text)
    {
        var caption = NotchUi.Text(10, Theme.GreyBrush, text);
        caption.Margin = new Thickness(0, 10, 0, 2);
        return caption;
    }

    /// <summary>Due righe: titolo bianco con dettaglio grigio a destra, sotto una riga grigia; pulsanti a destra.</summary>
    public static Border Item(UIElement? mark, string title, string? detail, string? subtitle, UIElement? right = null,
        Brush? titleBrush = null, Brush? detailBrush = null)
    {
        var top = new DockPanel();
        if (detail is not null)
        {
            var d = NotchUi.Text(11, detailBrush ?? Theme.GreyBrush, detail);
            d.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(d, Dock.Right);
            top.Children.Add(d);
        }
        top.Children.Add(NotchUi.Text(13, titleBrush ?? Theme.WhiteBrush, title));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(top);
        if (subtitle is { Length: > 0 })
            text.Children.Add(NotchUi.Text(11, Theme.GreyBrush, subtitle));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (mark is FrameworkElement m)
        {
            // colonna fissa: icone e punto restano nelle loro proporzioni, allineati fra le righe
            m.HorizontalAlignment = HorizontalAlignment.Left;
            m.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(new Border { Width = 28, Child = m });
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (right is not null)
        {
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);
        }
        return new Border { Padding = new Thickness(0, 3, 0, 3), Child = grid };
    }

    /// <summary>Barra a punti piccola (avanzamento, batteria).</summary>
    public static DotMatrixView Bar(double level, int columns, Color color) =>
        new() { Pitch = 2, Dots = DotBar.Render(level, columns), DotColor = color, OffColor = Theme.DotOff, VerticalAlignment = VerticalAlignment.Center };
}

/// <summary>
/// Pagina Telefono (alta, scorre): nome e stato, batteria, rete e segnale, cosa il telefono condivide (i permessi dati
/// nell'app KDE Connect), il file che sta arrivando con l'avanzamento, gli ultimi file ricevuti con Apri e Cartella e
/// le ultime notifiche.
/// </summary>
public sealed class PhonePage : NotchPageView
{
    private readonly ConnectedPageActions _actions;
    private readonly StackPanel _list = new();
    private readonly NotchScrollList _scroll;
    private readonly TextBlock _count;
    private PhonePageData _data = PhonePageData.Empty;

    public PhonePage(ConnectedPageActions actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        _actions = actions;
        var (header, _, count) = DevRows.Header("TELEFONO");
        _count = count;
        _scroll = new NotchScrollList(_list) { Margin = new Thickness(0, 6, 0, 0) };
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(_scroll);
        Children.Add(dock);
        Render();
    }

    public override NotchPage Page => NotchPage.Phone;

    public void Update(PhonePageData data)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        Render();
    }

    private void Render()
    {
        _list.Children.Clear();
        var device = _data.Device;
        _count.Text = device is { Connected: true } ? "collegato" : _data.Direct ? "non collegato" : string.Empty;

        if (device is not null)
            AddDevice(device);
        else
        {
            var hint = NotchUi.Text(12, Theme.GreyBrush, _data.Direct
                ? $"{_data.Status}. Apri KDE Connect sul telefono, sulla stessa rete Wi-Fi."
                : "Accendi il collegamento diretto in Impostazioni › TELEFONO per batteria, segnale e file. " +
                  "Qui sotto le notifiche da Collegamento al telefono.");
            hint.TextWrapping = TextWrapping.Wrap;
            hint.TextTrimming = TextTrimming.None;
            hint.Margin = new Thickness(0, 2, 0, 0);
            _list.Children.Add(hint);
        }

        if (_data.Incoming is not null || _data.Files.Count > 0)
            _list.Children.Add(TallRows.Caption("FILE RICEVUTI"));
        if (_data.Incoming is { } incoming)
        {
            _list.Children.Add(TallRows.Item(DevRows.Icon(DotIcons.Download, Theme.Red), incoming.Name,
                $"{Math.Round(incoming.Progress * 100):0}%", $"In arrivo · {DownloadTracker.FormatBytes(incoming.Received)} di {DownloadTracker.FormatBytes(incoming.Size)}",
                TallRows.Bar(incoming.Progress, 12, Theme.White)));
        }
        var now = DateTimeOffset.Now;
        foreach (var file in _data.Files)
        {
            var path = file.Path;
            _list.Children.Add(TallRows.Item(DevRows.Icon(DotIcons.Download, Theme.White), System.IO.Path.GetFileName(path),
                TaskTracker.FormatAgo(file.At, now), $"{DownloadTracker.FormatBytes(file.Size)} · da {file.From}",
                DevRows.Buttons(DevRows.Button("APRI", () => _actions.OpenFile(path)), DevRows.Button("CARTELLA", () => _actions.ShowFile(path)))));
        }

        if (device is { Connected: true } && _data.Incoming is null && _data.Files.Count == 0 && _data.Notifications.Count == 0)
        {
            _list.Children.Add(TallRows.Caption("FILE E NOTIFICHE"));
            var empty = NotchUi.Text(12, Theme.GreyBrush,
                "Ancora niente. Condividi un file o un link dal telefono verso questo PC: arriva in Download e compare qui.");
            empty.TextWrapping = TextWrapping.Wrap;
            empty.TextTrimming = TextTrimming.None;
            _list.Children.Add(empty);
        }

        if (_data.Notifications.Count > 0)
            _list.Children.Add(TallRows.Caption("NOTIFICHE"));
        foreach (var n in _data.Notifications)
        {
            var title = n.Kind == PhoneEventKind.MissedCall ? $"Persa: {n.Title}" : n.Title;
            _list.Children.Add(TallRows.Item(null, title.Length > 0 ? title : n.App ?? "Telefono",
                n.Received.ToLocalTime().ToString("HH:mm", NotchUi.Italian),
                string.Join(" · ", new[] { n.App, n.Text }.Where(x => x is { Length: > 0 })),
                titleBrush: n.Kind == PhoneEventKind.MissedCall ? Theme.RedBrush : null));
        }
    }

    /// <summary>
    /// Il telefono a colpo d'occhio: nome e stato con Trova, poi tre tessere affiancate (batteria, rete, invio di file e
    /// appunti) e cosa condivide. Le tessere riempiono la larghezza invece di lasciare una riga sola e spazio vuoto.
    /// </summary>
    private void AddDevice(PhoneDevice device)
    {
        var on = device.Connected;
        var status = on ? (_data.Direct ? "Collegato · KDE Connect" : "Collegato") : _data.Status is { Length: > 0 } st ? st : "Non raggiungibile";
        _list.Children.Add(TallRows.Item(DevRows.Icon(DotIcons.Phone, on ? Theme.White : Theme.Grey), device.Name, null, status,
            on ? DevRows.Button("TROVA", _actions.FindPhone) : null));

        var tiles = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        for (var i = 0; i < 3; i++)
            tiles.ColumnDefinitions.Add(new ColumnDefinition());

        // batteria: percentuale a punti, barra, stato
        var battery = new StackPanel();
        if (device.Battery is { } b)
        {
            var low = b.Low && !b.Charging;
            battery.Children.Add(DevRows.Dots($"{b.Charge}%", !on ? Theme.Grey : low ? Theme.Red : Theme.White, 3));
            var bar = new DotMatrixView
            {
                Pitch = 3, Dots = DotBar.Render(b.Charge / 100.0, 28, 2), OffColor = Theme.DotOff,
                DotColor = !on ? Theme.Grey : low ? Theme.Red : Theme.White, HorizontalAlignment = HorizontalAlignment.Left,
            };
            bar.Margin = new Thickness(0, 7, 0, 5);
            battery.Children.Add(bar);
            battery.Children.Add(NotchUi.Text(10, b.Charging ? Theme.RedBrush : Theme.GreyBrush, b.Charging ? "in carica" : low ? "scarica" : "a batteria"));
        }
        else
        {
            battery.Children.Add(DevRows.Dots("--", Theme.Grey, 3));
            battery.Children.Add(Hint("non condivisa"));
        }
        AddTile(tiles, 0, "BATTERIA", battery);

        // rete: tipo a punti e tacche
        var network = new StackPanel();
        if (device.Signal is { } s)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(DevRows.Dots(s.NetworkType, on ? Theme.White : Theme.Grey, 3));
            row.Children.Add(new DotMatrixView
            {
                Pitch = 3, Dots = DotBar.Signal(s.Strength), DotColor = on ? Theme.White : Theme.Grey, Margin = new Thickness(10, 0, 0, 0),
            });
            network.Children.Add(row);
            network.Children.Add(Hint(s.Strength switch { 0 => "nessun segnale", 1 => "segnale debole", 4 => "segnale pieno", _ => $"{s.Strength} tacche su 4" }));
        }
        else
        {
            network.Children.Add(DevRows.Dots("--", Theme.Grey, 3));
            network.Children.Add(Hint("non condivisa"));
        }
        AddTile(tiles, 1, "RETE", network);

        // invio al telefono: solo con un clic, mai da solo
        var send = new StackPanel();
        if (on)
        {
            var file = DevRows.Button("FILE", _actions.SendFile);
            var clip = DevRows.Button("APPUNTI", _actions.SendClipboard);
            foreach (var button in new[] { file, clip })
            {
                button.HorizontalAlignment = HorizontalAlignment.Left;
                button.Margin = new Thickness(0, 0, 0, 5);
                send.Children.Add(button);
            }
        }
        else
        {
            send.Children.Add(Hint("quando è collegato"));
        }
        AddTile(tiles, 2, "INVIA", send);
        _list.Children.Add(tiles);

        if (device.Shares.Count > 0)
        {
            _list.Children.Add(TallRows.Caption("CONDIVIDE"));
            var chips = new WrapPanel();
            foreach (var share in device.Shares)
                chips.Children.Add(new Border
                {
                    BorderBrush = Theme.OutlineBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(8, 1, 8, 2),
                    Margin = new Thickness(0, 2, 6, 2),
                    Child = NotchUi.Text(11, on ? Theme.WhiteBrush : Theme.GreyBrush, share),
                });
            _list.Children.Add(chips);
        }
    }

    private static void AddTile(Grid tiles, int column, string title, UIElement content)
    {
        var stack = new StackPanel();
        var caption = NotchUi.Text(10, Theme.GreyBrush, title);
        caption.Margin = new Thickness(0, 0, 0, 7);
        stack.Children.Add(caption);
        stack.Children.Add(content);
        var tile = new Border
        {
            Background = Theme.SurfaceBrush,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(column == 0 ? 0 : 4, 0, column == 2 ? 0 : 4, 0),
            Child = stack,
        };
        Grid.SetColumn(tile, column);
        tiles.Children.Add(tile);
    }

    private static TextBlock Hint(string text)
    {
        var hint = NotchUi.Text(10, Theme.GreyBrush, text);
        hint.Margin = new Thickness(0, 6, 0, 0);
        return hint;
    }
}

/// <summary>
/// Pagina Agenti (alta, scorre): oggi in tutto, poi una riga per sessione con stato (punto che pulsa = al lavoro,
/// ✓ = finito), modello, token in entrata e in uscita del turno e della sessione, costo; si aggiorna a ogni risposta.
/// </summary>
public sealed class AgentsPage : NotchPageView
{
    private readonly StackPanel _list = new();
    private readonly TextBlock _count;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private IReadOnlyList<AgentSnapshot> _sessions = [];
    private double? _today;

    public AgentsPage()
    {
        var (header, _, count) = DevRows.Header("AGENTI");
        _count = count;
        var scroll = new NotchScrollList(_list) { Margin = new Thickness(0, 6, 0, 0) };
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(scroll);
        Children.Add(dock);
        _tick.Tick += (_, _) => Render();
        Render();
    }

    public override NotchPage Page => NotchPage.Agents;

    public override void OnShown()
    {
        Render();
        _tick.Start();
    }

    public override void OnHidden() => _tick.Stop();

    public void Update(IReadOnlyList<AgentSnapshot> sessions, double? today)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions;
        _today = today;
        Render();
    }

    private void Render()
    {
        _list.Children.Clear();
        var working = _sessions.Count(s => s.State == AgentState.Working);
        _count.Text = $"oggi {AgentFormat.Cost(_today)}" + (working > 0 ? $" · {working} al lavoro" : string.Empty);
        if (_sessions.Count == 0)
        {
            var empty = DevRows.Empty();
            empty.Text = "Nessun agente nelle ultime ore. Claude Code compare da solo; altri agenti con " +
                         "NothingShell.exe --task '{\"event\":\"agent\",…}' (Impostazioni › AGENTI).";
            _list.Children.Add(empty);
            return;
        }
        var now = DateTimeOffset.Now;
        foreach (var s in _sessions)
        {
            UIElement mark = s.State switch
            {
                AgentState.Working => new Ellipse { Width = 7, Height = 7, Fill = Theme.RedBrush, Opacity = now.Second % 2 == 0 ? 1 : 0.35 },
                AgentState.Done => DevRows.Icon(DotIcons.Check, Theme.White),
                AgentState.Interrupted => DevRows.Icon(DotIcons.Close, Theme.Grey, 1.5),
                _ => DevRows.Icon(DotIcons.Agent, Theme.Grey, 1.5),
            };
            var detail = s.State == AgentState.Working ? TaskTracker.FormatElapsed(now - s.TurnStarted) : TaskTracker.FormatAgo(s.LastActivity, now);
            var model = AgentPricing.ShortName(s.Model);
            var turn = $"turno IN {AgentFormat.Tokens(s.TurnUsage.AllInput)} · OUT {AgentFormat.Tokens(s.TurnUsage.Output)} · {AgentFormat.Cost(s.TurnCost)}";
            var total = $"sessione IN {AgentFormat.Tokens(s.Total.AllInput)} · OUT {AgentFormat.Tokens(s.Total.Output)}";
            var cost = DevRows.Dots(AgentFormat.Cost(s.Cost), s.State == AgentState.Working ? Theme.White : Theme.Grey);
            cost.Margin = new Thickness(10, 0, 0, 0);
            var item = TallRows.Item(mark, $"{s.Title}{(model.Length > 0 ? $" · {model}" : string.Empty)}", detail, turn, cost);
            var block = new StackPanel { ToolTip = $"{s.Source}\n{s.Cwd}\nCache: letti {AgentFormat.Tokens(s.Total.CacheRead)}, scritti {AgentFormat.Tokens(s.Total.CacheWrite5m + s.Total.CacheWrite1h)}" };
            block.Children.Add(item);
            var totals = NotchUi.Text(10, Theme.GreyBrush, total);
            totals.Margin = new Thickness(28, -2, 0, 4);
            block.Children.Add(totals);
            _list.Children.Add(block);
        }
    }
}

/// <summary>
/// Pagina Dispositivi (alta, scorre): per ogni connettore nome, stato e valori (rossi oltre soglia); sotto, le azioni
/// se il permesso è stato dato. Le azioni con conferma vogliono un secondo clic entro 3 s.
/// </summary>
public sealed class DevicesPage : NotchPageView
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(3);

    private readonly ConnectedPageActions _actions;
    private readonly StackPanel _list = new();
    private readonly TextBlock _count;
    private readonly DispatcherTimer _confirmTimer = new() { Interval = ConfirmTimeout };
    private IReadOnlyList<ConnectorState> _states = [];
    private ConnectorAction? _confirming;

    public DevicesPage(ConnectedPageActions actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        _actions = actions;
        var (header, _, count) = DevRows.Header("DISPOSITIVI");
        _count = count;
        var scroll = new NotchScrollList(_list) { Margin = new Thickness(0, 6, 0, 0) };
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(scroll);
        Children.Add(dock);
        _confirmTimer.Tick += (_, _) =>
        {
            _confirmTimer.Stop();
            _confirming = null;
            Render();
        };
        Render();
    }

    public override NotchPage Page => NotchPage.Devices;

    public void Update(IReadOnlyList<ConnectorState> states)
    {
        _states = states ?? throw new ArgumentNullException(nameof(states));
        Render();
    }

    private void Render()
    {
        _list.Children.Clear();
        var warnings = _states.Sum(s => s.Readings.Count(r => r.Level == ReadingLevel.Warning));
        var errors = _states.Count(s => s.Status == ConnectorStatus.Error);
        _count.Text = _states.Count == 0 ? string.Empty
            : warnings > 0 ? (warnings == 1 ? "1 avviso" : $"{warnings} avvisi") : errors > 0 ? $"{errors} in errore"
            : _states.Count == 1 ? "1 connettore" : $"{_states.Count} connettori";
        if (_states.Count == 0)
        {
            var empty = DevRows.Empty();
            empty.Text = "Nessun connettore. Aggiungi un file in .nothing-shell\\connectors (esempi in Impostazioni › CONNETTORI): " +
                         "Home Assistant, Prometheus, MQTT, ping, API http, file e comandi.";
            _list.Children.Add(empty);
            return;
        }
        var now = DateTimeOffset.Now;
        foreach (var state in _states)
        {
            var m = state.Manifest;
            var color = state.Status switch
            {
                ConnectorStatus.Error => Theme.Red,
                ConnectorStatus.Ok when state.Readings.Any(r => r.Level == ReadingLevel.Warning) => Theme.Red,
                ConnectorStatus.Ok => Theme.White,
                _ => Theme.Grey,
            };
            var detail = state.Status switch
            {
                ConnectorStatus.NeedsGrant => "serve il permesso",
                ConnectorStatus.Waiting => "in attesa",
                _ when state.UpdatedAt is { } at => TaskTracker.FormatAgo(at, now),
                _ => null,
            };
            _list.Children.Add(TallRows.Item(DevRows.Icon(DotIcons.Device, color, 1.5), m.Name, detail,
                state.Error ?? (state.Status == ConnectorStatus.NeedsGrant ? "Esegue comandi o azioni: consentilo in Impostazioni › CONNETTORI" : null),
                detailBrush: state.Status is ConnectorStatus.Error or ConnectorStatus.NeedsGrant ? Theme.RedBrush : null));

            var values = new WrapPanel { Margin = new Thickness(28, 0, 0, 2) };
            foreach (var r in state.Readings)
            {
                var cell = new StackPanel { Margin = new Thickness(0, 2, 16, 2) };
                cell.Children.Add(NotchUi.Text(10, Theme.GreyBrush, r.Label));
                var value = NotchUi.Text(14, r.Level == ReadingLevel.Warning ? Theme.RedBrush : Theme.WhiteBrush,
                    r.Unit is { Length: > 0 } unit ? $"{r.Text} {unit}" : r.Text);
                cell.Children.Add(value);
                values.Children.Add(cell);
            }
            if (values.Children.Count > 0)
                _list.Children.Add(values);

            if (state.Granted && m.Actions.Count > 0)
            {
                var buttons = new WrapPanel { Margin = new Thickness(22, 0, 0, 6) };
                foreach (var action in m.Actions)
                {
                    var confirming = ReferenceEquals(_confirming, action);
                    var button = DevRows.Button(confirming ? "SICURO?" : action.Label, () => Run(m, action), confirming ? Theme.RedBrush : null);
                    button.Margin = new Thickness(6, 2, 0, 2);
                    buttons.Children.Add(button);
                }
                _list.Children.Add(buttons);
            }
        }
    }

    private void Run(ConnectorManifest manifest, ConnectorAction action)
    {
        if (action.Confirm && !ReferenceEquals(_confirming, action))
        {
            _confirming = action;
            _confirmTimer.Stop();
            _confirmTimer.Start();
            Render();
            return;
        }
        _confirming = null;
        _confirmTimer.Stop();
        _actions.RunAction(manifest, action);
        Render();
    }
}
