using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using NothingShell.App.Rendering;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Notch;
using NothingShell.Core.Sensors;
using NothingShell.Core.Widgets;

namespace NothingShell.App.Widgets;

/// <summary>Riquadro base dei widget: superficie scura, bordo sottile, intestazione grigia.</summary>
public abstract class WidgetView : Border
{
    protected const double Padding16 = 16;
    protected const int BarColumns = 16; // 16 × 8 = 128 DIP, entro i 134 utili del riquadro
    protected const double BarPitch = 8;
    protected static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    protected WidgetView(WidgetKind kind, string? header)
        : this(kind.ToString(), WidgetSizes.For(kind), header)
    {
        Kind = kind;
    }

    /// <summary>Widget generico (plugin): identificato da <paramref name="id"/>, taglia libera.</summary>
    protected WidgetView(string id, Size2 size, string? header)
    {
        Id = id;
        Width = size.Width;
        Height = size.Height;
        Background = Theme.SurfaceBrush;
        BorderBrush = Theme.OutlineBrush;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(24);
        Padding = new Thickness(Padding16);

        var root = new DockPanel { LastChildFill = true };
        if (header is not null)
        {
            HeaderText = Label(header, 11, Theme.GreyBrush);
            HeaderText.Margin = new Thickness(0, 0, 0, 12);
            DockPanel.SetDock(HeaderText, Dock.Top);
            root.Children.Add(HeaderText);
        }
        root.Children.Add(Body);
        Child = root;
    }

    /// <summary>Identificativo nelle impostazioni: nome del widget integrato o <c>plugin:&lt;id&gt;</c>.</summary>
    public string Id { get; }

    /// <summary>Tipo del widget integrato; null per i widget dei plugin.</summary>
    public WidgetKind? Kind { get; }

    protected TextBlock? HeaderText { get; }

    protected Grid Body { get; } = new();

    protected static TextBlock Label(string text, double size, Brush foreground) => new()
    {
        Text = text,
        FontFamily = Theme.BodyFont,
        FontSize = size,
        Foreground = foreground,
    };

    /// <summary>Barra a punti con i punti spenti visibili.</summary>
    protected static DotMatrixView Bar(int rows) => new()
    {
        Pitch = BarPitch,
        OffColor = Theme.DotOff,
        Dots = DotBar.Render(0, BarColumns, rows),
        HorizontalAlignment = HorizontalAlignment.Left,
    };
}

/// <summary>Orologio grande: HH:MM a punti, giorno in rosso, data in grigio.</summary>
public sealed class ClockWidget : WidgetView
{
    private readonly DotMatrixView _time = new() { Pitch = 9, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly DotMatrixView _weekday = new() { Pitch = 2.5, DotColor = Theme.Red };
    private readonly DotMatrixView _date = new() { Pitch = 2.5, DotColor = Theme.Grey, Margin = new Thickness(10, 0, 0, 0) };

    public ClockWidget()
        : base(WidgetKind.Clock, header: null)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var dateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 18, 0, 0) };
        dateRow.Children.Add(_weekday);
        dateRow.Children.Add(_date);
        stack.Children.Add(_time);
        stack.Children.Add(dateRow);
        Body.Children.Add(stack);
    }

    public void Update(DateTime now)
    {
        _time.Dots = DotText.Render(now.ToString("HH:mm", Italian));
        _weekday.Dots = DotText.Render(now.ToString("dddd", Italian));
        _date.Dots = DotText.Render(now.ToString("d MMMM", Italian));
    }
}

