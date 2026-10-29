using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NothingShell.App.Rendering;
using NothingShell.Core.Connectors;
using NothingShell.Core.Dev;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Widgets;

namespace NothingShell.App.Widgets;

/// <summary>
/// Widget di un connettore (docs/04-widgets.md): nome e stato in alto, i valori a punti in una griglia (rossi oltre
/// soglia) con etichetta e unità. La taglia segue il numero di valori (<see cref="ConnectorWidgetLayout"/>).
/// </summary>
public sealed class ConnectorWidget : WidgetView
{
    private const double MinPitch = 1.5;
    private const double ColumnGap = 16;

    private readonly TextBlock _name = Label(string.Empty, 11, Theme.GreyBrush);
    private readonly TextBlock _status = Label(string.Empty, 11, Theme.GreyBrush);
    private readonly Grid _cells = new();
    private ConnectorState? _state;

    public ConnectorWidget(ConnectorManifest manifest)
        : base(ConnectorWidgetLayout.WidgetId(Guard(manifest).Id), ConnectorWidgetLayout.Size(Count(manifest, 0)), header: null)
    {
        Manifest = manifest;
        _name.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.Margin = new Thickness(8, 0, 0, 0);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(_status, Dock.Right);
        header.Children.Add(_status);
        header.Children.Add(_name);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_cells);
        Body.Children.Add(root);
        Update(new ConnectorState(manifest, ConnectorStatus.Waiting, [], null, null));
    }

    public ConnectorManifest Manifest { get; private set; }

    /// <summary>Sollevato quando cambia la taglia (sono arrivati più valori di quelli dichiarati).</summary>
    public event Action<ConnectorWidget>? Resized;

    public void Update(ConnectorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        Manifest = state.Manifest;
        var count = Count(state.Manifest, state.Readings.Count);
        var size = ConnectorWidgetLayout.Size(count);
        if (size.Width != Width || size.Height != Height)
        {
            Width = size.Width;
            Height = size.Height;
            Resized?.Invoke(this);
        }

        _name.Text = state.Manifest.Name.ToUpperInvariant();
        _name.ToolTip = state.Error;
        (_status.Text, _status.Foreground) = state.Status switch
        {
            ConnectorStatus.Error => ("errore", Theme.RedBrush),
            ConnectorStatus.NeedsGrant => ("permesso", Theme.RedBrush),
            ConnectorStatus.Waiting => ("...", Theme.GreyBrush),
            _ when state.UpdatedAt is { } at => (TaskTracker.FormatAgo(at, DateTimeOffset.Now), Theme.GreyBrush),
            _ => (string.Empty, Theme.GreyBrush),
        };
        Render(count);
    }

    /// <summary>Riscrive "2 min fa" senza aspettare il prossimo giro del connettore.</summary>
    public void Refresh()
    {
        if (_state is { Status: ConnectorStatus.Ok, UpdatedAt: { } at })
            _status.Text = TaskTracker.FormatAgo(at, DateTimeOffset.Now);
    }

    private void Render(int count)
    {
        var state = _state!;
        _cells.Children.Clear();
        _cells.RowDefinitions.Clear();
        _cells.ColumnDefinitions.Clear();
        var columns = ConnectorWidgetLayout.Columns(count);
        var rows = ConnectorWidgetLayout.Rows(count);
        for (var c = 0; c < columns; c++)
            _cells.ColumnDefinitions.Add(new ColumnDefinition());
        for (var r = 0; r < rows; r++)
            _cells.RowDefinitions.Add(new RowDefinition());

        if (state.Readings.Count == 0)
        {
            var note = Label(state.Error ?? state.Status switch
            {
                ConnectorStatus.NeedsGrant => "Consenti comandi e azioni in Impostazioni › Connettori",
                ConnectorStatus.Error => "nessun valore",
                _ => "in attesa dei primi valori",
            }, 11, state.Status == ConnectorStatus.Waiting ? Theme.GreyBrush : Theme.RedBrush);
            note.TextWrapping = TextWrapping.Wrap;
            Grid.SetColumnSpan(note, columns);
            Grid.SetRowSpan(note, rows);
            _cells.Children.Add(note);
            return;
        }

        // spazio utile di una cella: il riquadro meno padding, bordo, intestazione (≈ 24) e spazio fra le colonne
        var cellWidth = (Width - 2 * Padding16 - 2 - (columns - 1) * ColumnGap) / columns;
        var cellHeight = (Height - 2 * Padding16 - 2 - 24) / rows;
        var maxPitch = Math.Min(columns == 1 ? 7 : rows > 2 ? 5 : 4, Math.Floor((cellHeight - 18) / Core.Design.DotFont.Height * 2) / 2);
        for (var i = 0; i < Math.Min(state.Readings.Count, ConnectorWidgetLayout.MaxReadings); i++)
        {
            var cell = Cell(state.Readings[i], cellWidth, maxPitch);
            cell.Margin = new Thickness(i % columns == 0 ? 0 : ColumnGap / 2, 0, i % columns == columns - 1 ? 0 : ColumnGap / 2, 0);
            Grid.SetColumn(cell, i % columns);
            Grid.SetRow(cell, i / columns);
            _cells.Children.Add(cell);
        }
    }

    /// <summary>Etichetta grigia, valore a punti (testo normale se a punti non entra) e unità grigia accanto.</summary>
    private static StackPanel Cell(ConnectorReading reading, double width, double maxPitch)
    {
        var warning = reading.Level == ReadingLevel.Warning;
        var cell = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var label = Label(reading.Label, 10, Theme.GreyBrush);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.Margin = new Thickness(0, 0, 0, 4);
        cell.Children.Add(label);

        var unit = reading.Unit is { Length: > 0 } u ? Label(u, 11, Theme.GreyBrush) : null;
        var unitWidth = unit is null ? 0 : 8 + 7 * unit.Text.Length;
        var dots = DotText.Render(reading.Text);
        var pitch = dots.Width == 0 ? maxPitch : Math.Min(maxPitch, Math.Floor((width - unitWidth) / dots.Width * 2) / 2);
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        if (pitch >= MinPitch && reading.Text.All(Core.Design.DotFont.Supports))
        {
            line.Children.Add(new DotMatrixView { Pitch = pitch, Dots = dots, DotColor = warning ? Theme.Red : Theme.White });
        }
        else
        {
            var text = Label(reading.Text, 14, warning ? Theme.RedBrush : Theme.WhiteBrush);
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.MaxWidth = Math.Max(20, width - unitWidth);
            text.ToolTip = reading.Text;
            line.Children.Add(text);
        }
        if (unit is not null)
        {
            unit.Margin = new Thickness(6, 0, 0, 0);
            unit.VerticalAlignment = VerticalAlignment.Bottom;
            line.Children.Add(unit);
        }
        cell.Children.Add(line);
        return cell;
    }

    private static int Count(ConnectorManifest manifest, int received) =>
        ConnectorWidgetLayout.Count(manifest.Readings.Count, received);

    private static ConnectorManifest Guard(ConnectorManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest;
    }
}
