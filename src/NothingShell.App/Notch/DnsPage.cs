using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NothingShell.App.Rendering;
using NothingShell.Core.Network;
using NothingShell.Core.Notch;

namespace NothingShell.App.Notch;

/// <summary>
/// Pagina DNS: si scrive un indirizzo (o un URL) nel campo e, con Invio, compaiono i record come in Google Dig. Il
/// campo accetta anche un tipo e un server: <c>miosito.it MX @8.8.8.8</c> (docs/17-dns-notch.md). Un clic su un record lo copia.
/// </summary>
public sealed class DnsPage : NotchPageView
{
    private readonly StackPanel _list = new();
    private readonly TextBlock _count;
    private readonly TextBox _input = new()
    {
        Background = System.Windows.Media.Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Foreground = Theme.InkBrush,
        CaretBrush = Theme.InkBrush,
        FontFamily = Theme.BodyFont,
        FontSize = 13,
        VerticalContentAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _placeholder = NotchUi.Text(13, Theme.MutedBrush, "Scrivi un dominio o un URL, es. miosito.it");
    private int _generation; // una ricerca nuova (o la pagina nascosta) rende vecchia quella in corso

    public DnsPage()
    {
        var (header, _, count) = DevRows.Header("DNS");
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

    public override NotchPage Page => NotchPage.Dns;

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

    /// <summary>Scrive nel campo e cerca, senza passare dalla tastiera (per <c>--snapshot</c>).</summary>
    public Task LookupAsync(string text)
    {
        _input.Text = text;
        return RunAsync(text);
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
            _ = RunAsync(_input.Text);
        }
    }

    private async Task RunAsync(string text)
    {
        var generation = ++_generation;
        if (DnsRequest.Parse(text) is not { } request)
        {
            Show(null, text.Trim().Length == 0 ? null : "Scrivi solo il nome, es. miosito.it, un URL, o un nome con il tipo e il server: miosito.it MX @8.8.8.8");
            return;
        }
        _count.Text = "…";
        try
        {
            var answer = await DnsLookup.QueryAsync(request, CancellationToken.None);
            if (generation == _generation)
                Show((request, answer), null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or SocketException or IOException or InvalidDataException or ArgumentException)
        {
            if (generation == _generation)
                Show(null, ex.Message);
        }
    }

    private void ShowHelp()
    {
        _count.Text = string.Empty;
        _list.Children.Clear();
        var help = DevRows.Empty();
        help.Text = "Scrivi un dominio o un URL e premi Invio: record A, AAAA, CNAME, MX, NS, TXT, SOA, CAA e DS. " +
                    "Per uno solo aggiungi il tipo (miosito.it MX), per un altro server @8.8.8.8.";
        _list.Children.Add(help);
    }

    private void Show((DnsRequest Request, DnsAnswer Answer)? result, string? error)
    {
        _list.Children.Clear();
        if (result is not var (request, answer))
        {
            _count.Text = string.Empty;
            var message = NotchUi.Text(12, error is null ? Theme.MutedBrush : Theme.CriticalBrush, error ?? string.Empty);
            message.TextWrapping = TextWrapping.Wrap;
            message.Margin = new Thickness(0, 10, 0, 0);
            if (error is null)
                ShowHelp();
            else
                _list.Children.Add(message);
            return;
        }

        _count.Text = $"{answer.Host} · {answer.Elapsed.TotalMilliseconds:0} ms · @{answer.Server}";
        foreach (var note in answer.Notes)
        {
            var line = NotchUi.Text(11, Theme.CriticalBrush, note);
            line.Margin = new Thickness(0, 4, 0, 0);
            _list.Children.Add(line);
        }

        // un indirizzo IP chiede il nome inverso, qualunque tipo si sia scritto
        IReadOnlyList<DnsType> types = System.Net.IPAddress.TryParse(answer.Host, out _) ? [DnsType.PTR] : request.Types;
        foreach (var type in types)
        {
            var records = answer.Of(type).ToList();
            if (records.Count == 0)
            {
                _list.Children.Add(Row(type.ToString(), "nessuno", null, muted: true));
                continue;
            }
            for (var i = 0; i < records.Count; i++)
                _list.Children.Add(Row(i == 0 ? type.ToString() : string.Empty, records[i].Value, $"{records[i].Ttl} s", muted: false));
        }
    }

    private static Border Row(string type, string value, string? ttl, bool muted)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = NotchUi.Text(11, Theme.AccentBrush);
        label.Text = type; // senza il filtro dei punti, che scriverebbe "Aaaa" e "Cname"
        label.VerticalAlignment = VerticalAlignment.Top;
        label.Margin = new Thickness(0, 2, 0, 0);
        var text = NotchUi.Text(13, muted ? Theme.MutedBrush : Theme.InkBrush, value);
        text.TextWrapping = TextWrapping.Wrap;
        text.TextTrimming = TextTrimming.None;
        Grid.SetColumn(text, 1);
        grid.Children.Add(label);
        grid.Children.Add(text);
        if (ttl is not null)
        {
            var t = NotchUi.Text(10, Theme.MutedBrush, ttl);
            t.Margin = new Thickness(8, 3, 0, 0);
            t.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(t, 2);
            grid.Children.Add(t);
        }
        var row = new Border
        {
            Padding = new Thickness(0, 3, 0, 3),
            CornerRadius = new CornerRadius(4),
            Background = System.Windows.Media.Brushes.Transparent,
            Child = grid,
        };
        if (muted)
            return row;
        row.Cursor = Cursors.Hand;
        row.ToolTip = "Clic per copiare";
        row.MouseEnter += (_, _) => row.Background = Theme.SurfaceBrush;
        row.MouseLeave += (_, _) => row.Background = System.Windows.Media.Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            try
            {
                Clipboard.SetText(value);
            }
            catch (COMException)
            {
                // gli appunti sono occupati da un'altra app: si riprova con un altro clic
            }
        };
        return row;
    }
}
