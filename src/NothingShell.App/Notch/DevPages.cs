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
    Func<ClipEntry, ClipAction?> ClipAction)
{
    /// <summary>Nessuna azione (anteprime di <c>--snapshot</c>).</summary>
    public static DevPageActions None { get; } = new(_ => { }, _ => { }, _ => true, _ => { }, _ => null);
}

/// <summary>Righe e pulsanti piccoli delle pagine Lavoro, Server e Appunti (docs/12-sviluppo.md).</summary>
internal static class DevRows
{
    public const int MaxRows = 3;

    /// <summary>Intestazione: titolo a punti a sinistra, conteggio grigio a destra.</summary>
    public static (Grid Header, DotMatrixView Title, TextBlock Count) Header(string title)
    {
        var dots = new DotMatrixView { Pitch = 2, Dots = DotText.Render(title), HorizontalAlignment = HorizontalAlignment.Left };
        var count = NotchUi.Text(11, Theme.GreyBrush);
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
        var title = NotchUi.Text(13, textBrush ?? Theme.WhiteBrush, text);
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
            CornerRadius = new CornerRadius(10),
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
        var detail = NotchUi.Text(11, brush ?? Theme.GreyBrush, text);
        detail.VerticalAlignment = VerticalAlignment.Center;
        return detail;
    }

    /// <summary>Pulsante a pillola piccolo dentro una riga; il clic non arriva alla riga.</summary>
    public static Border Button(string label, Action click, Brush? foreground = null)
    {
        var text = NotchUi.Text(10, foreground ?? Theme.WhiteBrush, label);
        var button = new Border
        {
            BorderBrush = Theme.GreyBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 2, 9, 2),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Theme.BlackBrush,
            Cursor = Cursors.Hand,
            Child = text,
        };
        button.MouseEnter += (_, _) => button.Background = Theme.SurfaceBrush;
        button.MouseLeave += (_, _) => button.Background = Theme.BlackBrush;
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
        var empty = NotchUi.Text(13, Theme.GreyBrush);
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
    private readonly TextBlock _ci = NotchUi.Text(11, Theme.GreyBrush);
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
        _ci.Foreground = status?.State == CiState.Failure ? Theme.RedBrush : Theme.GreyBrush;
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
            _rows.Children.Add(DevRows.Row(DevRows.Dots(TaskTracker.FormatElapsed(now - task.StartedAt), Theme.White),
                task.Title, DevRows.Detail("in corso"), click: null, task.Cwd));
        foreach (var task in _recent.Take(max - _rows.Children.Count))
        {
            var mark = task.Succeeded ? DevRows.Icon(DotIcons.Check, Theme.White) : DevRows.Icon(DotIcons.Close, Theme.Red, 1.5);
            var detail = $"{TaskTracker.FormatDuration(task.Duration)} · {TaskTracker.FormatAgo(task.FinishedAt, now)}";
            var open = task.Error is not null || task.Log is not null ? () => _open(task) : (Action?)null;
            var tooltip = task.Error is { } error ? $"{error.File}:{error.Line}\n{error.Message}" : task.Cwd;
            _rows.Children.Add(DevRows.Row(mark, task.Title, DevRows.Detail(detail, task.Succeeded ? null : Theme.RedBrush), open, tooltip));
        }
        _empty.Visibility = _rows.Children.Count == 0 && _ci.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>
/// Pagina Server (docs/12-sviluppo.md): porta a punti, processo o servizio, Apri e ✕ (due clic: il primo chiede
/// conferma per 3 s). Se il processo è di un amministratore, al posto dei pulsanti "serve l'amministratore".
/// </summary>
public sealed class ServersPage : NotchPageView
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(3);

    private readonly Action<DevServer> _open;
    private readonly Func<DevServer, bool> _kill;
    private readonly TextBlock _count;
    private readonly StackPanel _rows = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _empty = DevRows.Empty();
    private readonly DispatcherTimer _confirmTimer = new() { Interval = ConfirmTimeout };
    private IReadOnlyList<DevServer> _servers = [];
    private DevServer? _confirming;
    private DevServer? _denied;

    public ServersPage(Action<DevServer> open, Func<DevServer, bool> kill)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(kill);
        _open = open;
        _kill = kill;
        var (header, _, count) = DevRows.Header("SERVER");
        _count = count;
        _empty.Text = "Nessun server in ascolto su questo PC.";
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_rows);
        stack.Children.Add(_empty);
        Children.Add(stack);
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
        Render();
    }

    public void Update(IReadOnlyList<DevServer> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        _servers = servers;
        if (_confirming is { } c && !servers.Contains(c))
            _confirming = null;
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
        foreach (var server in _servers.Take(DevRows.MaxRows))
        {
            var name = server.Service is { } service && !service.Equals(server.Process, StringComparison.OrdinalIgnoreCase)
                ? $"{service} · {server.Process}"
                : $"{server.Service ?? server.Process} · PID {server.Pid}";
            var buttons = new List<UIElement>();
            if (server == _denied)
                buttons.Add(DevRows.Detail("serve l'amministratore", Theme.RedBrush));
            else if (server.CanOpen)
                buttons.Add(DevRows.Button("APRI", () => _open(server)));
            if (server.CanKill && server != _denied)
                buttons.Add(server == _confirming
                    ? DevRows.Button("TERMINA?", () =>
                    {
                        _confirmTimer.Stop();
                        _confirming = null;
                        if (!_kill(server))
                        {
                            _denied = server;
                            _confirmTimer.Start();
                        }
                        Render();
                    }, Theme.RedBrush)
                    : DevRows.Button("✕", () =>
                    {
                        _confirming = server;
                        _confirmTimer.Stop();
                        _confirmTimer.Start();
                        Render();
                    }));
            _rows.Children.Add(DevRows.Row(DevRows.Dots(server.Port.ToString(NotchUi.Italian), Theme.White), name,
                DevRows.Buttons([.. buttons]), server.CanOpen ? () => _open(server) : null, $"{server.Address} · {server.Process} (PID {server.Pid})"));
        }
        _empty.Visibility = _servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
            mark.Children.Add(DevRows.Dots(ClipClassifier.Tag(entry.Kind), sensitive ? Theme.Red : entry.Kind == ClipKind.Text ? Theme.Grey : Theme.White, 1.5));
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
                sensitive ? Theme.GreyBrush : null));
        }
        _empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
