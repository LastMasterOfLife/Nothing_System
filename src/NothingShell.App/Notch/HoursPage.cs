using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NothingShell.App.Rendering;
using NothingShell.Core.Notch;
using NothingShell.Core.Work;

namespace NothingShell.App.Notch;

/// <summary>
/// Pagina Ore: a vista oggi e questo mese per gruppo e per progetto, con il contatore del progetto in corso che scorre
/// dal vivo. Nel campo si definiscono progetti e gruppi e si chiedono altri periodi (<see cref="WorkCommands"/>,
/// docs/19-ore-di-lavoro.md); Invio a campo vuoto torna alla vista di oggi.
/// </summary>
public sealed class HoursPage : NotchPageView
{
    private readonly StackPanel _list = new();
    private readonly TextBlock _count;
    private readonly TextBox _input = new()
    {
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Foreground = Theme.InkBrush,
        CaretBrush = Theme.InkBrush,
        FontFamily = Theme.BodyFont,
        FontSize = 13,
        VerticalContentAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _placeholder = NotchUi.Text(13, Theme.MutedBrush, "aggiungi ore 2h30 a progetto · ore ottobre · progetto · gruppo");
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private WorkStore? _store;
    private WorkAnswer? _answer; // null: si vede la panoramica, che scorre

    public HoursPage()
    {
        var (header, _, count) = DevRows.Header("ORE");
        _count = count;

        _placeholder.VerticalAlignment = VerticalAlignment.Center;
        _placeholder.IsHitTestVisible = false;
        _placeholder.Margin = new Thickness(2, 0, 0, 0);
        var content = new Grid();
        content.Children.Add(_input);
        content.Children.Add(_placeholder);
        var box = new Border
        {
            Background = Theme.SurfaceBrush,
            CornerRadius = new CornerRadius(4),
            Height = 32,
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(0, 8, 0, 0),
            Child = content,
        };

        var scroll = new NotchScrollList(_list) { Margin = new Thickness(0, 6, 0, 0) };
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(box, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(box);
        dock.Children.Add(scroll);
        Children.Add(dock);

        _input.PreviewMouseLeftButtonDown += (_, _) => InputClicked?.Invoke();
        _input.TextChanged += (_, _) => _placeholder.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _input.PreviewKeyDown += OnKey;
        _tick.Tick += (_, _) => Render();
    }

    public override NotchPage Page => NotchPage.Hours;

    /// <summary>Clic sul campo: il notch non prende il focus da solo, va chiesto prima che si possa scrivere.</summary>
    public event Action? InputClicked;

    /// <summary>Esc nel campo: la tastiera torna alla finestra di prima.</summary>
    public event Action? InputCancelled;


    /// <summary>Clic sul nome di un gruppo: si apre il suo calendario con il dettaglio.</summary>
    public event Action<string>? GroupClicked;

    public bool IsMouseOverInput => _input.IsMouseOver;

    public bool IsInputFocused => _input.IsKeyboardFocused;

    public void FocusInput() => Keyboard.Focus(_input);

    public void ClearInput() => _input.Clear();

    public void SetStore(WorkStore? store)
    {
        _store = store;
        _answer = null;
        Render();
    }

    /// <summary>Scrive nel campo ed esegue, senza passare dalla tastiera (per <c>--snapshot</c>).</summary>
    public void RunForSnapshot(string text)
    {
        _input.Text = text;
        Execute(text);
    }

    public override void OnShown()
    {
        Render();
        _tick.Start();
    }

    public override void OnHidden() => _tick.Stop();

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            InputCancelled?.Invoke();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Execute(_input.Text);
        }
    }

    private void Execute(string text)
    {
        if (_store is null)
            return;
        if (string.IsNullOrWhiteSpace(text))
        {
            _answer = null;
            Render();
            return;
        }
        _answer = WorkCommands.Run(text, _store, DateTime.Now);
        if (_answer.Config is null && _answer.Changed)
            _input.Clear();
        if (_answer.Config is { } config)
        {
            _store.SetConfig(config);
            _input.Clear();
        }
        Render();
    }

    private void Render()
    {
        _list.Children.Clear();
        if (_store is null)
        {
            _count.Text = string.Empty;
            _list.Children.Add(DevRows.Empty());
            return;
        }
        var now = DateTime.Now;
        var answer = _answer ?? WorkCommands.Overview(_store, now);
        _count.Text = Live(now) ?? (_answer is null ? answer.Title : string.Empty); // dopo un comando il titolo è già nella lista
        if (_answer is not null)
            _list.Children.Add(Row(new WorkRow(string.Empty, answer.Title, false), true));
        foreach (var row in answer.Rows)
            _list.Children.Add(Row(row, false));
    }

    /// <summary>Il progetto in corso e quanto ha oggi, col tempo trascorso dall'ultimo campione: il contatore scorre ogni secondo.</summary>
    private string? Live(DateTime now)
    {
        if (_store?.Tracker.Current is not { } project)
            return null;
        var today = DateOnly.FromDateTime(now);
        var seconds = _store.Log.Seconds(project, today, today);
        if (_store.Tracker.LastSample is { } last && now - last < WorkTracker.MaxGap)
            seconds += (now - last).TotalSeconds;
        var time = TimeSpan.FromSeconds(seconds);
        return $"● {project} {(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}";
    }

    private Border Row(WorkRow row, bool title)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = row.Label.Length == 0 ? new GridLength(0) : new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = row.Label.Length == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var label = NotchUi.Text(13, row.IsGroup ? Theme.AccentBrush : Theme.InkBrush);
        label.Text = row.Label; // senza il filtro dei punti, che cambierebbe i nomi
        var value = NotchUi.Text(12, title ? Theme.AccentBrush : Theme.MutedBrush);
        value.Text = row.Value;
        value.TextWrapping = TextWrapping.Wrap;
        value.TextTrimming = TextTrimming.None;
        value.Margin = new Thickness(row.Label.Length == 0 ? 0 : 12, 0, 0, 0);
        Grid.SetColumn(value, 1);
        grid.Children.Add(label);
        grid.Children.Add(value);
        var border = new Border { Padding = new Thickness(0, 4, 0, 4), CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, Child = grid };
        if (GroupNamed(row) is { } group)
        {
            border.Cursor = Cursors.Hand;
            border.ToolTip = "Clic: calendario e dettaglio di questo gruppo";
            border.MouseEnter += (_, _) => border.Background = Theme.SurfaceBrush;
            border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
            border.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                GroupClicked?.Invoke(group);
            };
        }
        return border;
    }

    /// <summary>Il gruppo a cui si riferisce la riga (anche "gruppo Nome", come scritto dopo una registrazione); null se non è un gruppo.</summary>
    private string? GroupNamed(WorkRow row)
    {
        if (!row.IsGroup || _store is null || row.Label.Length == 0)
            return null;
        var label = row.Label.StartsWith("gruppo ", StringComparison.OrdinalIgnoreCase) && _store.Config.Group(row.Label) is null ? row.Label[7..] : row.Label;
        return _store.Config.Group(label)?.Name;
    }
}
