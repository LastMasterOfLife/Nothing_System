using System.Globalization;
using System.Text;
using System.Text.Json;
using NothingShell.Core.Storage;

namespace NothingShell.Core.Work;

/// <summary>Un progetto: la finestra in primo piano il cui titolo contiene una delle parole chiave conta come lavoro su di lui.</summary>
public sealed record WorkProject(string Name, IReadOnlyList<string> Keywords);

/// <summary>Un gruppo di progetti: le sue ore sono la somma di quelle dei membri (un progetto può stare in più gruppi).</summary>
public sealed record WorkGroup(string Name, IReadOnlyList<string> Members);

public sealed record WorkConfig(IReadOnlyList<WorkProject> Projects, IReadOnlyList<WorkGroup> Groups)
{
    public static WorkConfig Empty { get; } = new([], []);

    public WorkProject? Project(string name) => Projects.FirstOrDefault(p => Same(p.Name, name));

    public WorkGroup? Group(string name) => Groups.FirstOrDefault(g => Same(g.Name, name));

    public static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Quale progetto è la finestra in primo piano (docs/19-ore-di-lavoro.md).</summary>
public static class WorkMatcher
{
    /// <summary>Vince la parola chiave più lunga, così "sinapsi_web" batte "sinapsi"; null se nessuna corrisponde.</summary>
    public static WorkProject? Match(WorkConfig config, string? windowTitle)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(windowTitle))
            return null;
        WorkProject? best = null;
        var bestLength = 0;
        foreach (var project in config.Projects)
        {
            foreach (var keyword in project.Keywords)
            {
                if (keyword.Length > bestLength && windowTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    best = project;
                    bestLength = keyword.Length;
                }
            }
        }
        return best;
    }
}

/// <summary>I secondi lavorati, per giorno e per progetto. Del titolo delle finestre non resta nulla: solo il nome del progetto.</summary>
internal static class WorkJson
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}

public sealed class WorkLog
{
    private readonly Dictionary<DateOnly, Dictionary<string, double>> _days = [];

    public bool Dirty { get; private set; }

    /// <summary>Il nome sotto cui si scrivono le ore messe direttamente su un gruppo, senza passare da un suo progetto.</summary>
    public static string GroupKey(string group) => "gruppo:" + group.Trim();

    public void Add(DateOnly day, string project, double seconds)
    {
        if (seconds <= 0)
            return;
        if (!_days.TryGetValue(day, out var projects))
            _days[day] = projects = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        projects[project] = projects.GetValueOrDefault(project) + seconds;
        Dirty = true;
    }

    /// <summary>Toglie fino a <paramref name="seconds"/> da quel giorno, mai sotto zero; restituisce quanto ha tolto davvero.</summary>
    public double Remove(DateOnly day, string project, double seconds)
    {
        if (!_days.TryGetValue(day, out var projects) || !projects.TryGetValue(project, out var have) || seconds <= 0)
            return 0;
        var taken = Math.Min(have, seconds);
        projects[project] = have - taken;
        Dirty = taken > 0 || Dirty;
        return taken;
    }

    /// <summary>Cancella tutte le ore di un progetto (o del gruppo, con <see cref="GroupKey"/>), di ogni giorno; restituisce i secondi tolti.</summary>
    public double Clear(string project)
    {
        var removed = 0.0;
        foreach (var day in _days.Keys.ToArray())
        {
            if (_days[day].Remove(project, out var seconds))
                removed += seconds;
            if (_days[day].Count == 0)
                _days.Remove(day);
        }
        Dirty = removed > 0 || Dirty;
        return removed;
    }

