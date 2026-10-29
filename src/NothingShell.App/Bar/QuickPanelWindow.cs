using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NothingShell.App.Interop;
using NothingShell.App.Rendering;
using NothingShell.App.Services;
using NothingShell.Core.Bar;
using NothingShell.Core.Design;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Notch;
using NothingShell.Core.Widgets;

namespace NothingShell.App.Bar;

/// <summary>
/// Pannello rapido in stile Nothing (docs/11-agenda-pannello-rapido.md): si apre dal pulsante rete e batteria
/// della barra al posto di Win+A. Riquadri grandi (acceso = bianco), cursori a pillola per volume e luminosità.
/// Si chiude cliccando fuori o con Esc; i servizi audio girano solo mentre è aperto.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001",
    Justification = "Una Window non è IDisposable: i servizi audio si rilasciano in Closed.")]
public sealed class QuickPanelWindow : Window
{
    private const double PanelWidth = 372;
    private const double Gap = 12;
    private const byte VkLeftWin = 0x5B;
    private const byte VkA = 0x41;
    private const byte VkP = 0x50;
    private const byte VkSnapshot = 0x2C;
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");
    private static readonly Brush DarkText = Frozen(Color.FromRgb(0x55, 0x55, 0x55));

    private readonly Action _lock;
    private readonly Action _openSettings;
    private readonly AudioService _speaker = new();
    private readonly AudioService _microphone = new(capture: true);
    private readonly DispatcherTimer _brightnessApply = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly Border _root;
    private readonly DotMatrixView _clock = new() { Pitch = 4, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly DotMatrixView _date = new() { Pitch = 2, DotColor = Theme.Grey, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly DotMatrixView _battery = new() { Pitch = 2, VerticalAlignment = VerticalAlignment.Center };
    private readonly QuickTile _wifi;
    private readonly QuickTile _bluetooth;
    private readonly QuickTile _mic;
    private readonly QuickTile _phone;
    private readonly QuickTile _phoneFile;
    private readonly QuickTile _phoneClip;
    private readonly IPhoneActions _phoneActions;
    private readonly Func<bool> _phoneOn;
    private readonly PillSlider _volume;
    private readonly PillSlider _brightness;

    private RadioSwitch? _wifiRadio;
    private RadioSwitch? _bluetoothRadio;
    private bool _radiosLoaded;
    private int? _brightnessTarget;
    private DateTime _hiddenAt;

    /// <param name="phoneOn">Collegamento diretto col telefono acceso nelle impostazioni (docs/13-telefono.md).</param>
    public QuickPanelWindow(Action lockPc, Action openSettings, IPhoneActions phone, Func<bool> phoneOn)
    {
        ArgumentNullException.ThrowIfNull(lockPc);
        ArgumentNullException.ThrowIfNull(openSettings);
        ArgumentNullException.ThrowIfNull(phone);
        ArgumentNullException.ThrowIfNull(phoneOn);
        _lock = lockPc;
        _openSettings = openSettings;
        _phoneActions = phone;
        _phoneOn = phoneOn;

        Width = PanelWidth;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Title = "Nothing Shell — Pannello rapido";

        _wifi = new QuickTile(DotIcons.Wifi, "Wi-Fi", () => _ = ToggleRadioAsync(_wifiRadio));
        _bluetooth = new QuickTile(DotIcons.Bluetooth, "Bluetooth", () => _ = ToggleRadioAsync(_bluetoothRadio));
        _mic = new QuickTile(DotIcons.Microphone, "Microfono", ToggleMicrophone);
        var screenshot = new QuickTile(DotIcons.Screenshot, "Screenshot", Screenshot) { State = "Cattura" };
        var project = new QuickTile(DotIcons.Display, "Proietta", () => AfterHide(() => WindowNative.PressKeys(VkLeftWin, VkP))) { State = "Schermi" };
        var lockTile = new QuickTile(DotIcons.Lock, "Blocca", () => AfterHide(_lock)) { State = "Win+L" };
        _phone = new QuickTile(DotIcons.Phone, "Telefono", FindPhone);
        _phoneFile = new QuickTile(DotIcons.Download, "Invia file", () => _ = SendFileAsync());
        _phoneClip = new QuickTile(DotIcons.Clip, "Appunti", SendClipboard);

        _volume = new PillSlider(DotIcons.Speaker, v => _speaker.SetLevel(v), ToggleSpeakerMute);
        _brightness = new PillSlider(DotIcons.Sun, v =>
        {
            _brightnessTarget = (int)Math.Round(v * 100);
            _brightnessApply.Start();
        }, onIcon: null);
        _brightnessApply.Tick += (_, _) => ApplyBrightness();

        _root = BuildVisualTree([_wifi, _bluetooth, _mic, screenshot, project, lockTile, _phone, _phoneFile, _phoneClip]);
        Content = _root;

        _speaker.Changed += (level, muted) => ShowVolume(level, muted);
        _microphone.Changed += (_, muted) => ShowMicrophone(muted);
        _phoneActions.Changed += ShowPhone;
        ShowPhone();
        Deactivated += (_, _) => HidePanel();
        Closed += (_, _) =>
        {
            _speaker.Dispose();
            _microphone.Dispose();
            _phoneActions.Changed -= ShowPhone;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                HidePanel();
        };
        SourceInitialized += (_, _) =>
            NativeMethods.AddExStyle(new WindowInteropHelper(this).Handle, WindowNative.WsExToolWindow);
    }

    /// <summary>Apre il pannello sopra l'angolo destro della barra di <paramref name="monitor"/>, o lo chiude.</summary>
    public void Toggle(DisplayMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        if (IsVisible)
        {
            HidePanel();
            return;
        }
        // Il clic sul pulsante della barra toglie prima il fuoco al pannello (che si chiude): non va riaperto.
        if (DateTime.Now - _hiddenAt < TimeSpan.FromMilliseconds(300))
            return;
        Refresh();
        _root.Measure(new Size(PanelWidth, double.PositiveInfinity));
        Height = _root.DesiredSize.Height;

        var s = monitor.Scale;
        var (width, height) = (PanelWidth * s, Height * s);
        var bounds = new Rect2(Math.Round(monitor.Bounds.Right - width - (Gap * s)),
            Math.Round(MultiMonitorBar.Place(monitor).Y - height - (Gap * s)), Math.Round(width), Math.Round(height));
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        WindowNative.Place(hwnd, bounds);
        Show();
        WindowNative.Place(hwnd, bounds); // WPF può riapplicare la sua posizione alla prima apparizione
        // La barra non prende il focus: senza attivazione il pannello non si chiuderebbe cliccando altrove.
        WindowNative.BringToFront(hwnd);
        Activate();
        _ = LoadRadiosAsync();
    }

    /// <summary>Per <c>--snapshot</c>: il contenuto con valori d'esempio, senza aprire la finestra.</summary>
    public FrameworkElement PrepareSnapshot()
    {
        _clock.Dots = DotText.Render(DateTime.Now.ToString("HH:mm", Italian));
        _date.Dots = DotText.Render(DateTime.Now.ToString("ddd d MMM", Italian).ToUpperInvariant());
        _battery.Dots = DotText.Render("BATTERIA 82%");
        _wifi.Set(true, "Casa-5G");
        _bluetooth.Set(false, "Spento");
        _mic.Set(true, "Attivo");
        _volume.Value = 0.64;
        _brightness.Value = 0.8;
        _root.Width = PanelWidth;
        _root.Measure(new Size(PanelWidth, double.PositiveInfinity));
        _root.Height = _root.DesiredSize.Height;
        return _root;
    }

    private Border BuildVisualTree(QuickTile[] tiles)
    {
        var settings = RoundButton(DotIcons.Gear, "Impostazioni di Nothing Shell", () => AfterHide(_openSettings));
        settings.HorizontalAlignment = HorizontalAlignment.Right;
        settings.VerticalAlignment = VerticalAlignment.Top;
        var header = new Grid { Margin = new Thickness(4, 0, 0, 16) };
        var time = new StackPanel();
        _date.Margin = new Thickness(0, 8, 0, 0);
        time.Children.Add(_clock);
        time.Children.Add(_date);
        header.Children.Add(time);
        header.Children.Add(settings);

        var grid = new UniformGrid { Columns = 3, Margin = new Thickness(-4, 0, -4, 8) };
        foreach (var tile in tiles)
            grid.Children.Add(tile);

        _volume.Margin = new Thickness(0, 0, 0, 8);
        var windows = TextButton("IMPOSTAZIONI RAPIDE DI WINDOWS", () => AfterHide(() => WindowNative.PressKeys(VkLeftWin, VkA)));
        windows.HorizontalAlignment = HorizontalAlignment.Right;
        var footer = new Grid { Margin = new Thickness(4, 10, 0, 0) };
        footer.Children.Add(_battery);
        footer.Children.Add(windows);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(grid);
        stack.Children.Add(_volume);
        stack.Children.Add(_brightness);
        stack.Children.Add(footer);

        return new Border
        {
            Background = Theme.BlackBrush,
            BorderBrush = Theme.OutlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(28),
            Padding = new Thickness(18, 18, 18, 14),
            Child = stack,
        };
    }

    private void Refresh()
    {
        var now = DateTime.Now;
        _clock.Dots = DotText.Render(now.ToString("HH:mm", Italian));
        _date.Dots = DotText.Render(now.ToString("ddd d MMM", Italian).ToUpperInvariant());
        var battery = SystemMonitor.ReadBattery();
        _battery.Visibility = battery.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        _battery.Dots = DotText.Render($"BATTERIA {battery.Label}");
        _battery.DotColor = battery.IsLow ? Theme.Red : Theme.Grey;

        _speaker.Start();
        _microphone.Start();
        if (_speaker.Read() is { } speaker)
            ShowVolume(speaker.Level, speaker.Muted);
        else
            _volume.IsEnabled = false;
        if (_microphone.Read() is { } mic)
            ShowMicrophone(mic.Muted);
        else
            _mic.Set(null, "Nessun microfono");

        var level = ScreenBrightness.Read();
        _brightness.Visibility = level is null ? Visibility.Collapsed : Visibility.Visible;
        if (level is { } l)
            _brightness.Value = l / 100.0;
        ShowRadios();
        ShowPhone();
    }

    /// <summary>Telefono collegato: Trova lo fa squillare, gli altri due mandano file e appunti.</summary>
    private void ShowPhone()
    {
        var current = _phoneActions.Current;
        if (current is null)
        {
            var state = _phoneOn() ? "Non collegato" : "Spento";
            _phone.Set(false, state); // il clic apre le impostazioni
            _phoneFile.Set(null, state);
            _phoneClip.Set(null, state);
            return;
        }
        _phone.Set(true, current.Battery is { } b ? $"{current.Name} · {b.Charge}%" : current.Name);
        _phoneFile.Set(false, "Al telefono");
        _phoneClip.Set(false, "Al telefono");
    }

    private void FindPhone()
    {
        if (_phoneActions.Current is null)
        {
            AfterHide(_openSettings);
            return;
        }
        _phoneActions.FindPhone();
        _phone.State = "Squilla…";
    }

    private async Task SendFileAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Invia al telefono" };
        HidePanel();
        if (dialog.ShowDialog() != true)
            return;
        _phoneFile.State = "Invio…";
        var error = await _phoneActions.SendFileAsync(dialog.FileName);
        _phoneFile.State = error ?? "Inviato";
    }

    private void SendClipboard()
    {
        try
        {
            if (!Clipboard.ContainsText())
            {
                _phoneClip.State = "Appunti vuoti";
                return;
            }
            _phoneActions.SendClipboard(Clipboard.GetText());
            _phoneClip.State = "Inviati";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _phoneClip.State = "Appunti occupati";
        }
    }

    private void ShowVolume(double level, bool muted)
    {
        _volume.IsEnabled = true;
        _volume.Value = muted ? 0 : level;
        _volume.Icon = muted ? DotIcons.SpeakerMuted : DotIcons.Speaker;
    }

    private void ShowMicrophone(bool muted) => _mic.Set(!muted, muted ? "Disattivato" : "Attivo");

    private void ToggleSpeakerMute()
    {
        if (_speaker.Read() is { } current)
            _speaker.SetMute(!current.Muted);
    }

    private void ToggleMicrophone()
    {
        if (_microphone.Read() is { } current)
            _microphone.SetMute(!current.Muted);
    }

    private async Task LoadRadiosAsync()
    {
        if (_radiosLoaded)
            return;
        _radiosLoaded = true;
        (_wifiRadio, _bluetoothRadio) = await RadioSwitch.LoadAsync();
        if (_wifiRadio is not null)
            _wifiRadio.Changed += ShowRadios;
        if (_bluetoothRadio is not null)
            _bluetoothRadio.Changed += ShowRadios;
        ShowRadios();
    }

    private void ShowRadios()
    {
        if (!_radiosLoaded)
        {
            _wifi.Set(null, "…");
            _bluetooth.Set(null, "…");
            return;
        }
        _wifi.Set(_wifiRadio is { IsDisabled: false } w ? w.IsOn : null,
            _wifiRadio is null ? "Non disponibile" : _wifiRadio.IsOn ? RadioSwitch.ConnectedNetwork() ?? "Attivo" : "Spento");
        _bluetooth.Set(_bluetoothRadio is { IsDisabled: false } b ? b.IsOn : null,
            _bluetoothRadio is null ? "Non disponibile" : _bluetoothRadio.IsOn ? "Attivo" : "Spento");
    }

    private async Task ToggleRadioAsync(RadioSwitch? radio)
    {
        if (radio is null || radio.IsDisabled)
            return;
        if (!await radio.ToggleAsync())
            OpenUri(radio == _wifiRadio ? "ms-settings:network-wifi" : "ms-settings:bluetooth");
        ShowRadios();
    }

    /// <summary>Con Greenshot aperto il suo tasto (Stamp) apre la sua cattura; altrimenti lo Strumento di cattura.</summary>
    private void Screenshot() => AfterHide(() =>
    {
        var greenshot = Process.GetProcessesByName(ScreenshotSources.GreenshotProcess);
        foreach (var process in greenshot)
            process.Dispose();
        if (greenshot.Length > 0)
            WindowNative.PressKeys(VkSnapshot);
        else
            OpenUri("ms-screenclip:");
    });

    private void ApplyBrightness()
    {
        _brightnessApply.Stop();
        if (_brightnessTarget is not { } level)
            return;
        _brightnessTarget = null;
        _ = Task.Run(() => ScreenBrightness.Set(level));
    }

    /// <summary>Le azioni che aprono altro (cattura, blocco, Win+P) partono a pannello chiuso.</summary>
    private void AfterHide(Action action)
    {
        HidePanel();
        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            action();
        };
        delay.Start();
    }

