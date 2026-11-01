using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NothingShell.Core.Design;

namespace NothingShell.App.Rendering;

/// <summary>
/// Token del design system convertiti in tipi WPF (docs/02-design-system.md). I pennelli sono sempre gli stessi oggetti e
/// cambiano colore col tema, così tutto ciò che li usa si aggiorna da solo: niente da ricostruire, niente riavvio. Vivono
/// sul thread dell'interfaccia e non sono congelati: per animarne uno si usa <see cref="Motion"/>, che ne fa una copia.
/// </summary>
public static class Theme
{
    /// <summary>Raggio delle superfici (finestre, widget, pannelli), come in Windows 11.</summary>
    public const double RadiusLarge = 8;

    /// <summary>Raggio dei controlli e delle schede.</summary>
    public const double RadiusSmall = 4;

    private static readonly SolidColorBrush[] Shared = new SolidColorBrush[12];
    private static readonly GradientStop SheenTop = new(Colors.Transparent, 0);
    private static readonly GradientStop SheenBottom = new(Colors.Transparent, 0.55);
    private static readonly GradientStop RimTop = new(Colors.Transparent, 0);
    private static readonly GradientStop RimBottom = new(Colors.Transparent, 0.6);

    private static readonly ThemeSignal Signal = new();

    static Theme()
    {
        for (var i = 0; i < Shared.Length; i++)
            Shared[i] = new SolidColorBrush();
        PanelBrush = new LinearGradientBrush(new GradientStopCollection { SheenTop, SheenBottom }, 90);
        RimBrush = new LinearGradientBrush(new GradientStopCollection { RimTop, RimBottom }, 90);
        Current = AppearanceSettings.Default.ToPalette(ReadSystemDark(), ReadSystemAccent(), ReadSystemTransparency());
        Paint();
    }

    /// <summary>Tema, colori o vetro cambiati. Chi non può seguire da solo i pennelli si ridisegna qui.</summary>
    public static event EventHandler? Changed
    {
        add => Signal.Changed += value;
        remove => Signal.Changed -= value;
    }

    public static Palette Current { get; private set; }

    public static AppearanceSettings Settings { get; private set; } = AppearanceSettings.Default;

    /// <summary>Stile Windows: testo Segoe e icone Fluent al posto della matrice a punti.</summary>
    public static bool IsWindowsStyle => Settings.Display == DisplayStyle.Windows;

    /// <summary>Trasparenza accesa nelle impostazioni di Windows: spenta lì, il vetro qui non si attiva.</summary>
    public static bool SystemTransparency => ReadSystemTransparency();

    public static Color Base => ToColor(Current.Base);
    public static Color Surface => ToColor(Current.Surface);
    public static Color SurfaceHover => ToColor(Current.SurfaceHover);
    public static Color Ink => ToColor(Current.Ink);
    public static Color Muted => ToColor(Current.Muted);
    public static Color Accent => ToColor(Current.Accent);
    public static Color OnAccent => ToColor(Current.OnAccent);
    public static Color Critical => ToColor(Current.Critical);
    public static Color DotOff => ToColor(Current.DotOff);
    public static Color Outline => ToColor(Current.Outline);

    public static Brush BaseBrush => Shared[0];
    public static Brush BaseSolidBrush => Shared[1];
    public static Brush SurfaceBrush => Shared[2];
    public static Brush SurfaceHoverBrush => Shared[3];
    public static Brush CardBrush => Shared[4];
    public static Brush InkBrush => Shared[5];
    public static Brush MutedBrush => Shared[6];
    public static Brush AccentBrush => Shared[7];
    public static Brush OnAccentBrush => Shared[8];
    public static Brush CriticalBrush => Shared[9];
    public static Brush OutlineBrush => Shared[10];
    public static Brush DotOffBrush => Shared[11];

    /// <summary>Fondo delle superfici principali: col vetro ha un velo di luce in alto.</summary>
    public static Brush PanelBrush { get; }

