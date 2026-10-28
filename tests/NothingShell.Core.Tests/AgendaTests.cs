using NothingShell.Core.Notch;

namespace NothingShell.Core.Tests;

public class IcsCalendarTests
{
    private static readonly TimeZoneInfo Rome = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

    private static string Calendar(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" + string.Concat(events.Select(e => $"BEGIN:VEVENT\r\n{e}\r\nEND:VEVENT\r\n")) + "END:VCALENDAR\r\n";

    private static DateTimeOffset At(int year, int month, int day, int hour = 0, int minute = 0)
    {
        var wall = new DateTime(year, month, day, hour, minute, 0);
        return new DateTimeOffset(wall, Rome.GetUtcOffset(wall));
    }

    private static IReadOnlyList<CalendarEvent> Parse(string ics, DateTimeOffset from, DateTimeOffset to) =>
        IcsCalendar.Parse(ics, from, to, Rome);

    [Fact]
    public void SingleEventWithZoneLinkAndFoldedLines()
    {
        var ics = Calendar(
            "UID:1\r\nSUMMARY:Riunione\\, budget\r\nDTSTART;TZID=W. Europe Standard Time:20261028T143000\r\n" +
            "DTEND;TZID=W. Europe Standard Time:20261028T153000\r\nLOCATION:Sala 2\r\n" +
            "DESCRIPTION:Entra: https://teams.microsoft.com/l/meetup-join/19%3ameeting_\r\n abc/0?context=1\\nGrazie");
        var e = Assert.Single(Parse(ics, At(2026, 10, 28), At(2026, 10, 29)));
        Assert.Equal("Riunione, budget", e.Title);
        Assert.Equal(At(2026, 10, 28, 14, 30), e.Start);
        Assert.Equal(TimeSpan.FromHours(1), e.End - e.Start);
        Assert.Equal("Sala 2", e.Location);
        Assert.Equal("https://teams.microsoft.com/l/meetup-join/19%3ameeting_abc/0?context=1", e.MeetingLink);
        Assert.Equal("TEAMS", MeetingLinks.Service(e.MeetingLink));
    }

    [Fact]
    public void UtcIanaZoneAndDuration()
    {
        var ics = Calendar(
            "UID:1\r\nSUMMARY:Utc\r\nDTSTART:20261028T080000Z\r\nDURATION:PT45M",
            "UID:2\r\nSUMMARY:Iana\r\nDTSTART;TZID=Europe/Rome:20261028T100000\r\nDTEND;TZID=Europe/Rome:20261028T103000");
        var events = Parse(ics, At(2026, 10, 28), At(2026, 10, 29));
        Assert.Equal(["Utc", "Iana"], events.Select(e => e.Title));
        Assert.Equal(At(2026, 10, 28, 9, 0), events[0].Start); // 08:00 UTC = 09:00 a Roma (ora solare)
        Assert.Equal(TimeSpan.FromMinutes(45), events[0].End - events[0].Start);
        Assert.Equal(At(2026, 10, 28, 10, 0), events[1].Start);
    }

    [Fact]
    public void AllDayEventsCoverTheirDays()
    {
        var ics = Calendar("UID:1\r\nSUMMARY:Ferie\r\nDTSTART;VALUE=DATE:20261027\r\nDTEND;VALUE=DATE:20261030");
        var e = Assert.Single(Parse(ics, At(2026, 10, 29), At(2026, 10, 30)));
        Assert.True(e.AllDay);
        Assert.Equal(At(2026, 10, 27), e.Start);
        Assert.Equal(At(2026, 10, 30), e.End);
        Assert.Empty(Parse(ics, At(2026, 10, 30), At(2026, 10, 31)));
    }

    [Fact]
    public void WeeklyRuleKeepsLocalTimeAcrossDaylightSaving()
    {
        // Ogni lunedì e mercoledì alle 9:00 dal 19 ottobre; il 25 ottobre finisce l'ora legale.
        var ics = Calendar("UID:1\r\nSUMMARY:Standup\r\nDTSTART;TZID=Europe/Rome:20261019T090000\r\n" +
                           "DTEND;TZID=Europe/Rome:20261019T091500\r\nRRULE:FREQ=WEEKLY;BYDAY=MO,WE");
        var events = Parse(ics, At(2026, 10, 21), At(2026, 10, 29));
        Assert.Equal([At(2026, 10, 21, 9), At(2026, 10, 26, 9), At(2026, 10, 28, 9)], events.Select(e => e.Start));
        Assert.All(events, e => Assert.Equal(TimeSpan.FromMinutes(15), e.End - e.Start));
    }

    [Fact]
    public void CountUntilIntervalAndExdate()
    {
        var ics = Calendar(
            "UID:1\r\nSUMMARY:Count\r\nDTSTART;TZID=Europe/Rome:20261001T100000\r\nRRULE:FREQ=DAILY;COUNT=3",
            "UID:2\r\nSUMMARY:Until\r\nDTSTART;TZID=Europe/Rome:20261001T110000\r\nRRULE:FREQ=DAILY;INTERVAL=2;UNTIL=20261005T235959Z",
            "UID:3\r\nSUMMARY:Ex\r\nDTSTART;TZID=Europe/Rome:20261001T120000\r\nRRULE:FREQ=DAILY;COUNT=4\r\n" +
            "EXDATE;TZID=Europe/Rome:20261002T120000,20261003T120000");
        var events = Parse(ics, At(2026, 9, 1), At(2026, 11, 1));
        Assert.Equal([1, 2, 3], events.Where(e => e.Title == "Count").Select(e => e.Start.Day));
        Assert.Equal([1, 3, 5], events.Where(e => e.Title == "Until").Select(e => e.Start.Day));
        Assert.Equal([1, 4], events.Where(e => e.Title == "Ex").Select(e => e.Start.Day));
    }

    [Fact]
    public void MonthlyByOrdinalWeekdayAndMonthDay()
    {
        var ics = Calendar(
            "UID:1\r\nSUMMARY:Ultimo venerdì\r\nDTSTART;TZID=Europe/Rome:20260130T170000\r\nRRULE:FREQ=MONTHLY;BYDAY=-1FR",
            "UID:2\r\nSUMMARY:Il 31\r\nDTSTART;TZID=Europe/Rome:20260131T090000\r\nRRULE:FREQ=MONTHLY",
            "UID:3\r\nSUMMARY:Secondo martedì\r\nDTSTART;TZID=Europe/Rome:20260113T090000\r\nRRULE:FREQ=MONTHLY;BYDAY=2TU");
        var events = Parse(ics, At(2026, 9, 1), At(2026, 11, 1));
        Assert.Equal([25, 30], events.Where(e => e.Title == "Ultimo venerdì").Select(e => e.Start.Day));
        Assert.Equal([10], events.Where(e => e.Title == "Il 31").Select(e => e.Start.Month)); // settembre non ha il 31
        Assert.Equal([8, 13], events.Where(e => e.Title == "Secondo martedì").Select(e => e.Start.Day));
    }

    [Fact]
    public void MovedAndCancelledOccurrences()
    {
        var ics = Calendar(
            "UID:a\r\nSUMMARY:Weekly\r\nDTSTART;TZID=Europe/Rome:20261019T100000\r\nRRULE:FREQ=WEEKLY",
            "UID:a\r\nRECURRENCE-ID;TZID=Europe/Rome:20261026T100000\r\nSUMMARY:Weekly (spostata)\r\nDTSTART;TZID=Europe/Rome:20261027T150000",
            "UID:b\r\nSUMMARY:Annullata\r\nSTATUS:CANCELLED\r\nDTSTART;TZID=Europe/Rome:20261026T100000",
            "UID:c\r\nSUMMARY:Con promemoria\r\nDTSTART;TZID=Europe/Rome:20261026T110000\r\nBEGIN:VALARM\r\nTRIGGER:-PT15M\r\n" +
            "DESCRIPTION:Promemoria\r\nEND:VALARM");
        var events = Parse(ics, At(2026, 10, 25), At(2026, 11, 3));
        Assert.Equal(["Con promemoria", "Weekly (spostata)", "Weekly"], events.Select(e => e.Title));
        Assert.Equal(At(2026, 10, 27, 15), events[1].Start);
        Assert.Equal(At(2026, 11, 2, 10), events[2].Start);
    }

    [Fact]
    public void DurationsAndLinks()
    {
        Assert.Equal(TimeSpan.FromMinutes(90), IcsCalendar.ParseDuration("PT1H30M"));
        Assert.Equal(TimeSpan.FromDays(8), IcsCalendar.ParseDuration("P1W1D"));
        Assert.Equal(TimeSpan.FromMinutes(-15), IcsCalendar.ParseDuration("-PT15M"));
        Assert.Null(IcsCalendar.ParseDuration("domani"));

        Assert.Equal("https://us02web.zoom.us/j/123?pwd=x", MeetingLinks.Find(null, "Link: https://us02web.zoom.us/j/123?pwd=x."));
        Assert.Equal("MEET", MeetingLinks.Service(MeetingLinks.Find("https://meet.google.com/abc-defg-hij")));
        Assert.Null(MeetingLinks.Find("https://example.com/meeting"));
    }
}

public class AgendaTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 9, 0, 0, TimeSpan.FromHours(1));

    private static CalendarEvent Event(string title, double startHours, double lengthHours = 1, bool allDay = false) =>
        new(title, Now.AddHours(startHours), Now.AddHours(startHours + lengthHours), allDay);

    [Fact]
    public void NextIsTheCurrentMeetingThenTheUpcomingOne()
    {
        var events = new[] { Event("Prima", -0.25), Event("Dopo", 2), Event("Ferie", -9, 24, allDay: true) };
        Assert.Equal("Prima", Agenda.Next(events, Now)?.Title); // iniziata da 15 minuti su 60
        Assert.Equal("Dopo", Agenda.Next(events, Now.AddMinutes(40))?.Title); // oltre metà: si guarda avanti
        Assert.Null(Agenda.Next(events, Now.AddHours(4)));
        Assert.Null(Agenda.Next([Event("Lontano", 30)], Now));
    }

    [Fact]
    public void RelativeAndWhenTexts()
    {
        Assert.Equal("tra 25 min", Agenda.Relative(Event("x", 25 / 60.0), Now));
        Assert.Equal("tra 1 h 20 min", Agenda.Relative(Event("x", 80 / 60.0), Now));
        Assert.Equal("tra 2 h", Agenda.Relative(Event("x", 2), Now));
        Assert.Equal("ora", Agenda.Relative(Event("x", 0.5 / 60), Now));
        Assert.StartsWith("in corso · fino alle", Agenda.Relative(Event("x", -0.1), Now), StringComparison.Ordinal);

        var local = Now.LocalDateTime;
        Assert.Equal(local.AddHours(5).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), Agenda.When(Event("x", 5), new DateTimeOffset(local.Date.AddHours(8))));
        Assert.StartsWith("domani", Agenda.When(Event("x", 24), new DateTimeOffset(local.AddHours(-1))), StringComparison.Ordinal);
        Assert.Equal("tutto il giorno", Agenda.When(Event("x", 0, 24, allDay: true), Now));
    }

    [Fact]
    public void MergeRemovesDuplicatesKeepingTheLink()
    {
        var plain = Event("Riunione", 1);
        var withLink = plain with { MeetingLink = "https://meet.google.com/a" };
        var merged = Agenda.Merge([plain, Event("Altro", 3), withLink]);
        Assert.Equal(2, merged.Count);
        Assert.Equal("https://meet.google.com/a", merged[0].MeetingLink);
    }

    [Fact]
    public void ReminderTenMinutesBeforeAndAtStartOnlyOnce()
    {
        var reminder = new AppointmentReminder();
        var meeting = Event("Call", 0.5);
        Assert.Empty(reminder.Due([meeting], Now));
        var soon = Assert.Single(reminder.Due([meeting], Now.AddMinutes(20)));
        Assert.Equal(ReminderKind.Soon, soon.Kind);
        Assert.Empty(reminder.Due([meeting], Now.AddMinutes(25)));
        Assert.Equal(ReminderKind.Starting, Assert.Single(reminder.Due([meeting], Now.AddMinutes(30))).Kind);
        Assert.Empty(reminder.Due([meeting], Now.AddMinutes(31)));
        Assert.Empty(reminder.Due([meeting], Now.AddMinutes(40))); // già iniziata da troppo
    }
}

