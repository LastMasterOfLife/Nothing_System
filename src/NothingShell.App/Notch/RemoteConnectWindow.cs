using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NothingShell.App.Interop;
using NothingShell.App.Rendering;
using NothingShell.App.Services;
using NothingShell.Core.RemoteAccess;

namespace NothingShell.App.Notch;

/// <summary>Cosa serve alla finestra dei collegamenti (implementato da ShellController).</summary>
public interface IRemoteConnectActions
{
    /// <summary>Salva il computer e la password nel Gestore credenziali; restituisce il problema, o null.</summary>
    string? SaveComputer(RemoteComputer computer, RemoteComputer? replacing, string password);

    /// <summary>Connessione aperta; <paramref name="phrase"/> è la frase da ricordare se è stata scelta dall'elenco.</summary>
    void ComputerConnected(RemoteComputer computer, string? phrase);

    /// <summary>Il computer com'è salvato ora: mentre si guardava la finestra può aver preso un altro nome.</summary>
    RemoteComputer? Current(RemoteComputer computer);

    /// <summary>Passa una richiesta all'assistente: ogni comando parte da lì (docs/15-comandi-cervello.md).</summary>
    void AskAssistant(string text);
}

/// <summary>
/// La finestra che si apre dal campo del notch (docs/16-assistente-notch.md): la scelta fra più scorciatoie, il modulo di
/// un computer nuovo e, dal vivo, i comandi veri di una connessione con Desktop remoto.
/// </summary>
public sealed class RemoteConnectWindow : Window
{
    private const double PanelWidth = 640;
    private const double PanelHeight = 460;
    private const double Gap = 46; // sotto la pillola compatta, che resta visibile
    private static readonly TimeSpan CloseAfterSuccess = TimeSpan.FromSeconds(2.5);
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private readonly IRemoteConnectActions _actions;
    private readonly Border _frame = new()
    {
        Background = Theme.BlackBrush,
        BorderBrush = Theme.OutlineBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(28),
        Padding = new Thickness(22, 18, 22, 18),
    };
    private readonly TextBlock _title = NotchUi.Text(16, Theme.WhiteBrush);
    private readonly TextBlock _subtitle = NotchUi.Text(12, Theme.GreyBrush);
    private readonly Rendering.DotMatrixView _status = new() { Pitch = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
    private readonly ContentControl _body = new() { Focusable = false, Margin = new Thickness(0, 14, 0, 12) };
    private readonly TextBlock _hint = NotchUi.Text(11, Theme.GreyBrush);
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly List<(Border Row, Action Pick)> _rows = [];

    private FlowDocument? _log;
    private RichTextBox? _logView;
    private CancellationTokenSource? _running;
    private DispatcherTimer? _closing;
    private Action? _enter;
    private int _selected = -1;
    private bool _retrying;

    public RemoteConnectWindow(IRemoteConnectActions actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        _actions = actions;
        Width = PanelWidth;
        Height = PanelHeight;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Title = "Nothing Shell — Collegamento";
        Content = BuildVisualTree();
        PreviewKeyDown += OnKey;
        Closed += (_, _) =>
        {
            _running?.Cancel();
            _closing?.Stop();
        };
    }

    /// <summary>Connessione in corso: una nuova richiesta non deve chiudere questa finestra.</summary>
    public bool IsBusy => _running is not null;

    /// <summary>Più scorciatoie possibili: nulla parte finché non se ne sceglie una.</summary>
    public void ShowChoice(RequestRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        SetHeader("Quale scorciatoia?", route.Input, null, default);
        var list = new StackPanel();
        _rows.Clear();
        _selected = -1;
        foreach (var option in route.Likely)
            AddRow(list, option.Title, option.Detail, QualityTag(option), () => UseOption(option, route), code: true);
        if (route.Connection.IsConnection)
            AddRow(list, route.Connection.SuggestedName is { } name ? $"Nuovo computer: {name}" : "Nuovo computer", "Indirizzo, utente e password, una volta sola", "NUOVO", () => ShowForm(route));
        AddRow(list, "Chiedi all'assistente", route.Input, "COMANDO", () => Hand(route.Input));
        if (route.Different.Count > 0)
        {
            var heading = NotchUi.Text(11, Theme.GreyBrush, "SIMILI, MA NON UGUALI");
            heading.Margin = new Thickness(12, 10, 0, 6);
            list.Children.Add(heading);
            foreach (var option in route.Different)
                AddRow(list, option.Title, option.Detail, $"MANCA: {string.Join(' ', option.Missing).ToUpperInvariant()}", () => UseOption(option, route), code: true);
        }
        _body.Content = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        Footer("↑↓ per scegliere · Invio per usarla · Esc per chiudere");
        _enter = () =>
        {
            if (_selected >= 0)
                _rows[_selected].Pick();
        };
        Select(0);
    }

    /// <summary>Il computer non c'è ancora: come aprire Desktop remoto e scrivere indirizzo, utente e password, ma una volta sola.</summary>
    public void ShowForm(RequestRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        BuildForm(route.Connection.SuggestedName ?? string.Empty, route.Connection.SuggestedAddress ?? string.Empty, string.Empty, route.Input, null, route);
    }

    /// <summary>Modulo vuoto o con un computer da correggere, dalle impostazioni o dopo una connessione fallita.</summary>
    public void ShowForm(RemoteComputer? computer)
    {
        BuildForm(computer?.Name ?? string.Empty, computer?.Address ?? string.Empty, computer?.UserName ?? string.Empty, computer is null ? "Un computer con Desktop remoto" : computer.Name, computer, null);
    }

    /// <summary>La connessione, comando per comando.</summary>
    public void Connect(RemoteComputer computer, string? phrase) => _ = ConnectAsync(computer, phrase, savedPassword: false, skipReach: false);

    public void Present()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = Gap;
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Show();
        WindowNative.BringToFront(hwnd);
        Activate();
        Motion.Open(_frame);
        FocusFirstField();
    }

