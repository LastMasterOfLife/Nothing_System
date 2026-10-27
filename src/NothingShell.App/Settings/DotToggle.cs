using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NothingShell.App.Rendering;

namespace NothingShell.App.Settings;

/// <summary>Interruttore stile Nothing: acceso = pillola bianca con punto nero a destra; spento = punto grigio a sinistra.</summary>
public sealed class DotToggle : Border
{
    private readonly Ellipse _dot = new() { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
    private bool _isOn;

    public DotToggle(bool isOn)
    {
        Width = 44;
        Height = 24;
        CornerRadius = new CornerRadius(12);
        BorderThickness = new Thickness(1.5);
        Padding = new Thickness(5, 0, 5, 0);
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
        Background = _isOn ? Theme.WhiteBrush : Brushes.Transparent;
        BorderBrush = _isOn ? Theme.WhiteBrush : Theme.GreyBrush;
        _dot.Fill = _isOn ? Theme.BlackBrush : Theme.GreyBrush;
        _dot.HorizontalAlignment = _isOn ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }
}