public class MorningBriefTests
{
    [Fact]
    public void OnlyInTheMorningAndOncePerDay()
    {
        var morning = new DateTime(2026, 10, 28, 8, 15, 0);
        Assert.True(MorningBrief.ShouldShow(morning, null));
        Assert.True(MorningBrief.ShouldShow(morning, new DateOnly(2026, 10, 27)));
        Assert.False(MorningBrief.ShouldShow(morning, new DateOnly(2026, 10, 28)));
        Assert.False(MorningBrief.ShouldShow(morning.AddHours(-4), null)); // 4:15
        Assert.False(MorningBrief.ShouldShow(morning.AddHours(4), null)); // 12:15
    }

    [Fact]
    public void SummaryOfAppointmentsAndMail()
    {
        var now = new DateTime(2026, 10, 28, 8, 15, 0);
        CalendarEvent At(string title, int hour, int minute = 0) =>
            new(title, new DateTimeOffset(now.Date.AddHours(hour).AddMinutes(minute)), new DateTimeOffset(now.Date.AddHours(hour + 1).AddMinutes(minute)));

        var brief = MorningBrief.Compose(now, [At("Standup", 9, 30), At("Pranzo", 13), At("Ieri", -10)], 5);
        Assert.Equal("BUONGIORNO", brief.Greeting);
        Assert.Equal("Mercoledì 28 ottobre", brief.Date);
        Assert.Equal("2 appuntamenti · alle 9:30 Standup", brief.AgendaLine);
        Assert.Equal("5 mail da leggere", brief.MailLine);
        Assert.Equal("Standup", brief.First?.Title);

        var empty = MorningBrief.Compose(now, [], null);
        Assert.Equal("Nessun appuntamento oggi", empty.AgendaLine);
        Assert.Null(empty.MailLine);

        var holiday = new CalendarEvent("Ferie", new DateTimeOffset(now.Date), new DateTimeOffset(now.Date.AddDays(1)), AllDay: true);
        Assert.Equal("Oggi: Ferie", MorningBrief.Compose(now, [holiday], 0).AgendaLine);
        Assert.Equal("Nessuna mail da leggere", MorningBrief.Compose(now, [holiday], 0).MailLine);
    }
}

