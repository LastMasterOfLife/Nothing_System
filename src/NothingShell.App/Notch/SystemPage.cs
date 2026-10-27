using System.Windows;
using System.Windows.Controls;
using NothingShell.App.Rendering;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Notch;
using NothingShell.Core.Sensors;

namespace NothingShell.App.Notch;

/// <summary>Pagina Sistema: CPU, RAM e batteria con percentuale e barra a punti.</summary>
public sealed class SystemPage : NotchPageView
{
    private const double BarPitch = 6;
    private const double LabelWidth = 64;
    private const double ValueWidth = 56;
    private static readonly int BarColumns = (int)((NotchUi.PageWidth - LabelWidth - ValueWidth) / BarPitch);

    private readonly (DotMatrixView Bar, DotMatrixView Value) _cpu;
    private readonly (DotMatrixView Bar, DotMatrixView Value) _ram;
    private readonly (DotMatrixView Bar, DotMatrixView Value) _battery;
    private readonly Grid _batteryRow;

    public SystemPage()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        (_cpu, _) = AddRow(stack, "CPU");
        (_ram, _) = AddRow(stack, "RAM");
        (_battery, _batteryRow) = AddRow(stack, "BATTERIA");
        Children.Add(stack);
    }

    public override NotchPage Page => NotchPage.System;

    public void Update(SystemStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        Set(_cpu, stats.Cpu, alert: false);
        Set(_ram, stats.Memory, alert: false);
    }

    public void Update(BatteryStatus battery)
    {
        ArgumentNullException.ThrowIfNull(battery);
        _batteryRow.Visibility = battery.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        Set(_battery, battery.BarFraction, battery.IsLow);
    }

    private static void Set((DotMatrixView Bar, DotMatrixView Value) row, double fraction, bool alert)
    {
        row.Bar.Dots = DotBar.Render(fraction, BarColumns);
        row.Bar.DotColor = alert ? Theme.Red : Theme.White;
        row.Value.Dots = DotText.Render($"{Math.Round(fraction * 100):0}%");
    }

    private static ((DotMatrixView, DotMatrixView), Grid) AddRow(Panel parent, string name)
    {
        var row = new Grid { Height = 34 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ValueWidth) });

        var label = NotchUi.Text(11, Theme.GreyBrush, name);
        label.VerticalAlignment = VerticalAlignment.Center;
        var bar = new DotMatrixView
        {
            Pitch = BarPitch,
            OffColor = Theme.DotOff,
            Dots = DotBar.Render(0, BarColumns),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var value = new DotMatrixView { Pitch = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(bar, 1);
        Grid.SetColumn(value, 2);
        row.Children.Add(label);
        row.Children.Add(bar);
        row.Children.Add(value);
        parent.Children.Add(row);
        return ((bar, value), row);
    }
}