    /// <summary>Le ore di un giorno per progetto (e per gruppo, con <see cref="GroupKey"/>); vuoto se quel giorno non c'è niente.</summary>
    public IReadOnlyDictionary<string, double> Day(DateOnly day) =>
        _days.TryGetValue(day, out var projects) ? new Dictionary<string, double>(projects, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, double>();

    /// <summary>I secondi di un progetto fra due giorni, estremi compresi.</summary>
    public double Seconds(string project, DateOnly from, DateOnly to) =>
        _days.Where(d => d.Key >= from && d.Key <= to).Sum(d => d.Value.GetValueOrDefault(project));

    public IReadOnlyList<DateOnly> Days => [.. _days.Keys.Order()];

    public string ToJson()
    {
        var plain = _days.OrderBy(d => d.Key).ToDictionary(d => d.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d => d.Value);
        return JsonSerializer.Serialize(plain, WorkJson.Indented);
    }

    public void MarkSaved() => Dirty = false;

    public static WorkLog FromJson(string? json)
    {
        var log = new WorkLog();
        if (string.IsNullOrWhiteSpace(json))
            return log;
        try
        {
            var plain = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(json);
            foreach (var (day, projects) in plain ?? [])
            {
                if (!DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    continue;
                foreach (var (project, seconds) in projects)
                    log.Add(date, project, seconds);
            }
        }
        catch (JsonException)
        {
            // file rovinato a mano: si riparte da vuoto, il vecchio resta finché non si salva
        }
        log.MarkSaved();
        return log;
    }
}

/// <summary>
/// Conta il tempo fra un campione e l'altro e lo dà al progetto in primo piano, solo se l'utente è davvero al PC. Un buco
/// più lungo di <see cref="MaxGap"/> (PC in sospensione, shell ferma) non si conta: meglio perdere un minuto che inventarlo.
/// </summary>
public sealed class WorkTracker
{
    public static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(30);

    /// <summary>Senza toccare mouse e tastiera per tanto, non si sta lavorando.</summary>
    public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(5);

    private readonly WorkLog _log;
    private DateTime? _last;

    public WorkTracker(WorkLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Il progetto che sta contando adesso; null se in pausa.</summary>
    public string? Current { get; private set; }

    /// <summary>Quando è stato preso l'ultimo campione: il contatore dal vivo aggiunge il tempo da allora.</summary>
    public DateTime? LastSample => _last;

    /// <summary>Il tempo, dal campione precedente a questo, va a <paramref name="project"/> (il campione precedente non conta più: serve un tempo continuo).</summary>
    public void Sample(DateTime now, string? project, TimeSpan idle)
    {
        if (_last is { } last && Current is { } before && now > last && now - last <= MaxGap)
        {
            // Il tempo trascorso è di chi aveva la finestra all'inizio, e a mezzanotte si divide fra i due giorni.
            AddSplit(last, now, before);
        }
        _last = now;
        Current = idle >= IdleLimit ? null : project;
    }

    private void AddSplit(DateTime from, DateTime to, string project)
    {
        var midnight = to.Date;
        if (from.Date < midnight)
        {
            _log.Add(DateOnly.FromDateTime(from), project, (midnight - from).TotalSeconds);
            _log.Add(DateOnly.FromDateTime(to), project, (to - midnight).TotalSeconds);
        }
        else
        {
            _log.Add(DateOnly.FromDateTime(to), project, (to - from).TotalSeconds);
        }
    }
}

/// <summary>Un periodo di giorni: quello che si chiede come "ottobre", "oggi", "questa settimana".</summary>
public readonly record struct WorkPeriod(DateOnly From, DateOnly To, string Label)
{
    private static readonly string[] Months = ["gennaio", "febbraio", "marzo", "aprile", "maggio", "giugno", "luglio", "agosto", "settembre", "ottobre", "novembre", "dicembre"];

    public static string MonthName(int month) => Months[month - 1];

    public static WorkPeriod Month(int year, int month) =>
        new(new DateOnly(year, month, 1), new DateOnly(year, month, DateTime.DaysInMonth(year, month)), $"{Months[month - 1]} {year}");

    /// <summary>
    /// "oggi", "ieri", "settimana", "mese", "anno", il nome di un mese (con l'anno se non è l'ultimo passato o in corso) o
    /// "2026-10"; null se non è un periodo.
    /// </summary>
    public static WorkPeriod? Parse(string text, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(text);
        var word = text.Trim().ToLowerInvariant();
        var parts = word.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (word)
        {
            case "oggi":
                return new(today, today, "oggi");
            case "ieri":
                return new(today.AddDays(-1), today.AddDays(-1), "ieri");
            case "settimana" or "questa settimana":
                var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
                return new(monday, today, "questa settimana");
            case "mese" or "questo mese":
                return Month(today.Year, today.Month);
            case "mese scorso" or "scorso mese":
                var last = today.AddMonths(-1);
                return Month(last.Year, last.Month);
            case "anno" or "quest'anno" or "questo anno":
                return new(new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31), today.Year.ToString(CultureInfo.InvariantCulture));
        }
        if (DateOnly.TryParseExact(word + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
            return Month(iso.Year, iso.Month);
        var index = Array.FindIndex(Months, m => m == parts[0]);
        if (index < 0)
            return null;
        var month = index + 1;
        if (parts.Length > 1)
            return int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= 2000 and <= 2100 ? Month(year, month) : null;
        // Senza anno: il mese più recente che non è nel futuro ("dicembre" a novembre è quello dell'anno scorso).
        return Month(month <= today.Month ? today.Year : today.Year - 1, month);
    }
}

/// <param name="Direct">Per un gruppo: le ore scritte direttamente sul gruppo, già comprese in <paramref name="Seconds"/>.</param>
public sealed record WorkLine(string Name, double Seconds, double Direct = 0);

/// <summary>Ore per progetto e per gruppo in un periodo.</summary>
public sealed record WorkReport(WorkPeriod Period, IReadOnlyList<WorkLine> Projects, IReadOnlyList<WorkLine> Groups)
{
    public double TotalSeconds => Projects.Sum(p => p.Seconds) + Groups.Sum(g => g.Direct);

    public static WorkReport Build(WorkConfig config, WorkLog log, WorkPeriod period)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(log);
        var projects = config.Projects.Select(p => new WorkLine(p.Name, log.Seconds(p.Name, period.From, period.To))).ToArray();
        var groups = config.Groups.Select(g => GroupLine(g, log, period)).ToArray();
        return new WorkReport(period, projects, groups);
    }

    private static WorkLine GroupLine(WorkGroup group, WorkLog log, WorkPeriod period)
    {
        var direct = log.Seconds(WorkLog.GroupKey(group.Name), period.From, period.To);
        return new WorkLine(group.Name, group.Members.Distinct(StringComparer.OrdinalIgnoreCase).Sum(m => log.Seconds(m, period.From, period.To)) + direct, direct);
    }

    /// <summary>"2 h 05 min": così il modello e chi legge non devono convertire i decimali.</summary>
    public static string Duration(double seconds)
    {
        var minutes = (long)Math.Round(Math.Max(0, seconds) / 60);
        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60:00} min";
    }

    /// <summary>Con le ore decimali in coda, per i conti: "2 h 30 min (2,50 ore)".</summary>
    public static string DurationWithDecimals(double seconds) =>
        $"{Duration(seconds)} ({(seconds / 3600).ToString("0.00", CultureInfo.GetCultureInfo("it-IT"))} ore)";

    /// <summary>
    /// Il testo che l'assistente legge con Get-Content: già sommato, perché un modello piccolo sbaglia le somme. Ogni gruppo
    /// elenca i suoi progetti, così una domanda su un gruppo trova la risposta in una riga.
    /// </summary>
    public string ToText(WorkConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var text = new StringBuilder();
        text.Append("Ore lavorate ").Append(Period.Label).Append(" (tempo reale davanti al PC, senza pause)\n");
        text.Append("TOTALE: ").Append(DurationWithDecimals(TotalSeconds)).Append('\n');
        foreach (var group in Groups)
        {
            var members = config.Group(group.Name)?.Members ?? [];
            var detail = string.Join("; ", members.Select(m => $"{m} {Duration(Projects.FirstOrDefault(p => WorkConfig.Same(p.Name, m))?.Seconds ?? 0)}"));
            if (group.Direct > 0)
                detail += (detail.Length > 0 ? "; " : string.Empty) + $"ore messe direttamente sul gruppo {Duration(group.Direct)}";
            text.Append("GRUPPO ").Append(group.Name).Append(": ").Append(DurationWithDecimals(group.Seconds));
            text.Append(detail.Length > 0 ? $" (progetti: {detail})\n" : "\n");
        }
        foreach (var project in Projects)
            text.Append("PROGETTO ").Append(project.Name).Append(": ").Append(DurationWithDecimals(project.Seconds)).Append('\n');
        return text.ToString();
    }
}

/// <summary>Progetti, gruppi e ore su disco (<c>~/.nothing-shell/work</c>) e i file di testo che legge l'assistente.</summary>
public sealed class WorkStore
{
    public static readonly string DefaultFolder = Path.Combine(AtomicFile.DataFolder, "work");

    private readonly string _folder;

    public WorkStore(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        _folder = folder;
        Log = WorkLog.FromJson(AtomicFile.TryReadAllText(Path.Combine(folder, "time.json")));
        Config = LoadConfig(AtomicFile.TryReadAllText(Path.Combine(folder, "projects.json")));
        Tracker = new WorkTracker(Log);
    }

    public WorkConfig Config { get; private set; }

    public WorkLog Log { get; }

    public WorkTracker Tracker { get; }

    /// <summary>Il file di testo del mese, per l'assistente.</summary>
    public string ReportPath(int year, int month) => Path.Combine(_folder, $"ore-{year:0000}-{month:00}.txt");

    /// <summary>Un campione di tempo: il progetto dalla finestra in primo piano, con la sua parte di ore già conteggiate.</summary>
    public void Sample(DateTime now, string? windowTitle, TimeSpan idle) =>
        Tracker.Sample(now, WorkMatcher.Match(Config, windowTitle)?.Name, idle);

    public WorkReport Report(WorkPeriod period) => WorkReport.Build(Config, Log, period);

    /// <summary>Azzera le ore di progetti e gruppi (per nome, quelle messe direttamente sul gruppo comprese); restituisce i secondi cancellati.</summary>
    public double Reset(IEnumerable<string> projects, IEnumerable<string> groups)
    {
        var removed = projects.Sum(Log.Clear) + groups.Sum(g => Log.Clear(WorkLog.GroupKey(g)));
        Save();
        return removed;
    }

    public void SetConfig(WorkConfig config)
    {
        Config = config;
        AtomicFile.WriteAllText(Path.Combine(_folder, "projects.json"), JsonSerializer.Serialize(config, WorkJson.Indented));
        WriteReports();
    }

    /// <summary>Salva le ore, se sono cambiate, e riscrive i testi dei mesi.</summary>
    public void Save()
    {
        if (!Log.Dirty)
            return;
        AtomicFile.WriteAllText(Path.Combine(_folder, "time.json"), Log.ToJson());
        Log.MarkSaved();
        WriteReports();
    }

    /// <summary>Un file per ogni mese con ore, più quello del mese in corso anche se vuoto: così l'assistente non trova "file mancante".</summary>
    public void WriteReports()
    {
        // Anche i mesi che hanno già un file: dopo un azzeramento il file vecchio non deve restare con ore che non ci sono più.
        var written = Directory.Exists(_folder)
            ? Directory.GetFiles(_folder, "ore-????-??.txt").Select(p => Path.GetFileNameWithoutExtension(p)[4..].Split('-')).Where(p => p.Length == 2 && int.TryParse(p[0], out _) && int.TryParse(p[1], out _)).Select(p => (int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture)))
            : [];
        var months = Log.Days.Select(d => (d.Year, d.Month)).Concat(written).Append((DateTime.Now.Year, DateTime.Now.Month)).Where(m => m.Item2 is >= 1 and <= 12).Distinct();
        foreach (var (year, month) in months)
            AtomicFile.WriteAllText(ReportPath(year, month), Report(WorkPeriod.Month(year, month)).ToText(Config));
    }

    private static WorkConfig LoadConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return WorkConfig.Empty;
        try
        {
            var config = JsonSerializer.Deserialize<WorkConfig>(json);
            return config is null ? WorkConfig.Empty : new WorkConfig(config.Projects ?? [], config.Groups ?? []);
        }
        catch (JsonException)
        {
            return WorkConfig.Empty;
        }
    }
}
