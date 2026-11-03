using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NothingShell.App.Rendering;
using NothingShell.Core.Network;
using NothingShell.Core.Notch;

namespace NothingShell.App.Notch;

/// <summary>
/// Pagina Diagnosi: si scrive un indirizzo (o un URL) nel campo e, con Invio, compaiono i record DNS come in Google Dig;
/// con un comando davanti (<c>http</c>, <c>redirect</c>, <c>propaga</c>, <c>porta</c>) i tempi di una richiesta, i redirect,
/// la propagazione DNS o chi tiene una porta (Core <see cref="Diagnosi"/>, docs/17 e docs/18). Un clic su una riga la copia.
/// </summary>
public sealed class DiagnosiPage : NotchPageView
{
    private static readonly Brush Amber = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xA2, 0x3C)));

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
    private readonly TextBlock _placeholder = NotchUi.Text(13, Theme.MutedBrush, "miosito.it · http miosito.it · redirect · propaga · porta 5432");
    private int _generation; // una ricerca nuova (o la pagina nascosta) rende vecchia quella in corso

    public DiagnosiPage()
    {
        var (header, _, count) = DevRows.Header("DIAGNOSI");
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
        ShowHelp();
    }

    public override NotchPage Page => NotchPage.Diagnosi;

    /// <summary>Clic sul campo: il notch non prende il focus da solo, va chiesto prima che si possa scrivere.</summary>
    public event Action? InputClicked;

    /// <summary>Esc nel campo: la tastiera torna alla finestra di prima.</summary>
    public event Action? InputCancelled;

    /// <summary>Il mouse è sul campo: trascinando si seleziona il testo, non si cambia pagina.</summary>
    public bool IsMouseOverInput => _input.IsMouseOver;

    public bool IsInputFocused => _input.IsKeyboardFocused;

    public void FocusInput() => Keyboard.Focus(_input);

    /// <summary>Esc: il testo scritto si butta e i risultati restano.</summary>
    public void ClearInput() => _input.Clear();

    /// <summary>Scrive nel campo ed esegue, senza passare dalla tastiera (per <c>--snapshot</c>).</summary>
    public Task RunAsync(string text)
    {
        _input.Text = text;
        return ExecuteAsync(text);
    }

    public override void OnHidden() => _generation++;

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
            _ = ExecuteAsync(_input.Text);
        }
    }

    private async Task ExecuteAsync(string text)
    {
        var generation = ++_generation;
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowHelp();
            return;
        }
        _count.Text = "…";
        var report = await Diagnosi.RunAsync(text, CancellationToken.None);
        if (generation == _generation)
            Show(report);
    }

    private void ShowHelp()
    {
        _count.Text = string.Empty;
        _list.Children.Clear();
        var help = DevRows.Empty();
        help.Text = Diagnosi.Help;
        _list.Children.Add(help);
    }

    private void Show(DiagReport report)
    {
        _list.Children.Clear();
        _count.Text = report.Title;
        var wide = report.Lines.Any(l => l.Label.Length > 6);
        foreach (var line in report.Lines)
            _list.Children.Add(Row(line, wide ? 118 : 52));
    }

    private static Brush BrushOf(DiagLevel level) => level switch
    {
        DiagLevel.Good => Theme.AccentBrush,
        DiagLevel.Warn => Amber,
        DiagLevel.Bad => Theme.CriticalBrush,
        DiagLevel.Muted => Theme.MutedBrush,
        _ => Theme.InkBrush,
    };

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Border Row(DiagLine line, double labelWidth)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = line.Label.Length == 0 ? new GridLength(0) : new GridLength(labelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = NotchUi.Text(11, Theme.AccentBrush);
        label.Text = line.Label; // senza il filtro dei punti, che scriverebbe "Aaaa" e "Cname"
        label.VerticalAlignment = VerticalAlignment.Top;
        label.Margin = new Thickness(0, 2, 0, 0);
        var text = NotchUi.Text(13, BrushOf(line.Level), line.Value);
        text.TextWrapping = TextWrapping.Wrap;
        text.TextTrimming = TextTrimming.None;
        Grid.SetColumn(text, 1);
        grid.Children.Add(label);
        grid.Children.Add(text);
        if (line.Extra is not null)
        {
            var extra = NotchUi.Text(10, Theme.MutedBrush, line.Extra);
            extra.Margin = new Thickness(8, 3, 0, 0);
            extra.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(extra, 2);
            grid.Children.Add(extra);
        }
        var row = new Border
        {
            Padding = new Thickness(0, 3, 0, 3),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Child = grid,
        };
        if (line.Level == DiagLevel.Muted)
            return row;
        row.Cursor = Cursors.Hand;
        row.ToolTip = "Clic per copiare";
        row.MouseEnter += (_, _) => row.Background = Theme.SurfaceBrush;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            try
            {
                Clipboard.SetText(line.Value);
            }
            catch (COMException)
            {
                // gli appunti sono occupati da un'altra app: si riprova con un altro clic
            }
        };
        return row;
    }
}