public class ScreenshotSourceTests
{
    [Fact]
    public void GreenshotFolderFromIniOrDesktop()
    {
        static string Expand(string s) => s.Replace("%USERPROFILE%", @"C:\Users\a", StringComparison.OrdinalIgnoreCase);
        const string desktop = @"C:\Users\a\Desktop";
        var ini = "; Greenshot\r\n[Core]\r\nLanguage=it-IT\r\nOutputFilePath=%USERPROFILE%\\Pictures\\Greenshot\\\r\n[Editor]\r\nOutputFilePath=X";
        Assert.Equal(@"C:\Users\a\Pictures\Greenshot", ScreenshotSources.GreenshotFolder(ini, desktop, Expand));
        Assert.Equal(desktop, ScreenshotSources.GreenshotFolder("[Core]\r\nOutputFilePath=\r\n", desktop, Expand));
        Assert.Equal(desktop, ScreenshotSources.GreenshotFolder("[Editor]\r\nOutputFilePath=C:\\x", desktop, Expand));
        Assert.Contains("Greenshot", ScreenshotSources.ClipboardTools);
        Assert.True(ScreenshotSources.IsImage("2026-10-28 09_00_00-Pagina.JPG"));
        Assert.False(ScreenshotSources.IsImage("cattura.greenshot"));
    }
}
