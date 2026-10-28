using System.Text.Json;

namespace NothingShell.Core.Dev;

public enum CiState
{
    Pending,
    Success,
    Failure,
}

/// <summary>Un'esecuzione di GitHub Actions come la restituisce <c>gh run list --json</c>.</summary>
public sealed record CiRun(long Id, string Name, string Status, string? Conclusion, string Url, DateTimeOffset CreatedAt)
{
    public bool Completed => Status.Equals("completed", StringComparison.OrdinalIgnoreCase);

    /// <summary>Saltata e neutra non sono fallimenti; annullata sì (la CI non ha detto "va bene").</summary>
    public bool Failed => Completed && Conclusion?.ToLowerInvariant() is not ("success" or "skipped" or "neutral");
}

/// <summary>Le esecuzioni partite dopo un push e il loro stato complessivo (docs/12-sviluppo.md).</summary>
public sealed record CiStatus(string Folder, string Branch, IReadOnlyList<CiRun> Runs)
{
    public CiState State => CiRuns.State(Runs);

    public string Repository => Path.GetFileName(Folder.TrimEnd('\\', '/'));
}

public static class CiRuns
{
    public const string JsonFields = "databaseId,status,conclusion,name,workflowName,url,createdAt";

    /// <summary>Un'esecuzione creata poco prima del push (orologi diversi) conta ancora.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    public static IReadOnlyList<CiRun> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];
            var runs = new List<CiRun>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("databaseId", out var id) || !id.TryGetInt64(out var databaseId)
                    || !item.TryGetProperty("createdAt", out var created) || !created.TryGetDateTimeOffset(out var createdAt))
                    continue;
                var name = Text(item, "workflowName") ?? Text(item, "name") ?? "CI";
                runs.Add(new CiRun(databaseId, name, Text(item, "status") ?? "queued", Text(item, "conclusion"),
                    Text(item, "url") ?? string.Empty, createdAt));
            }
            return runs;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<CiRun> Since(IEnumerable<CiRun> runs, DateTimeOffset pushedAt)
    {
        ArgumentNullException.ThrowIfNull(runs);
        return runs.Where(r => r.CreatedAt >= pushedAt - ClockSkew).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>In corso finché una non è conclusa; poi fallita se almeno una è fallita.</summary>
    public static CiState State(IReadOnlyList<CiRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        return runs.Count == 0 || runs.Any(r => !r.Completed) ? CiState.Pending
            : runs.Any(r => r.Failed) ? CiState.Failure
            : CiState.Success;
    }

    /// <summary>"build, test" (i falliti se la CI è fallita).</summary>
    public static string Summary(IReadOnlyList<CiRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var shown = State(runs) == CiState.Failure ? runs.Where(r => r.Failed) : runs;
        return string.Join(", ", shown.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
