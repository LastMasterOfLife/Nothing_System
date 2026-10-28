using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NothingShell.Core.Notch;

/// <summary>Un appuntamento (un'occorrenza, già espansa se ricorrente); gli eventi di tutto il giorno iniziano a mezzanotte.</summary>
public sealed record CalendarEvent(
    string Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool AllDay = false,
    string? Location = null,
    string? MeetingLink = null)
{
    /// <summary>Stesso appuntamento visto da due calendari (Windows e link iCal): stesso titolo e stesso inizio.</summary>
    public string Key => $"{Title.Trim().ToUpperInvariant()}|{Start.UtcDateTime:O}";
}

/// <summary>Link per entrare in una riunione online: Teams, Zoom, Meet, Webex, GoToMeeting, Whereby, Jitsi.</summary>
public static partial class MeetingLinks
{
    public static string? Find(params string?[] texts)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text))
                continue;
            if (MeetingUrl().Match(text) is { Success: true } match)
                return match.Value.TrimEnd('.', ',', ';', ')', '>', '"', '\'');
        }
        return null;
    }

    /// <summary>"TEAMS", "ZOOM", "MEET"… per il pulsante; null se il link non è riconosciuto.</summary>
    public static string? Service(string? link) => link switch
    {
        null => null,
        _ when link.Contains("teams.microsoft", StringComparison.OrdinalIgnoreCase)
               || link.Contains("teams.live", StringComparison.OrdinalIgnoreCase) => "TEAMS",
        _ when link.Contains("zoom.", StringComparison.OrdinalIgnoreCase) => "ZOOM",
        _ when link.Contains("meet.google", StringComparison.OrdinalIgnoreCase) => "MEET",
        _ when link.Contains("webex", StringComparison.OrdinalIgnoreCase) => "WEBEX",
        _ => null,
    };

    [GeneratedRegex(@"https://(?:[\w-]+\.)*(?:teams\.microsoft\.com|teams\.live\.com|zoom\.us|zoom\.com|meet\.google\.com|webex\.com|gotomeeting\.com|meet\.goto\.com|whereby\.com|meet\.jit\.si)/[^\s<>""']*",
        RegexOptions.IgnoreCase)]
    private static partial Regex MeetingUrl();
}

/// <summary>
/// Lettura dei calendari iCalendar (RFC 5545), quelli dei link "iCal"/".ics" di Outlook, Google e iCloud
/// (docs/11-agenda-pannello-rapido.md). Si espandono solo le occorrenze nella finestra richiesta.
/// Ricorrenze: DAILY, WEEKLY (con BYDAY), MONTHLY (BYMONTHDAY o BYDAY come 2TU e -1FR), YEARLY, con
/// INTERVAL, COUNT, UNTIL, EXDATE e le occorrenze spostate (RECURRENCE-ID). Gli eventi annullati non contano.
/// </summary>
public static class IcsCalendar
{
    private const int MaxIterations = 200_000; // una riunione quotidiana da anni resta nel conto

    public static IReadOnlyList<CalendarEvent> Parse(string ics, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo? local = null)
    {
        ArgumentNullException.ThrowIfNull(ics);
        local ??= TimeZoneInfo.Local;
        var events = ReadEvents(ics);
        var overrides = events.Where(e => e.RecurrenceId is not null).ToArray();
        var result = new List<CalendarEvent>();

        foreach (var item in events.Where(e => e.RecurrenceId is null && e.Start is not null))
        {
            var moved = overrides
                .Where(o => o.Uid == item.Uid)
                .Select(o => o.RecurrenceId!.Value.Instant(local))
                .ToHashSet();
            if (item.Cancelled)
                continue;
            foreach (var start in Occurrences(item, to, local))
            {
                var startInstant = start.Instant(local);
                if (moved.Contains(startInstant) || item.Excluded.Any(x => x.Matches(start, local)))
                    continue;
                if (Build(item, start, local) is { } occurrence && Overlaps(occurrence, from, to))
                    result.Add(occurrence);
            }
        }

        foreach (var item in overrides.Where(o => !o.Cancelled && o.Start is not null))
            if (Build(item, item.Start!.Value, local) is { } occurrence && Overlaps(occurrence, from, to))
                result.Add(occurrence);

        return result.OrderBy(e => e.Start).ThenBy(e => e.Title, StringComparer.CurrentCulture).ToArray();
    }