    /// <summary>Prepara la finestra senza mostrarla (per <c>--snapshot</c>).</summary>
    public FrameworkElement PrepareSnapshot()
    {
        var root = (FrameworkElement)Content;
        root.Width = Width;
        root.Height = Height;
        return root;
    }

    /// <summary>Riempie il registro con righe di prova (per <c>--snapshot</c>).</summary>
    public void ShowSampleLog(RemoteComputer computer)
    {
        ArgumentNullException.ThrowIfNull(computer);
        SetHeader(computer.Name, $"{computer.Address} · desktop remoto", "COLLEGATO", Theme.White);
        StartLog();
        Footer("Si chiude da sola");
        Comment($"{computer.Name} → {computer.Address}");
        Command(RemoteDesktop.ReachScript(computer));
        Output("raggiungibile", false);
        Command(RemoteDesktop.CredentialScript(computer));
        Output($"Destinazione: LegacyGeneric:target={RemoteDesktop.CredentialTarget(computer)}", false);
        Output($"Utente: {computer.UserName}", false);
        Command(RemoteDesktop.MstscCommand(computer));
        Output("avviato, processo 12408", false);
        Command(RemoteDesktop.WaitScript(computer, 12408));
        Output("connessione aperta", false);
        Comment("Il computer ha accettato il collegamento: il resto è nella finestra di Desktop remoto.");
    }

    private Grid BuildVisualTree()
    {
        // Si trascina solo dall'intestazione: altrove DragMove si prenderebbe il rilascio del clic su righe e pulsanti.
        var header = new Grid { Background = Brushes.Transparent };
        header.MouseLeftButtonDown += (_, _) => DragMove();
        var titles = new StackPanel { Margin = new Thickness(0, 0, 170, 0) };
        _subtitle.Margin = new Thickness(0, 3, 0, 0);
        titles.Children.Add(_title);
        titles.Children.Add(_subtitle);
        header.Children.Add(titles);
        header.Children.Add(_status);

        var footer = new Grid();
        _hint.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_hint);
        footer.Children.Add(_buttons);

        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(header);
        dock.Children.Add(footer);
        dock.Children.Add(_body);
        _frame.Child = dock;