    /// <summary>Bordo delle superfici principali: col vetro la luce sta sul lato alto.</summary>
    public static Brush RimBrush { get; }

    public static readonly FontFamily BodyFont = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static Color ToColor(Argb c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    /// <summary>Applica le scelte della pagina Aspetto, leggendo da Windows ciò che segue il sistema.</summary>
    public static void Apply(AppearanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var palette = settings.ToPalette(ReadSystemDark(), ReadSystemAccent(), ReadSystemTransparency());
        // Windows avvisa di continuo per impostazioni che qui non contano: niente da ridisegnare.
        if (settings == Settings && palette == Current)
            return;
        Settings = settings;
        Current = palette;
        Paint();
        Signal.Raise();
    }

    /// <summary>Tema e colore di Windows possono cambiare in qualsiasi momento: la shell li segue.</summary>
    public static void FollowSystem() =>
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle
                && Application.Current is { } app)
                app.Dispatcher.BeginInvoke(() => Apply(Settings));
        };

    /// <summary>Lo stesso token di <paramref name="palette"/> nel tema corrente; gli altri colori restano come sono.</summary>
    public static Color Follow(Color color, Palette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (ReferenceEquals(palette, Current))
            return color;
        foreach (var (from, to) in new (Argb From, Argb To)[]
                 {
                     (palette.Ink, Current.Ink),
                     (palette.Muted, Current.Muted),
                     (palette.Accent, Current.Accent),
                     (palette.OnAccent, Current.OnAccent),
                     (palette.Critical, Current.Critical),
                     (palette.Base, Current.Base),
                     (palette.Surface, Current.Surface),
                     (palette.SurfaceHover, Current.SurfaceHover),
                     (palette.DotOff, Current.DotOff),
                     (palette.Outline, Current.Outline),
                 })
            if (ToColor(from) == color)
                return ToColor(to);
        return color;
    }

    /// <summary>Uno dei pennelli condivisi: non si anima direttamente, cambierebbe colore a tutta la shell.</summary>
    public static bool IsShared(Brush brush) => Array.IndexOf(Shared, brush) >= 0;

    /// <summary>Superficie principale: fondo, bordo e angoli uguali per finestre, widget e pannelli.</summary>
    public static T Panel<T>(T border, double radius = RadiusLarge)
        where T : Border
    {
        ArgumentNullException.ThrowIfNull(border);
        border.Background = PanelBrush;
        border.BorderBrush = RimBrush;
        border.BorderThickness = new Thickness(1);
        border.CornerRadius = new CornerRadius(radius);
        return border;
    }

    private static void Paint()
    {
        var p = Current;
        foreach (var (index, color) in new (int, Argb)[]
                 {
                     (0, p.Base), (1, p.BaseSolid), (2, p.Surface), (3, p.SurfaceHover), (4, p.Card), (5, p.Ink), (6, p.Muted),
                     (7, p.Accent), (8, p.OnAccent), (9, p.Critical), (10, p.Outline), (11, p.DotOff),
                 })
            Shared[index].Color = ToColor(color);
        SheenTop.Color = ToColor(p.SheenTop);
        SheenBottom.Color = ToColor(p.Base);
        RimTop.Color = ToColor(p.RimTop);
        RimBottom.Color = ToColor(p.RimBottom);
    }

    private static bool ReadSystemDark() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;

    private static bool ReadSystemTransparency() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1) is not 0;

    /// <summary>Il colore d'accento di Windows; l'azzurro di serie se non si riesce a leggere.</summary>
    private static Argb ReadSystemAccent()
    {
        try
        {
            var c = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
            return new Argb(0xFF, c.R, c.G, c.B);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or TypeInitializationException)
        {
            return Argb.FromRgb(0x0078D4);
        }
    }

    /// <summary>Sorgente dell'evento: una classe statica non può esserlo per <see cref="WeakEventManager{TEventSource,TEventArgs}"/>.</summary>
    private sealed class ThemeSignal
    {
        public event EventHandler? Changed;

        public void Raise() => Changed?.Invoke(null, EventArgs.Empty);
    }
}
