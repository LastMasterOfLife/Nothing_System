using System.Windows;
using System.Windows.Controls;
using NothingShell.App.Rendering;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Notch;

namespace NothingShell.App.Notch;

/// <summary>Pagina Timer: conto alla rovescia a punti, preset 5 · 15 · 25 minuti e stop.</summary>
public sealed class TimerPage : NotchPageView
{
    private static readonly int[] PresetMinutes = [5, 15, 25];

    private readonly DotMatrixView _remaining = new() { Pitch = 6, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _presets = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Border _stop;

    public TimerPage(Action<TimeSpan> start, Action stop)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(stop);

        foreach (var minutes in PresetMinutes)
        {
            var label = new DotMatrixView { Pitch = 2.5, Dots = DotText.Render($"{minutes} MIN") };
            _presets.Children.Add(NotchUi.Button(label, () => start(TimeSpan.FromMinutes(minutes)), width: 84));
        }
        _stop = NotchUi.Button(new DotMatrixView { Pitch = 2.5, Dots = DotText.Render("STOP"), DotColor = Theme.Red },
            stop, width: 84);
        _stop.HorizontalAlignment = HorizontalAlignment.Center;

        var stack = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        _remaining.Margin = new Thickness(0, 0, 0, 22);
        stack.Children.Add(_remaining);
        stack.Children.Add(_presets);
        stack.Children.Add(_stop);
        Children.Add(stack);
        Update(running: false, TimeSpan.Zero);
    }

    public override NotchPage Page => NotchPage.Timer;

    public void Update(bool running, TimeSpan remaining)
    {
        _remaining.Dots = DotText.Render(Countdown.Format(remaining));
        _remaining.DotColor = running ? Theme.White : Theme.Grey;
        _presets.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        _stop.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }
}
