using NothingShell.Core.Work;

namespace NothingShell.Core.Tests;

public class WorkHoursTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 10, 14, 9, 0, 0);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ns-work-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, true);
        GC.SuppressFinalize(this);
    }

    private WorkStore Store()
    {
        var store = new WorkStore(_folder);
        Run(store, "progetto web = sinapsi_web, sinapsi");
        Run(store, "progetto label = printer");
        Run(store, "progetto altro = zzz");
        return store;
    }

    private static WorkAnswer Run(WorkStore store, string text, DateTime? now = null)
    {
        var answer = WorkCommands.Run(text, store, now ?? Start);
        if (answer.Config is { } config)
            store.SetConfig(config);
        return answer;
    }

    /// <summary>Un campione ogni 5 s per <paramref name="seconds"/> secondi, col titolo dato.</summary>
    private static DateTime Work(WorkStore store, DateTime from, string? title, int seconds, TimeSpan? idle = null)
    {
        var at = from;
        store.Sample(at, title, idle ?? TimeSpan.Zero);
        for (var i = 0; i < seconds; i += 5)
        {
            at = at.AddSeconds(5);
            store.Sample(at, title, idle ?? TimeSpan.Zero);
        }
        return at;
    }

    private static double Seconds(WorkStore store, string project) =>
        store.Log.Seconds(project, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

    [Fact]
    public void LongestKeywordWins()
    {
        var config = new WorkConfig([new("a", ["sinapsi"]), new("b", ["sinapsi_web"])], []);
        Assert.Equal("b", WorkMatcher.Match(config, "index.html - sinapsi_web - Visual Studio Code")!.Name);
        Assert.Equal("a", WorkMatcher.Match(config, "Sinapsi gestionale")!.Name);
        Assert.Null(WorkMatcher.Match(config, "YouTube"));
        Assert.Null(WorkMatcher.Match(config, null));
    }

    [Fact]
    public void CountsTimeOfTheForegroundProject()
    {
        var store = Store();
        var at = Work(store, Start, "app.py - printer - Code", 600);
        at = Work(store, at, "index.html - sinapsi_web", 300);
        Assert.Equal(600, Seconds(store, "label"), 1);
        Assert.Equal(300, Seconds(store, "web"), 1);
    }

    [Fact]
    public void UnrelatedWindowsAndIdleTimeDoNotCount()
    {
        var store = Store();
        Work(store, Start, "YouTube", 300);
        Work(store, Start.AddHours(1), "printer", 300, idle: TimeSpan.FromMinutes(6));
        Assert.Empty(store.Log.Days);
    }

    [Fact]
    public void LongGapsAreNotInvented()
    {
        var store = Store();
        store.Sample(Start, "printer", TimeSpan.Zero);
        store.Sample(Start.AddHours(2), "printer", TimeSpan.Zero); // sospensione del PC
        Assert.Equal(0, Seconds(store, "label"));
    }

    [Fact]
    public void MidnightSplitsTheInterval()
    {
        var store = Store();
        store.Sample(new DateTime(2026, 10, 14, 23, 59, 50), "printer", TimeSpan.Zero);
        store.Sample(new DateTime(2026, 10, 15, 0, 0, 10), "printer", TimeSpan.Zero);
        Assert.Equal(10, store.Log.Seconds("label", new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 14)), 1);
        Assert.Equal(10, store.Log.Seconds("label", new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 15)), 1);
    }

    [Fact]
    public void GroupSumsItsProjectsOnly()
    {
        var store = Store();
        Run(store, "gruppo Cliente A = web, label");
        var at = Work(store, Start, "sinapsi_web", 3600);
        at = Work(store, at, "printer", 1800);
        Work(store, at, "zzz", 900);
        var report = store.Report(WorkPeriod.Month(2026, 10));
        Assert.Equal(5400, report.Groups.Single().Seconds, 1);
        Assert.Equal(6300, report.TotalSeconds, 1);
    }

    [Fact]
    public void PeriodsAreRecognised()
    {
        var today = new DateOnly(2026, 11, 3);
        Assert.Equal(new DateOnly(2026, 10, 31), WorkPeriod.Parse("ottobre", today)!.Value.To);
        Assert.Equal(new DateOnly(2025, 12, 1), WorkPeriod.Parse("dicembre", today)!.Value.From);
        Assert.Equal(new DateOnly(2026, 2, 28), WorkPeriod.Parse("febbraio 2026", today)!.Value.To);
        Assert.Equal(new DateOnly(2026, 9, 1), WorkPeriod.Parse("2026-09", today)!.Value.From);
        Assert.Equal(new DateOnly(2026, 11, 2), WorkPeriod.Parse("settimana", today)!.Value.From);
        Assert.Equal(new DateOnly(2026, 10, 1), WorkPeriod.Parse("mese scorso", today)!.Value.From);
        Assert.Null(WorkPeriod.Parse("boh", today));
    }

    [Fact]
    public void FormatsHoursReadably()
    {
        Assert.Equal("45 min", WorkReport.Duration(2700));
        Assert.Equal("2 h 05 min", WorkReport.Duration(7500));
        Assert.Equal("2 h 30 min (2,50 ore)", WorkReport.DurationWithDecimals(9000));
    }

    [Fact]
    public void CommandsBuildProjectsAndGroups()
    {
        var store = new WorkStore(_folder);
        Assert.Equal(["sinapsi"], Run(store, "progetto sinapsi").Config!.Projects[0].Keywords);
        Run(store, "gruppo Cliente A = sinapsi, nuovo");
        Assert.Equal(2, store.Config.Group("cliente a")!.Members.Count);
        Assert.NotNull(store.Config.Project("nuovo")); // creato dal gruppo
        Run(store, "togli nuovo da Cliente A");
        Assert.Equal(["sinapsi"], store.Config.Group("Cliente A")!.Members);
        Run(store, "aggiungi nuovo a Cliente A");
        Assert.Equal(2, store.Config.Group("Cliente A")!.Members.Count);
        Run(store, "togli nuovo");
        Assert.Null(store.Config.Project("nuovo"));
        Assert.Equal(["sinapsi"], store.Config.Group("Cliente A")!.Members);
        Run(store, "togli Cliente A");
        Assert.Empty(store.Config.Groups);
    }

    [Fact]
    public void CommandsRefuseTheConfusing()
    {
        var store = Store();
        Run(store, "gruppo G = web");
        Assert.Null(Run(store, "progetto G").Config);
        Assert.Null(Run(store, "gruppo web").Config);
        Assert.Null(Run(store, "aggiungi web a Fantasma").Config);
        Assert.Null(Run(store, "blabla").Config);
        Assert.Contains("Quale periodo", Run(store, "ore mai").Rows[0].Value);
    }

    [Fact]
    public void HoursCommandAnswersByGroupAndMonth()
    {
        var store = Store();
        Run(store, "gruppo Cliente A = web, label");
        Run(store, "gruppo Altri = altro");
        var at = Work(store, Start, "sinapsi_web", 3600);
        Work(store, at, "zzz", 1800);
        var answer = Run(store, "ore ottobre gruppo Cliente A");
        Assert.Contains("1 h 00 min", answer.Title);
        Assert.Equal("web", answer.Rows[0].Label);
        Assert.Contains("30 min", Run(store, "ore oggi gruppo altri", Start).Title);
        Assert.Contains("0 min", Run(store, "ore novembre gruppo Cliente A", Start.AddMonths(1)).Title);
        Assert.Contains("non c'è", Run(store, "ore gruppo Fantasma").Rows[0].Value);
    }

    [Fact]
    public void WritesTheMonthlyTextForTheAssistant()
    {
        var store = Store();
        Run(store, "gruppo Cliente A = web, label");
        Work(store, Start, "printer", 5400);
        store.Save();
        var text = File.ReadAllText(store.ReportPath(2026, 10));
        Assert.Contains("TOTALE: 1 h 30 min (1,50 ore)", text);
        Assert.Contains("GRUPPO Cliente A: 1 h 30 min (1,50 ore) (progetti: web 0 min; label 1 h 30 min)", text);
        Assert.Contains("PROGETTO label: 1 h 30 min (1,50 ore)", text);
    }

    [Fact]
    public void HoursSurviveARestartButNotTheWindowTitles()
    {
        var store = Store();
        Work(store, Start, "SEGRETO sinapsi_web banca", 600);
        store.Save();
        var again = new WorkStore(_folder);
        Assert.Equal(600, again.Log.Seconds("web", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)), 1);
        Assert.Equal(3, again.Config.Projects.Count);
        Assert.DoesNotContain("SEGRETO", File.ReadAllText(Path.Combine(_folder, "time.json")));
        Assert.DoesNotContain("banca", File.ReadAllText(again.ReportPath(2026, 10)));
    }

    [Fact]
    public void BrokenFilesStartEmpty()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "time.json"), "{ non json");
        File.WriteAllText(Path.Combine(_folder, "projects.json"), "][");
        var store = new WorkStore(_folder);
        Assert.Empty(store.Config.Projects);
        Assert.Empty(store.Log.Days);
    }

    [Theory]
    [InlineData("2h30", 9000)]
    [InlineData("2h 30m", 9000)]
    [InlineData("1,5h", 5400)]
    [InlineData("90m", 5400)]
    [InlineData("2:30", 9000)]
    [InlineData("3 ore", 10800)]
    public void ParsesDurations(string text, double seconds) => Assert.Equal(seconds, WorkPhrase.ParseDuration(text));

    [Fact]
    public void ManualHoursAreAddedAndRemoved()
    {
        var store = Store();
        Run(store, "gruppo G = web");
        var today = new DateOnly(2026, 10, 14);
        Assert.Contains("+2 h 30 min oggi", Run(store, "aggiungi ore 2h30 a web").Rows[0].Value);
        Assert.Equal(9000, store.Log.Seconds("web", today, today));
        Run(store, "aggiungi ore 1h a web ieri");
        Run(store, "aggiungi ore 45m a web 2026-10-01");
        Assert.Equal(3600, store.Log.Seconds("web", today.AddDays(-1), today.AddDays(-1)));
        Assert.Equal(9000 + 3600 + 2700, store.Report(WorkPeriod.Month(2026, 10)).Groups.Single().Seconds);
        Assert.Contains("−30 min", Run(store, "togli ore 30m da web").Rows[0].Value);
        Assert.Equal(7200, store.Log.Seconds("web", today, today));
        Assert.Contains("−2 h 00 min", Run(store, "togli ore 5h da web").Rows[0].Value); // mai sotto zero
        Assert.Equal(0, store.Log.Seconds("web", today, today));
        Assert.Contains("niente da togliere", Run(store, "togli ore 1h da web").Rows[0].Value);
        Assert.Contains("TOTALE: 1 h 45 min", File.ReadAllText(store.ReportPath(2026, 10))); // l'assistente vede subito le ore a mano
    }

    [Fact]
    public void ManualHoursRefuseTheWrong()
    {
        var store = Store();
        Assert.Contains("fantasma", Run(store, "aggiungi ore 1h a fantasma").Rows[0].Value);
        Assert.Contains("durata possibile", Run(store, "aggiungi ore 30h a web").Rows[0].Value);
        Assert.Contains("non è ancora arrivato", Run(store, "aggiungi ore 1h a web 2026-12-01").Rows[0].Value);
        Assert.Empty(store.Log.Days);
    }

    private static readonly DateTime Today = new(2026, 11, 3, 10, 0, 0); // martedì

    private static double DaySeconds(WorkStore store, string key, int month, int day) =>
        store.Log.Seconds(key, new DateOnly(2026, month, day), new DateOnly(2026, month, day));

    private WorkStore Plainest()
    {
        var store = new WorkStore(_folder);
        Run(store, "progetto plainest = plainest", Today);
        Run(store, "progetto wpshop = wpshop", Today);
        Run(store, "gruppo wpweb = wpshop", Today);
        Run(store, "gruppo Cliente A = plainest, wpshop", Today);
        return store;
    }

    [Fact]
    public void TheWholeSentenceIsUnderstood()
    {
        var store = Plainest();
        var answer = Run(store, "aggiungi 7h a plainest e 1h al gruppo wpweb che ho fatto ieri e aggiungi 4h su plainest xche ho fatto quest amattina.", Today);
        Assert.True(answer.Changed);
        Assert.Equal(7 * 3600, DaySeconds(store, "plainest", 11, 2)); // ieri
        Assert.Equal(1 * 3600, DaySeconds(store, WorkLog.GroupKey("wpweb"), 11, 2)); // ieri, sul gruppo
        Assert.Equal(4 * 3600, DaySeconds(store, "plainest", 11, 3)); // questa mattina = oggi
        Assert.Equal(0, DaySeconds(store, "wpshop", 11, 2)); // le ore del gruppo non vanno a un suo progetto

        var report = store.Report(WorkPeriod.Month(2026, 11));
        Assert.Equal(3600, report.Groups.First(g => g.Name == "wpweb").Seconds);
        Assert.Equal(11 * 3600, report.Groups.First(g => g.Name == "Cliente A").Seconds);
        Assert.Equal(12 * 3600, report.TotalSeconds);

        // Il notch mostra per ogni gruppo il totale.
        Assert.Contains(answer.Rows, r => r.Label == "wpweb" && r.IsGroup && r.Value == "1 h 00 min");
        Assert.Contains(answer.Rows, r => r.Label == "plainest" && r.Value == "11 h 00 min");
        Assert.Contains(answer.Rows, r => r.Label == "gruppo wpweb" && r.Value == "+1 h 00 min ieri");
        Assert.Contains(answer.Rows, r => r.Label == "plainest" && r.Value == "+4 h 00 min oggi");
    }

    [Fact]
    public void GroupHoursAreInTheAssistantsFile()
    {
        var store = Plainest();
        Run(store, "aggiungi 1h al gruppo wpweb ieri", Today);
        Assert.Contains("GRUPPO wpweb: 1 h 00 min (1,00 ore) (progetti: wpshop 0 min; ore messe direttamente sul gruppo 1 h 00 min)", File.ReadAllText(store.ReportPath(2026, 11)));
        Assert.Contains("TOTALE: 1 h 00 min", File.ReadAllText(store.ReportPath(2026, 11)));
        Assert.Contains("(sul gruppo)", Run(store, "ore novembre gruppo wpweb", Today).Rows.Select(r => r.Label));
    }

    [Theory]
    [InlineData("aggiungi 2h a plainest avantieri", 11, 1)]
    [InlineData("aggiungi 2h a plainest 14/10", 10, 14)]
    [InlineData("aggiungi 2h a plainest il 2026-10-20", 10, 20)]
    [InlineData("aggiungi 2h a plainest lunedì", 11, 2)]
    [InlineData("metti 2 ore e 30 minuti sul progetto plainest ieri sera", 11, 2)]
    [InlineData("segna 2h a plainest oggi", 11, 3)]
    [InlineData("ho lavorato 2h su plainest stamattina", 11, 3)]
    public void DaysAreRecognised(string text, int month, int day)
    {
        var store = Plainest();
        Assert.True(Run(store, text, Today).Changed);
        Assert.True(DaySeconds(store, "plainest", month, day) >= 2 * 3600);
    }

    [Fact]
    public void ADayInTheMiddleOfASentenceOnlyBelongsToItsClause()
    {
        var store = Plainest();
        Run(store, "aggiungi 1h a plainest ieri. aggiungi 2h a wpshop", Today);
        Assert.Equal(3600, DaySeconds(store, "plainest", 11, 2));
        Assert.Equal(7200, DaySeconds(store, "wpshop", 11, 3));
        Run(store, "aggiungi 1,5h a plainest e togli 30m da wpshop", Today);
        Assert.Equal(5400, DaySeconds(store, "plainest", 11, 3));
        Assert.Equal(5400, DaySeconds(store, "wpshop", 11, 3));
    }

    [Fact]
    public void NothingIsWrittenIfOneEntryIsNotUnderstood()
    {
        var store = Plainest();
        var answer = Run(store, "aggiungi 7h a plainest e 1h a fantasma ieri", Today);
        Assert.False(answer.Changed);
        Assert.Contains(answer.Rows, r => r.Value.Contains("fantasma", StringComparison.Ordinal));
        Assert.Empty(store.Log.Days);
        Assert.Contains("a quale", Run(store, "aggiungi 2h", Today).Rows[0].Value);
        Assert.Contains("non è ancora arrivato", Run(store, "aggiungi 2h a plainest 25/12/2026", Today).Rows[0].Value);
        Assert.Empty(store.Log.Days);
    }

    [Fact]
    public void OnlyRequestsAreTakenFromTheModel()
    {
        Assert.True(WorkPhrase.IsRequest("aggiungi 7h a plainest"));
        Assert.True(WorkPhrase.IsRequest("per favore metti 90 minuti su plainest"));
        Assert.False(WorkPhrase.IsRequest("quante ore ho lavorato a ottobre su plainest?"));
        Assert.False(WorkPhrase.IsRequest("aggiungi printer a Cliente A"));
        Assert.False(WorkPhrase.IsRequest("ore ottobre gruppo X"));
    }

    [Fact]
    public void ResettingClearsOnlyWhatIsAsked()
    {
        var store = Plainest();
        Run(store, "aggiungi 7h a plainest e 2h a wpshop e 1h al gruppo wpweb ieri", Today);
        Assert.Equal(7 * 3600, store.Reset(["plainest"], []));
        var month = store.Report(WorkPeriod.Month(2026, 11));
        Assert.Equal(0, month.Projects.First(p => p.Name == "plainest").Seconds);
        Assert.Equal(2 * 3600, month.Projects.First(p => p.Name == "wpshop").Seconds);
        Assert.Equal(3 * 3600, month.Groups.First(g => g.Name == "wpweb").Seconds);

        Assert.Equal(3 * 3600, store.Reset(["wpshop"], ["wpweb"])); // il gruppo con le sue ore dirette
        Assert.Equal(0, store.Report(WorkPeriod.Month(2026, 11)).TotalSeconds);
        Assert.Empty(store.Log.Days);
        Assert.Contains("TOTALE: 0 min", File.ReadAllText(store.ReportPath(2026, 11)));
        Assert.Empty(new WorkStore(_folder).Log.Days); // è su disco
        Assert.Equal(0, store.Reset(["plainest"], []));
    }

    [Fact]
    public void ResettingAPastMonthRewritesItsFileForTheAssistant()
    {
        var store = Plainest();
        Run(store, "aggiungi 2h a plainest 14/03/2026", Today);
        Assert.Contains("TOTALE: 2 h 00 min", File.ReadAllText(store.ReportPath(2026, 3)));
        store.Reset(["plainest"], []);
        Assert.Contains("TOTALE: 0 min", File.ReadAllText(store.ReportPath(2026, 3)));
    }

    [Fact]
    public void WordsBetweenTheDurationAndTheNameDoNotMatter()
    {
        var store = Plainest();
        var answer = Run(store, "aggiungi le 7h passate ieri sul progetto Plainest e 1h passata su progetti del gruppo WpWeb sempre di ieri", Today);
        Assert.True(answer.Changed, string.Join(" | ", answer.Rows.Select(r => r.Value)));
        Assert.Equal(7 * 3600, DaySeconds(store, "plainest", 11, 2));
        Assert.Equal(3600, DaySeconds(store, WorkLog.GroupKey("wpweb"), 11, 2));
        Assert.Equal(0, DaySeconds(store, "plainest", 11, 3));
    }

    [Fact]
    public void TheKindWordMustMatchTheName()
    {
        var store = Plainest();
        Assert.Contains("è un progetto, non un gruppo", Run(store, "aggiungi 1h al gruppo plainest", Today).Rows[0].Value);
        Assert.Contains("è un gruppo, non un progetto", Run(store, "aggiungi 1h sul progetto wpweb", Today).Rows[0].Value);
        Assert.Empty(store.Log.Days);
        Assert.Contains("non trovo un progetto o gruppo", Run(store, "aggiungi 1h per via della riunione", Today).Rows[0].Value);
    }

    [Fact]
    public void ANameInTheNextEntryIsNotTakenByThePreviousOne()
    {
        var store = Plainest();
        Run(store, "aggiungi 2h e 1h a wpshop", Today); // la prima voce non ha un nome: niente da indovinare
        Assert.Empty(store.Log.Days);
    }

    [Fact]
    public void CalendarColoursTheDaysWithHoursAndFiltersByGroup()
    {
        var store = Plainest();
        Run(store, "aggiungi 7h a plainest e 1h al gruppo wpweb e 2h a wpshop ieri", Today);
        Run(store, "aggiungi 4h a plainest", Today);
        Run(store, "aggiungi 3h a plainest 20/10", Today);

        var all = WorkCalendar.Month(store, null, 2026, 11);
        Assert.Equal([2, 3], all.Days.Keys.Order());
        Assert.Equal(10 * 3600, all.Days[2]);
        Assert.Equal(14 * 3600, all.Total);
        Assert.Equal(10 * 3600, all.Max);
        Assert.Equal(3 * 3600, WorkCalendar.Month(store, null, 2026, 10).Total);

        var group = WorkCalendar.Month(store, "wpweb", 2026, 11);
        Assert.Equal([2], group.Days.Keys.Order());
        Assert.Equal(3 * 3600, group.Total); // wpshop 2h + 1h sul gruppo, senza plainest
        Assert.Equal(0, WorkCalendar.Month(store, "fantasma", 2026, 11).Total);
    }

    [Fact]
    public void DayDetailListsGroupsAndProjects()
    {
        var store = Plainest();
        Run(store, "aggiungi 7h a plainest e 1h al gruppo wpweb e 2h a wpshop ieri", Today);
        var yesterday = new DateOnly(2026, 11, 2);

        var all = WorkCalendar.Day(store, null, yesterday);
        Assert.Equal(10 * 3600, all.Total);
        Assert.Contains(all.Rows, r => r.Label == "wpweb" && r.IsGroup && r.Value == "3 h 00 min");
        Assert.Contains(all.Rows, r => r.Label == "Cliente A" && r.IsGroup && r.Value == "9 h 00 min");
        Assert.Contains(all.Rows, r => r.Label == "plainest" && !r.IsGroup && r.Value == "7 h 00 min");

        var group = WorkCalendar.Day(store, "wpweb", yesterday);
        Assert.Equal(3 * 3600, group.Total);
        Assert.Equal(["wpshop", "(sul gruppo)"], group.Rows.Select(r => r.Label));
        Assert.Empty(WorkCalendar.Day(store, "wpweb", new DateOnly(2026, 11, 3)).Rows);
    }
}
