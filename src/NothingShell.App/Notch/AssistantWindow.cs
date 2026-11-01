using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NothingShell.App.Interop;
using NothingShell.App.Rendering;
using NothingShell.Core.Brain;
using NothingShell.Core.Design;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Notch;

namespace NothingShell.App.Notch;

/// <summary>Cosa serve al pannello dell'assistente (implementato da ShellController).</summary>
public interface IAssistantPanelActions
{
    CommandAssistant Assistant { get; }

    /// <summary>Riga sotto la casella: chi risponde. Si aggiorna ogni volta che il pannello si apre.</summary>
    Task<string> PrepareAsync(CancellationToken token);

    void OpenTerminal();

    /// <summary>Lo stesso comando nel terminale vero, per i programmi interattivi; se critico, chiede di scrivere SI.</summary>
    void OpenInTerminal(string? phrase, string command, string? criticalReason);
}

/// <summary>
/// Il pannello che scende dal notch (docs/15-comandi-cervello.md): si scrive cosa serve, sotto si vedono i comandi che
/// partono, la risposta e le scelte. Un comando critico si conferma solo col mouse e in due clic: né Invio né un doppio
/// clic per sbaglio possono farlo partire.
/// </summary>
public sealed class AssistantWindow : Window, IAssistantView
{
    private const double PanelWidth = 640;
    private const double MaxLogHeight = 520;
    private const double Gap = 46; // sotto la pillola compatta, che resta visibile

    private readonly IAssistantPanelActions _actions;
    private readonly Border _frame = Theme.Panel(new Border
    {
        Padding = new Thickness(20, 16, 20, 16),
        VerticalAlignment = VerticalAlignment.Top,
    });
    private readonly TextBox _input = new()
    {
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Foreground = Theme.InkBrush,
        CaretBrush = Theme.InkBrush,
        FontFamily = Theme.BodyFont,
        FontSize = 17,
        VerticalContentAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _placeholder = NotchUi.Text(17, Theme.MutedBrush, "Chiedi qualcosa o scrivi un comando");
    private readonly TextBlock _who = NotchUi.Text(11, Theme.MutedBrush);
    private readonly StackPanel _log = new();
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        MaxHeight = MaxLogHeight,
        Margin = new Thickness(0, 10, 0, 0),
        Focusable = false,
    };

    private CancellationTokenSource? _running;
    private FrameworkElement? _waiting;
    private string? _phrase;
    private bool _busy;

    public AssistantWindow(IAssistantPanelActions actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        _actions = actions;
        Width = PanelWidth;
        Height = Math.Min(SystemParameters.WorkArea.Height - Gap, MaxLogHeight + 150);
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Title = "Nothing Shell — Assistente";
        Content = BuildVisualTree();
        GlassBackdrop.Attach(this, _frame);

        SourceInitialized += (_, _) => NativeMethods.AddExStyle(new WindowInteropHelper(this).Handle, WindowNative.WsExToolWindow);
        PreviewKeyDown += OnKey;
        _input.TextChanged += (_, _) => _placeholder.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Si chiude cliccando altrove, ma non mentre lavora o aspetta una risposta: si perderebbe di vista.
        Deactivated += (_, _) =>
        {
            if (!_busy)
                Hide();
        };
    }

    /// <summary>Apre o chiude il pannello sotto il notch; la conversazione resta finché non si preme NUOVA.</summary>
    public void Toggle()
    {
        if (IsVisible && IsActive)
            Hide();
        else
            Open();
    }

    /// <summary>Apre il pannello e invia il testo come se fosse stato scritto lì; se sta lavorando, resta nella casella.</summary>
    public void Ask(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Open();
        if (_busy)
            _input.Text = text;
        else
            _ = AskAsync(text);
    }