    private void HidePanel()
    {
        if (!IsVisible)
            return;
        _hiddenAt = DateTime.Now;
        ApplyBrightness();
        Hide();
        _speaker.Stop();
        _microphone.Stop();
    }

    private static Border RoundButton(DotGlyph icon, string tooltip, Action click)
    {
        var button = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            Background = Theme.SurfaceBrush,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Child = new DotMatrixView
            {
                Pitch = 2.5,
                Dots = DotGrid.From(icon),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        button.MouseEnter += (_, _) => button.Background = Theme.OutlineBrush;
        button.MouseLeave += (_, _) => button.Background = Theme.SurfaceBrush;
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return button;
    }

    private static Border TextButton(string text, Action click)
    {
        var button = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 5, 10, 5),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = text, FontFamily = Theme.BodyFont, FontSize = 10, Foreground = Theme.GreyBrush },
        };
        button.MouseEnter += (_, _) => button.Background = Theme.SurfaceBrush;
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return button;
    }

    private static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // pagina o app non disponibile in questa versione di Windows
        }
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Riquadro del pannello: acceso è bianco con icona e testo neri, spento è grigio scuro.</summary>
    private sealed class QuickTile : Border
    {
        private readonly DotMatrixView _icon;
        private readonly TextBlock _label;
        private readonly TextBlock _state;
        private bool? _on = false;

        public QuickTile(DotGlyph icon, string label, Action click)
        {
            _icon = new DotMatrixView { Pitch = 2.5, Dots = DotGrid.From(icon), HorizontalAlignment = HorizontalAlignment.Left };
            _label = new TextBlock { Text = label, FontFamily = Theme.BodyFont, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) };
            _state = new TextBlock { FontFamily = Theme.BodyFont, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
            var stack = new StackPanel();
            stack.Children.Add(_icon);
            stack.Children.Add(_label);
            stack.Children.Add(_state);
            Child = stack;
            Height = 84;
            Margin = new Thickness(4);
            Padding = new Thickness(14, 14, 10, 10);
            CornerRadius = new CornerRadius(22);
            Cursor = Cursors.Hand;
            MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (_on is not null)
                    click();
            };
            MouseEnter += (_, _) => Opacity = _on is null ? 0.45 : 0.85;
            MouseLeave += (_, _) => Opacity = _on is null ? 0.45 : 1;
            Apply();
        }

        public string State
        {
            get => _state.Text;
            set => _state.Text = value;
        }

        /// <param name="on">Acceso, spento, o null se non disponibile.</param>
        public void Set(bool? on, string state)
        {
            _on = on;
            _state.Text = state;
            Apply();
        }

        private void Apply()
        {
            var lit = _on == true;
            Background = lit ? Theme.WhiteBrush : Theme.SurfaceBrush;
            _icon.DotColor = lit ? Theme.Black : Theme.White;
            _label.Foreground = lit ? Theme.BlackBrush : Theme.WhiteBrush;
            _state.Foreground = lit ? DarkText : Theme.GreyBrush;
            Opacity = _on is null ? 0.45 : 1;
        }
    }

    /// <summary>Cursore a pillola alla Nothing: la parte piena è bianca, l'icona resta sempre sulla parte piena.</summary>
    private sealed class PillSlider : Grid
    {
        private const double PillHeight = 52;

        private readonly Border _fill = new()
        {
            Background = Theme.WhiteBrush,
            CornerRadius = new CornerRadius(PillHeight / 2),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        private readonly DotMatrixView _icon = new()
        {
            Pitch = 2.5,
            DotColor = Theme.Black,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20, 0, 0, 0),
        };
        private readonly DotMatrixView _value = new()
        {
            Pitch = 2,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 20, 0),
        };
        private readonly Action<double> _changed;
        private readonly Action? _onIcon;
        private double _value01;
        private bool _dragging;

        public PillSlider(DotGlyph icon, Action<double> changed, Action? onIcon)
        {
            _changed = changed;
            _onIcon = onIcon;
            Height = PillHeight;
            Background = Brushes.Transparent;
            Cursor = Cursors.Hand;
            var track = new Border { Background = Theme.SurfaceBrush, CornerRadius = new CornerRadius(PillHeight / 2) };
            Children.Add(track);
            Children.Add(_fill);
            Children.Add(_icon);
            Children.Add(_value);
            Icon = icon;
            SizeChanged += (_, _) => Render();
            IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1 : 0.45;

            MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                var x = e.GetPosition(this).X;
                if (_onIcon is not null && x < PillHeight)
                {
                    _onIcon();
                    return;
                }
                _dragging = true;
                CaptureMouse();
                SetFrom(x);
            };
            MouseMove += (_, e) =>
            {
                if (_dragging)
                    SetFrom(e.GetPosition(this).X);
            };
            MouseLeftButtonUp += (_, _) =>
            {
                _dragging = false;
                ReleaseMouseCapture();
            };
            LostMouseCapture += (_, _) => _dragging = false;
        }

        public DotGlyph Icon
        {
            set => _icon.Dots = DotGrid.From(value);
        }

        public double Value
        {
            get => _value01;
            set
            {
                if (_dragging)
                    return; // durante il trascinamento comanda il mouse, non l'eco del sistema
                _value01 = Math.Clamp(value, 0, 1);
                Render();
            }
        }

        private void SetFrom(double x)
        {
            var width = ActualWidth;
            if (width <= PillHeight)
                return;
            _value01 = Math.Clamp((x - (PillHeight / 2)) / (width - PillHeight), 0, 1);
            Render();
            _changed(_value01);
        }

        /// <summary>La parte piena parte dalla larghezza dell'icona: a zero resta un cerchio bianco con l'icona.</summary>
        private void Render()
        {
            var width = double.IsNaN(Width) ? ActualWidth : Width;
            if (width <= 0)
                width = PanelWidth - 36;
            _fill.Width = PillHeight + ((width - PillHeight) * _value01);
            _value.Dots = DotText.Render($"{Math.Round(_value01 * 100):0}");
            _value.DotColor = _fill.Width > width - 56 ? Theme.Black : Theme.Grey;
        }
    }
}