        var root = new Grid();
        root.Children.Add(_frame);
        return root;
    }

    private void SetHeader(string title, string subtitle, string? status, Color color)
    {
        _title.Text = title;
        _subtitle.Text = subtitle;
        _status.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
        if (status is null)
            return;
        _status.Dots = NothingShell.Core.DotMatrix.DotText.Render(status);
        _status.DotColor = color;
    }

    private void Footer(string hint, params (string Text, Action Click)[] buttons)
    {
        _hint.Text = hint;
        _buttons.Children.Clear();
        foreach (var (text, click) in buttons)
            _buttons.Children.Add(DevRows.Button(text, click));
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                if (IsBusy)
                    _running?.Cancel();
                else
                    Close();
                break;
            case Key.Enter when _enter is not null:
                e.Handled = true;
                _enter();
                break;
            case Key.Down when _rows.Count > 0:
                e.Handled = true;
                Select(Math.Min(_selected + 1, _rows.Count - 1));
                break;
            case Key.Up when _rows.Count > 0:
                e.Handled = true;
                Select(Math.Max(_selected - 1, 0));
                break;
        }
    }

    // ── Scelta ──────────────────────────────────────────────────────────────────

    private void AddRow(StackPanel list, string title, string detail, string tag, Action pick, bool code = false)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        text.Children.Add(NotchUi.Text(14, Theme.WhiteBrush, title));
        var line = NotchUi.Text(11, Theme.GreyBrush, detail);
        if (code)
            line.FontFamily = Mono;
        line.Margin = new Thickness(0, 3, 0, 0);
        text.Children.Add(line);
        var label = NotchUi.Text(10, Theme.GreyBrush, tag);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(label, 1);
        grid.Children.Add(text);
        grid.Children.Add(label);

        var row = new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 3),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };
        var at = _rows.Count;
        row.MouseEnter += (_, _) => Select(at);
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            pick();
        };
        _rows.Add((row, pick));
        list.Children.Add(row);
    }

    private void Select(int index)
    {
        if (_selected >= 0 && _selected < _rows.Count)
            _rows[_selected].Row.Background = Brushes.Transparent;
        _selected = index;
        _rows[index].Row.Background = Theme.SurfaceBrush;
        _rows[index].Row.BringIntoView();
    }

    private static string QualityTag(RequestOption option) => option.Quality switch
    {
        MatchQuality.Exact => "ESATTA",
        MatchQuality.Contained => "CONTIENE",
        MatchQuality.Close => "QUASI UGUALE",
        _ => "SIMILE",
    };

    private void UseOption(RequestOption option, RequestRoute route)
    {
        if (option.Computer is { } computer)
            Connect(computer, route.Input); // scelta da questa frase: se riesce, la prossima volta non si chiede più
        else
            Hand(option.Title);
    }

    private void Hand(string text)
    {
        Close();
        _actions.AskAssistant(text);
    }

    // ── Modulo ──────────────────────────────────────────────────────────────────

    private void BuildForm(string name, string address, string user, string subtitle, RemoteComputer? replacing, RequestRoute? route)
    {
        SetHeader(replacing is null ? "Nuovo computer" : "Modifica computer", subtitle, null, default);
        _rows.Clear();
        _selected = -1;
        var nameBox = FormBox(name);
        var addressBox = FormBox(address);
        var userBox = FormBox(user);
        var passwordBox = new PasswordBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Theme.WhiteBrush, CaretBrush = Theme.WhiteBrush, FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center };
        var problem = NotchUi.Text(12, Theme.RedBrush);
        problem.TextWrapping = TextWrapping.Wrap;
        problem.TextTrimming = TextTrimming.None;
        problem.Margin = new Thickness(0, 10, 0, 0);

        var form = new StackPanel();
        form.Children.Add(FormField("Nome della scorciatoia", nameBox));
        form.Children.Add(FormField("Indirizzo IP o nome del computer", addressBox));
        form.Children.Add(FormField("Utente sul computer remoto (es. luca oppure .\\luca)", userBox));
        form.Children.Add(FormField(replacing is null ? "Password: se la lasci vuota la chiede Desktop remoto" : "Password: se la lasci vuota resta quella salvata", passwordBox));
        form.Children.Add(problem);
        _body.Content = form;

        void Submit()
        {
            var (computer, invalid) = RemoteDesktop.Build(nameBox.Text, addressBox.Text, userBox.Text, passwordBox.Password.Length > 0, replacing);
            if (computer is null)
            {
                problem.Text = invalid;
                return;
            }
            var password = passwordBox.Password;
            if (_actions.SaveComputer(computer, replacing, password) is { } refused)
            {
                problem.Text = refused;
                return;
            }
            passwordBox.Clear();
            if (replacing is not null && route is null && !_retrying)
                Close();
            else
                _ = ConnectAsync(computer, null, password.Length > 0, skipReach: false);
        }

        _enter = Submit;
        var buttons = new List<(string, Action)>();
        if (route is not null)
            buttons.Add(("NON È UN COMPUTER", () => Hand(route.Input)));
        buttons.Add((replacing is null ? "SALVA E COLLEGA" : _retrying ? "SALVA E RIPROVA" : "SALVA", Submit));
        Footer("La password resta nel Gestore credenziali di Windows", buttons.ToArray());
        Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusFirstField);
    }

    private static TextBox FormBox(string text) => new()
    {
        Text = text,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Foreground = Theme.WhiteBrush,
        CaretBrush = Theme.WhiteBrush,
        FontFamily = Theme.BodyFont,
        FontSize = 13,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private static StackPanel FormField(string label, Control input)
    {
        var field = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var caption = NotchUi.Text(11, Theme.GreyBrush, label);
        caption.Margin = new Thickness(0, 0, 0, 4);
        field.Children.Add(caption);
        field.Children.Add(new Border
        {
            BorderBrush = Theme.OutlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 0, 10, 0),
            Height = 32,
            Child = input,
        });
        return field;
    }

    private void FocusFirstField()
    {
        if (_body.Content is not StackPanel { Children.Count: > 0 } form)
        {
            _frame.Focusable = true; // i tasti ↑↓, Invio ed Esc devono arrivare anche senza un campo
            Keyboard.Focus(_frame);
            return;
        }
        var boxes = form.Children.OfType<StackPanel>().Select(f => f.Children.OfType<Border>().First().Child).ToArray();
        var target = boxes.FirstOrDefault(b => b is TextBox { Text.Length: 0 } or PasswordBox) ?? boxes.FirstOrDefault();
        if (target is IInputElement element)
            Keyboard.Focus(element);
    }

    // ── Connessione ─────────────────────────────────────────────────────────────

    private async Task ConnectAsync(RemoteComputer computer, string? phrase, bool savedPassword, bool skipReach)
    {
        _closing?.Stop();
        _rows.Clear();
        _selected = -1;
        _enter = null;
        SetHeader(computer.Name, $"{computer.Address} · desktop remoto", "IN CORSO", Theme.Grey);
        StartLog();
        Footer("Esc per fermare");
        using var cancel = new CancellationTokenSource();
        _running = cancel;
        try
        {
            Comment($"{computer.Name} → {computer.Address}");
            if (savedPassword)
            {
                Comment("Password salvata nel Gestore credenziali di Windows");
                Output($"CredWrite {RemoteDesktop.CredentialTarget(computer)}  utente {computer.UserName}  password ••••••••", false);
            }

            if (!skipReach && await StepAsync(RemoteDesktop.ReachScript(computer), null, cancel.Token) != 0)
            {
                Failed(computer, phrase, $"Il computer non risponde sulla porta {computer.Port}. È acceso? L'indirizzo è giusto? Desktop remoto è attivo su quel PC? (Windows Home non accetta connessioni.)");
                return;
            }

            var listed = new List<string>();
            await StepAsync(RemoteDesktop.CredentialScript(computer), listed, cancel.Token);
            if (!RemoteDesktop.HasCredential(listed))
                Comment("Nessuna password salvata: la chiederà Desktop remoto.");

            await TypeAsync(RemoteDesktop.MstscCommand(computer), cancel.Token);
            using var mstsc = StartMstsc(computer);
            if (mstsc is null)
            {
                Failed(computer, phrase, "Desktop remoto (mstsc) non si è avviato.");
                return;
            }
            Output($"avviato, processo {mstsc.Id}", false);

            // mstsc chiuso durante l'attesa: non serve aspettare oltre.
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
            mstsc.EnableRaisingEvents = true;
            mstsc.Exited += (_, _) => CancelQuietly(wait);
            int code;
            try
            {
                code = await StepAsync(RemoteDesktop.WaitScript(computer, mstsc.Id), null, wait.Token);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                Failed(computer, phrase, "Desktop remoto si è chiuso prima di collegarsi.");
                return;
            }
            if (code != 0)
            {
                // mstsc è aperto e mostra il suo messaggio: non è un errore da ripetere, e non si impara nulla.
                SetHeader(computer.Name, $"{computer.Address} · desktop remoto", "DA VEDERE", Theme.Grey);
                Comment("Desktop remoto è aperto ma non vedo ancora la connessione: guarda la sua finestra.");
                Footer("Esc per chiudere");
                return;
            }

            _actions.ComputerConnected(_actions.Current(computer) ?? computer, phrase);
            SetHeader(computer.Name, $"{computer.Address} · desktop remoto", "COLLEGATO", Theme.White);
            Comment("Il computer ha accettato il collegamento: il resto è nella finestra di Desktop remoto.");
            Footer("Si chiude da sola");
            CloseSoon();
        }
        catch (OperationCanceledException)
        {
            SetHeader(computer.Name, $"{computer.Address} · desktop remoto", "FERMATO", Theme.Grey);
            Comment("Fermato.");
            Footer("Esc per chiudere", ("RIPROVA", () => Connect(computer, phrase)));
            _enter = () => Connect(computer, phrase);
        }
        finally
        {
            _running = null;
        }
    }

    private void Failed(RemoteComputer computer, string? phrase, string reason)
    {
        SetHeader(computer.Name, $"{computer.Address} · desktop remoto", "NON COLLEGATO", Theme.Red);
        Output(reason, true);
        _enter = () => Connect(computer, phrase);
        Footer("Invio per riprovare · Esc per chiudere",
            ("CORREGGI", () =>
            {
                _retrying = true;
                ShowForm(_actions.Current(computer) ?? computer);
            }),
            ("APRI COMUNQUE", () => _ = ConnectAsync(computer, phrase, savedPassword: false, skipReach: true)),
            ("RIPROVA", () => Connect(computer, phrase)));
    }

    /// <summary>mstsc può chiudersi in ogni momento, anche quando l'attesa è già finita.</summary>
    private static void CancelQuietly(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // l'attesa è già finita
        }
    }

    private void CloseSoon()
    {
        _closing = new DispatcherTimer { Interval = CloseAfterSuccess };
        _closing.Tick += (_, _) =>
        {
            if (IsMouseOver)
                return; // si sta guardando: si chiude quando il mouse esce
            _closing?.Stop();
            Close();
        };
        _closing.Start();
    }

    private static Process? StartMstsc(RemoteComputer computer)
    {
        var start = new ProcessStartInfo(RemoteDesktop.Program) { UseShellExecute = false };
        start.ArgumentList.Add(RemoteDesktop.MstscArgument(computer));
        try
        {
            return Process.Start(start);
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Mostra lo script mentre si scrive, poi lo esegue con le righe che arrivano dal vivo.</summary>
    private async Task<int> StepAsync(string script, List<string>? collect, CancellationToken token)
    {
        await TypeAsync(script, token);
        return await ScriptRunner.RunAsync(script, (text, error) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                collect?.Add(text);
                if (text.Length > 0)
                    Output(text, error);
            });
        }, token);
    }

    // ── Registro ────────────────────────────────────────────────────────────────

    private void StartLog()
    {
        _log = new FlowDocument { PagePadding = new Thickness(0), FontFamily = Mono, FontSize = 12 };
        _logView = new RichTextBox
        {
            Document = _log,
            IsReadOnly = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Theme.WhiteBrush,
            CaretBrush = Brushes.Transparent,
            SelectionBrush = Theme.GreyBrush,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsDocumentEnabled = false,
        };
        _body.Content = _logView;
    }

    private Run AddLine(string text, Brush color, double top)
    {
        var run = new Run(text) { Foreground = color };
        _log!.Blocks.Add(new Paragraph(run) { Margin = new Thickness(0, top, 0, 0) });
        _logView!.ScrollToEnd();
        return run;
    }

    private void Comment(string text) => AddLine($"# {text}", Theme.GreyBrush, 6);

    private void Command(string text) => AddLine($"PS> {text}", Theme.WhiteBrush, 8);

    private void Output(string text, bool error) => AddLine(text, error ? Theme.RedBrush : Theme.GreyBrush, 2);

    /// <summary>Il comando compare scrivendosi, in pochi istanti: si vede cosa sta per partire, senza far aspettare.</summary>
    private async Task TypeAsync(string text, CancellationToken token)
    {
        const int frames = 12;
        var run = AddLine("PS> ", Theme.WhiteBrush, 8);
        if (Motion.Enabled)
        {
            for (var frame = 1; frame < frames; frame++)
            {
                run.Text = "PS> " + text[..(text.Length * frame / frames)];
                await Task.Delay(10, token);
            }
        }
        run.Text = "PS> " + text;
        _logView?.ScrollToEnd();
    }
}
