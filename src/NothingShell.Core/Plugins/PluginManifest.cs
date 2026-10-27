using System.Text.Json;
using System.Text.RegularExpressions;

namespace NothingShell.Core.Plugins;

public enum PluginSize
{
    Small,
    Wide,
}

public enum PluginSourceType
{
    Http,
    Command,
}

public sealed record PluginDisplay(string? Header, string? Value, string? Caption, string? Bar, double BarMax);

public sealed record PluginManifest(
    string Id,
    string Name,
    PluginSize Size,
    TimeSpan Refresh,
    PluginSourceType SourceType,
    string Target,
    IReadOnlyList<string> Arguments,
    PluginDisplay Display)
{
    public const int MinRefreshSeconds = 5;
    public const int DefaultRefreshSeconds = 300;

    /// <summary>Identificativo del widget nelle impostazioni e in widgets.json.</summary>
    public string WidgetId => $"plugin:{Id}";
}

/// <summary>Esito della lettura di un manifest: il manifest oppure il motivo dell'errore.</summary>
public sealed record PluginLoadResult(string FolderName, PluginManifest? Manifest, string? Error);

/// <summary>Lettura e validazione di plugin.json (docs/07-settings-plugins.md).</summary>
public static partial class PluginManifestParser
{
    public static PluginLoadResult Parse(string folderName, string json)
    {
        ArgumentNullException.ThrowIfNull(folderName);
        if (!IdPattern().IsMatch(folderName))
            return Fail(folderName, "nome della cartella non valido (solo minuscole, cifre e trattini)");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            return Fail(folderName, $"JSON non valido: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Fail(folderName, "il manifest deve essere un oggetto JSON");

            var name = String(root, "name");
            if (string.IsNullOrWhiteSpace(name))
                return Fail(folderName, "manca \"name\"");

            var size = String(root, "size")?.ToLowerInvariant() switch
            {
                null or "small" => PluginSize.Small,
                "wide" => PluginSize.Wide,
                var other => (PluginSize?)null,
            };
            if (size is null)
                return Fail(folderName, "\"size\" deve essere \"small\" o \"wide\"");

            var refresh = root.TryGetProperty("refreshSeconds", out var r) && r.TryGetInt32(out var seconds)
                ? seconds
                : PluginManifest.DefaultRefreshSeconds;
            if (refresh < PluginManifest.MinRefreshSeconds)
                return Fail(folderName, $"\"refreshSeconds\" deve essere almeno {PluginManifest.MinRefreshSeconds}");

            if (!root.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object)
                return Fail(folderName, "manca \"source\"");

            var display = root.TryGetProperty("display", out var d) && d.ValueKind == JsonValueKind.Object
                ? new PluginDisplay(String(d, "header"), String(d, "value"), String(d, "caption"), String(d, "bar"),
                    d.TryGetProperty("barMax", out var max) && max.TryGetDouble(out var m) && m > 0 ? m : 1)
                : new PluginDisplay(null, null, null, null, 1);

            switch (String(source, "type")?.ToLowerInvariant())
            {
                case "http":
                    var url = String(source, "url");
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                        return Fail(folderName, "\"source.url\" deve essere un indirizzo http(s) completo");
                    if (string.IsNullOrWhiteSpace(display.Value))
                        return Fail(folderName, "con una sorgente http serve \"display.value\"");
                    return Ok(folderName, new PluginManifest(folderName, name, size.Value, TimeSpan.FromSeconds(refresh),
                        PluginSourceType.Http, uri.ToString(), [], display));

                case "command":
                    var command = String(source, "command");
                    if (string.IsNullOrWhiteSpace(command))
                        return Fail(folderName, "manca \"source.command\"");
                    var args = source.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
                        ? a.EnumerateArray().Select(x => x.ToString()).ToArray()
                        : [];
                    return Ok(folderName, new PluginManifest(folderName, name, size.Value, TimeSpan.FromSeconds(refresh),
                        PluginSourceType.Command, command, args, display));

                default:
                    return Fail(folderName, "\"source.type\" deve essere \"http\" o \"command\"");
            }
        }
    }

    private static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static PluginLoadResult Ok(string folder, PluginManifest manifest) => new(folder, manifest, null);

    private static PluginLoadResult Fail(string folder, string error) => new(folder, null, error);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex IdPattern();
}
