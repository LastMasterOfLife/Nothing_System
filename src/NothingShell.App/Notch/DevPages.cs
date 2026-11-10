using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NothingShell.App.Rendering;
using NothingShell.Core.Design;
using NothingShell.Core.Dev;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Notch;

namespace NothingShell.App.Notch;

/// <summary>Cosa fanno i clic nelle pagine Lavoro, Server e Appunti: li decide il ShellController.</summary>
public sealed record DevPageActions(
    Action<FinishedTask> OpenTask,
    Action<DevServer> OpenServer,
    Func<DevServer, bool> KillServer,
    Action<ClipEntry> CopyClip,
    Func<ClipEntry, ClipAction?> ClipAction,
    Func<DevServer, Task<DevServerDetails>> ServerDetails,
    Action<string> ShowFile)
{
    /// <summary>Nessuna azione (anteprime di <c>--snapshot</c>).</summary>
    public static DevPageActions None { get; } =
        new(_ => { }, _ => { }, _ => true, _ => { }, _ => null, _ => Task.FromResult(DevServerDetails.Empty), _ => { });
}

/// <summary>Righe e pulsanti piccoli delle pagine Lavoro, Server e Appunti (docs/12-sviluppo.md).</summary>
internal static class DevRows
{
    public const int MaxRows = 3;

    /// <summary>Intestazione: titolo a punti a sinistra, conteggio grigio a destra.</summary>
    public static (Grid Header, DotMatrixView Title, TextBlock Count) Header(string title)
    {
        var dots = new DotMatrixView { Pitch = 2, Dots = DotText.Render(title), HorizontalAlignment = HorizontalAlignment.Left };
        var count = NotchUi.Text(11, Theme.MutedBrush);
        count.HorizontalAlignment = HorizontalAlignment.Right;
        count.VerticalAlignment = VerticalAlignment.Center;
        var header = new Grid();
        header.Children.Add(dots);
        header.Children.Add(count);
        return (header, dots, count);
    }

    /// <summary>Riga alta 30: segno a sinistra (52 di larghezza), testo, dettaglio o pulsanti a destra.</summary>
    public static Border Row(UIElement mark, string text, UIElement? right, Action? click, string? tooltip = null, Brush? textBrush = null)
    {
        if (mark is FrameworkElement fe)
            fe.VerticalAlignment = VerticalAlignment.Center;
        var title = NotchUi.Text(13, textBrush ?? Theme.InkBrush, text);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(12, 0, 8, 0);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 1);
        grid.Children.Add(mark);
        grid.Children.Add(title);
        if (right is not null)
        {
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);
        }

        var row = new Border
        {
            Height = 30,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(-8, 0, -8, 0),
            Background = Brushes.Transparent,
            Child = grid,
            ToolTip = tooltip,
        };
        if (click is null)
            return row;
        row.Cursor = Cursors.Hand;
        row.MouseEnter += (_, _) => row.Background = Theme.SurfaceBrush;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return row;
    }

    public static TextBlock Detail(string text, Brush? brush = null)
    {
        var detail = NotchUi.Text(11, brush ?? Theme.MutedBrush, text);
        detail.VerticalAlignment = VerticalAlignment.Center;
        return detail;
    }

    /// <summary>Pulsante a pillola piccolo dentro una riga; il clic non arriva alla riga.</summary>
    public static Border Button(string label, Action click, Brush? foreground = null)
    {
        var text = NotchUi.Text(12, foreground ?? Theme.InkBrush, label);
        var button = new Border
        {
            BorderBrush = Theme.OutlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Theme.RadiusSmall),
            Padding = new Thickness(9, 2, 9, 3),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Child = text,
        };
        Motion.StateLayer(button, Theme.Surface, Theme.SurfaceHover);
        Motion.Press(button);
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return button;
    }

    public static StackPanel Buttons(params UIElement[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var button in buttons)
            row.Children.Add(button);
        return row;
    }

    public static DotMatrixView Dots(string text, Color color, double pitch = 2) =>
        new() { Pitch = pitch, Dots = DotText.Render(text), DotColor = color };

    public static DotMatrixView Icon(DotGlyph glyph, Color color, double pitch = 2) =>
        new() { Pitch = pitch, Dots = DotGrid.From(glyph), DotColor = color };

    public static TextBlock Empty()
    {
        var empty = NotchUi.Text(13, Theme.MutedBrush);
        empty.Margin = new Thickness(0, 18, 0, 0);
        empty.TextWrapping = TextWrapping.Wrap;
        return empty;
    }
}

