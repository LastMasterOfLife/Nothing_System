using System.Globalization;
using System.Text.RegularExpressions;

namespace NothingShell.Core.Work;

public sealed record WorkRow(string Label, string Value, bool IsGroup = false);

/// <summary>Quello che la pagina Ore mostra dopo un comando; <see cref="Config"/> è la nuova configurazione se il comando l'ha cambiata.</summary>
public sealed record WorkAnswer(string Title, IReadOnlyList<WorkRow> Rows, WorkConfig? Config = null, bool Changed = false);

/// <summary>I comandi scritti nel campo della pagina Ore (docs/19-ore-di-lavoro.md).</summary>
public static partial class WorkCommands
{
    public const string Help =
        "progetto sinapsi_web = sinapsi, elfin · gruppo Cliente A = sinapsi_web, printer · aggiungi ore 2h30 a sinapsi_web · ore ottobre gruppo Cliente A";

    public static WorkAnswer Run(string text, WorkStore store, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(store);
        var line = Spaces().Replace(text.Trim(), " ");
        if (WorkPhrase.IsRequest(line))
            return WorkPhrase.Apply(line, store, now);
        var (verb, rest) = Split(line);
        var config = store.Config;
        switch (verb.ToLowerInvariant())
        {
            case "":
            case "?":
            case "aiuto":
                return Overview(store, now);
            case "progetto":
                return Define(config, rest);
            case "gruppo":
                return DefineGroup(config, rest);
            case "aggiungi":
                return Add(config, rest);
            case "togli" or "rimuovi" or "elimina":
                return Remove(config, rest);
            case "ore":
                return Hours(store, rest, now);
            default:
                // Solo un periodo ("ottobre", "oggi"): vale come "ore ottobre".
                return WorkPeriod.Parse(line, DateOnly.FromDateTime(now)) is not null ? Hours(store, line, now) : Problem($"Non capisco «{line}»");
        }
    }

    /// <summary>Oggi e questo mese, per gruppo e per progetto: quello che si vede senza scrivere niente.</summary>
    public static WorkAnswer Overview(WorkStore store, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var month = store.Report(WorkPeriod.Month(now.Year, now.Month));
        var day = store.Report(new WorkPeriod(today, today, "oggi"));
        if (store.Config.Projects.Count == 0)
            return new WorkAnswer("ORE", [new WorkRow(string.Empty, "Nessun progetto. " + Help)]);
        var rows = new List<WorkRow>();
        foreach (var group in month.Groups)
            rows.Add(new WorkRow(group.Name, $"totale {WorkReport.Duration(group.Seconds)}", true));
        foreach (var project in month.Projects)
            rows.Add(new WorkRow(project.Name, $"{WorkReport.Duration(day.Projects.First(p => p.Name == project.Name).Seconds)} oggi · {WorkReport.Duration(project.Seconds)} mese"));
        return new WorkAnswer("ORE · " + WorkReport.Duration(month.TotalSeconds) + " nel mese", rows);
    }

    private static WorkAnswer Hours(WorkStore store, string rest, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var match = Target().Match(rest);
        var periodText = match.Success ? rest[..match.Index].Trim() : rest.Trim();
        var period = periodText.Length == 0 ? WorkPeriod.Month(now.Year, now.Month) : WorkPeriod.Parse(periodText, today);
        if (period is null)
            return Problem($"Quale periodo? «{periodText}» non lo conosco: oggi, ieri, settimana, mese, un mese per nome o 2026-10");
        var report = store.Report(period.Value);
        var rows = new List<WorkRow>();
        if (match.Success)
        {
            var name = match.Groups[2].Value.Trim();
            if (match.Groups[1].Value.Equals("gruppo", StringComparison.OrdinalIgnoreCase))
            {
                var group = store.Config.Group(name);
                if (group is null)
                    return Problem($"Il gruppo «{name}» non c'è" + Known(store.Config.Groups.Select(g => g.Name)));
                rows.AddRange(group.Members.Select(m => new WorkRow(m, WorkReport.Duration(report.Projects.FirstOrDefault(p => WorkConfig.Same(p.Name, m))?.Seconds ?? 0))));
                if (report.Groups.First(g => WorkConfig.Same(g.Name, group.Name)).Direct is > 0 and var direct)
                    rows.Add(new WorkRow("(sul gruppo)", WorkReport.Duration(direct)));
                var total = report.Groups.First(g => WorkConfig.Same(g.Name, group.Name)).Seconds;
                return new WorkAnswer($"{group.Name.ToUpperInvariant()} · {period.Value.Label} · {WorkReport.Duration(total)}", rows);
            }
            var project = store.Config.Project(name);
            if (project is null)
                return Problem($"Il progetto «{name}» non c'è" + Known(store.Config.Projects.Select(p => p.Name)));
            return new WorkAnswer($"{project.Name.ToUpperInvariant()} · {period.Value.Label}",
                [new WorkRow(project.Name, WorkReport.DurationWithDecimals(report.Projects.First(p => WorkConfig.Same(p.Name, project.Name)).Seconds))]);
        }
        rows.AddRange(report.Groups.Select(g => new WorkRow(g.Name, WorkReport.Duration(g.Seconds), true)));
        rows.AddRange(report.Projects.Select(p => new WorkRow(p.Name, WorkReport.Duration(p.Seconds))));
        return new WorkAnswer($"{period.Value.Label.ToUpperInvariant()} · {WorkReport.Duration(report.TotalSeconds)}", rows);
    }

