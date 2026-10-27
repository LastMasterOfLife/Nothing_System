using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NothingShell.App.Rendering;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Plugins;

namespace NothingShell.App.Settings;

/// <summary>Operazioni che la finestra delle impostazioni chiede alla shell (implementate da ShellController).</summary>
public interface IShellSettingsActions
{
    bool NotchEnabled { get; set; }

    bool BarEnabled { get; set; }

    bool UseWinKey { get; set; }

    bool WidgetsLocked { get; set; }

    bool MailInNotch { get; set; }

    string NotificationAccessText { get; }

    bool CanRequestNotificationAccess { get; }

    Task RequestNotificationAccessAsync();

    IEnumerable<(string Id, string Name)> Widgets { get; }

    IReadOnlyList<PluginLoadResult> PluginResults { get; }

    string DataFolder { get; }

    string PluginFolder { get; }

    bool IsWidgetEnabled(string id);

    void SetWidgetEnabled(string id, bool enabled);

    void ResetWidgetLayout();

    void ReloadPlugins();
}

/// <summary>Impostazioni in stile Nothing (docs/07-settings-plugins.md). Ogni modifica si applica subito.</summary>
public sealed class SettingsWindow : Window
{
    private const double PanelWidth = 520;
    private const double PanelHeight = 640;

    private readonly IShellSettingsActions _shell;
    private readonly StackPanel _sections = new() { Margin = new Thickness(0, 0, 8, 0) };

    public SettingsWindow(IShellSettingsActions shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;

        Width = PanelWidth;
        Height = PanelHeight;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = "Impostazioni Nothing Shell";
        Content = BuildVisualTree();
        Rebuild();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
    }

    /// <summary>Ridisegna le sezioni (dopo una ricarica dei plugin cambiano i widget e gli errori).</summary>
    public void Rebuild()
    {
        _sections.Children.Clear();

        Section("GENERALE");
        ToggleRow("Avvia con Windows", "Solo per questo utente", Autostart.IsEnabled, Autostart.Set);

        Section("COMPONENTI");
        ToggleRow("Notch", "Orologio e musica in alto al centro", _shell.NotchEnabled, v => _shell.NotchEnabled = v);
        ToggleRow("Barra al posto della taskbar", "Spenta: torna la taskbar di Windows", _shell.BarEnabled, v => _shell.BarEnabled = v);
        ToggleRow("Tasto Win apre il launcher", "Le combinazioni Win+… restano a Windows", _shell.UseWinKey, v => _shell.UseWinKey = v);

        Section("NOTCH");
        ToggleRow("Mail nel notch", "Mostra l'oggetto delle mail in arrivo", _shell.MailInNotch, v => _shell.MailInNotch = v);
        Note(_shell.NotificationAccessText, Theme.GreyBrush);
        if (_shell.CanRequestNotificationAccess)
            ButtonRow(("CONSENTI ACCESSO", async () =>
            {
                await _shell.RequestNotificationAccessAsync();
                Rebuild();
            }));
        Note("Swipe sul notch aperto (touchpad, rotellina o trascinamento) per Posta, Timer e Sistema.", Theme.GreyBrush);

        Section("WIDGET");
        foreach (var (id, name) in _shell.Widgets)
            ToggleRow(name, null, _shell.IsWidgetEnabled(id), v => _shell.SetWidgetEnabled(id, v));
        ToggleRow("Blocca posizione", "I widget non si trascinano", _shell.WidgetsLocked, v => _shell.WidgetsLocked = v);
        ButtonRow(("RIPRISTINA DISPOSIZIONE", _shell.ResetWidgetLayout));

        Section("PLUGIN");
        var valid = _shell.PluginResults.Count(r => r.Manifest is not null);
        Note(valid == 0 ? "Nessun plugin installato." : $"{valid} plugin attivi.", Theme.GreyBrush);
        foreach (var bad in _shell.PluginResults.Where(r => r.Error is not null))
            Note($"{bad.FolderName}: {bad.Error}", Theme.RedBrush);
        ButtonRow(
            ("APRI CARTELLA", () => OpenFolder(_shell.PluginFolder)),
            ("RICARICA", () =>
            {
                _shell.ReloadPlugins();
                Rebuild();
            }));

        Section("INFO");
        Note($"Nothing Shell {typeof(SettingsWindow).Assembly.GetName().Version?.ToString(2)}", Theme.GreyBrush);
        Note($"Dati: {_shell.DataFolder}", Theme.GreyBrush);
        ButtonRow(("APRI CARTELLA DEI DATI", () => OpenFolder(_shell.DataFolder)));
    }

    private Border BuildVisualTree()
    {
        var header = new Grid { Margin = new Thickness(0, 0, 0, 18), Background = Brushes.Transparent };
        header.Children.Add(new DotMatrixView
        {
            Pitch = 3,
            Dots = DotText.Render("IMPOSTAZIONI"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var close = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor = Cursors.Hand,
            ToolTip = "Chiudi",
            Child = new DotMatrixView
            {
                Pitch = 2,
                Dots = DotText.Render("X"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        close.MouseEnter += (_, _) => close.Background = Theme.SurfaceBrush;
        close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Close();
        };
        header.Children.Add(close);
        header.MouseLeftButtonDown += (_, _) => DragMove();

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = _sections,
        };
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(scroll);

        return new Border
        {
            Background = Theme.BlackBrush,
            BorderBrush = Theme.OutlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(28),
            Padding = new Thickness(24, 20, 24, 20),
            Child = dock,
        };
    }

    private void Section(string title)
    {
        var label = Label(title, 11, Theme.GreyBrush);
        label.Margin = new Thickness(0, _sections.Children.Count == 0 ? 0 : 22, 0, 8);
        _sections.Children.Add(label);
    }

    private void ToggleRow(string title, string? description, bool isOn, Action<bool> changed)
    {
        var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 60, 0) };
        text.Children.Add(Label(title, 14, Theme.WhiteBrush));
        if (description is not null)
        {
            var d = Label(description, 11, Theme.GreyBrush);
            d.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(d);
        }
        var toggle = new DotToggle(isOn) { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        toggle.Toggled += changed;
        row.Children.Add(text);
        row.Children.Add(toggle);
        _sections.Children.Add(row);
    }

    private void ButtonRow(params (string Text, Action Click)[] buttons) =>
        ButtonRow(buttons.Select(b => (b.Text, (Func<Task>)(() =>
        {
            b.Click();
            return Task.CompletedTask;
        }))).ToArray());

    private void ButtonRow(params (string Text, Func<Task> Click)[] buttons)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        foreach (var (text, click) in buttons)
        {
            var button = new Border
            {
                BorderBrush = Theme.GreyBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 0, 8, 0),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = Label(text, 11, Theme.WhiteBrush),
            };
            button.MouseEnter += (_, _) => button.Background = Theme.SurfaceBrush;
            button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
            button.MouseLeftButtonUp += async (_, e) =>
            {
                e.Handled = true;
                await click();
            };
            row.Children.Add(button);
        }
        _sections.Children.Add(row);
    }

    private void Note(string text, Brush brush)
    {
        var note = Label(text, 12, brush);
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(0, 2, 0, 2);
        _sections.Children.Add(note);
    }

    private static void OpenFolder(string path)
    {
        System.IO.Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
    }

    private static TextBlock Label(string text, double size, Brush foreground) => new()
    {
        Text = text,
        FontFamily = Theme.BodyFont,
        FontSize = size,
        Foreground = foreground,
    };
}
