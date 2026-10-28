using System.Globalization;
using System.Text;

namespace NothingShell.Core.Launcher;

/// <summary>
/// App del catalogo: <see cref="Id"/> è il parsing name in shell:AppsFolder (l'AppUserModelID);
/// <see cref="TargetPath"/> è l'eseguibile del collegamento, se l'app ne ha uno.
/// </summary>
public sealed record AppEntry(string Id, string Name, string? TargetPath = null);

public sealed record AppSection(char Letter, IReadOnlyList<AppEntry> Apps);

/// <summary>Ricerca, filtro e raggruppamento delle app (docs/06-launcher.md).</summary>
public static class AppSearch
{
    public const int MaxResults = 12;
    public const int RecentCount = 6;
    private const int UsageBonusPerLaunch = 5;
    private const int UsageBonusCap = 20;
    private const int MaxSkipPerLetter = 3;

    private static readonly string[] NoiseWords =
    [
        "uninstall", "disinstall", "readme", "leggimi", "documentation", "documentazione",
        "help", "guida", "license", "licenza", "website", "sito web", "release notes",
    ];

    private static readonly string[] NoiseExtensions = [".url", ".txt", ".chm", ".pdf", ".htm", ".html"];

    /// <summary>Elementi dello Start che non sono app da avviare (disinstallazioni, documenti…).</summary>
    public static bool IsNoise(AppEntry app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var name = Normalize(app.Name);
        return NoiseWords.Any(w => name.Contains(w, StringComparison.Ordinal))
               || NoiseExtensions.Any(e => app.Id.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Punteggio della corrispondenza, null se il nome non corrisponde.</summary>
    public static int? Score(string name, string query)
    {
        ArgumentNullException.ThrowIfNull(name);
        var n = Normalize(name);
        var q = Normalize(query);
        if (q.Length == 0)
            return null;

        if (n.StartsWith(q, StringComparison.Ordinal))
            return 1000 - n.Length;

        var words = n.Split([' ', '-', '_', '.', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith(q, StringComparison.Ordinal)))
            return 800;

        if (q.Length >= 2 && string.Concat(words.Select(w => w[0])).StartsWith(q, StringComparison.Ordinal))
            return 700;

        if (n.Contains(q, StringComparison.Ordinal))
            return 500;

        return Subsequence(n, q) is { } skipped ? 300 - skipped : null;
    }

    public static IReadOnlyList<AppEntry> Search(
        IEnumerable<AppEntry> apps, string query, IReadOnlyDictionary<string, UsageEntry> usage, int max = MaxResults)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return apps
            .Select(a => (App: a, Score: Score(a.Name, query)))
            .Where(x => x.Score is not null)
            .Select(x => (x.App, Total: x.Score!.Value + Bonus(usage, x.App.Id)))
            .OrderByDescending(x => x.Total)
            .ThenBy(x => x.App.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .Select(x => x.App)
            .ToArray();
    }

    /// <summary>Sezioni alfabetiche; nomi che non iniziano con una lettera vanno in '#', in fondo.</summary>
    public static IReadOnlyList<AppSection> Sections(IEnumerable<AppEntry> apps) =>
        apps
            .GroupBy(a => SectionLetter(a.Name))
            .OrderBy(g => g.Key == '#' ? 1 : 0)
            .ThenBy(g => g.Key)
            .Select(g => new AppSection(g.Key, g.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToArray()))
            .ToArray();

    /// <summary>Le app avviate più di recente, presenti nel catalogo.</summary>
    public static IReadOnlyList<AppEntry> Recents(
        IEnumerable<AppEntry> apps, IReadOnlyDictionary<string, UsageEntry> usage, int count = RecentCount)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return apps
            .Where(a => usage.ContainsKey(a.Id))
            .OrderByDescending(a => usage[a.Id].LastUsed)
            .Take(count)
            .ToArray();
    }

    public static char SectionLetter(string name)
    {
        var n = Normalize(name);
        return n.Length > 0 && n[0] is >= 'a' and <= 'z' ? char.ToUpperInvariant(n[0]) : '#';
    }

    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        return builder.ToString();
    }

    private static int Bonus(IReadOnlyDictionary<string, UsageEntry> usage, string id) =>
        usage.TryGetValue(id, out var u) ? Math.Min(u.Count, UsageBonusCap) * UsageBonusPerLaunch : 0;

    /// <summary>
    /// Lettere saltate per trovare <paramref name="query"/> in ordine dentro <paramref name="name"/>,
    /// partendo dall'inizio di una parola; null se non si trova o se si saltano troppe lettere.
    /// </summary>
    private static int? Subsequence(string name, string query)
    {
        int? best = null;
        for (var start = 0; start < name.Length; start++)
        {
            var wordStart = start == 0 || !char.IsLetterOrDigit(name[start - 1]);
            if (!wordStart || name[start] != query[0])
                continue;

            int qi = 1, skipped = 0;
            for (var i = start + 1; i < name.Length && qi < query.Length; i++)
            {
                if (name[i] == query[qi])
                    qi++;
                else
                    skipped++;
            }
            if (qi == query.Length && skipped <= MaxSkipPerLetter * query.Length && (best is null || skipped < best))
                best = skipped;
        }
        return best;
    }
}
