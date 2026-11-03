using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NothingShell.App.Rendering;
using NothingShell.Core.Work;

namespace NothingShell.App.Notch;

/// <summary>
/// Il calendario di un mese con i giorni colorati dove ci sono ore (più sono, più il colore è pieno) e, sotto, il dettaglio del
/// giorno scelto (docs/19-ore-di-lavoro.md). Per tutto o per un solo gruppo: lo stesso controllo sta nelle impostazioni e nella
/// finestra che si apre cliccando un gruppo nel notch.
/// </summary>
public sealed class HoursCalendarView : StackPanel
{
    private static readonly string[] Weekdays = ["L", "M", "M", "G", "V", "S", "D"];

    private readonly WorkStore _store;
    private readonly string? _group;
    private readonly TextBlock _title = Text(15, Theme.InkBrush);
    private readonly TextBlock _total = Text(12, Theme.MutedBrush);
    private readonly Grid _grid = new();
    private readonly StackPanel _detail = new() { Margin = new Thickness(0, 12, 0, 0) };
    private DateOnly _month;
    private DateOnly _selected;

    /// <param name="group">Il gruppo di cui si vedono le ore; null per tutte.</param>
    public HoursCalendarView(WorkStore store, string? group)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _group = group;
        var today = DateOnly.FromDateTime(DateTime.Now);
        _month = new DateOnly(today.Year, today.Month, 1);
        _selected = today;

        var previous = Arrow("‹", -1);
        var next = Arrow("›", +1);
        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.Margin = new Thickness(6, 0, 6, 0);
        _title.MinWidth = 120;
        _title.TextAlignment = TextAlignment.Center;
        _total.VerticalAlignment = VerticalAlignment.Center;
        _total.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_title, 1);
        Grid.SetColumn(next, 2);
        Grid.SetColumn(_total, 3);
        header.Children.Add(previous);
        header.Children.Add(_title);
        header.Children.Add(next);
        header.Children.Add(_total);

        for (var column = 0; column < 7; column++)
            _grid.ColumnDefinitions.Add(new ColumnDefinition());
        Children.Add(header);
        Children.Add(_grid);
        Children.Add(_detail);
        Refresh();
    }

    /// <summary>Rilegge le ore: dopo una modifica, o mentre il contatore avanza.</summary>
    public void Refresh()
    {
        var month = WorkCalendar.Month(_store, _group, _month.Year, _month.Month);
        _title.Text = $"{WorkPeriod.MonthName(_month.Month)} {_month.Year}";
        _total.Text = month.Total > 0 ? $"totale {WorkReport.Duration(month.Total)}" : "nessuna ora";
        BuildGrid(month);
        BuildDetail();
    }

    private void BuildGrid(WorkMonth month)
    {
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        for (var row = 0; row < 7; row++)
            _grid.RowDefinitions.Add(new RowDefinition { Height = row == 0 ? new GridLength(22) : GridLength.Auto });
        for (var column = 0; column < 7; column++)
        {
            var name = Text(11, Theme.MutedBrush, Weekdays[column]);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(name, column);
            _grid.Children.Add(name);
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var offset = ((int)_month.DayOfWeek + 6) % 7; // il lunedì è la prima colonna
        for (var day = 1; day <= DateTime.DaysInMonth(_month.Year, _month.Month); day++)
        {
            var date = new DateOnly(_month.Year, _month.Month, day);
            var position = offset + day - 1;
            var seconds = month.Days.GetValueOrDefault(day);
            var cell = Cell(date, seconds, month.Max, date == today, date == _selected);
            Grid.SetColumn(cell, position % 7);
            Grid.SetRow(cell, 1 + position / 7);
            _grid.Children.Add(cell);
        }
    }

    private Border Cell(DateOnly date, double seconds, double max, bool isToday, bool isSelected)
    {
        var strong = seconds > 0 && seconds >= max * 0.5;
        var number = Text(13, seconds > 0 ? (strong ? Theme.OnAccentBrush : Theme.InkBrush) : Theme.MutedBrush, date.Day.ToString(CultureInfo.InvariantCulture));
        number.HorizontalAlignment = HorizontalAlignment.Center;
        number.FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal;
        var hours = Text(9, strong ? Theme.OnAccentBrush : Theme.MutedBrush, seconds > 0 ? Short(seconds) : " ");
        hours.HorizontalAlignment = HorizontalAlignment.Center;
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(number);
        stack.Children.Add(hours);

        var accent = Theme.Accent;
        var fill = seconds > 0
            ? new SolidColorBrush(Color.FromArgb((byte)(70 + 185 * Math.Min(1, seconds / Math.Max(max, 1))), accent.R, accent.G, accent.B))
            : Theme.SurfaceBrush;
        var cell = new Border
        {
            Height = 40,
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(5),
            Background = fill,
            BorderBrush = isSelected ? Theme.InkBrush : isToday ? Theme.MutedBrush : Brushes.Transparent,
            BorderThickness = new Thickness(isSelected ? 2 : 1),
            Cursor = Cursors.Hand,
            Child = stack,
            ToolTip = seconds > 0 ? $"{date:dd/MM/yyyy}: {WorkReport.Duration(seconds)}" : $"{date:dd/MM/yyyy}: nessuna ora",
        };
        cell.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _selected = date;
            Refresh();
        };
        return cell;
    }

    private void BuildDetail()
    {
        _detail.Children.Clear();
        var detail = WorkCalendar.Day(_store, _group, _selected);
        var title = Text(13, Theme.AccentBrush, $"{Capitalise(_selected.ToDateTime(TimeOnly.MinValue).ToString("dddd d MMMM", CultureInfo.GetCultureInfo("it-IT")))} · {(detail.Total > 0 ? "totale " + WorkReport.Duration(detail.Total) : "nessuna ora registrata")}");
        title.Margin = new Thickness(0, 0, 0, 6);
        _detail.Children.Add(title);
        foreach (var row in detail.Rows)
        {
            var line = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = Text(13, row.IsGroup ? Theme.AccentBrush : Theme.InkBrush, row.Label);
            var value = Text(13, Theme.MutedBrush, row.Value);
            value.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(value, 1);
            line.Children.Add(label);
            line.Children.Add(value);
            _detail.Children.Add(line);
        }
    }

    private TextBlock Arrow(string symbol, int step)
    {
        var arrow = Text(18, Theme.InkBrush, symbol);
        arrow.Padding = new Thickness(8, 0, 8, 2);
        arrow.Cursor = Cursors.Hand;
        arrow.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _month = _month.AddMonths(step);
            _selected = _month; // il primo del mese: si vede subito qualcosa
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (today.Year == _month.Year && today.Month == _month.Month)
                _selected = today;
            Refresh();
        };
        return arrow;
    }

    /// <summary>"7h", "7,5h", "45m": le ore dentro una cella.</summary>
    private static string Short(double seconds)
    {
        var minutes = (int)Math.Round(seconds / 60);
        if (minutes < 60)
            return $"{minutes}m";
        var text = (minutes / 60.0).ToString("0.#", CultureInfo.GetCultureInfo("it-IT"));
        return text + "h";
    }

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.GetCultureInfo("it-IT")) + text[1..];

    private static TextBlock Text(double size, Brush foreground, string text = "") => new()
    {
        Text = text, // senza il filtro dei punti di NotchUi.Text: i nomi restano com'è
        FontFamily = Theme.BodyFont,
        FontSize = size,
        Foreground = foreground,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
}
