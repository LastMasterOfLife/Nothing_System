using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NothingShell.App.Rendering;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Notch;

namespace NothingShell.App.Notch;

/// <summary>
/// Pagina Agenda (docs/11-agenda-pannello-rapido.md): gli appuntamenti che restano oggi, con ora a punti (rossa se
/// manca poco o è in corso) e quanto manca; se oggi non c'è più nulla, il prossimo giorno con appuntamenti.
/// Un clic apre il link della riunione online, altrimenti il calendario.
/// </summary>
public sealed class AgendaPage : NotchPageView
{
    private const int MaxRows = 3;

    private readonly Action<CalendarEvent> _open;
    private readonly DotMatrixView _title = new() { Pitch = 2, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _count = NotchUi.Text(11, Theme.GreyBrush);
    private readonly StackPanel _rows = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _empty = NotchUi.Text(13, Theme.GreyBrush);

    public AgendaPage(Action<CalendarEvent> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        _open = open;
        var header = new Grid();
        _count.HorizontalAlignment = HorizontalAlignment.Right;
        _count.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(_title);
        header.Children.Add(_count);
        _empty.Margin = new Thickness(0, 18, 0, 0);
        _empty.TextWrapping = TextWrapping.Wrap;

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_rows);
        stack.Children.Add(_empty);
        Children.Add(stack);
        Update([], DateTimeOffset.Now, "Collega un calendario nelle impostazioni");
    }

    public override NotchPage Page => NotchPage.Agenda;

    public void Update(IReadOnlyList<CalendarEvent> events, DateTimeOffset now, string emptyMessage)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(emptyMessage);
        var today = now.LocalDateTime.Date;
        var remaining = Agenda.OnDay(events, today).Where(e => e.AllDay || e.End > now).ToArray();
        var day = today;
        if (remaining.Length == 0 && Agenda.Upcoming(events, now) is [var next, ..])
        {
            day = next.Start.LocalDateTime.Date;
            remaining = Agenda.OnDay(events, day).ToArray();
        }

        _title.Dots = DotText.Render(day == today ? "OGGI" : day == today.AddDays(1) ? "DOMANI" : day.ToString("dddd d", NotchUi.Italian).ToUpperInvariant());
        _count.Text = remaining.Length switch
        {
            0 => string.Empty,
            1 => "1 appuntamento",
            var n => $"{n} appuntamenti",
        };

        _rows.Children.Clear();
        foreach (var e in remaining.Take(MaxRows))
            _rows.Children.Add(Row(e, now));
        _empty.Text = events.Count == 0 ? emptyMessage : "Nessun altro appuntamento questa settimana";
        _empty.Visibility = remaining.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border Row(CalendarEvent e, DateTimeOffset now)
    {
        var soon = !e.AllDay && e.Start - now <= Agenda.ReminderLead && e.End > now;
        var time = new DotMatrixView
        {
            Pitch = 2,
            Dots = DotText.Render(e.AllDay ? "--:--" : e.Start.LocalDateTime.ToString("HH:mm", NotchUi.Italian)),
            DotColor = soon ? Theme.Red : e.AllDay ? Theme.Grey : Theme.White,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var title = NotchUi.Text(13, Theme.WhiteBrush, e.Title);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(12, 0, 8, 0);
        var detail = e.AllDay ? "tutto il giorno"
            : e.Start.LocalDateTime.Date == now.LocalDateTime.Date || e.Start <= now ? Agenda.Relative(e, now)
            : e.Location ?? string.Empty;
        if (e.MeetingLink is { } link)
            detail = $"{MeetingLinks.Service(link) ?? "ONLINE"} · {detail}".TrimEnd(' ', '·');
        var info = NotchUi.Text(11, soon ? Theme.RedBrush : Theme.GreyBrush, detail);
        info.VerticalAlignment = VerticalAlignment.Center;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 1);
        Grid.SetColumn(info, 2);
        grid.Children.Add(time);
        grid.Children.Add(title);
        grid.Children.Add(info);

        var row = new Border
        {
            Height = 30,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(-8, 0, -8, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = grid,
            ToolTip = e.Location is { } where ? $"{e.Title}\n{where}" : e.Title,
        };
        row.MouseEnter += (_, _) => row.Background = Theme.SurfaceBrush;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            _open(e);
        };
        return row;
    }
}
