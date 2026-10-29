using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NothingShell.Core.Plugins;

/// <summary>Segnaposto <c>{a.b[0].c}</c> risolti su un documento JSON; percorsi mancanti → <c>--</c>.</summary>
public static class JsonTemplate
{
    public const string Missing = "--";

    public static string Render(string template, JsonElement root)
    {
        ArgumentNullException.ThrowIfNull(template);
        var output = new StringBuilder(template.Length);
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf('{', i);
            var close = open < 0 ? -1 : template.IndexOf('}', open + 1);
            if (open < 0 || close < 0)
            {
                output.Append(template, i, template.Length - i);
                break;
            }
            output.Append(template, i, open - i);
            var path = template.Substring(open + 1, close - open - 1).Trim();
            output.Append(TryResolve(root, path, out var value) ? Format(value) : Missing);
            i = close + 1;
        }
        return output.ToString();
    }

    /// <summary>Segue un percorso come <c>current.temperature_2m</c> o <c>hourly.time[0]</c>.</summary>
    public static bool TryResolve(JsonElement root, string path, out JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(path);
        value = root;
        if (path.Length == 0)
            return false;

        foreach (var segment in path.Split('.'))
        {
            var name = segment;
            var indexes = new List<int>();
            var bracket = name.IndexOf('[', StringComparison.Ordinal);
            if (bracket >= 0)
            {
                foreach (var part in name[bracket..].Split('[', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!int.TryParse(part.TrimEnd(']'), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index))
                        return false;
                    indexes.Add(index);
                }
                name = name[..bracket];
            }

            if (name.Length > 0)
            {
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out value))
                    return false;
            }
            foreach (var index in indexes)
            {
                if (value.ValueKind != JsonValueKind.Array)
                    return false;
                var at = index < 0 ? value.GetArrayLength() + index : index; // [-1] è l'ultimo
                if (at < 0 || at >= value.GetArrayLength())
                    return false;
                value = value[at];
            }
        }
        return true;
    }

    private static string Format(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.TryGetDouble(out var d)
            ? Math.Round(d, 1).ToString("0.#", CultureInfo.InvariantCulture)
            : value.GetRawText(),
        JsonValueKind.True => "SI",
        JsonValueKind.False => "NO",
        JsonValueKind.Null or JsonValueKind.Undefined => Missing,
        _ => value.GetRawText(),
    };
}
