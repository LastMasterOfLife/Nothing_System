using System.Text.Json;
using System.Text.Json.Serialization;
using NothingShell.Core.Storage;

namespace NothingShell.Core.Widgets;

/// <summary>Posizione di un widget plugin, per id del plugin.</summary>
public sealed record PluginPlacement(string Id, double X, double Y);

public sealed record WidgetSettings(bool Locked, IReadOnlyList<WidgetPlacement> Widgets, IReadOnlyList<PluginPlacement>? Plugins = null)
{
    public static WidgetSettings Empty { get; } = new(false, []);
}

/// <summary>Salvataggio JSON delle posizioni dei widget. Un file mancante o rovinato vale come vuoto.</summary>
public sealed class WidgetSettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath { get; } = Path.Combine(AtomicFile.DataFolder, "widgets.json");

    public string FilePath { get; } = path;

    public static string Serialize(WidgetSettings settings) => JsonSerializer.Serialize(settings, Options);

    public static WidgetSettings Deserialize(string json)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<WidgetSettings>(json, Options);
            return settings is { Widgets: not null } ? settings : WidgetSettings.Empty;
        }
        catch (JsonException)
        {
            return WidgetSettings.Empty;
        }
    }

    public WidgetSettings Load() =>
        AtomicFile.TryReadAllText(FilePath) is { } json ? Deserialize(json) : WidgetSettings.Empty;

    public void Save(WidgetSettings settings) => AtomicFile.WriteAllText(FilePath, Serialize(settings));
}
