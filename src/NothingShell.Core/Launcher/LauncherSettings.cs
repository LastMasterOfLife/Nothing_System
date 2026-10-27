using System.Text.Json;
using NothingShell.Core.Storage;

namespace NothingShell.Core.Launcher;

public sealed record UsageEntry(int Count, DateTimeOffset LastUsed);

public sealed record LauncherSettings(bool UseWinKey, IReadOnlyDictionary<string, UsageEntry> Usage)
{
    public static LauncherSettings Default { get; } = new(true, new Dictionary<string, UsageEntry>());

    public LauncherSettings RecordLaunch(string appId, DateTimeOffset now)
    {
        var usage = new Dictionary<string, UsageEntry>(Usage);
        usage[appId] = new UsageEntry((usage.TryGetValue(appId, out var u) ? u.Count : 0) + 1, now);
        return this with { Usage = usage };
    }
}

/// <summary>Salvataggio JSON delle preferenze del launcher. File mancante o rovinato → predefinite.</summary>
public sealed class LauncherSettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string DefaultPath { get; } = Path.Combine(AtomicFile.DataFolder, "launcher.json");

    public static string Serialize(LauncherSettings settings) => JsonSerializer.Serialize(settings, Options);

    public static LauncherSettings Deserialize(string json)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<LauncherSettings>(json, Options);
            return settings is { Usage: not null } ? settings : LauncherSettings.Default;
        }
        catch (JsonException)
        {
            return LauncherSettings.Default;
        }
    }

    public LauncherSettings Load() =>
        AtomicFile.TryReadAllText(path) is { } json ? Deserialize(json) : LauncherSettings.Default;

    public void Save(LauncherSettings settings) => AtomicFile.WriteAllText(path, Serialize(settings));
}
