using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NothingShell.App.Interop;

namespace NothingShell.App.Widgets;

/// <summary>Finestra trasparente che ospita un widget sul livello del desktop.</summary>
public sealed class WidgetWindow : Window
{
    public WidgetWindow(WidgetView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        View = view;
        Width = view.Width;
        Height = view.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = $"Nothing Shell — {view.Id}";
        Content = view;

        DesktopLayer.Attach(this);
        MouseLeftButtonDown += OnMouseLeftButtonDown;
    }

    public WidgetView View { get; }

    public bool Locked { get; set; }

    /// <summary>Sollevato al termine di un trascinamento, con la posizione grezza (non agganciata).</summary>
    public event Action<WidgetWindow>? DragCompleted;

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Locked || e.ButtonState != MouseButtonState.Pressed)
            return;
        var start = new Point(Left, Top);
        DragMove(); // ciclo di spostamento di sistema: ritorna al rilascio
        if (Left != start.X || Top != start.Y)
            DragCompleted?.Invoke(this);
    }
}