    private static bool Overlaps(CalendarEvent e, DateTimeOffset from, DateTimeOffset to) =>
        e.Start < to && (e.End > from || (e.End == e.Start && e.Start >= from));

    private static CalendarEvent? Build(VEvent item, IcsTime start, TimeZoneInfo local)
    {
        var startInstant = start.Instant(local);
        DateTimeOffset end;
        if (item.Duration is { } duration)
            end = start.AllDay ? start.AddDays(duration.Days).Instant(local) : startInstant + duration;
        else if (item.End is { } dtEnd && item.Start is { } dtStart)
            end = start.AllDay
                ? start.AddDays((dtEnd.Local.Date - dtStart.Local.Date).Days).Instant(local)
                : startInstant + (dtEnd.Instant(local) - dtStart.Instant(local));
        else
            end = start.AllDay ? start.AddDays(1).Instant(local) : startInstant;
        if (end < startInstant)
            end = startInstant;
        var title = string.IsNullOrWhiteSpace(item.Summary) ? "(senza titolo)" : item.Summary.Trim();
        var link = MeetingLinks.Find(item.ConferenceUrl, item.Location, item.Url, item.Description);
        return new CalendarEvent(title, startInstant, end, start.AllDay,
            string.IsNullOrWhiteSpace(item.Location) ? null : item.Location.Trim(), link);
    }

    /// <summary>Inizi delle occorrenze (ora del fuso dell'evento) fino a <paramref name="to"/>.</summary>
    private static IEnumerable<IcsTime> Occurrences(VEvent item, DateTimeOffset to, TimeZoneInfo local)
    {
        var start = item.Start!.Value;
        if (item.Rule is not { } rule)
        {
            yield return start;
            yield break;
        }

        var count = 0;
        var iterations = 0;
        foreach (var candidate in Candidates(start, rule))
        {
            if (++iterations > MaxIterations)
                yield break;
            if (candidate.Local < start.Local)
                continue;
            if (rule.Until is { } until && candidate.Instant(local) > until.Instant(local))
                yield break;
            if (rule.Count is { } max && count >= max)
                yield break;
            if (candidate.Instant(local) >= to)
                yield break;
            count++;
            yield return candidate;
        }
    }

    private static IEnumerable<IcsTime> Candidates(IcsTime start, RRule rule)
    {
        var time = start.Local.TimeOfDay;
        var first = start.Local.Date;
        switch (rule.Frequency)
        {
            case "DAILY":
                for (var day = first; ; day = day.AddDays(rule.Interval))
                    yield return start.With(day + time);
            case "WEEKLY":
                var days = rule.ByDay.Count > 0 ? rule.ByDay.Select(d => d.Day).Distinct().ToArray() : [first.DayOfWeek];
                var weekStart = first.AddDays(-(((int)first.DayOfWeek + 6) % 7)); // lunedì
                for (var week = weekStart; ; week = week.AddDays(7 * rule.Interval))
                    foreach (var day in days.Select(d => week.AddDays(((int)d + 6) % 7)).Order())
                        yield return start.With(day + time);
            case "MONTHLY":
                for (var month = new DateTime(first.Year, first.Month, 1); ; month = month.AddMonths(rule.Interval))
                    foreach (var day in MonthDays(month, rule, first.Day))
                        yield return start.With(day + time);
            case "YEARLY":
                for (var year = first.Year; year <= 9000; year += rule.Interval)
                    if (first.Day <= DateTime.DaysInMonth(year, first.Month))
                        yield return start.With(new DateTime(year, first.Month, first.Day) + time);
                yield break;
            default:
                yield return start; // frequenza non gestita (orari, minuti): almeno la prima occorrenza
                yield break;
        }
    }

