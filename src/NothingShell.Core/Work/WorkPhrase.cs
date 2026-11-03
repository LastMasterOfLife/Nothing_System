using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NothingShell.Core.Work;

/// <summary>
/// Ore dette a parole (docs/19-ore-di-lavoro.md): "aggiungi 7h a plainest e 1h al gruppo wpweb che ho fatto ieri e aggiungi 4h su
/// plainest perché ho fatto questa mattina". Non passa dal modello: un modello piccolo sbaglierebbe giorni e somme, e qui si
/// scrivono dati. Si divide in frasi a ogni verbo (aggiungi, togli…) e a ogni punto; in ogni frase ogni durata ha il suo progetto
/// o gruppo, e il giorno detto in quella frase (ieri, questa mattina, 14/10, lunedì) vale per tutte le sue voci. Se anche una sola
/// voce non si capisce non si scrive niente: meglio dirlo che indovinare.
/// </summary>
public static partial class WorkPhrase
{
    private static readonly string[] Weekdays = ["domenica", "lunedì", "martedì", "mercoledì", "giovedì", "venerdì", "sabato"];

    /// <summary>Comincia con un verbo che scrive ore e contiene almeno una durata.</summary>
    public static bool IsRequest(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Start().IsMatch(text) && Duration().IsMatch(text);
    }

    /// <summary>"2h30", "2h 30m", "1,5h", "90m", "2:30" da soli; null se non è una durata.</summary>
    public static double? ParseDuration(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = Duration().Match(text.Trim());
        return match.Success && match.Length == text.Trim().Length ? Seconds(match) : null;
    }

    public static WorkAnswer Apply(string text, WorkStore store, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(store);
        var today = DateOnly.FromDateTime(now);
        var entries = new List<Entry>();
        var problems = new List<string>();
        var sign = 1;
        foreach (var clause in Clauses(text))
        {
            var removes = Remove().IsMatch(clause);
            var adds = Add().IsMatch(clause);
            if (removes && !adds)
                sign = -1;
            else if (adds)
                sign = 1;
            Read(clause, sign, store.Config, today, entries, problems);
        }
        if (problems.Count == 0 && entries.Count == 0)
            problems.Add("Non trovo quante ore (2h30, 90m, 1,5h) e a chi.");
        if (problems.Count > 0)
            return new WorkAnswer("ORE · niente registrato", [.. problems.Select(p => new WorkRow(string.Empty, p))]);

        var rows = new List<WorkRow>();
        foreach (var entry in entries)
        {
            var key = entry.IsGroup ? WorkLog.GroupKey(entry.Name) : entry.Name;
            var when = entry.Day == today ? "oggi" : entry.Day == today.AddDays(-1) ? "ieri" : entry.Day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            var label = entry.IsGroup ? $"gruppo {entry.Name}" : entry.Name;
            if (entry.Sign > 0)
            {
                store.Log.Add(entry.Day, key, entry.Seconds);
                rows.Add(new WorkRow(label, $"+{WorkReport.Duration(entry.Seconds)} {when}", entry.IsGroup));
                continue;
            }
            var taken = store.Log.Remove(entry.Day, key, entry.Seconds);
            rows.Add(new WorkRow(label, taken == 0 ? $"niente da togliere {when}" : $"−{WorkReport.Duration(taken)} {when}", entry.IsGroup));
        }
        store.Save();
        rows.AddRange(Totals(store, entries));
        return new WorkAnswer("ORE · registrate", rows, Changed: true);
    }

