using System.Text.Json;
using NothingShell.Core.Notch;
using NothingShell.Core.Storage;

namespace NothingShell.Core.Settings;

/// <summary>Preferenze generali della shell (docs/07-settings-plugins.md).</summary>
public sealed record ShellSettings(
    bool Notch,
    bool Bar,
    IReadOnlyList<string> DisabledWidgets,
    bool MailInNotch = true,
    IReadOnlyList<string>? ExtraMailApps = null,
    bool Access = true,
    MailAccount? MailAccount = null,
    IReadOnlyList<LiveActivity>? DisabledActivities = null,
    IReadOnlyList<string>? CalendarFeeds = null,
    DateOnly? MorningBriefShown = null)
{
    public static ShellSettings Default { get; } = new(true, true, []);

    public bool IsActivityEnabled(LiveActivity activity) => DisabledActivities?.Contains(activity) != true;

    public ShellSettings WithActivity(LiveActivity activity, bool enabled)
    {
        var disabled = (DisabledActivities ?? []).Where(a => a != activity).ToList();
        if (!enabled)
            disabled.Add(activity);
        return this with { DisabledActivities = disabled };
    }

    public bool IsWidgetEnabled(string widgetId) => !DisabledWidgets.Contains(widgetId, StringComparer.Ordinal);

    public ShellSettings WithWidget(string widgetId, bool enabled)
    {
        var disabled = DisabledWidgets.Where(w => w != widgetId).ToList();
        if (!enabled)
            disabled.Add(widgetId);
        return this with { DisabledWidgets = disabled };
    }
}

public sealed class ShellSettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string DefaultPath { get; } = Path.Combine(AtomicFile.DataFolder, "settings.json");

    public static string Serialize(ShellSettings settings) => JsonSerializer.Serialize(settings, Options);

    public static ShellSettings Deserialize(string json)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<ShellSettings>(json, Options);
            return settings is null ? ShellSettings.Default
                : settings.DisabledWidgets is null ? settings with { DisabledWidgets = [] }
                : settings;
        }
        catch (JsonException)
        {
            return ShellSettings.Default;
        }
    }

    public ShellSettings Load() => AtomicFile.TryReadAllText(path) is { } json ? Deserialize(json) : ShellSettings.Default;

    public void Save(ShellSettings settings) => AtomicFile.WriteAllText(path, Serialize(settings));
}