/// <summary>Batteria: percentuale grande, barra a punti (rossa se scarica), stato.</summary>
public sealed class BatteryWidget : WidgetView
{
    private readonly DotMatrixView _value = new() { Pitch = 5, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly DotMatrixView _bar = Bar(rows: 2);
    private readonly TextBlock _state = Label(string.Empty, 11, Theme.GreyBrush);

    public BatteryWidget()
        : base(WidgetKind.Battery, "BATTERIA")
    {
        var stack = new StackPanel();
        _bar.Margin = new Thickness(0, 16, 0, 12);
        stack.Children.Add(_value);
        stack.Children.Add(_bar);
        stack.Children.Add(_state);
        Body.Children.Add(stack);
    }

    public void Update(BatteryStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        _value.Dots = DotText.Render(status.Label);
        _bar.Dots = DotBar.Render(status.BarFraction, BarColumns, rows: 2);
        _bar.DotColor = status.IsLow ? Theme.Red : Theme.White;
        _state.Text = status.StateText;
        _state.Foreground = status.Charging ? Theme.RedBrush : Theme.GreyBrush;
    }
}

/// <summary>Sistema: CPU e RAM con percentuale e barra a punti.</summary>
public sealed class SystemWidget : WidgetView
{
    private readonly (DotMatrixView Value, DotMatrixView Bar) _cpu;
    private readonly (DotMatrixView Value, DotMatrixView Bar) _ram;

    public SystemWidget()
        : base(WidgetKind.System, "SISTEMA")
    {
        var stack = new StackPanel();
        _cpu = AddMeter(stack, "CPU");
        _ram = AddMeter(stack, "RAM");
        _ram.Bar.Margin = new Thickness(0, 6, 0, 0);
        Body.Children.Add(stack);
    }

    public void Update(SystemStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        Set(_cpu, stats.Cpu);
        Set(_ram, stats.Memory);
    }

    private static void Set((DotMatrixView Value, DotMatrixView Bar) meter, double fraction)
    {
        meter.Value.Dots = DotText.Render($"{Math.Round(fraction * 100):0}%");
        meter.Bar.Dots = DotBar.Render(fraction, BarColumns);
    }

    private static (DotMatrixView, DotMatrixView) AddMeter(Panel parent, string name)
    {
        var row = new Grid { Margin = new Thickness(0, parent.Children.Count == 0 ? 0 : 16, 0, 6) };
        var label = Label(name, 11, Theme.GreyBrush);
        label.VerticalAlignment = VerticalAlignment.Center;
        var value = new DotMatrixView { Pitch = 3, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(label);
        row.Children.Add(value);
        var bar = Bar(rows: 1);
        parent.Children.Add(row);
        parent.Children.Add(bar);
        return (value, bar);
    }
}

/// <summary>Calendario del mese, settimana da lunedì, oggi cerchiato in rosso.</summary>
public sealed class CalendarWidget : WidgetView
{
    private readonly UniformGrid _grid = new() { Columns = 7 };
    private DateOnly _shown;

    public CalendarWidget()
        : base(WidgetKind.Calendar, string.Empty)
    {
        Body.Children.Add(_grid);
    }

    public void Update(DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        if (today == _shown)
            return;
        _shown = today;

        HeaderText!.Text = now.ToString("MMMM yyyy", Italian).ToUpper(Italian);
        _grid.Children.Clear();
        foreach (var initial in MonthCalendar.WeekdayInitials)
            _grid.Children.Add(Cell(initial, Theme.GreyBrush, highlighted: false));

        var weeks = MonthCalendar.Weeks(today.Year, today.Month);
        _grid.Rows = weeks.Length + 1;
        foreach (var week in weeks)
            for (var c = 0; c < 7; c++)
            {
                var day = week[c];
                var isToday = day == today.Day;
                var brush = c == 6 ? Theme.GreyBrush : Theme.WhiteBrush;
                _grid.Children.Add(Cell(day == 0 ? string.Empty : day.ToString(Italian), brush, isToday));
            }
    }

    private static Grid Cell(string text, Brush foreground, bool highlighted)
    {
        var cell = new Grid();
        if (highlighted)
            cell.Children.Add(new Ellipse { Width = 17, Height = 17, Fill = Theme.RedBrush });
        var label = Label(text, 10, highlighted ? Theme.WhiteBrush : foreground);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        if (highlighted)
            label.FontWeight = FontWeights.SemiBold;
        cell.Children.Add(label);
        return cell;
    }
}
