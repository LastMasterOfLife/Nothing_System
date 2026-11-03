using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NothingShell.App.Interop;
using NothingShell.App.Rendering;
using NothingShell.Core.Work;

namespace NothingShell.App.Notch;

/// <summary>
/// Il widget che si apre cliccando un gruppo nella pagina Ore del notch: lo stesso calendario e dettaglio delle impostazioni,
/// ma solo di quel gruppo (docs/19-ore-di-lavoro.md). Si sposta trascinando il titolo, si chiude con × o Esc, e si aggiorna da
/// solo mentre il contatore avanza.
/// </summary>
public sealed class HoursDetailWindow : Window
{
    private readonly Border _frame = Theme.Panel(new Border { Padding = new Thickness(20, 16, 20, 18) });
    private readonly HoursCalendarView _calendar;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(30) };

    public HoursDetailWindow(WorkStore store, string group)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(group);
        Group = group;
        _calendar = new HoursCalendarView(store, group);
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Title = $"Nothing Shell — Ore {group}";

        var name = new TextBlock
        {
            Text = group,
            FontFamily = Theme.BodyFont,
            FontSize = 17,
            Foreground = Theme.AccentBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var caption = new TextBlock { Text = "ORE DEL GRUPPO", FontFamily = Theme.BodyFont, FontSize = 10, Foreground = Theme.MutedBrush };
        var close = new TextBlock { Text = "×", FontFamily = Theme.BodyFont, FontSize = 22, Foreground = Theme.MutedBrush, Cursor = Cursors.Hand, Padding = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Close();
        };
        var titles = new StackPanel();
        titles.Children.Add(caption);
        titles.Children.Add(name);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14), Background = Brushes.Transparent };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(titles);
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource != close)
                DragMove();
        };

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(_calendar);
        _frame.Child = content;
        Content = _frame;
        _frame.VerticalAlignment = VerticalAlignment.Top;
        GlassBackdrop.Attach(this, _frame);

        SourceInitialized += (_, _) => NativeMethods.AddExStyle(new WindowInteropHelper(this).Handle, WindowNative.WsExToolWindow);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
        _refresh.Tick += (_, _) => _calendar.Refresh();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                _refresh.Start();
            else
                _refresh.Stop();
        };
    }

    public string Group { get; }

    /// <summary>Il contenuto com'è, senza mostrare la finestra (per <c>--snapshot</c>).</summary>
    public FrameworkElement PrepareSnapshot()
    {
        Motion.Suspended = true;
        _calendar.Refresh();
        _frame.Width = Width;
        _frame.Height = 560;
        return _frame;
    }

    /// <summary>Apre il widget (o lo porta davanti) in un punto che non copra il notch.</summary>
    public void Present()
    {
        if (!IsVisible)
        {
            _calendar.Refresh();
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 60;
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            Show();
            WindowNative.BringToFront(hwnd);
            Motion.Open(_frame);
        }
        Activate();
    }
}