    private static IEnumerable<DateTime> MonthDays(DateTime month, RRule rule, int defaultDay)
    {
        var length = DateTime.DaysInMonth(month.Year, month.Month);
        var days = new SortedSet<int>();
        foreach (var d in rule.ByMonthDay)
        {
            var day = d > 0 ? d : length + d + 1;
            if (day >= 1 && day <= length)
                days.Add(day);
        }
        foreach (var (ordinal, weekday) in rule.ByDay)
        {
            var matches = Enumerable.Range(1, length).Where(d => month.AddDays(d - 1).DayOfWeek == weekday).ToArray();
            if (ordinal == 0)
                days.UnionWith(matches);
            else if (ordinal > 0 && ordinal <= matches.Length)
                days.Add(matches[ordinal - 1]);
            else if (ordinal < 0 && -ordinal <= matches.Length)
                days.Add(matches[matches.Length + ordinal]);
        }
        if (rule.ByMonthDay.Count == 0 && rule.ByDay.Count == 0 && defaultDay <= length)
            days.Add(defaultDay);
        return days.Select(d => month.AddDays(d - 1));
    }

    private static List<VEvent> ReadEvents(string ics)
    {
        var events = new List<VEvent>();
        VEvent? current = null;
        var depth = 0; // VALARM dentro un VEVENT: le sue proprietà non sono dell'evento
        foreach (var line in Unfold(ics))
        {
            var (name, parameters, value) = Split(line);
            switch (name)
            {
                case "BEGIN" when value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase):
                    current = new VEvent();
                    depth = 0;
                    continue;
                case "END" when value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase):
                    if (current is not null)
                        events.Add(current);
                    current = null;
                    continue;
                case "BEGIN" when current is not null:
                    depth++;
                    continue;
                case "END" when current is not null:
                    depth--;
                    continue;
            }
            if (current is null || depth > 0)
                continue;
            switch (name)
            {
                case "UID": current.Uid = value; break;
                case "SUMMARY": current.Summary = Unescape(value); break;
                case "LOCATION": current.Location = Unescape(value); break;
                case "DESCRIPTION": current.Description = Unescape(value); break;
                case "URL": current.Url = value; break;
                case "X-MICROSOFT-SKYPETEAMSMEETINGURL" or "X-GOOGLE-CONFERENCE" or "CONFERENCE": current.ConferenceUrl = value; break;
                case "STATUS": current.Cancelled = value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase); break;
                case "DTSTART": current.Start = IcsTime.Parse(value, parameters); break;
                case "DTEND": current.End = IcsTime.Parse(value, parameters); break;
                case "DURATION": current.Duration = ParseDuration(value); break;
                case "RRULE": current.Rule = RRule.Parse(value); break;
                case "RECURRENCE-ID": current.RecurrenceId = IcsTime.Parse(value, parameters); break;
                case "EXDATE":
                    foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (IcsTime.Parse(part, parameters) is { } excluded)
                            current.Excluded.Add(excluded);
                    break;
            }
        }
        return events;
    }

    private static IEnumerable<string> Unfold(string ics)
    {
        var builder = new StringBuilder();
        foreach (var raw in ics.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t'))
            {
                builder.Append(raw, 1, raw.Length - 1);
                continue;
            }
            if (builder.Length > 0)
                yield return builder.ToString();
            builder.Clear().Append(raw.TrimEnd('\r'));
        }
        if (builder.Length > 0)
            yield return builder.ToString();
    }

    /// <summary><c>NOME;PARAM=VALORE;PARAM2="a:b":valore</c>: i due punti fra virgolette non separano.</summary>
    private static (string Name, Dictionary<string, string> Parameters, string Value) Split(string line)
    {
        var quoted = false;
        var colon = -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
                quoted = !quoted;
            else if (line[i] == ':' && !quoted)
            {
                colon = i;
                break;
            }
        }
        if (colon < 0)
            return (line.ToUpperInvariant(), [], string.Empty);
        var head = line[..colon].Split(';');
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in head.Skip(1))
        {
            var eq = p.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
                parameters[p[..eq]] = p[(eq + 1)..].Trim('"');
        }
        return (head[0].ToUpperInvariant(), parameters, line[(colon + 1)..]);
    }

    private static string Unescape(string value) => value
        .Replace(@"\\", "\u0001", StringComparison.Ordinal)
        .Replace(@"\n", "\n", StringComparison.OrdinalIgnoreCase)
        .Replace(@"\,", ",", StringComparison.Ordinal)
        .Replace(@"\;", ";", StringComparison.Ordinal)
        .Replace("\u0001", @"\", StringComparison.Ordinal);

    /// <summary>Durate ISO 8601 del calendario: P1D, PT1H30M, P1W, -PT15M.</summary>
    public static TimeSpan? ParseDuration(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var match = Regex.Match(value.Trim(), @"^([+-])?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$",
            RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;
        int Group(int i) => match.Groups[i].Success ? int.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
        var span = new TimeSpan((Group(2) * 7) + Group(3), Group(4), Group(5), Group(6));
        return match.Groups[1].Value == "-" ? -span : span;
    }

    private sealed class VEvent
    {
        public string Uid { get; set; } = string.Empty;
        public string? Summary { get; set; }
        public string? Location { get; set; }
        public string? Description { get; set; }
        public string? Url { get; set; }
        public string? ConferenceUrl { get; set; }
        public bool Cancelled { get; set; }
        public IcsTime? Start { get; set; }
        public IcsTime? End { get; set; }
        public TimeSpan? Duration { get; set; }
        public RRule? Rule { get; set; }
        public IcsTime? RecurrenceId { get; set; }
        public List<IcsTime> Excluded { get; } = [];
    }

    /// <summary>Un istante del calendario: data (tutto il giorno), ora UTC, ora di un fuso (TZID) o ora "fluttuante".</summary>
    private readonly record struct IcsTime(DateTime Local, bool AllDay, bool Utc, TimeZoneInfo? Zone)
    {
        public static IcsTime? Parse(string value, Dictionary<string, string> parameters)
        {
            value = value.Trim();
            var allDay = value.Length == 8 || (parameters.TryGetValue("VALUE", out var kind) && kind.Equals("DATE", StringComparison.OrdinalIgnoreCase));
            if (allDay)
                return DateTime.TryParseExact(value[..Math.Min(8, value.Length)], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? new IcsTime(date, true, false, null)
                    : null;
            var utc = value.EndsWith('Z');
            if (!DateTime.TryParseExact(value.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                return null;
            var zone = !utc && parameters.TryGetValue("TZID", out var tzid) ? FindZone(tzid) : null;
            return new IcsTime(time, false, utc, zone);
        }

        public DateTimeOffset Instant(TimeZoneInfo local)
        {
            if (Utc)
                return new DateTimeOffset(DateTime.SpecifyKind(Local, DateTimeKind.Utc));
            var zone = Zone ?? local;
            var wall = DateTime.SpecifyKind(Local, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(wall))
                wall = wall.AddHours(1); // ora che non esiste (passaggio all'ora legale)
            return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
        }

        public IcsTime With(DateTime local) => this with { Local = local };

        public IcsTime AddDays(int days) => this with { Local = Local.AddDays(days) };

        public bool Matches(IcsTime occurrence, TimeZoneInfo local) =>
            AllDay || occurrence.AllDay ? Local.Date == occurrence.Local.Date : Instant(local) == occurrence.Instant(local);

        /// <summary>Nomi di Windows ("W. Europe Standard Time") e IANA ("Europe/Rome"); sconosciuto: ora locale.</summary>
        private static TimeZoneInfo? FindZone(string id)
        {
            id = id.Trim().TrimStart('/');
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return null;
            }
        }
    }

    private sealed record RRule(string Frequency, int Interval, int? Count, IcsTime? Until,
        IReadOnlyList<(int Ordinal, DayOfWeek Day)> ByDay, IReadOnlyList<int> ByMonthDay)
    {
        private static readonly Dictionary<string, DayOfWeek> Days = new(StringComparer.OrdinalIgnoreCase)
        {
            ["MO"] = DayOfWeek.Monday, ["TU"] = DayOfWeek.Tuesday, ["WE"] = DayOfWeek.Wednesday, ["TH"] = DayOfWeek.Thursday,
            ["FR"] = DayOfWeek.Friday, ["SA"] = DayOfWeek.Saturday, ["SU"] = DayOfWeek.Sunday,
        };

        public static RRule? Parse(string value)
        {
            var parts = value.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1], StringComparer.Ordinal);
            if (!parts.TryGetValue("FREQ", out var frequency))
                return null;
            var interval = parts.TryGetValue("INTERVAL", out var i) && int.TryParse(i, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 1;
            int? count = parts.TryGetValue("COUNT", out var c) && int.TryParse(c, CultureInfo.InvariantCulture, out var m) ? m : null;
            var until = parts.TryGetValue("UNTIL", out var u) ? IcsTime.Parse(u, new Dictionary<string, string>()) : null;
            if (until is { AllDay: true } date)
                until = date with { Local = date.Local.AddDays(1).AddTicks(-1), AllDay = false }; // fino alla fine di quel giorno
            var byDay = new List<(int, DayOfWeek)>();
            if (parts.TryGetValue("BYDAY", out var bd))
                foreach (var token in bd.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (token.Length >= 2 && Days.TryGetValue(token[^2..], out var day))
                        byDay.Add((token.Length > 2 && int.TryParse(token[..^2], CultureInfo.InvariantCulture, out var ord) ? ord : 0, day));
            var byMonthDay = parts.TryGetValue("BYMONTHDAY", out var bmd)
                ? bmd.Split(',').Select(s => int.TryParse(s, CultureInfo.InvariantCulture, out var d) ? d : 0).Where(d => d != 0).ToArray()
                : [];
            return new RRule(frequency.ToUpperInvariant(), interval, count, until, byDay, byMonthDay);
        }
    }
}

/// <summary>Prossimo appuntamento e promemoria (docs/11-agenda-pannello-rapido.md).</summary>
public static class Agenda
{
    public static readonly TimeSpan ReminderLead = TimeSpan.FromMinutes(10);

    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>Appuntamenti di due calendari senza doppioni, in ordine di inizio.</summary>
    public static IReadOnlyList<CalendarEvent> Merge(IEnumerable<CalendarEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return events
            .GroupBy(e => e.Key)
            .Select(g => g.OrderByDescending(e => e.MeetingLink is not null).First())
            .OrderBy(e => e.Start)
            .ThenBy(e => e.Title, StringComparer.CurrentCulture)
            .ToArray();
    }

    /// <summary>Appuntamenti (con orario) non ancora finiti, in ordine di inizio.</summary>
    public static IReadOnlyList<CalendarEvent> Upcoming(IEnumerable<CalendarEvent> events, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(events);
        return events.Where(e => !e.AllDay && e.End > now).OrderBy(e => e.Start).ToArray();
    }

    /// <summary>Gli appuntamenti di quel giorno (anche quelli di tutto il giorno), in ordine.</summary>
    public static IReadOnlyList<CalendarEvent> OnDay(IEnumerable<CalendarEvent> events, DateTime day)
    {
        ArgumentNullException.ThrowIfNull(events);
        var start = day.Date;
        var end = start.AddDays(1);
        return events
            .Where(e => e.Start.LocalDateTime < end && (e.End.LocalDateTime > start || (e.End == e.Start && e.Start.LocalDateTime >= start)))
            .OrderBy(e => !e.AllDay)
            .ThenBy(e => e.Start)
            .ToArray();
    }

    /// <summary>
    /// Quello da mostrare sotto l'ora: l'appuntamento in corso (iniziato da meno di metà della sua durata) o il
    /// prossimo che inizia entro <paramref name="horizon"/> (di norma 24 ore).
    /// </summary>
    public static CalendarEvent? Next(IEnumerable<CalendarEvent> events, DateTimeOffset now, TimeSpan? horizon = null)
    {
        var upcoming = Upcoming(events, now);
        var limit = now + (horizon ?? TimeSpan.FromHours(24));
        return upcoming.FirstOrDefault(e => e.Start <= now && now - e.Start < (e.End - e.Start) / 2)
               ?? upcoming.FirstOrDefault(e => e.Start > now && e.Start <= limit);
    }

    /// <summary>"ora", "tra 5 min", "tra 1 h 20 min", "tra 3 h"; "in corso" se è già iniziato.</summary>
    public static string Relative(CalendarEvent e, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Start <= now)
            return e.End > now ? $"in corso · fino alle {e.End.LocalDateTime.ToString("HH:mm", Italian)}" : "finito";
        var minutes = (int)Math.Ceiling((e.Start - now).TotalMinutes);
        return minutes switch
        {
            <= 1 => "ora",
            < 60 => $"tra {minutes} min",
            _ when minutes % 60 == 0 || minutes >= 180 => $"tra {(int)Math.Round(minutes / 60.0)} h",
            _ => $"tra {minutes / 60} h {minutes % 60} min",
        };
    }

    /// <summary>"14:30", "domani 9:00", "ven 9:00"; "tutto il giorno" per gli eventi senza orario.</summary>
    public static string When(CalendarEvent e, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.AllDay)
            return "tutto il giorno";
        var start = e.Start.LocalDateTime;
        var days = (start.Date - now.LocalDateTime.Date).Days;
        var time = start.ToString("H:mm", Italian);
        return days switch
        {
            0 => start.ToString("HH:mm", Italian),
            1 => $"domani {time}",
            > 1 and < 7 => $"{start.ToString("ddd", Italian)} {time}",
            _ => $"{start.ToString("d MMM", Italian)} {time}",
        };
    }
}

public enum ReminderKind
{
    /// <summary>Dieci minuti prima (o appena noto, se manca meno).</summary>
    Soon,

    /// <summary>All'inizio.</summary>
    Starting,
}

/// <summary>Decide quando avvisare di un appuntamento: una volta prima e una all'inizio, mai due volte la stessa.</summary>
public sealed class AppointmentReminder
{
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);

    public IReadOnlyList<(CalendarEvent Event, ReminderKind Kind)> Due(IEnumerable<CalendarEvent> events, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(events);
        var due = new List<(CalendarEvent, ReminderKind)>();
        foreach (var e in events.Where(e => !e.AllDay).OrderBy(e => e.Start))
        {
            if (now >= e.Start && now - e.Start < TimeSpan.FromMinutes(2))
            {
                if (_announced.Add($"{e.Key}|start"))
                {
                    _announced.Add($"{e.Key}|soon");
                    due.Add((e, ReminderKind.Starting));
                }
            }
            else if (now < e.Start && e.Start - now <= Agenda.ReminderLead && _announced.Add($"{e.Key}|soon"))
            {
                due.Add((e, ReminderKind.Soon));
            }
        }
        return due;
    }
}

