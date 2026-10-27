namespace NothingShell.Core.Media;

/// <summary>Stato immutabile della sessione media corrente.</summary>
public sealed record MediaSnapshot(string Title, string Artist, bool IsPlaying, string SourceApp);

/// <summary>Contratto del servizio media (docs/01-architecture.md). L'evento può arrivare da qualsiasi thread.</summary>
public interface IMediaService
{
    MediaSnapshot? Current { get; }

    event Action<MediaSnapshot?>? Changed;

    Task PlayPauseAsync();

    Task NextAsync();

    Task PreviousAsync();
}

public static class SourceNames
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ZuneMusic"] = "Media Player",
        ["ZuneVideo"] = "Film e TV",
        ["MSEdge"] = "Edge",
        ["chrome"] = "Chrome",
        ["firefox"] = "Firefox",
        ["308046B0AF4A39CB"] = "Firefox",
        ["spotify"] = "Spotify",
        ["vlc"] = "VLC",
    };

    /// <summary>
    /// Nome leggibile da un AppUserModelId: "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" → "Spotify",
    /// "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" → "Media Player", "chrome.exe" → "Chrome".
    /// </summary>
    public static string Friendly(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
            return string.Empty;

        var name = appUserModelId.Trim();
        var bang = name.LastIndexOf('!');
        if (bang >= 0 && bang < name.Length - 1)
            name = name[(bang + 1)..];
        var slash = name.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
            name = name[(slash + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1)
            name = name[(dot + 1)..];
        return Aliases.TryGetValue(name, out var alias) ? alias : name;
    }
}