    /// <summary>Il totale di ogni gruppo (e dei progetti toccati) nei mesi in cui sono state messe ore.</summary>
    private static IEnumerable<WorkRow> Totals(WorkStore store, List<Entry> entries)
    {
        foreach (var (year, month) in entries.Select(e => (e.Day.Year, e.Day.Month)).Distinct().Order())
        {
            var report = store.Report(WorkPeriod.Month(year, month));
            yield return new WorkRow(string.Empty, $"Totale {report.Period.Label}");
            foreach (var group in report.Groups)
                yield return new WorkRow(group.Name, WorkReport.Duration(group.Seconds), true);
            var touched = entries.Where(e => !e.IsGroup && e.Day.Year == year && e.Day.Month == month).Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var project in report.Projects.Where(p => touched.Contains(p.Name, StringComparer.OrdinalIgnoreCase)))
                yield return new WorkRow(project.Name, WorkReport.Duration(project.Seconds));
        }
    }

    /// <summary>La risposta come testo, per l'assistente.</summary>
    public static string ToText(WorkAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var text = new StringBuilder(answer.Title);
        foreach (var row in answer.Rows)
            text.Append('\n').Append(row.Label.Length == 0 ? row.Value : $"{row.Label}: {row.Value}");
        return text.ToString();
    }

    private sealed record Entry(double Seconds, string Name, bool IsGroup, DateOnly Day, int Sign);

    private static IEnumerable<string> Clauses(string text) =>
        Boundary().Split(text).Select(c => c.Trim()).Where(c => c.Length > 0);

    private static void Read(string clause, int sign, WorkConfig config, DateOnly today, List<Entry> entries, List<string> problems)
    {
        var durations = Duration().Matches(clause);
        if (durations.Count == 0)
            return;
        var day = FindDay(clause, today, out var dayProblem);
        if (dayProblem is not null)
        {
            problems.Add(dayProblem);
            return;
        }
        if (day > today)
        {
            problems.Add("Quel giorno non è ancora arrivato.");
            return;
        }
        var cleaned = Dates().Replace(Verbs().Replace(clause, " "), " ");
        // Il testo di ogni voce: quello fra la sua durata e la successiva (o la fine); se è vuoto, quello prima ("aggiungi a plainest 7h").
        var spans = Duration().Matches(cleaned);
        for (var i = 0; i < spans.Count; i++)
        {
            var after = cleaned[(spans[i].Index + spans[i].Length)..(i + 1 < spans.Count ? spans[i + 1].Index : cleaned.Length)];
            var before = cleaned[(i == 0 ? 0 : spans[i - 1].Index + spans[i - 1].Length)..spans[i].Index];
            var target = Target(after, config) ?? (i == 0 ? Target(before, config) : null);
            var seconds = Seconds(spans[i]);
            var shown = WorkReport.Duration(seconds);
            if (seconds <= 0 || seconds > 24 * 3600)
                problems.Add($"«{spans[i].Value.Trim()}» non è una durata possibile (al massimo 24 ore per voce).");
            else if (target is null)
                problems.Add($"{shown}: a quale progetto o gruppo?" + Known(config));
            else if (target.Value.Problem is { } trouble)
                problems.Add($"{shown}: {trouble}");
            else
                entries.Add(new Entry(seconds, target.Value.Name!, target.Value.IsGroup, day, sign));
        }
    }

    private static string Known(WorkConfig config)
    {
        var names = config.Projects.Select(p => p.Name).Concat(config.Groups.Select(g => "gruppo " + g.Name)).ToArray();
        return names.Length == 0 ? " Non c'è ancora nessun progetto." : " Ci sono: " + string.Join(", ", names);
    }

    /// <param name="Problem">Cosa non va, se non si è trovato un progetto o gruppo valido.</param>
    private readonly record struct Found(string? Name, bool IsGroup, string? Problem);

    /// <summary>
    /// Il progetto o gruppo nominato nel testo di una voce, ovunque sia: fra la durata e il nome possono esserci parole qualsiasi
    /// ("passate ieri sul progetto Plainest", "passata su progetti del gruppo WpWeb sempre di"). Vince il nome noto che compare per
    /// primo (a parità il più lungo). Se prima del nome c'è "gruppo" o "progetto" deve essere del tipo giusto.
    /// </summary>
    private static Found? Target(string text, WorkConfig config)
    {
        var words = text.Trim();
        if (words.Length == 0)
            return null;
        (string Name, bool IsGroup, int At)? best = null;
        foreach (var (name, isGroup) in config.Projects.Select(p => (p.Name, false)).Concat(config.Groups.Select(g => (g.Name, true))))
        {
            for (var at = words.IndexOf(name, StringComparison.OrdinalIgnoreCase); at >= 0; at = words.IndexOf(name, at + 1, StringComparison.OrdinalIgnoreCase))
            {
                var end = at + name.Length;
                if ((at > 0 && IsWordChar(words[at - 1])) || (end < words.Length && IsWordChar(words[end])))
                    continue;
                if (best is null || at < best.Value.At || (at == best.Value.At && name.Length > best.Value.Name.Length))
                    best = (name, isGroup, at);
                break;
            }
        }
        if (best is not { } found)
        {
            var shown = words.Length > 40 ? words[..40] + "…" : words;
            return new Found(null, false, $"non trovo un progetto o gruppo in «{shown}»." + Known(config));
        }
        var kind = Kind().Match(words[..found.At]);
        if (kind.Success && kind.Groups[1].Value.StartsWith("gr", StringComparison.OrdinalIgnoreCase) != found.IsGroup)
            return new Found(null, false, found.IsGroup ? $"«{found.Name}» è un gruppo, non un progetto." : $"«{found.Name}» è un progetto, non un gruppo.");
        return new Found(found.Name, found.IsGroup, null);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';


    private static double Seconds(Match match)
    {
        if (match.Groups["h"].Success)
        {
            var hours = double.Parse(match.Groups["h"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            var minutes = match.Groups["m"].Success ? match.Groups["m"] : match.Groups["m2"];
            return hours * 3600 + (minutes.Success ? int.Parse(minutes.Value, CultureInfo.InvariantCulture) * 60 : 0);
        }
        if (match.Groups["mm"].Success)
            return int.Parse(match.Groups["mm"].Value, CultureInfo.InvariantCulture) * 60;
        return int.Parse(match.Groups["c"].Value, CultureInfo.InvariantCulture) * 3600 + int.Parse(match.Groups["cm"].Value, CultureInfo.InvariantCulture) * 60;
    }

    /// <summary>Il giorno detto nella frase (oggi se non c'è): ieri, avantieri, questa mattina, 14/10, 2026-10-14, un giorno della settimana.</summary>
    private static DateOnly FindDay(string clause, DateOnly today, out string? problem)
    {
        problem = null;
        var iso = Iso().Match(clause);
        if (iso.Success)
        {
            if (DateOnly.TryParseExact(iso.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return parsed;
            problem = $"«{iso.Value}» non è una data.";
            return today;
        }
        var local = Local().Match(clause);
        if (local.Success)
        {
            var day = int.Parse(local.Groups[1].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(local.Groups[2].Value, CultureInfo.InvariantCulture);
            var year = local.Groups[3].Success ? int.Parse(local.Groups[3].Value, CultureInfo.InvariantCulture) : today.Year;
            if (year < 100)
                year += 2000;
            if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
            {
                problem = $"«{local.Value}» non è una data.";
                return today;
            }
            var date = new DateOnly(year, month, day);
            // Senza anno è l'ultima volta che quel giorno è passato.
            return !local.Groups[3].Success && date > today ? date.AddYears(-1) : date;
        }
        if (DayBeforeYesterday().IsMatch(clause))
            return today.AddDays(-2);
        if (Yesterday().IsMatch(clause))
            return today.AddDays(-1);
        var weekday = Weekday().Match(clause);
        if (weekday.Success)
        {
            var wanted = Array.FindIndex(Weekdays, d => string.Equals(d.Replace('ì', 'i'), weekday.Value.ToLowerInvariant().Replace('ì', 'i'), StringComparison.Ordinal));
            var back = ((int)today.DayOfWeek - wanted + 7) % 7;
            return today.AddDays(-(back == 0 ? 7 : back));
        }
        return today; // oggi, stamattina, questa sera… e quando non si dice niente
    }

    private const string Verb = @"aggiungi(?:mi)?|aggiungere|metti(?:mi)?|mettere|segna(?:mi)?|segnare|registra(?:mi)?|registrare|inserisci|inserire|togli(?:mi)?|togliere|rimuovi|rimuovere|sottrai|sottrarre|scala";

    [GeneratedRegex($@"^\s*(?:(?:per favore|per piacere|ti prego|puoi|potresti|vorrei|devi|dovresti)\s+)*(?:(?:{Verb})|(?:ho|abbiamo)\s+(?:lavorato|fatto))(?![\p{{L}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Start();

    [GeneratedRegex($@"(?<![\p{{L}}])(?:togli(?:mi)?|togliere|rimuovi|rimuovere|sottrai|sottrarre|scala)(?![\p{{L}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Remove();

    [GeneratedRegex($@"(?<![\p{{L}}])(?:aggiungi(?:mi)?|aggiungere|metti(?:mi)?|mettere|segna(?:mi)?|segnare|registra(?:mi)?|registrare|inserisci|inserire)(?![\p{{L}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Add();

    [GeneratedRegex($@"(?<![\p{{L}}])(?:{Verb})(?![\p{{L}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Verbs();

    /// <summary>Dove comincia una nuova frase: un verbo, un punto, un punto e virgola, un a capo.</summary>
    [GeneratedRegex($@"(?<![\p{{L}}])(?=(?:{Verb})(?![\p{{L}}]))|(?:\.(?!\d)|[;\n])+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Boundary();

    // 7h · 7 ore · 2h30 · 2h 30m · 2 ore e 30 minuti · 1,5h · 90m · 2:30
    [GeneratedRegex(@"(?<![\p{L}\d])(?:(?<h>\d+(?:[.,]\d+)?)\s*(?:ore|ora|h)(?![\p{L}])(?:\s*(?:e\s*)?(?<m>\d{1,2})\s*(?:minuti|min|m)(?![\p{L}])|(?<=h)(?<m2>\d{1,2})(?![\d:]|\s*(?:ore|ora|h)(?![\p{L}])))?|(?<mm>\d+)\s*(?:minuti|min|m)(?![\p{L}])|(?<c>\d{1,2}):(?<cm>\d{2})(?!\d))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Duration();

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Iso();

    [GeneratedRegex(@"(?<!\d)(\d{1,2})/(\d{1,2})(?:/(\d{2,4}))?(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Local();

    [GeneratedRegex(@"(?<![\p{L}])(?:l['’]\s*altro\s+ieri|avantieri)(?![\p{L}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DayBeforeYesterday();

    [GeneratedRegex(@"(?<![\p{L}])ieri(?![\p{L}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Yesterday();

    [GeneratedRegex(@"(?<![\p{L}])(?:domenica|luned[iì]|marted[iì]|mercoled[iì]|gioved[iì]|venerd[iì]|sabato)(?![\p{L}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Weekday();

    /// <summary>Le parole di giorno e di data, tolte prima di cercare il nome: non ne fanno parte.</summary>
    [GeneratedRegex(@"(?<![\p{L}])(?:l['’]\s*altro\s+ieri|avantieri|ieri(?:\s+(?:sera|mattina|pomeriggio))?|oggi(?:\s+pomeriggio)?|stamattina|stamani|stamane|stasera|stanotte|(?:questa|quest['’]?\s*a)\s*(?:mattina|mattinata|sera|notte|pomeriggio)|(?:questo|quest['’]?\s*o)\s*pomeriggio|domenica|luned[iì]|marted[iì]|mercoled[iì]|gioved[iì]|venerd[iì]|sabato)(?![\p{L}])|\b\d{4}-\d{2}-\d{2}\b|(?<!\d)\d{1,2}/\d{1,2}(?:/\d{2,4})?(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Dates();


    /// <summary>La parola "gruppo" o "progetto" subito prima di un nome (con al massimo due parole in mezzo: "gruppo di", "progetti del gruppo").</summary>
    [GeneratedRegex(@"(gruppo|gruppi|progetto|progetti)(?:\s+\p{L}{1,4}){0,2}\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Kind();
}
