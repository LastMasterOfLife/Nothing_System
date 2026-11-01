using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NothingShell.App.Rendering;

namespace NothingShell.App.Settings;

/// <summary>Interruttore di Windows 11: acceso = pillola nel colore d'accento col punto a destra; spento = solo bordo col punto a sinistra.</summary>
public sealed class DotToggle : Border
{
    private readonly Ellipse _dot = new() { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
    private bool _isOn;

    public DotToggle(bool isOn)
    {
        Width = 40;
        Height = 20;
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(3, 0, 3, 0);
        Cursor = Cursors.Hand;
        Child = _dot;
        _isOn = isOn;
        Render();
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            IsOn = !IsOn;
            Toggled?.Invoke(IsOn);
        };
    }

    public event Action<bool>? Toggled;

    public bool IsOn
    {
        get => _isOn;
        set
        {
            _isOn = value;
            Render();
        }
    }

    private void Render()
    {
        Background = _isOn ? Theme.AccentBrush : Brushes.Transparent;
        BorderBrush = _isOn ? Theme.AccentBrush : Theme.MutedBrush;
        _dot.Fill = _isOn ? Theme.OnAccentBrush : Theme.MutedBrush;
        _dot.HorizontalAlignment = _isOn ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }
}
