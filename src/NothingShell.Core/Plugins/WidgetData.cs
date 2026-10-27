using System.Globalization;
using System.Text.Json;

namespace NothingShell.Core.Plugins;

/// <summary>Contenuto di un widget plugin. <see cref="Bar"/> è 0..1, null = nessuna barra.</summary>
public sealed record WidgetData(string Header, string Value, string Caption, double? Bar, bool Alert)
{
    public static WidgetData Error(PluginManifest manifest, string reason)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new(DefaultHeader(manifest), JsonTemplate.Missing, reason, null, false);
    }

    /// <summary>Sorgente http: i campi di display risolti sulla risposta JSON.</summary>
    public static WidgetData FromHttp(PluginManifest manifest, JsonElement response)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var d = manifest.Display;
        return new WidgetData(
            d.Header is { } h ? JsonTemplate.Render(h, response) : DefaultHeader(manifest),
            JsonTemplate.Render(d.Value ?? string.Empty, response),
            d.Caption is { } c ? JsonTemplate.Render(c, response) : string.Empty,
            d.Bar is { } b ? ParseFraction(JsonTemplate.Render(b, response), d.BarMax) : null,
            false);
    }

    /// <summary>
    /// Sorgente command: stdout JSON (i campi presenti sostituiscono quelli di display) oppure testo semplice → value.
    /// </summary>
    public static WidgetData FromCommandOutput(PluginManifest manifest, string stdout)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(stdout);
        var d = manifest.Display;
        var text = stdout.Trim();
        var header = d.Header ?? DefaultHeader(manifest);
        var value = d.Value ?? string.Empty;
        var caption = d.Caption ?? string.Empty;
        double? bar = null;
        var alert = false;

        if (text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                header = Field(root, "header") ?? header;
                value = Field(root, "value") ?? value;
                caption = Field(root, "caption") ?? caption;
                if (root.TryGetProperty("bar", out var b))
                    bar = b.ValueKind == JsonValueKind.Number ? Math.Clamp(b.GetDouble(), 0, 1) : ParseFraction(b.ToString(), 1);
                alert = root.TryGetProperty("alert", out var a) && a.ValueKind == JsonValueKind.True;
                return new WidgetData(header, value, caption, bar, alert);
            }
            catch (JsonException)
            {
                // non è JSON: si mostra come testo
            }
        }

        var firstLine = text.Split('\n', 2)[0].Trim();
        return new WidgetData(header, firstLine.Length > 0 ? firstLine : JsonTemplate.Missing, caption, bar, alert);
    }

    /// <summary>Numero (anche "73%" o "0,5") diviso per <paramref name="max"/> e limitato a 0..1; null se non è un numero.</summary>
    public static double? ParseFraction(string text, double max)
    {
        ArgumentNullException.ThrowIfNull(text);
        var cleaned = text.Trim().TrimEnd('%').Replace(',', '.');
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && max > 0
            ? Math.Clamp(v / max, 0, 1)
            : null;
    }

    private static string DefaultHeader(PluginManifest manifest) => manifest.Name.ToUpperInvariant();

    private static string? Field(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
            : null;
}