/// <summary>
/// Pagina Lavoro (docs/12-sviluppo.md): comandi in corso con il tempo, poi gli ultimi finiti con ✓/✗, durata e
/// quanto tempo fa; un clic apre l'errore, altrimenti il log. Sotto, la riga della CI.
/// </summary>
public sealed class WorkPage : NotchPageView
{
    private readonly Action<FinishedTask> _open;
    private readonly TextBlock _count;
    private readonly StackPanel _rows = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _ci = NotchUi.Text(11, Theme.MutedBrush);
    private readonly TextBlock _empty = DevRows.Empty();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private IReadOnlyList<RunningTask> _running = [];
    private IReadOnlyList<FinishedTask> _recent = [];

    public WorkPage(Action<FinishedTask> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        _open = open;
        var (header, _, count) = DevRows.Header("LAVORO");
        _count = count;
        _ci.Margin = new Thickness(0, 4, 0, 0);
        _ci.Visibility = Visibility.Collapsed;
        _empty.Text = "Nessun comando: nel terminale scrivi ns seguito dal comando (modulo in Impostazioni › Sviluppo).";
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_rows);
        stack.Children.Add(_empty);
        stack.Children.Add(_ci);
        Children.Add(stack);
        _tick.Tick += (_, _) => Render();
    }

    public override NotchPage Page => NotchPage.Work;

    public override void OnShown()
    {
        Render();
        if (_running.Count > 0)
            _tick.Start();
    }

    public override void OnHidden() => _tick.Stop();

    public void Update(IReadOnlyList<RunningTask> running, IReadOnlyList<FinishedTask> recent)
    {
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(recent);
        _running = running;
        _recent = recent;
        if (running.Count == 0)
            _tick.Stop();
        else if (IsVisible)
            _tick.Start();
        Render();
    }

    /// <summary>Riga della CI: stato della CI seguita, o il problema (gh mancante); null la nasconde.</summary>
    public void SetCi(CiStatus? status, string? problem)
    {
        _ci.Text = status is null ? problem ?? string.Empty : status.State switch
        {
            CiState.Pending => $"CI · {CiRuns.Summary(status.Runs)} · in corso",
            CiState.Success => $"CI OK · {status.Repository} · {status.Branch}",
            _ => $"CI FALLITA · {CiRuns.Summary(status.Runs)}",
        };
        _ci.Foreground = status?.State == CiState.Failure ? Theme.CriticalBrush : Theme.MutedBrush;
        _ci.Visibility = _ci.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Render();
    }

    private void Render()
    {
        var now = DateTimeOffset.Now;
        var max = _ci.Visibility == Visibility.Visible ? DevRows.MaxRows - 1 : DevRows.MaxRows;
        _count.Text = _running.Count > 0 ? $"{_running.Count} in corso" : _recent.Count > 0 ? $"{_recent.Count} recenti" : string.Empty;
        _rows.Children.Clear();
        foreach (var task in _running.Take(max))
            _rows.Children.Add(DevRows.Row(DevRows.Dots(TaskTracker.FormatElapsed(now - task.StartedAt), Theme.Ink),
                task.Title, DevRows.Detail("in corso"), click: null, task.Cwd));
        foreach (var task in _recent.Take(max - _rows.Children.Count))
        {
            var mark = task.Succeeded ? DevRows.Icon(DotIcons.Check, Theme.Ink) : DevRows.Icon(DotIcons.Close, Theme.Critical, 1.5);
            var detail = $"{TaskTracker.FormatDuration(task.Duration)} · {TaskTracker.FormatAgo(task.FinishedAt, now)}";
            var open = task.Error is not null || task.Log is not null ? () => _open(task) : (Action?)null;
            var tooltip = task.Error is { } error ? $"{error.File}:{error.Line}\n{error.Message}" : task.Cwd;
            _rows.Children.Add(DevRows.Row(mark, task.Title, DevRows.Detail(detail, task.Succeeded ? null : Theme.CriticalBrush), open, tooltip));
        }
        _empty.Visibility = _rows.Children.Count == 0 && _ci.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>
/// Pagina Server (docs/12-sviluppo.md, alta, scorre): tutti i server con la porta a punti, processo o servizio, Apri e ✕
/// (due clic: il primo chiede conferma per 3 s). Se il processo è di un amministratore, al posto dei pulsanti "serve
/// l'amministratore". Un clic su una riga apre il dettaglio: indirizzi e chi li raggiunge, eseguibile, riga di comando,
/// chi l'ha avviato, da quando, memoria e le altre porte dello stesso processo; ‹ SERVER torna all'elenco.
/// </summary>
public sealed class ServersPage : NotchPageView
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(3);
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private readonly DevPageActions _actions;
    private readonly TextBlock _count;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _empty = DevRows.Empty();
    private readonly DockPanel _listView = new();
    private readonly DockPanel _detailView = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _detail = new();
    private readonly NotchScrollList _detailScroll;
    private readonly TextBlock _detailCount;
    private readonly DispatcherTimer _confirmTimer = new() { Interval = ConfirmTimeout };
    private IReadOnlyList<DevServer> _servers = [];
    private DevServer? _confirming;
    private DevServer? _denied;
    private DevServer? _selected;
    private DevServerDetails? _details;
    private int _generation; // un dettaglio nuovo (o l'elenco) rende vecchia la lettura in corso

    public ServersPage(DevPageActions actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        _actions = actions;

        var (header, _, count) = DevRows.Header("SERVER");
        _count = count;
        _empty.Text = "Nessun server in ascolto su questo PC.";
        var list = new StackPanel();
        list.Children.Add(_rows);
        list.Children.Add(_empty);
        var scroll = new NotchScrollList(list) { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(header, Dock.Top);
        _listView.Children.Add(header);
        _listView.Children.Add(scroll);

        var back = DevRows.Button("‹ SERVER", ShowList);
        back.Margin = new Thickness(0);
        back.HorizontalAlignment = HorizontalAlignment.Left;
        _detailCount = NotchUi.Text(11, Theme.MutedBrush);
        _detailCount.HorizontalAlignment = HorizontalAlignment.Right;
        _detailCount.VerticalAlignment = VerticalAlignment.Center;
        var detailHeader = new Grid();
        detailHeader.Children.Add(back);
        detailHeader.Children.Add(_detailCount);
        _detailScroll = new NotchScrollList(_detail) { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(detailHeader, Dock.Top);
        _detailView.Children.Add(detailHeader);
        _detailView.Children.Add(_detailScroll);

        Children.Add(_listView);
        Children.Add(_detailView);
        _confirmTimer.Tick += (_, _) =>
        {
            _confirmTimer.Stop();
            _confirming = null;
            _denied = null;
            Render();
        };
        Render();
    }

    public override NotchPage Page => NotchPage.Servers;

    public override void OnHidden()
    {
        _confirmTimer.Stop();
        _confirming = null;
        _denied = null;
        ShowList();
    }

    public void Update(IReadOnlyList<DevServer> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        _servers = servers;
        if (_confirming is { } c && !servers.Contains(c))
            _confirming = null;
        if (_selected is { } s && !servers.Contains(s))
        {
            ShowList(); // il server si è fermato
            return;
        }
        Render();
    }

    private void ShowList()
    {
        _generation++;
        _selected = null;
        _details = null;
        _detailView.Visibility = Visibility.Collapsed;
        _listView.Visibility = Visibility.Visible;
        Render();
    }

    private async void ShowDetail(DevServer server)
    {
        var generation = ++_generation;
        _selected = server;
        _details = null;
        _listView.Visibility = Visibility.Collapsed;
        _detailView.Visibility = Visibility.Visible;
        _detailScroll.ScrollToTop();
        Render();
        DevServerDetails details;
        try
        {
            details = await _actions.ServerDetails(server);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            details = DevServerDetails.Empty;
        }
        if (generation != _generation)
            return;
        _details = details;
        Render();
    }

    private void Render()
    {
        _count.Text = _servers.Count switch
        {
            0 => string.Empty,
            1 => "1 server",
            var n => $"{n} server",
        };
        _rows.Children.Clear();
        foreach (var server in _servers)
            _rows.Children.Add(DevRows.Row(DevRows.Dots(server.Port.ToString(NotchUi.Italian), Theme.Ink), Label(server),
                DevRows.Buttons([.. Buttons(server)]), () => ShowDetail(server), "Clic: tutti i dettagli"));
        _empty.Visibility = _servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_selected is { } selected)
            RenderDetail(selected);
    }

    private static string Label(DevServer server) =>
        server.Service is { } service && !service.Equals(server.Process, StringComparison.OrdinalIgnoreCase)
            ? $"{service} · {server.Process}"
            : $"{server.Service ?? server.Process} · PID {server.Pid}";

    /// <summary>Apri e ✕ (poi TERMINA?), o "serve l'amministratore" in rosso.</summary>
    private List<UIElement> Buttons(DevServer server)
    {
        var buttons = new List<UIElement>();
        if (server == _denied)
            buttons.Add(DevRows.Detail("serve l'amministratore", Theme.CriticalBrush));
        else if (server.CanOpen)
            buttons.Add(DevRows.Button("APRI", () => _actions.OpenServer(server)));
        if (server.CanKill && server != _denied)
            buttons.Add(server == _confirming
                ? DevRows.Button("TERMINA?", () =>
                {
                    _confirmTimer.Stop();
                    _confirming = null;
                    if (!_actions.KillServer(server))
                    {
                        _denied = server;
                        _confirmTimer.Start();
                    }
                    Render();
                }, Theme.CriticalBrush)
                : DevRows.Button("✕", () =>
                {
                    _confirming = server;
                    _confirmTimer.Stop();
                    _confirmTimer.Start();
                    Render();
                }));
        return buttons;
    }

    private void RenderDetail(DevServer server)
    {
        _detailCount.Text = server.Address;
        _detail.Children.Clear();
        var kind = server.Service ?? (server.CanOpen ? "server web" : "servizio");
        var where = server.IsBackgroundService ? "servizio di Windows" : "app";
        _detail.Children.Add(TallRows.Item(DevRows.Dots(server.Port.ToString(NotchUi.Italian), Theme.Ink),
            Label(server), null, $"{kind} · {where}", DevRows.Buttons([.. Buttons(server)])));

        if (_details is not { } details)
        {
            _detail.Children.Add(Hint("Lettura del processo…"));
            return;
        }

        _detail.Children.Add(TallRows.Caption("RETE"));
        _detail.Children.Add(Field("Indirizzo", $"http://{server.Address}"));
        if (details.Addresses.Count > 0)
            _detail.Children.Add(Field("In ascolto su", string.Join("   ", details.Addresses.Select(a => DevServers.FormatBinding(a, server.Port)))));
        _detail.Children.Add(Field("Raggiungibile", DevServers.Reach(details.Addresses)));
        var others = _servers.Where(s => s.Pid == server.Pid && s.Port != server.Port).Select(s => s.Port.ToString(NotchUi.Italian)).ToArray();
        if (others.Length > 0)
            _detail.Children.Add(Field("Altre porte", string.Join(", ", others)));

        _detail.Children.Add(TallRows.Caption("PROCESSO"));
        _detail.Children.Add(Field("Nome", $"{server.Process} · PID {server.Pid}"));
        if (details.Started is { } started)
            _detail.Children.Add(Field("Avviato", $"{started.ToLocalTime().ToString("d MMM HH:mm", NotchUi.Italian)} · da {Uptime(DateTimeOffset.Now - started)}"));
        if (details.Parent is { } parent)
            _detail.Children.Add(Field("Avviato da", parent));
        if (details.Memory is { } memory)
            _detail.Children.Add(Field("Memoria", DownloadTracker.FormatBytes(memory)));
        if (details.Threads is { } threads)
            _detail.Children.Add(Field("Thread", threads.ToString(NotchUi.Italian)));

        if (details.Path is { } path)
        {
            _detail.Children.Add(TallRows.Caption("ESEGUIBILE"));
            _detail.Children.Add(Block(path, DevRows.Button("CARTELLA", () => _actions.ShowFile(path))));
        }
        if (details.CommandLine is { } command)
        {
            _detail.Children.Add(TallRows.Caption("COMANDO"));
            _detail.Children.Add(Block(command, DevRows.Button("COPIA", () => Copy(command))));
        }
        if (details.Path is null && details.CommandLine is null)
            _detail.Children.Add(Hint("Eseguibile e comando non si leggono: il processo è di un amministratore."));
    }

    /// <summary>"45 s", "12 min", "3 h 20 min", "2 g 4 h".</summary>
    private static string Uptime(TimeSpan span) => span.TotalMinutes switch
    {
        < 1 => $"{Math.Max(0, (int)span.TotalSeconds)} s",
        < 60 => $"{(int)span.TotalMinutes} min",
        < 24 * 60 => span.Minutes > 0 ? $"{(int)span.TotalHours} h {span.Minutes} min" : $"{(int)span.TotalHours} h",
        _ => span.Hours > 0 ? $"{(int)span.TotalDays} g {span.Hours} h" : $"{(int)span.TotalDays} g",
    };

    /// <summary>Etichetta grigia a sinistra, valore bianco a destra che va a capo; un clic copia il valore.</summary>
    private Border Field(string label, string value)
    {
        var key = NotchUi.Text(11, Theme.MutedBrush, label);
        key.VerticalAlignment = VerticalAlignment.Top;
        key.Margin = new Thickness(0, 1, 0, 0);
        var text = NotchUi.Text(12, Theme.InkBrush, value);
        text.TextWrapping = TextWrapping.Wrap;
        text.TextTrimming = TextTrimming.None;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(text, 1);
        grid.Children.Add(key);
        grid.Children.Add(text);
        return Copyable(grid, value, 2, 2);
    }

    /// <summary>Testo lungo (percorso, comando) a tutta larghezza, a capo, col pulsante sotto a destra.</summary>
    private Border Block(string value, FrameworkElement button)
    {
        var text = NotchUi.Text(11, Theme.InkBrush, value);
        text.TextWrapping = TextWrapping.Wrap;
        text.TextTrimming = TextTrimming.None;
        text.FontFamily = Mono;
        button.HorizontalAlignment = HorizontalAlignment.Right;
        button.Margin = new Thickness(0, 4, 0, 0);
        var stack = new StackPanel();
        stack.Children.Add(text);
        stack.Children.Add(button);
        return Copyable(stack, value, 2, 4);
    }

    private Border Copyable(UIElement child, string value, double top, double bottom)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, top, 8, bottom),
            Margin = new Thickness(-8, 0, -8, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = "Clic: copia",
            Child = child,
        };
        row.MouseEnter += (_, _) => row.Background = Theme.SurfaceBrush;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Copy(value);
        };
        return row;
    }

    private void Copy(string value)
    {
        try
        {
            Clipboard.SetText(value);
            _detailCount.Text = "copiato";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            _detailCount.Text = "appunti occupati";
        }
    }

    private static TextBlock Hint(string text)
    {
        var hint = NotchUi.Text(12, Theme.MutedBrush, text);
        hint.TextWrapping = TextWrapping.Wrap;
        hint.TextTrimming = TextTrimming.None;
        hint.Margin = new Thickness(0, 8, 0, 0);
        return hint;
    }
}