/// <summary>Il riepilogo del mattino: data, appuntamenti di oggi e mail da leggere.</summary>
public sealed record MorningBrief(string Greeting, string Date, string AgendaLine, string? MailLine, CalendarEvent? First)
{
    public const int FirstHour = 5;
    public const int LastHour = 12;

    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>Al primo accesso della giornata, solo di mattina (5–12), una volta al giorno.</summary>
    public static bool ShouldShow(DateTime now, DateOnly? lastShown) =>
        now.Hour >= FirstHour && now.Hour < LastHour && lastShown != DateOnly.FromDateTime(now);

    /// <param name="unreadMail">Mail da leggere nella casella collegata; null se non c'è una casella collegata.</param>
    public static MorningBrief Compose(DateTime now, IEnumerable<CalendarEvent> events, int? unreadMail)
    {
        ArgumentNullException.ThrowIfNull(events);
        var today = Agenda.OnDay(events, now);
        var timed = today.Where(e => !e.AllDay).ToArray();
        var allDay = today.Where(e => e.AllDay).ToArray();
        var offset = new DateTimeOffset(now);
        var first = timed.FirstOrDefault(e => e.End > offset);

        string agenda;
        if (today.Count == 0)
            agenda = "Nessun appuntamento oggi";
        else if (first is null)
            agenda = allDay.Length > 0 && timed.Length == 0
                ? $"Oggi: {string.Join(", ", allDay.Select(e => e.Title))}"
                : "Nessun altro appuntamento oggi";
        else
        {
            var count = timed.Count(e => e.End > offset);
            var lead = count == 1 ? "1 appuntamento" : $"{count} appuntamenti";
            agenda = $"{lead} · {(first.Start <= offset ? "ora" : $"alle {first.Start.LocalDateTime.ToString("H:mm", Italian)}")} {first.Title}";
        }

        string? mail = unreadMail switch
        {
            null => null,
            0 => "Nessuna mail da leggere",
            1 => "1 mail da leggere",
            var n => $"{n} mail da leggere",
        };
        var date = now.ToString("dddd d MMMM", Italian);
        return new MorningBrief("BUONGIORNO", char.ToUpper(date[0], Italian) + date[1..], agenda, mail, first);
    }
}