    private void Open()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = Gap;
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Show();
        WindowNative.BringToFront(hwnd);
        Activate();
        _input.Focus();
        Keyboard.Focus(_input);
        Motion.Open(_frame);
        _ = RefreshWhoAsync();
    }

    /// <summary>Contenuto di prova senza mostrare la finestra (per <c>--snapshot</c>).</summary>
    public void PrepareSnapshot(Action<AssistantWindow> fill)
    {
        ArgumentNullException.ThrowIfNull(fill);
        Motion.Suspended = true;
        _log.Children.Clear();
        _who.Text = "Qwen3.5 4B · su questo PC";
        fill(this);
        var root = (FrameworkElement)Content;
        root.Width = Width;
        root.Height = Height;
    }

    /// <summary>La domanda com'è stata scritta (per <c>--snapshot</c>).</summary>
    public void ShowQuestion(string text) => AddQuestion(text);

    private async Task RefreshWhoAsync()
    {
        try
        {
            _who.Text = await _actions.PrepareAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // si riprova alla prossima apertura
        }
    }

    private Grid BuildVisualTree()
    {
        var inputRow = new Grid { Height = 40 };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition());
        var glyph = new DotMatrixView { Pitch = 2.5, Dots = DotGrid.From(DotIcons.Terminal), DotColor = Theme.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        _placeholder.VerticalAlignment = VerticalAlignment.Center;
        _placeholder.IsHitTestVisible = false;
        Grid.SetColumn(_input, 1);
        Grid.SetColumn(_placeholder, 1);
        inputRow.Children.Add(glyph);
        inputRow.Children.Add(_input);
        inputRow.Children.Add(_placeholder);

        var tools = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        _who.VerticalAlignment = VerticalAlignment.Center;
        tools.Children.Add(_who);
        var buttons = DevRows.Buttons(
            DevRows.Button("NUOVA", NewConversation),
            DevRows.Button("TERMINALE", () =>
            {
                _actions.OpenTerminal();
                HideIfIdle();
            }),
            DevRows.Button("Chiudi", () =>
            {
                _running?.Cancel();
                Hide();
            }));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        tools.Children.Add(buttons);

        _scroll.Content = _log;
        var stack = new StackPanel();
        stack.Children.Add(inputRow);
        stack.Children.Add(tools);
        stack.Children.Add(_scroll);
        _frame.Child = stack;

        var root = new Grid(); // senza Background: sotto il pannello i clic passano
        root.Children.Add(_frame);
        return root;
    }

    private void HideIfIdle()
    {
        if (!_busy)
            Hide();
    }

    private void NewConversation()
    {
        _running?.Cancel();
        _actions.Assistant.Reset();
        _log.Children.Clear();
        _input.Clear();
        _input.Focus();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_busy)
                _running?.Cancel();
            else
                Hide();
        }
        else if (e.Key == Key.Enter && _input.IsKeyboardFocused && !_busy)
        {
            e.Handled = true;
            _ = AskAsync(_input.Text);
        }
    }

    private async Task AskAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _busy)
            return;
        _input.Clear();
        _phrase = text.Trim();
        AddQuestion(_phrase);
        _busy = true;
        _input.IsReadOnly = true;
        using var cancel = new CancellationTokenSource();
        _running = cancel;
        try
        {
            await _actions.Assistant.AskAsync(text, this, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            Note("Interrotto.");
        }
        finally
        {
            ClearWaiting();
            _running = null;
            _busy = false;
            _input.IsReadOnly = false;
            if (IsVisible)
                _input.Focus();
        }
    }

    // ── IAssistantView ──────────────────────────────────────────────────────────

    public void Thinking() => ShowWaiting("Penso…");

    public void Running(string command) => ShowWaiting(command);

    public void Finished(AssistantStep result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ClearWaiting();
        Add(StepRow(result));
    }

    public async Task<bool> ConfirmAsync(AssistantConfirm request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ClearWaiting();
        var answer = new TaskCompletionSource<bool>();
        using var registration = token.Register(() => answer.TrySetCanceled(token));
        var card = ConfirmCard(request, accepted => answer.TrySetResult(accepted));
        Add(card);
        if (IsVisible)
            Activate(); // si deve vedere anche se nel frattempo si è andati altrove
        try
        {
            return await answer.Task;
        }
        finally
        {
            _log.Children.Remove(card);
        }
    }

    public void Answer(string text, bool verified)
    {
        ArgumentNullException.ThrowIfNull(text);
        ClearWaiting();
        var answer = Selectable(text, 14, Theme.InkBrush, Theme.BodyFont);
        answer.Margin = new Thickness(0, 10, 0, 4);
        Add(answer);
        if (!verified)
            Note("Non verificata: nessun comando è riuscito a leggere questi dati dal PC.");
    }

    public void Choices(IReadOnlyList<string> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ClearWaiting();
        if (choices.Count == 0)
            return;
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        foreach (var choice in choices)
        {
            var button = DevRows.Button(choice, () => _ = AskAsync(choice));
            button.Margin = new Thickness(0, 0, 6, 6);
            row.Children.Add(button);
        }
        Add(row);
    }

    public void Note(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ClearWaiting();
        var note = NotchUi.Text(12, Theme.MutedBrush, text);
        note.TextWrapping = TextWrapping.Wrap;
        note.TextTrimming = TextTrimming.None;
        note.Margin = new Thickness(0, 6, 0, 4);
        Add(note);
    }

    public void OfferShortcut(string phrase, string command)
    {
        ArgumentNullException.ThrowIfNull(phrase);
        ArgumentNullException.ThrowIfNull(command);
        Border? button = null;
        button = DevRows.Button("RICORDA COME SCORCIATOIA", () =>
        {
            _actions.Assistant.Remember(phrase, command);
            button!.Visibility = Visibility.Collapsed;
            Note($"Ricordata: la prossima volta \"{phrase}\" parte senza modello.");
        });
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Margin = new Thickness(0, 4, 0, 4);
        Add(button);
    }

    // ── Pezzi del registro ──────────────────────────────────────────────────────

    private void AddQuestion(string text)
    {
        var line = Selectable(text, 13, Theme.MutedBrush, Theme.BodyFont);
        line.Margin = new Thickness(0, _log.Children.Count == 0 ? 2 : 16, 0, 4);
        Add(line);
    }

    private void Add(UIElement element)
    {
        _log.Children.Add(element);
        Motion.Enter(element, 8);
        _scroll.ScrollToEnd();
    }

    private void ShowWaiting(string text)
    {
        ClearWaiting();
        var line = NotchUi.Text(12, Theme.MutedBrush, text);
        line.FontFamily = text.EndsWith('…') ? Theme.BodyFont : MonoFont;
        line.Margin = new Thickness(0, 4, 0, 4);
        _waiting = line;
        _log.Children.Add(line);
        Motion.Breathe(line);
        _scroll.ScrollToEnd();
    }

    private void ClearWaiting()
    {
        if (_waiting is not null)
            _log.Children.Remove(_waiting);
        _waiting = null;
    }

    private static FontFamily MonoFont { get; } = new("Cascadia Mono, Consolas");

    /// <summary>Testo che si può selezionare e copiare, senza l'aspetto di una casella.</summary>
    private static TextBox Selectable(string text, double size, Brush foreground, FontFamily font) => new()
    {
        Text = text,
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Foreground = foreground,
        CaretBrush = Brushes.Transparent,
        SelectionBrush = Theme.MutedBrush,
        FontFamily = font,
        FontSize = size,
        Padding = new Thickness(0),
    };

    /// <summary>Il comando eseguito con l'esito; un clic mostra o nasconde quello che ha scritto.</summary>
    private static StackPanel StepRow(AssistantStep step)
    {
        var ok = step.Run.Status == AssistantRunStatus.Ok;
        var refused = step.Run.Status is AssistantRunStatus.Refused or AssistantRunStatus.Rewrite;
        var mark = ok ? DevRows.Icon(DotIcons.Check, Theme.Ink, 1.5) : DevRows.Icon(DotIcons.Close, refused ? Theme.Muted : Theme.Critical, 1.5);
        mark.VerticalAlignment = VerticalAlignment.Top;
        mark.Margin = new Thickness(0, 3, 10, 0);
        var command = NotchUi.Text(12, ok ? Theme.InkBrush : Theme.MutedBrush, step.Command);
        command.FontFamily = MonoFont;
        command.TextWrapping = TextWrapping.Wrap;
        command.TextTrimming = TextTrimming.None;

        var header = new DockPanel();
        DockPanel.SetDock(mark, Dock.Left);
        header.Children.Add(mark);
        header.Children.Add(command);

        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 2) };
        var head = new Border { Child = header, Padding = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent };
        panel.Children.Add(head);

        var body = refused ? $"Non è partito: {step.Run.Reason}"
            : string.Join('\n', new[] { step.Run.Output, step.Run.Error }.Where(t => !string.IsNullOrWhiteSpace(t)));
        if (body.Length == 0)
            body = ok ? "(nessun risultato)" : "non è riuscito";
        var output = Selectable(body, 11, ok ? Theme.MutedBrush : Theme.CriticalBrush, MonoFont);
        output.TextWrapping = TextWrapping.NoWrap;
        output.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        output.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        output.MaxHeight = 220;
        output.Margin = new Thickness(0, 4, 0, 4);
        output.Visibility = step.ShowOutput || !ok ? Visibility.Visible : Visibility.Collapsed;
        panel.Children.Add(output);

        head.Cursor = Cursors.Hand;
        head.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            output.Visibility = output.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        };
        return panel;
    }

    private Border ConfirmCard(AssistantConfirm request, Action<bool> answer)
    {
        var critical = request.Level == ConfirmLevel.Critical;
        var accent = critical ? Theme.CriticalBrush : Theme.MutedBrush;
        var title = DevRows.Dots(critical ? "COMANDO CRITICO" : "VUOI ESEGUIRLO?", critical ? Theme.Critical : Theme.Ink, 1.8);
        title.HorizontalAlignment = HorizontalAlignment.Left;
        var reason = NotchUi.Text(12, critical ? Theme.CriticalBrush : Theme.MutedBrush, request.Reason);
        reason.TextWrapping = TextWrapping.Wrap;
        reason.TextTrimming = TextTrimming.None;
        reason.Margin = new Thickness(0, 8, 0, 8);
        var command = Selectable(request.Command, 12, Theme.InkBrush, MonoFont); // tutto, mai tagliato: si conferma ciò che si legge

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(DevRows.Button("ANNULLA", () => answer(false)));
        buttons.Children.Add(DevRows.Button("NEL TERMINALE", () =>
        {
            _actions.OpenInTerminal(_phrase, request.Command, critical ? request.Reason : null);
            answer(false);
        }));
        buttons.Children.Add(critical ? ArmedButton(() => answer(true)) : DevRows.Button("ESEGUI", () => answer(true)));

        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(reason);
        stack.Children.Add(command);
        stack.Children.Add(buttons);
        return new Border
        {
            Child = stack,
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 8, 0, 8),
        };
    }

    /// <summary>Due clic distinti (<see cref="TwoStepConfirm"/>). Non prende il focus: Invio non lo preme.</summary>
    private static Border ArmedButton(Action confirmed)
    {
        var steps = new TwoStepConfirm();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TwoStepConfirm.Window };
        Border? button = null;
        TextBlock? label = null;

        void Show(bool armed)
        {
            label!.Text = armed ? "CONFERMI? CLICCA ANCORA" : "ESEGUI";
            label.Foreground = armed ? Theme.CriticalBrush : Theme.InkBrush;
        }

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Show(false);
        };
        button = DevRows.Button("ESEGUI", () =>
        {
            var wasArmed = steps.IsArmed(clock.Elapsed);
            if (steps.Click(clock.Elapsed))
            {
                timer.Stop();
                Show(false);
                confirmed();
            }
            else if (!wasArmed)
            {
                timer.Start();
                Show(true);
            }
        });
        label = (TextBlock)button.Child;
        return button;
    }
}