    private static WorkAnswer Define(WorkConfig config, string rest)
    {
        var (name, keywords) = NameAndList(rest);
        if (name.Length == 0)
            return Problem("Serve un nome: progetto sinapsi_web = sinapsi, elfin");
        if (config.Group(name) is not null)
            return Problem($"«{name}» è già un gruppo");
        var project = new WorkProject(name, keywords.Count > 0 ? keywords : [name]);
        var projects = config.Projects.Where(p => !WorkConfig.Same(p.Name, name)).Append(project).ToArray();
        return new WorkAnswer("PROGETTO", [new WorkRow(name, "titoli che contengono: " + string.Join(", ", project.Keywords))], config with { Projects = projects });
    }

    private static WorkAnswer DefineGroup(WorkConfig config, string rest)
    {
        var (name, members) = NameAndList(rest);
        if (name.Length == 0)
            return Problem("Serve un nome: gruppo Cliente A = sinapsi_web, printer");
        if (config.Project(name) is not null)
            return Problem($"«{name}» è già un progetto");
        var next = config;
        var canonical = new List<string>();
        foreach (var member in members)
        {
            // Un progetto che non c'è ancora si crea: si cerca nei titoli col suo nome.
            if (next.Project(member) is null)
                next = next with { Projects = [.. next.Projects, new WorkProject(member, [member])] };
            canonical.Add(next.Project(member)!.Name);
        }
        var group = new WorkGroup(name, canonical.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        next = next with { Groups = [.. next.Groups.Where(g => !WorkConfig.Same(g.Name, name)), group] };
        return new WorkAnswer("GRUPPO", [new WorkRow(name, group.Members.Count == 0 ? "vuoto" : string.Join(", ", group.Members), true)], next);
    }

    private static WorkAnswer Add(WorkConfig config, string rest)
    {
        var parts = Preposition("a").Split(rest, 2);
        if (parts.Length != 2 || parts[0].Trim().Length == 0 || parts[1].Trim().Length == 0)
            return Problem("aggiungi sinapsi_web a Cliente A");
        var group = config.Group(parts[1].Trim());
        var project = config.Project(parts[0].Trim());
        if (group is null)
            return Problem($"Il gruppo «{parts[1].Trim()}» non c'è" + Known(config.Groups.Select(g => g.Name)));
        if (project is null)
            return Problem($"Il progetto «{parts[0].Trim()}» non c'è: prima progetto {parts[0].Trim()}");
        var members = group.Members.Contains(project.Name, StringComparer.OrdinalIgnoreCase) ? group.Members : [.. group.Members, project.Name];
        var groups = config.Groups.Select(g => g == group ? group with { Members = members } : g).ToArray();
        return new WorkAnswer("GRUPPO", [new WorkRow(group.Name, string.Join(", ", members), true)], config with { Groups = groups });
    }

    private static WorkAnswer Remove(WorkConfig config, string rest)
    {
        var from = Preposition("da").Split(rest, 2);
        if (from.Length == 2)
        {
            var group = config.Group(from[1].Trim());
            if (group is null)
                return Problem($"Il gruppo «{from[1].Trim()}» non c'è" + Known(config.Groups.Select(g => g.Name)));
            var members = group.Members.Where(m => !WorkConfig.Same(m, from[0])).ToArray();
            var groups = config.Groups.Select(g => g == group ? group with { Members = members } : g).ToArray();
            return new WorkAnswer("GRUPPO", [new WorkRow(group.Name, members.Length == 0 ? "vuoto" : string.Join(", ", members), true)], config with { Groups = groups });
        }
        var name = rest.Trim();
        if (config.Project(name) is { } project)
        {
            var groups = config.Groups.Select(g => g with { Members = g.Members.Where(m => !WorkConfig.Same(m, name)).ToArray() }).ToArray();
            return new WorkAnswer("TOLTO", [new WorkRow(project.Name, "progetto tolto (le ore già contate restano nel registro)")],
                new WorkConfig(config.Projects.Where(p => p != project).ToArray(), groups));
        }
        if (config.Group(name) is { } removed)
            return new WorkAnswer("TOLTO", [new WorkRow(removed.Name, "gruppo tolto, i suoi progetti restano", true)], config with { Groups = config.Groups.Where(g => g != removed).ToArray() });
        return Problem($"«{name}» non è né un progetto né un gruppo");
    }

    private static WorkAnswer Problem(string text) => new("ORE", [new WorkRow(string.Empty, text)]);

    private static string Known(IEnumerable<string> names)
    {
        var list = names.ToArray();
        return list.Length == 0 ? string.Empty : ": " + string.Join(", ", list);
    }

    private static (string Verb, string Tail) Split(string line)
    {
        var space = line.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? (line, string.Empty) : (line[..space], line[(space + 1)..].Trim());
    }

    /// <summary>"Nome = uno, due": il nome è prima del segno =, la lista dopo, separata da virgole.</summary>
    private static (string Name, List<string> Items) NameAndList(string text)
    {
        var equals = text.IndexOf('=', StringComparison.Ordinal);
        var name = (equals < 0 ? text : text[..equals]).Trim();
        var items = equals < 0 ? [] : text[(equals + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return (name, items);
    }

    private static Regex Preposition(string word) => new($@"\s+{word}\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    [GeneratedRegex(@"(?:^|\s)(gruppo|progetto)\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Target();
}