/// <summary>Azione principale di una voce degli appunti (Formatta, Apri, Copia HEX…), o null.</summary>
public sealed record ClipAction(string Label, Action Run);

/// <summary>
/// Pagina Appunti (docs/12-sviluppo.md): le ultime tre copie con il tipo a punti, l'anteprima e l'azione
/// principale; un clic sulla riga la rimette negli appunti (non per i segreti).
/// </summary>
public sealed class ClipboardPage : NotchPageView
{
    private readonly Action<ClipEntry> _copy;
    private readonly Func<ClipEntry, ClipAction?> _action;
    private readonly TextBlock _count;
    private readonly StackPanel _rows = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _empty = DevRows.Empty();

    public ClipboardPage(Action<ClipEntry> copy, Func<ClipEntry, ClipAction?> action)
    {
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(action);
        _copy = copy;
        _action = action;
        var (header, _, count) = DevRows.Header("APPUNTI");
        _count = count;
        _empty.Text = "Copia del testo: qui le ultime copie, con le azioni per JSON, errori, colori e link.";
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_rows);
        stack.Children.Add(_empty);
        Children.Add(stack);
        Update([]);
    }

    public override NotchPage Page => NotchPage.Clipboard;

    public void Update(IReadOnlyList<ClipEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _count.Text = entries.Count > 0 ? $"{entries.Count} in memoria" : string.Empty;
        _rows.Children.Clear();
        foreach (var entry in entries.Take(DevRows.MaxRows))
        {
            var sensitive = ClipClassifier.IsSensitive(entry.Kind);
            var mark = new StackPanel { Orientation = Orientation.Horizontal };
            mark.Children.Add(DevRows.Dots(ClipClassifier.Tag(entry.Kind), sensitive ? Theme.Critical : entry.Kind == ClipKind.Text ? Theme.Muted : Theme.Ink, 1.5));
            if (entry.Kind == ClipKind.Color && entry.Text is { } text && ClipClassifier.Color(text.Trim()) is { } color)
                mark.Children.Add(new Ellipse
                {
                    Width = 10,
                    Height = 10,
                    Margin = new Thickness(6, 0, 0, 0),
                    Fill = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B)),
                    Stroke = Theme.OutlineBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            var action = _action(entry);
            _rows.Children.Add(DevRows.Row(mark, entry.Preview,
                action is null ? null : DevRows.Button(action.Label, action.Run),
                entry.Text is null ? null : () => _copy(entry),
                entry.Text is null ? "Segreto: non si conserva" : "Clic: di nuovo negli appunti",
                sensitive ? Theme.MutedBrush : null));
        }
        _empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
