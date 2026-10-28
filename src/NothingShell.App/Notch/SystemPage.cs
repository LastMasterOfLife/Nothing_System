using System.Windows;
using System.Windows.Controls;
using NothingShell.App.Rendering;
using NothingShell.App.Services;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Notch;
using NothingShell.Core.Sensors;

namespace NothingShell.App.Notch;

/// <summary>
/// Pagina Sistema: CPU, RAM e batteria con percentuale e barra a punti; sotto, chi usa microfono e webcam
/// e i download in corso.
/// </summary>
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
    private readonly TextBlock _privacy = NotchUi.Text(11, Theme.RedBrush);
    private readonly TextBlock _download = NotchUi.Text(11, Theme.GreyBrush);

    public SystemPage()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        (_cpu, _) = AddRow(stack, "CPU");
        (_ram, _) = AddRow(stack, "RAM");
        (_battery, _batteryRow) = AddRow(stack, "BATTERIA");
        _privacy.Margin = new Thickness(0, 4, 0, 0);
        _privacy.Visibility = Visibility.Collapsed;
        _download.Margin = new Thickness(0, 2, 0, 0);
        _download.Visibility = Visibility.Collapsed;
        stack.Children.Add(_privacy);
        stack.Children.Add(_download);
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

    public void SetActivities(PrivacyState privacy, IReadOnlyList<Core.Notch.ActiveDownload> downloads)
    {
        ArgumentNullException.ThrowIfNull(privacy);
        ArgumentNullException.ThrowIfNull(downloads);
        var mic = string.Join(", ", privacy.Microphone);
        var cam = string.Join(", ", privacy.Camera);
        _privacy.Text = (mic.Length, cam.Length) switch
        {
            (> 0, > 0) when mic == cam => $"Microfono e webcam in uso: {mic}",
            (> 0, > 0) => $"Microfono: {mic}  ·  Webcam: {cam}",
            (> 0, _) => $"Microfono in uso: {mic}",
            (_, > 0) => $"Webcam in uso: {cam}",
            _ => string.Empty,
        };
        _privacy.Visibility = privacy.Any ? Visibility.Visible : Visibility.Collapsed;

        if (downloads is [var first, ..])
            _download.Text = $"Download: {first.Name} · {Core.Notch.DownloadTracker.FormatBytes(first.Bytes)}"
                             + (first.BytesPerSecond > 0 ? $" · {Core.Notch.DownloadTracker.FormatBytes((long)first.BytesPerSecond)}/s" : string.Empty)
                             + (downloads.Count > 1 ? $" (+{downloads.Count - 1})" : string.Empty);
        _download.Visibility = downloads.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
