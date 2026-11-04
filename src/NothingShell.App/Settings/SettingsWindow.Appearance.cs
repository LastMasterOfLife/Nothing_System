using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NothingShell.App.Interop;
using NothingShell.App.Rendering;
using NothingShell.Core.Design;
using NothingShell.Core.Notch;

namespace NothingShell.App.Settings;

/// <summary>Pagina Aspetto: ogni scelta si vede subito su tutta la shell, questa finestra compresa.</summary>
public sealed partial class SettingsWindow
{
    private const double SwatchSize = 30;

    /// <summary>Durante il trascinamento del cursore la pagina non si rifà, altrimenti il cursore sparirebbe sotto il mouse.</summary>
    private bool _holdRebuild;

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (!_holdRebuild)
            Rebuild();
    }

    private void SetAppearance(Func<AppearanceSettings, AppearanceSettings> change) => _shell.Appearance = change(_shell.Appearance);

    private void AppearancePage()
    {
        var look = _shell.Appearance;

        Section("Stile rapido");
        var presets = new UniformGrid2(AppearanceChoices.Presets.Count);
        foreach (var (name, description, settings) in AppearanceChoices.Presets)
            presets.Add(PresetTile(name, description, settings.WithNotchOf(look), settings.WithNotchOf(look) == look));
        _current.Children.Add(presets);

        Section("Tema");
        ChoiceRow("Modalità", "Sistema segue l'impostazione chiaro o scuro di Windows",
            [("Sistema", AppTheme.System), ("Chiaro", AppTheme.Light), ("Scuro", AppTheme.Dark)],
            look.Theme, v => SetAppearance(a => a with { Theme = v }));

        Section("Colori");
        AccentRow(look);
        ChoiceRow("Sfondo", "Colorato prende una punta dell'accento; Profondo è nero o bianco puro",
            [("Neutro", SurfaceTone.Neutral), ("Colorato", SurfaceTone.Tinted), ("Profondo", SurfaceTone.Deep)],
            look.Tone, v => SetAppearance(a => a with { Tone = v }));

        Section("Materiale");
        var transparency = Theme.SystemTransparency;
        ToggleRow("Liquid Glass",
            transparency
                ? "Notch, widget, barra e pannelli lasciano intravedere ciò che sta dietro, sfocato"
                : "Gli effetti di trasparenza di Windows sono spenti: il vetro resta inattivo finché non li riaccendi",
            look.Glass, v => SetAppearance(a => a with { Glass = v }));
        if (look.Glass && transparency)
            GlassStrengthRow(look);

        Section("Stile");
        ChoiceRow("Testo e icone", "Windows usa Segoe UI e le icone Fluent; Punti è la matrice di Nothing",
            [("Windows", DisplayStyle.Windows), ("Punti", DisplayStyle.Dots)],
            look.Display, v => SetAppearance(a => a with { Display = v }));

        Section("Notch");
        ChoiceRow("Forma", "Unito al bordo si fonde con l'alto dello schermo, con due raccordi ai lati",
            [("Sospeso", NotchStyle.Floating), ("Unito al bordo", NotchStyle.Attached)],
            look.NotchStyle, v => SetAppearance(a => a with { NotchStyle = v }));
        ChoiceRow("Misura", "Piccola è quella di sempre, Grande è più larga. Cambiandola la shell si riavvia",
            [("Piccola", NotchSize.Small), ("Grande", NotchSize.Large)],
            look.NotchSize, v => SetAppearance(a => a with { NotchSize = v }));
        ToggleRow("Mascotte", "A riposo, al posto dell'ora, due occhi che sbattono le palpebre, seguono il mouse, si stupiscono agli avvisi, gioiscono quando un agente o un comando finisce e si addormentano",
            look.NotchMascot, v => SetAppearance(a => a with { NotchMascot = v }));
        SliderRow("Ombra", "Quanto si allarga l'ombra sotto il notch aperto: tutta a sinistra non c'è",
            look.NotchShadow, v => _shell.Appearance with { NotchShadow = v });
        ToggleRow("Notch discreto", "A riposo resta solo una lineetta contro il bordo alto dello schermo: passandoci sopra si apre",
            look.QuietNotch, v => SetAppearance(a => a with { QuietNotch = v }));
        ChoiceRow("Apertura", "Col mouse sopra, quanto aspetta prima di aprirsi: così passarci di sfuggita non lo apre",
            [("Subito", 0), ("0,15 s", 150), ("0,3 s", AppearanceSettings.DefaultNotchOpenDelayMs), ("0,6 s", 600)],
            look.NotchOpenDelayMs, v => SetAppearance(a => a with { NotchOpenDelayMs = v }));
        ChoiceRow("Chiusura", "Uscito il mouse, quanto resta aperto: tornandoci sopra intanto non si chiude",
            [("Subito", 0), ("0,3 s", 300), ("0,7 s", 700), ("1,5 s", 1500)],
            look.NotchCloseDelayMs, v => SetAppearance(a => a with { NotchCloseDelayMs = v }));
        NotchScreenRows(look);

        Section("Movimento");
        ChoiceRow("Molla del notch", "Morbida si apre piano senza rimbalzo, Nusa rimbalza appena, Scattante è svelta e vivace",
            [("Morbida", NotchFeel.Soft), ("Nusa", NotchFeel.Nusa), ("Scattante", NotchFeel.Snappy)],
            look.NotchFeel, v => SetAppearance(a => a with { NotchFeel = v }));
        ChoiceRow("Velocità", "Di tutte le animazioni della shell: sotto 1× più calme, sopra più svelte",
            [("0,5×", 0.5), ("0,75×", 0.75), ("1×", 1.0), ("1,25×", 1.25), ("1,5×", 1.5)],
            look.AnimationSpeed, v => SetAppearance(a => a with { AnimationSpeed = v }));
    }

    /// <summary>Su quale schermo: con più schermi si può sceglierne uno, numerati da sinistra a destra.</summary>
    private void NotchScreenRows(AppearanceSettings look)
    {
        var monitors = NotchPlacement.InOrder(WindowNative.Monitors());
        var several = monitors.Count > 1;
        (string, NotchScreen)[] screens = several
            ? [("Principale", NotchScreen.Primary), ("Dove sta il mouse", NotchScreen.Mouse), ("Uno scelto", NotchScreen.Chosen)]
            : [("Principale", NotchScreen.Primary), ("Dove sta il mouse", NotchScreen.Mouse)];
        ChoiceRow("Schermo", "Dove sta il mouse: il notch ci passa quando è chiuso, e porta lì avvisi e schede",
            screens, look.NotchScreen, v => SetAppearance(a => a with
            {
                NotchScreen = v,
                NotchMonitor = v == NotchScreen.Chosen ? a.NotchMonitor ?? monitors.FirstOrDefault(m => !m.IsPrimary)?.Name : a.NotchMonitor,
            }));
        if (!several || look.NotchScreen != NotchScreen.Chosen)
            return;
        var chosen = NotchPlacement.Pick(monitors, NotchScreen.Chosen, look.NotchMonitor, 0)?.Name ?? "";
        ChoiceRow("Quale schermo", "Numerati da sinistra a destra. Se lo scolleghi, il notch torna sul principale",
            [.. monitors.Select((m, i) => ($"{i + 1}{(m.IsPrimary ? " · principale" : "")}", m.Name))],
            chosen, v => SetAppearance(a => a with { NotchMonitor = v }));
    }

    /// <summary>Scelta fra poche voci: titolo a sinistra, controllo a segmenti a destra.</summary>
    private void ChoiceRow<T>(string title, string description, (string Label, T Value)[] options, T selected, Action<T> pick)
        where T : notnull
    {
        var row = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(Label(title, 14, Theme.InkBrush));
        var hint = Label(description, 12, Theme.MutedBrush);
        hint.Margin = new Thickness(0, 3, 0, 0);
        hint.TextWrapping = TextWrapping.Wrap;
        text.Children.Add(hint);
        row.Children.Add(text);

        var segments = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (label, value) in options)
        {
            var chip = Chip(label, EqualityComparer<T>.Default.Equals(value, selected));
            chip.Margin = new Thickness(4, 0, 0, 0);
            chip.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                pick(value);
            };
            segments.Children.Add(chip);
        }
        Grid.SetColumn(segments, 1);
        row.Children.Add(segments);
        AddRow(row);
    }

    /// <summary>Colore d'accento: quello di Windows, i colori proposti o uno scritto a mano.</summary>
    private void AccentRow(AppearanceSettings look)
    {
        var block = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        block.Children.Add(Label("Colore d'accento", 14, Theme.InkBrush));
        var hint = Label(look.Accent is null ? "Segue il colore d'accento di Windows" : $"Scelto: {look.Accent}", 12, Theme.MutedBrush);
        hint.Margin = new Thickness(0, 3, 0, 10);
        block.Children.Add(hint);

        var swatches = new WrapPanel();
        swatches.Children.Add(Swatch(null, "Colore di Windows", look.Accent is null));
        foreach (var (name, hex) in AppearanceChoices.Accents)
            swatches.Children.Add(Swatch(hex, name, string.Equals(look.Accent, hex, StringComparison.OrdinalIgnoreCase)));
        block.Children.Add(swatches);
        block.Children.Add(CustomAccent(look));
        AddRow(block);
    }

    private Border Swatch(string? hex, string name, bool selected)
    {
        var color = hex is not null && Argb.TryParse(hex) is { } parsed
            ? Theme.ToColor(parsed)
            : Theme.Accent;
        var fill = new Grid();
        fill.Children.Add(new Ellipse { Fill = new SolidColorBrush(color) });
        if (hex is null)
            fill.Children.Add(new TextBlock
            {
                Text = "\uE713",
                FontFamily = Theme.IconFont,
                FontSize = 13,
                Foreground = Theme.OnAccentBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        else if (selected)
            fill.Children.Add(new TextBlock
            {
                Text = "\uE73E",
                FontFamily = Theme.IconFont,
                FontSize = 13,
                Foreground = Argb.TryParse(hex) is { } c && c.ContrastWith(Argb.FromRgb(0xFFFFFF)) >= 3 ? Brushes.White : Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        var swatch = new Border
        {
            Width = SwatchSize + 6,
            Height = SwatchSize + 6,
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(2),
            BorderThickness = new Thickness(1.5),
            BorderBrush = selected ? Theme.InkBrush : Brushes.Transparent,
            CornerRadius = new CornerRadius((SwatchSize + 6) / 2),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = name,
            Child = fill,
        };
        swatch.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SetAppearance(a => a with { Accent = hex });
        };
        return swatch;
    }

    /// <summary>Colore scritto a mano: si applica con Invio o col pulsante, così la pagina non si rifà mentre si digita.</summary>
    private StackPanel CustomAccent(AppearanceSettings look)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var box = new TextBox
        {
            Text = look.Accent ?? string.Empty,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Theme.InkBrush,
            FontFamily = Theme.BodyFont,
            FontSize = 13,
            Width = 96,
            MaxLength = 7,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var frame = new Border
        {
            Background = Theme.SurfaceBrush,
            BorderBrush = Theme.OutlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Theme.RadiusSmall),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 8, 0),
            Child = box,
        };
        box.GotKeyboardFocus += (_, _) => frame.BorderBrush = Theme.AccentBrush;
        box.LostKeyboardFocus += (_, _) => frame.BorderBrush = Theme.OutlineBrush;
        var problem = Label(string.Empty, 12, Theme.CriticalBrush);
        problem.VerticalAlignment = VerticalAlignment.Center;
        problem.Margin = new Thickness(8, 0, 0, 0);

        void Commit()
        {
            if (Argb.TryParse(box.Text) is { } color)
                SetAppearance(a => a with { Accent = color.ToHex() });
            else
                problem.Text = "Scrivi un colore come #3A7BD5";
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Commit();
            }
        };
        var apply = Button("Usa questo colore", primary: false);
        apply.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Commit();
        };
        row.Children.Add(frame);
        row.Children.Add(apply);
        row.Children.Add(problem);
        return row;
    }

    /// <summary>Quanto si vede attraverso il vetro: si applica mentre si trascina.</summary>
    private void GlassStrengthRow(AppearanceSettings look) =>
        SliderRow("Trasparenza", "Più velato si legge meglio, più limpido si vede di più",
            look.GlassStrength, v => _shell.Appearance with { GlassStrength = v });

    /// <summary>Cursore da 0 a 1 con <paramref name="change"/> che dice l'aspetto risultante: si vede mentre si trascina.</summary>
    private void SliderRow(string title, string description, double value, Func<double, AppearanceSettings> change)
    {
        var row = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(Label(title, 14, Theme.InkBrush));
        var hint = Label(description, 12, Theme.MutedBrush);
        hint.Margin = new Thickness(0, 3, 0, 0);
        hint.TextWrapping = TextWrapping.Wrap;
        text.Children.Add(hint);
        row.Children.Add(text);

        var slider = new AccentSlider(value);
        slider.DragStarted += () => _holdRebuild = true;
        // Mentre si trascina si vede subito ma non si scrive su disco: si salva al rilascio.
        slider.ValueChanged += v => Theme.Apply(change(v));
        slider.DragCompleted += v =>
        {
            _holdRebuild = false;
            _shell.Appearance = change(v);
        };
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        AddRow(row);
    }

    /// <summary>Stile rapido: un'anteprima del tema con i suoi veri colori, così si sceglie guardando.</summary>
    private Border PresetTile(string name, string description, AppearanceSettings settings, bool selected)
    {
        var palette = settings.ToPalette(Theme.Current.IsDark, Theme.Current.Accent, systemTransparency: false);
        var preview = new Border
        {
            Height = 58,
            CornerRadius = new CornerRadius(Theme.RadiusSmall),
            Background = new SolidColorBrush(Theme.ToColor(palette.BaseSolid)),
            BorderBrush = Theme.OutlineBrush,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 8),
        };
        var sample = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        sample.Children.Add(new Rectangle { Width = 44, Height = 5, RadiusX = 2.5, RadiusY = 2.5, Fill = new SolidColorBrush(Theme.ToColor(palette.Ink)), HorizontalAlignment = HorizontalAlignment.Left });
        sample.Children.Add(new Rectangle { Width = 72, Height = 5, RadiusX = 2.5, RadiusY = 2.5, Fill = new SolidColorBrush(Theme.ToColor(palette.Muted)), Margin = new Thickness(0, 5, 0, 7), HorizontalAlignment = HorizontalAlignment.Left });
        sample.Children.Add(new Rectangle { Width = 28, Height = 8, RadiusX = 4, RadiusY = 4, Fill = new SolidColorBrush(Theme.ToColor(palette.Accent)), HorizontalAlignment = HorizontalAlignment.Left });
        preview.Child = sample;

        var stack = new StackPanel();
        stack.Children.Add(preview);
        stack.Children.Add(Label(name, 13, Theme.InkBrush));
        var hint = Label(description, 11, Theme.MutedBrush);
        hint.TextWrapping = TextWrapping.Wrap;
        hint.Margin = new Thickness(0, 2, 0, 0);
        stack.Children.Add(hint);
        var tile = new Border
        {
            CornerRadius = new CornerRadius(Theme.RadiusSmall + 2),
            BorderThickness = new Thickness(selected ? 2 : 1),
            BorderBrush = selected ? Theme.AccentBrush : Brushes.Transparent,
            Background = Brushes.Transparent,
            Padding = new Thickness(selected ? 7 : 8),
            Margin = new Thickness(0, 4, 0, 4),
            Cursor = Cursors.Hand,
            Child = stack,
        };
        if (!selected)
        {
            tile.MouseEnter += (_, _) => tile.Background = Theme.SurfaceBrush;
            tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
        }
        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _shell.Appearance = settings;
        };
        return tile;
    }

    /// <summary>Tessere affiancate a larghezza uguale.</summary>
    private sealed class UniformGrid2 : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGrid2(int columns) => Columns = columns;

        public void Add(UIElement child) => Children.Add(child);
    }

    /// <summary>Cursore di Windows 11: traccia sottile, parte piena nel colore d'accento, levetta tonda.</summary>
    private sealed class AccentSlider : Grid
    {
        private const double ThumbSize = 18;

        private readonly Border _fill = new() { Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.AccentBrush, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        private readonly TranslateTransform _shift = new();
        private double _value;

        public AccentSlider(double value)
        {
            _value = Math.Clamp(value, 0, 1);
            Height = 28;
            Background = Brushes.Transparent;
            Cursor = Cursors.Hand;
            Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.DotOffBrush, VerticalAlignment = VerticalAlignment.Center });
            Children.Add(_fill);
            Children.Add(new Border
            {
                Width = ThumbSize,
                Height = ThumbSize,
                CornerRadius = new CornerRadius(ThumbSize / 2),
                Background = Theme.BaseSolidBrush,
                BorderBrush = Theme.OutlineBrush,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = _shift,
                Child = new Ellipse { Width = 10, Height = 10, Fill = Theme.AccentBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            SizeChanged += (_, _) => Render();
            MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                DragStarted?.Invoke();
                CaptureMouse();
                SetFrom(e.GetPosition(this).X);
            };
            MouseMove += (_, e) =>
            {
                if (IsMouseCaptured)
                    SetFrom(e.GetPosition(this).X);
            };
            MouseLeftButtonUp += (_, _) =>
            {
                ReleaseMouseCapture();
                DragCompleted?.Invoke(_value);
            };
        }

        public event Action? DragStarted;

        public event Action<double>? ValueChanged;

        public event Action<double>? DragCompleted;

        private void SetFrom(double x)
        {
            if (ActualWidth <= ThumbSize)
                return;
            _value = Math.Clamp((x - (ThumbSize / 2)) / (ActualWidth - ThumbSize), 0, 1);
            Render();
            ValueChanged?.Invoke(_value);
        }

        private void Render()
        {
            var center = (ThumbSize / 2) + ((ActualWidth - ThumbSize) * _value);
            _fill.Width = Math.Max(0, center);
            _shift.X = center - (ThumbSize / 2);
        }
    }
}
