using System.Windows;
using System.Windows.Controls;
using NothingShell.App.Rendering;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Notch;
using NothingShell.Core.Plugins;
using NothingShell.Core.Widgets;

namespace NothingShell.App.Widgets;

/// <summary>Widget di un plugin: intestazione, valore a punti, didascalia, barra opzionale (docs/07-settings-plugins.md).</summary>
public sealed class PluginWidget : WidgetView
{
    private const double MinPitch = 2;

    private readonly DotMatrixView _value = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _caption = Label(string.Empty, 11, Theme.GreyBrush);
    private readonly DotMatrixView _bar;
    private readonly int _barColumns;
    private readonly double _innerWidth;
    private readonly double _maxPitch;

    public PluginWidget(PluginManifest manifest)
        : base(Guard(manifest).WidgetId, SizeOf(manifest), manifest.Name.ToUpperInvariant())
    {
        Manifest = manifest;
        var wide = manifest.Size == PluginSize.Wide;
        _innerWidth = Width - 2 * Padding16 - 2;
        _maxPitch = wide ? 8 : 5;
        _barColumns = (int)(_innerWidth / BarPitch);
        _bar = new DotMatrixView
        {
            Pitch = BarPitch,
            OffColor = Theme.DotOff,
            Dots = DotBar.Render(0, _barColumns, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 14, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        _caption.Margin = new Thickness(0, 10, 0, 0);
        _caption.TextTrimming = TextTrimming.CharacterEllipsis;

        var stack = new StackPanel();
        stack.Children.Add(_value);
        stack.Children.Add(_bar);
        stack.Children.Add(_caption);
        Body.Children.Add(stack);
        Update(new WidgetData(manifest.Name.ToUpperInvariant(), "...", "caricamento", null, false));
    }

    public PluginManifest Manifest { get; }

    public void Update(WidgetData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        HeaderText!.Text = data.Header;
        var dots = DotText.Render(data.Value);
        _value.Dots = dots;
        _value.Pitch = FitPitch(dots.Width);
        _value.DotColor = data.Alert ? Theme.Red : Theme.White;
        _caption.Text = data.Caption;

        if (data.Bar is { } fraction)
        {
            _bar.Dots = DotBar.Render(fraction, _barColumns, 2);
            _bar.DotColor = data.Alert ? Theme.Red : Theme.White;
            _bar.Visibility = Visibility.Visible;
        }
        else
        {
            _bar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Il passo più grande (a mezzi punti) con cui il valore entra nella larghezza utile.</summary>
    private double FitPitch(int columns) =>
        columns == 0 ? _maxPitch : Math.Max(MinPitch, Math.Min(_maxPitch, Math.Floor(_innerWidth / columns * 2) / 2));

    public static Size2 SizeOf(PluginManifest manifest) =>
        Guard(manifest).Size == PluginSize.Wide ? WidgetSizes.For(WidgetKind.Clock) : WidgetSizes.For(WidgetKind.Battery);

    private static PluginManifest Guard(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest;
    }
}
