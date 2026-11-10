using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NothingShell.App.Rendering;
using NothingShell.Core.Design;
using NothingShell.Core.Notch;
using NothingShell.Core.PortalDebug;

namespace NothingShell.App.Notch;

/// <summary>
/// Pagina Debug del notch (docs/24-debug-potenziato.md, alta, scorre): per ogni portale collegato il pallino dello stato,
/// il nome, i numeri degli ultimi 5 minuti e DETTAGLIO, che apre la dashboard; sotto, le poche cose da guardare (errori,
/// 5xx, la richiesta più lenta, controlli che non vanno). Un clic su una di queste apre la dashboard proprio lì.
/// </summary>
public sealed class DebugNotchPage : NotchPageView
{
    private static readonly Color Green = Color.FromRgb(0x3D, 0xD6, 0x8C);

    private readonly TextBlock _count;
    private readonly StackPanel _rows = new();
    private readonly StackPanel _empty = new() { Visibility = Visibility.Collapsed };
    private IReadOnlyList<DebugPortalView> _portals = [];

    public DebugNotchPage()
    {
        var (header, _, count) = DevRows.Header("DEBUG");
        _count = count;

        var note = DevRows.Empty();
        note.Text = "Nessun portale collegato. Aggiungi nothing_debug al portale e collegalo con il codice.";
        _empty.Children.Add(note);
        var connect = DevRows.Button("COLLEGA UN PORTALE", () => SettingsRequested?.Invoke());
        connect.HorizontalAlignment = HorizontalAlignment.Left;
        connect.Margin = new Thickness(0, 10, 0, 0);
        _empty.Children.Add(connect);

        var list = new StackPanel();
        list.Children.Add(_rows);
        list.Children.Add(_empty);
        var scroll = new NotchScrollList(list) { Margin = new Thickness(0, 8, 0, 0) };
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(scroll);
        Children.Add(dock);
        Render();
    }

    public override NotchPage Page => NotchPage.Debug;

    /// <summary>Dettaglio: la dashboard del portale, sull'evento se c'è.</summary>
    public event Action<string, DebugEvent?>? Opened;

    /// <summary>Nessun portale: le impostazioni del Debug potenziato.</summary>
    public event Action? SettingsRequested;

    public override void OnShown() => Render(); // i "min fa" si rifanno a ogni apertura

    public void Update(IReadOnlyList<DebugPortalView> portals)
    {
        ArgumentNullException.ThrowIfNull(portals);
        _portals = portals;
        Render();
    }

    private void Render()
    {
        var bad = _portals.Count(p => p.Bad);
        _count.Text = _portals.Count switch
        {
            0 => string.Empty,
            1 => bad > 0 ? "1 portale · da guardare" : "1 portale",
            var n => bad > 0 ? $"{n} portali · {bad} da guardare" : $"{n} portali",
        };
        _count.Foreground = bad > 0 ? Theme.CriticalBrush : Theme.MutedBrush;
        _rows.Children.Clear();
        var now = DateTimeOffset.Now;
        for (var i = 0; i < _portals.Count; i++)
        {
            var portal = _portals[i];
            if (i > 0)
                _rows.Children.Add(new Border { Height = 1, Background = Theme.OutlineBrush, Margin = new Thickness(0, 6, 0, 6) });
            _rows.Children.Add(PortalRow(portal));
            if (portal.Highlights.Count == 0 && portal.Reach == PortalReach.Online)
                _rows.Children.Add(DevRows.Row(DevRows.Icon(DotIcons.Check, Theme.Ink), "Tutto a posto", DevRows.Detail("ultimi 30 min"), null, null, Theme.MutedBrush));
            foreach (var item in portal.Highlights)
                _rows.Children.Add(HighlightRow(portal, item, now));
        }
        _empty.Visibility = _portals.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Pallino, nome e numeri su due righe; DETTAGLIO a destra. Anche il clic sulla riga apre la dashboard.</summary>
    private Border PortalRow(DebugPortalView portal)
    {
        var dot = new Ellipse
        {
            Width = 9,
            Height = 9,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(4, 0, 0, 0),
            Fill = portal.Paused || portal.Reach == PortalReach.Unknown ? Theme.MutedBrush
                : portal.Reach != PortalReach.Online ? Theme.CriticalBrush
                : new SolidColorBrush(Green),
        };
        var name = NotchUi.Text(14, Theme.InkBrush, portal.Name);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var summary = NotchUi.Text(11, portal.Reach is PortalReach.Offline or PortalReach.Unauthorized ? Theme.CriticalBrush : Theme.MutedBrush, portal.Summary);
        summary.TextTrimming = TextTrimming.CharacterEllipsis;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        text.Children.Add(name);
        text.Children.Add(summary);
        var detail = DevRows.Button("DETTAGLIO ›", () => Opened?.Invoke(portal.Id, null), Theme.AccentBrush);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(dot);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(detail, 2);
        grid.Children.Add(detail);

        var row = new Border
        {
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(-8, 0, -8, 2),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Clic: la dashboard con tutte le informazioni",
            Child = grid,
        };
        row.MouseEnter += (_, _) => row.Background = Theme.SurfaceBrush;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Opened?.Invoke(portal.Id, null);
        };
        return row;
    }

    private Border HighlightRow(DebugPortalView portal, DebugHighlight item, DateTimeOffset now)
    {
        var (tag, color) = item.Kind switch
        {
            HighlightKind.Error => ("ERR", Theme.Critical),
            HighlightKind.ServerError => ("5XX", Theme.Critical),
            HighlightKind.Slow => ("MS", Theme.Accent),
            _ => ("CHK", item.Critical ? Theme.Critical : Theme.Accent),
        };
        var parts = new List<string>();
        if (item.Detail is { Length: > 0 } detail)
            parts.Add(detail);
        if (item.Time is { } time)
            parts.Add(DebugHighlights.Ago(time, now));
        var right = DevRows.Detail(string.Join(" · ", parts), item.Critical ? Theme.CriticalBrush : null);
        right.MaxWidth = 210;
        right.TextTrimming = TextTrimming.CharacterEllipsis;
        var row = DevRows.Row(DevRows.Dots(tag, color, 1.5), item.Text, right, () => Opened?.Invoke(portal.Id, item.Event),
            $"{item.Text}\n{string.Join(" · ", parts)}\nClic: apri nella dashboard");
        if (row.Child is Grid { Children: [_, TextBlock title, ..] })
            title.TextTrimming = TextTrimming.CharacterEllipsis;
        return row;
    }
}
