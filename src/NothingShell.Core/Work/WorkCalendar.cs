namespace NothingShell.Core.Work;

/// <summary>Le ore di un mese, giorno per giorno: quello che il calendario colora.</summary>
public sealed record WorkMonth(int Year, int Month, IReadOnlyDictionary<int, double> Days, double Total)
{
    public double Max => Days.Count == 0 ? 0 : Days.Values.Max();
}

/// <summary>Il dettaglio di un giorno: il totale e una riga per gruppo e progetto che hanno ore.</summary>
public sealed record WorkDayDetail(DateOnly Day, double Total, IReadOnlyList<WorkRow> Rows);

/// <summary>
/// Calendario e dettaglio delle ore (docs/19-ore-di-lavoro.md), per tutto o per un solo gruppo: nelle impostazioni e nella finestra
/// aperta cliccando un gruppo nel notch si vede la stessa cosa, con <paramref name="group"/> diverso.
/// </summary>
public static class WorkCalendar
{
    public static WorkMonth Month(WorkStore store, string? group, int year, int month)
    {
        ArgumentNullException.ThrowIfNull(store);
        var keys = Keys(store.Config, group);
        var days = new Dictionary<int, double>();
        for (var day = 1; day <= DateTime.DaysInMonth(year, month); day++)
        {
            var seconds = Sum(store.Log.Day(new DateOnly(year, month, day)), keys);
            if (seconds > 0)
                days[day] = seconds;
        }
        return new WorkMonth(year, month, days, days.Values.Sum());
    }

    public static WorkDayDetail Day(WorkStore store, string? group, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(store);
        var config = store.Config;
        var logged = store.Log.Day(day);
        var rows = new List<WorkRow>();
        double Of(string key) => logged.GetValueOrDefault(key);
        if (group is null)
        {
            foreach (var g in config.Groups)
            {
                var seconds = Of(WorkLog.GroupKey(g.Name)) + g.Members.Distinct(StringComparer.OrdinalIgnoreCase).Sum(Of);
                if (seconds > 0)
                    rows.Add(new WorkRow(g.Name, WorkReport.Duration(seconds), true));
            }
            rows.AddRange(config.Projects.Where(p => Of(p.Name) > 0).Select(p => new WorkRow(p.Name, WorkReport.Duration(Of(p.Name)))));
        }
        else if (config.Group(group) is { } known)
        {
            rows.AddRange(known.Members.Distinct(StringComparer.OrdinalIgnoreCase).Where(m => Of(m) > 0).Select(m => new WorkRow(m, WorkReport.Duration(Of(m)))));
            if (Of(WorkLog.GroupKey(known.Name)) is > 0 and var direct)
                rows.Add(new WorkRow("(sul gruppo)", WorkReport.Duration(direct)));
        }
        return new WorkDayDetail(day, Sum(logged, Keys(config, group)), rows);
    }

    /// <summary>Le voci del registro che contano: tutte quelle dei progetti e gruppi che ci sono, o quelle di un gruppo.</summary>
    private static HashSet<string> Keys(WorkConfig config, string? group)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (group is null)
        {
            keys.UnionWith(config.Projects.Select(p => p.Name));
            keys.UnionWith(config.Groups.Select(g => WorkLog.GroupKey(g.Name)));
        }
        else if (config.Group(group) is { } known)
        {
            keys.UnionWith(known.Members);
            keys.Add(WorkLog.GroupKey(known.Name));
        }
        return keys;
    }

    private static double Sum(IReadOnlyDictionary<string, double> logged, HashSet<string> keys) =>
        logged.Where(l => keys.Contains(l.Key)).Sum(l => l.Value);
}
