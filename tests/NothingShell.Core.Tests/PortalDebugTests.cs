using NothingShell.Core.PortalDebug;
using NothingShell.Core.Settings;

namespace NothingShell.Core.Tests;

public class PortalDebugTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);

    private static DebugRequest Req(long seq, string path = "/", int status = 200, double ms = 50, string? rid = null, int sql = 0, double secondsAgo = 0) =>
        new(seq, Now.AddSeconds(-secondsAgo), rid ?? $"r{seq}", "GET", path, status, ms, SqlCount: sql);

    private static DebugCrash Crash(long seq, string type = "KeyError", string msg = "'id'", string file = "/srv/portale/core/views.py", int line = 42, string? rid = null) =>
        new(seq, Now, rid ?? $"r{seq}", type, msg,
            [new DebugFrame("/venv/django/core/handlers.py", 10, "inner", null, false), new DebugFrame(file, line, "ordine", "x = d['id']", true)],
            "GET", "/ordini/12");

    // ── Protocollo ──────────────────────────────────────────────────────────────

    [Fact]
    public void ParsesAPageOfEveryKind()
    {
        const string json = """
        {"boot":"b1","seq":45,"events":[
          {"seq":41,"kind":"request","t":1794312000.25,"rid":"a","method":"post","path":"/ordini/12","status":500,"ms":182.4,
           "size":1234,"view":"core.views.ordine","user":"andrea","ip":"127.0.0.1","sql":{"count":12,"ms":40.1},
           "headers":{"Accept":"text/html","Authorization":"[nascosto]"}},
          {"seq":42,"kind":"log","t":1794312000.3,"rid":"a","level":"warning","logger":"core","msg":"lento"},
          {"seq":43,"kind":"error","rid":"a","type":"KeyError","msg":"'id'","path":"/ordini/12","method":"post",
           "frames":[{"file":"django/core.py","line":"10","func":"inner"},{"file":"core/views.py","line":42,"func":"ordine","code":"x","app":true}]},
          {"seq":44,"kind":"sql","rid":"a","sql":"SELECT 1","ms":"312.5","db":"default"},
          {"seq":45,"kind":"custom","name":"ordine creato","data":{"id":12,"totale":"9.50"}},
          {"seq":46,"kind":"sconosciuto"},
          "rotto"
        ]}
        """;
        var page = DebugProtocol.ParseEvents(json)!;

        Assert.Equal("b1", page.Boot);
        Assert.Equal(45, page.Seq);
        Assert.Equal(5, page.Events.Count);
        var request = Assert.IsType<DebugRequest>(page.Events[0]);
        Assert.Equal("POST", request.Method);
        Assert.Equal(500, request.Status);
        Assert.Equal(12, request.SqlCount);
        Assert.Equal(40.1, request.SqlMs, 3);
        Assert.True(request.IsProblem);
        Assert.Equal("[nascosto]", request.RequestHeaders!["authorization"]);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1794312000250), request.Time);
        var log = Assert.IsType<DebugLogLine>(page.Events[1]);
        Assert.Equal("WARNING", log.Level);
        Assert.True(log.IsProblem);
        var crash = Assert.IsType<DebugCrash>(page.Events[2]);
        Assert.Equal(10, crash.Frames[0].Line); // anche un numero scritto come testo
        Assert.Equal("core/views.py", crash.Culprit!.File);
        Assert.Equal("KeyError@core/views.py:ordine", crash.Fingerprint);
        Assert.Equal(312.5, Assert.IsType<DebugSql>(page.Events[3]).DurationMs);
        Assert.Equal("12", Assert.IsType<DebugCustom>(page.Events[4]).Data["id"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("non json")]
    [InlineData("[1,2]")]
    public void ABadPageIsNull(string json) => Assert.Null(DebugProtocol.ParseEvents(json));

    [Fact]
    public void ParsesPairAndStatus()
    {
        var pair = DebugProtocol.ParsePair("""{"token":"abc","portal":{"name":"sinapsi_web","framework":"Django 5.2","connectors":["requests","logs"],"debug":true}}""")!;
        Assert.Equal("abc", pair.Token);
        Assert.Equal("sinapsi_web", pair.Info.Name);
        Assert.Equal(["requests", "logs"], pair.Info.Connectors);
        Assert.True(pair.Info.DebugMode);
        Assert.Null(DebugProtocol.ParsePair("""{"error":"codice sbagliato"}"""));

        var status = DebugProtocol.ParseStatus("""
            {"portal":{"name":"p","uptime":12.5,"pid":4242},"checks":[
              {"name":"Coda email","value":3,"state":"ok"},
              {"name":"Cache","value":{"hit":0.4},"state":"warn","detail":"poche risposte dalla cache"},
              {"name":"Pagamenti","value":"timeout","state":"error"},
              {"value":"senza nome"}]}
            """)!;
        Assert.Equal(4242, status.Info.Pid);
        Assert.Equal(3, status.Checks.Count);
        Assert.Equal("3", status.Checks[0].Value);
        Assert.Equal(CheckState.Warning, status.Checks[1].State);
        Assert.Contains("hit", status.Checks[1].Value, StringComparison.Ordinal);
        Assert.Equal(CheckState.Failing, status.Checks[2].State);
    }

    [Theory]
    [InlineData("k7q2-9xmp", "K7Q29XMP")]
    [InlineData(" K7Q2 9XMP ", "K7Q29XMP")]
    [InlineData("K7Q29XM", null)] // corto
    [InlineData("K7Q2-9XM0", null)] // 0 non è nell'alfabeto
    [InlineData("", null)]
    public void NormalizesPairingCodes(string text, string? expected) => Assert.Equal(expected, PairingCode.Normalize(text));

    [Fact]
    public void FormatsPairingCodes() => Assert.Equal("K7Q2-9XMP", PairingCode.Format("K7Q29XMP"));

    [Theory]
    [InlineData("localhost:8000", "http://localhost:8000")]
    [InlineData("http://127.0.0.1:8000/", "http://127.0.0.1:8000")]
    [InlineData("https://dev.sinapsi.it/portale/_nothing/", "https://dev.sinapsi.it/portale")]
    [InlineData("http://localhost:8000/ordini/?x=1", "http://localhost:8000/ordini")]
    [InlineData("ftp://host", null)]
    [InlineData("   ", null)]
    public void NormalizesAddresses(string text, string? expected) => Assert.Equal(expected, PortalAddress.Normalize(text));

    [Fact]
    public void SplitsAddressAndCodeFromOneLine()
    {
        Assert.Equal(("http://127.0.0.1:8000", "K7Q29XMP"), PortalAddress.Split("http://127.0.0.1:8000 K7Q2-9XMP"));
        Assert.Equal(("http://localhost:8000", "K7Q29XMP"), PortalAddress.Split("nothing://localhost:8000/K7Q2-9XMP"));
        Assert.Equal(("http://localhost:8000", null), PortalAddress.Split("localhost:8000"));
    }

    [Fact]
    public void KnowsLocalPortals()
    {
        Assert.True(PortalAddress.IsLocal("http://127.0.0.1:8000"));
        Assert.True(PortalAddress.IsLocal("http://localhost:8000"));
        Assert.False(PortalAddress.IsLocal("https://dev.sinapsi.it"));
    }

    [Fact]
    public void BuildsEndpoints()
    {
        Assert.Equal("http://localhost:8000/_nothing/events?after=12&limit=500", DebugProtocol.EventsEndpoint("http://localhost:8000", 12).ToString());
        Assert.Equal("https://h/base/_nothing/pair", DebugProtocol.Endpoint("https://h/base/", "/pair").ToString());
    }

    // ── Registro ────────────────────────────────────────────────────────────────

    [Fact]
    public void KeepsOnlyNewEventsAndFollowsTheCursor()
    {
        var log = new DebugLog();
        Assert.Equal(2, log.Add(new DebugPage("b1", 2, [Req(1), Req(2)])).Count);
        Assert.Equal(2, log.Cursor);
        var again = log.Add(new DebugPage("b1", 3, [Req(2), Req(3)]));
        Assert.Single(again);
        Assert.Equal(3, log.Count);
    }

    [Fact]
    public void ARestartResetsTheCursorButKeepsTheEvents()
    {
        var log = new DebugLog();
        log.Add(new DebugPage("b1", 50, [Req(50)]));
        var added = log.Add(new DebugPage("b2", 1, [Req(1)]));
        Assert.Single(added);
        Assert.Equal(1, log.Restarts);
        Assert.Equal(1, log.Cursor);
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public void DropsTheOldestBeyondCapacity()
    {
        var log = new DebugLog(capacity: 3);
        log.Add(new DebugPage("b", 5, [Req(1), Req(2), Req(3), Req(4), Req(5)]));
        Assert.Equal(3, log.Count);
        Assert.Equal(3, log.Events.First().Seq);
    }

    [Fact]
    public void ComputesStats()
    {
        var log = new DebugLog();
        log.Add(new DebugPage("b", 0,
        [
            Req(1, ms: 10, sql: 2), Req(2, ms: 20, sql: 4), Req(3, status: 404, ms: 30), Req(4, status: 500, ms: 900),
            Req(5, ms: 5000, secondsAgo: 600), // fuori dalla finestra
            Crash(6),
            new DebugLogLine(7, Now, null, "WARNING", "core", "piano"),
        ]));
        var stats = log.Stats(Now, TimeSpan.FromMinutes(5));
        Assert.Equal(4, stats.Requests);
        Assert.Equal(0.8, stats.RequestsPerMinute, 3);
        Assert.Equal(1, stats.ServerErrors);
        Assert.Equal(1, stats.ClientErrors);
        Assert.Equal(1, stats.Exceptions);
        Assert.Equal(1, stats.Warnings);
        Assert.Equal(20, stats.P50Ms);
        Assert.Equal(900, stats.P95Ms);
        Assert.Equal(1.5, stats.SqlPerRequest);
        Assert.Equal(0.25, stats.ServerErrorRate);
    }

    [Theory]
    [InlineData("/ordini/12", "/ordini/{id}")]
    [InlineData("/ordini/12/righe/7?x=1", "/ordini/{id}/righe/{id}")]
    [InlineData("/file/3f2504e0-4f89-11d3-9a0c-0305e82c3301", "/file/{id}")]
    [InlineData("/api/v2/clienti", "/api/v2/clienti")]
    [InlineData("/", "/")]
    public void GroupsPathsByPattern(string path, string expected) => Assert.Equal(expected, DebugLog.RoutePattern(path));

    [Fact]
    public void RanksRoutesBySlowness()
    {
        var log = new DebugLog();
        log.Add(new DebugPage("b", 0, [Req(1, "/ordini/1", ms: 100), Req(2, "/ordini/2", ms: 300, status: 500), Req(3, "/home", ms: 20)]));
        var routes = log.Routes(Now, TimeSpan.FromMinutes(5));
        Assert.Equal("GET /ordini/{id}", routes[0].Route);
        Assert.Equal(2, routes[0].Count);
        Assert.Equal(1, routes[0].ServerErrors);
        Assert.Equal(300, routes[0].P95Ms);
    }

    [Fact]
    public void GroupsErrorsByFingerprint()
    {
        var log = new DebugLog();
        log.Add(new DebugPage("b", 0, [Crash(1, msg: "'id'"), Crash(2, msg: "'nome'"), Crash(3, type: "ValueError")]));
        Assert.Equal(2, log.ErrorGroups.Count);
        Assert.Equal(2, log.ErrorGroups.Single(g => g.Fingerprint.StartsWith("KeyError", StringComparison.Ordinal)).Count);
    }

    [Fact]
    public void FiltersAndTraces()
    {
        var log = new DebugLog();
        log.Add(new DebugPage("b", 0,
        [
            Req(1, "/ordini/1", rid: "x"), new DebugSql(2, Now, "x", "SELECT * FROM ordini", 5), Req(3, "/home", status: 404, rid: "y"),
            Crash(4, rid: "x"),
        ]));
        Assert.Single(log.Find(new DebugFilter(DebugEventKind.Request, "ordini")));
        Assert.Equal(2, log.Find(new DebugFilter(ProblemsOnly: true)).Count); // la 404 e l'errore
        Assert.Single(log.Find(new DebugFilter(Text: "views.py"))); // cerca anche nei file dello stack
        Assert.Equal([1L, 2, 4], log.Trace("x").Select(e => e.Seq));
        Assert.Equal(4, log.Find(new DebugFilter())[0].Seq); // dal più recente
    }

    // ── Attenzione ──────────────────────────────────────────────────────────────

    [Fact]
    public void AlertsOnceForEachNewError()
    {
        var watch = new DebugWatch();
        var rules = DebugAlertRules.Default;
        var first = watch.OnEvents([Crash(1)], rules, Now);
        var alert = Assert.Single(first);
        Assert.Equal(DebugAlertKind.NewError, alert.Kind);
        Assert.Contains("views.py:42 in ordine", alert.Detail, StringComparison.Ordinal);
        Assert.Empty(watch.OnEvents([Crash(2, msg: "'altro'")], rules, Now.AddSeconds(1)));
        Assert.Single(watch.OnEvents([Crash(3, type: "ValueError")], rules, Now.AddSeconds(2)));
    }

    [Fact]
    public void TheFirstRoundLearnsWithoutAlerting()
    {
        var watch = new DebugWatch();
        Assert.Empty(watch.OnEvents([Crash(1), Req(2, status: 500), Req(3, ms: 9000)], DebugAlertRules.Default, Now, initial: true));
        Assert.Empty(watch.OnEvents([Crash(4)], DebugAlertRules.Default, Now.AddSeconds(5)));
    }

    [Fact]
    public void AServerErrorWithItsOwnNewErrorAlertsOnce()
    {
        var watch = new DebugWatch();
        var alerts = watch.OnEvents([Req(1, status: 500, rid: "z"), Crash(2, rid: "z")], DebugAlertRules.Default, Now);
        Assert.Equal(DebugAlertKind.NewError, Assert.Single(alerts).Kind);
    }

    [Fact]
    public void ServerErrorsAndSlowRequestsRespectTheCooldown()
    {
        var watch = new DebugWatch();
        var rules = DebugAlertRules.Default with { CooldownSeconds = 60, SlowMs = 1000 };
        var alerts = watch.OnEvents([Req(1, status: 502), Req(2, status: 503), Req(3, ms: 1500)], rules, Now);
        Assert.Equal([DebugAlertKind.ServerError, DebugAlertKind.Slow], alerts.Select(a => a.Kind));
        Assert.Contains("e altre 1", alerts[0].Detail, StringComparison.Ordinal);
        Assert.Empty(watch.OnEvents([Req(4, status: 500), Req(5, ms: 2000)], rules, Now.AddSeconds(30)));
        Assert.Equal(2, watch.OnEvents([Req(6, status: 500), Req(7, ms: 2000)], rules, Now.AddSeconds(61)).Count);
    }

    [Fact]
    public void RulesCanBeTurnedOff()
    {
        var watch = new DebugWatch();
        var off = new DebugAlertRules(NewErrors: false, ServerErrors: false, SlowRequests: false);
        Assert.Empty(watch.OnEvents([Crash(1), Req(2, status: 500), Req(3, ms: 99999)], off, Now));
    }

    [Fact]
    public void ErrorLogsAlertOnlyWhenAsked()
    {
        var line = new DebugLogLine(1, Now, null, "ERROR", "pagamenti", "gateway non risponde");
        Assert.Empty(new DebugWatch().OnEvents([line], DebugAlertRules.Default, Now));
        var alert = Assert.Single(new DebugWatch().OnEvents([line], DebugAlertRules.Default with { ErrorLogs = true }, Now));
        Assert.Equal("LOG ERROR", alert.Title);
    }

    [Fact]
    public void SaysDownAfterThreeFailuresAndBackUpOnce()
    {
        var watch = new DebugWatch();
        var rules = DebugAlertRules.Default;
        Assert.Null(watch.OnReach(PortalReach.Online, rules, Now));
        Assert.Null(watch.OnReach(PortalReach.Offline, rules, Now));
        Assert.Null(watch.OnReach(PortalReach.Offline, rules, Now));
        Assert.Equal(DebugAlertKind.Down, watch.OnReach(PortalReach.Offline, rules, Now)!.Kind);
        Assert.Null(watch.OnReach(PortalReach.Offline, rules, Now));
        Assert.Equal(DebugAlertKind.BackUp, watch.OnReach(PortalReach.Online, rules, Now)!.Kind);
        Assert.Null(watch.OnReach(PortalReach.Online, rules, Now));
    }

    [Fact]
    public void APortalAlreadyDownAtStartIsNotAnAlarm()
    {
        var watch = new DebugWatch();
        for (var i = 0; i < 5; i++)
            Assert.Null(watch.OnReach(PortalReach.Offline, DebugAlertRules.Default, Now));
        Assert.Equal(DebugAlertKind.BackUp, watch.OnReach(PortalReach.Online, DebugAlertRules.Default, Now)!.Kind);
    }

    [Fact]
    public void ARejectedTokenAlertsOnce()
    {
        var watch = new DebugWatch();
        Assert.NotNull(watch.OnReach(PortalReach.Unauthorized, DebugAlertRules.Default, Now));
        Assert.Null(watch.OnReach(PortalReach.Unauthorized, DebugAlertRules.Default, Now));
    }

    [Fact]
    public void ChecksAlertWhenTheyGetWorse()
    {
        var watch = new DebugWatch();
        var rules = DebugAlertRules.Default;
        Assert.Empty(watch.OnChecks([new PortalCheck("Coda", "3", CheckState.Ok)], rules));
        Assert.Single(watch.OnChecks([new PortalCheck("Coda", "80", CheckState.Warning)], rules));
        Assert.Empty(watch.OnChecks([new PortalCheck("Coda", "90", CheckState.Warning)], rules));
        Assert.Equal("CONTROLLO ROTTO", Assert.Single(watch.OnChecks([new PortalCheck("Coda", "500", CheckState.Failing)], rules)).Title);
        Assert.Empty(watch.OnChecks([new PortalCheck("Coda", "2", CheckState.Ok)], rules));
    }

    // ── Impostazioni ────────────────────────────────────────────────────────────

    [Fact]
    public void PortalIdsAreUniqueSlugs()
    {
        var settings = DebugSettings.Default.WithPortal(new DebugPortal("sinapsi-web", "sinapsi_web", "http://localhost:8000"));
        Assert.Equal("sinapsi-web-2", settings.NewId("Sinapsi Web"));
        Assert.Equal("portale", settings.NewId("!!!"));
        Assert.Empty(settings.WithoutPortal("sinapsi-web").PortalList);
    }

    [Fact]
    public void DebugSettingsSurviveTheSettingsFile()
    {
        var settings = ShellSettings.Default with
        {
            Debug = new DebugSettings(true, [new DebugPortal("p", "Portale", "http://localhost:8000", Watch: false)], DebugAlertRules.Default with { SlowMs = 800 }),
        };
        var back = ShellSettingsStore.Deserialize(ShellSettingsStore.Serialize(settings));
        Assert.Equal("http://localhost:8000", back.DebugOrDefault().PortalList[0].Address);
        Assert.False(back.DebugOrDefault().PortalList[0].Watch);
        Assert.Equal(800, back.DebugOrDefault().AlertRules.SlowMs);
        Assert.DoesNotContain("token", ShellSettingsStore.Serialize(settings), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(10, "10 ms")]
    [InlineData(1500, "1.5 s")]
    public void FormatsDurations(double ms, string expected) => Assert.Equal(expected, DebugWatch.FormatMs(ms));

    [Fact]
    public void ShortensFiles() => Assert.Equal("core/views.py", DebugWatch.ShortFile(@"C:\dev\portale\core\views.py"));
}

public class PortalDebugRealPayloadTests
{
    /// <summary>La risposta vera di nothing_debug su un runserver (Django 5.2, Python 3.14).</summary>
    [Fact]
    public void ParsesTheStatusOfARealPortal()
    {
        const string json = """{"portal": {"name": "portale_demo", "framework": "Django 5.2.16 \u00b7 Python 3.14.3", "version": "1", "connectors": ["requests", "logs", "errors", "sql", "checks", "custom"], "debug": true, "uptime": 23.5, "pid": 32464, "root": "C:/portale"}, "checks": [{"name": "Database default", "value": "1 ms", "state": "ok", "detail": "sqlite", "ms": 0.8}, {"name": "Migrazioni", "value": "nessuna da applicare", "state": "ok", "detail": null, "ms": 2.9}]}""";
        var status = NothingShell.Core.PortalDebug.DebugProtocol.ParseStatus(json);
        Assert.NotNull(status);
        Assert.Equal(32464, status.Info.Pid);
        Assert.Equal(2, status.Checks.Count);
    }
}

public class DebugHighlightsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);

    private static DebugRequest Req(long seq, int status = 200, double ms = 50, string? rid = null, double minutesAgo = 0) =>
        new(seq, Now.AddMinutes(-minutesAgo), rid ?? $"r{seq}", "GET", $"/p/{seq}", status, ms);

    private static DebugCrash Crash(long seq, string type, string rid, double minutesAgo = 0) =>
        new(seq, Now.AddMinutes(-minutesAgo), rid, type, "x", [new DebugFrame("/srv/core/views.py", (int)seq, type.ToLowerInvariant(), null, true)]);

    private static DebugLog Log(params DebugEvent[] events)
    {
        var log = new DebugLog();
        log.Add(new DebugPage("b", 0, events));
        return log;
    }

    [Fact]
    public void ErrorsComeFirstAndTheirServerErrorIsNotRepeated()
    {
        var log = Log(Req(1, 500, rid: "a"), Crash(2, "KeyError", "a"), Req(3, 502), Req(4, ms: 4000));
        var items = DebugHighlights.For(log, null, DebugAlertRules.Default, Now);
        Assert.Equal([HighlightKind.Error, HighlightKind.ServerError, HighlightKind.Slow], items.Select(i => i.Kind));
        Assert.Equal("502 GET /p/3", items[1].Text);
        Assert.Equal("4.0 s", items[2].Detail);
        Assert.Same(log.Events.OfType<DebugCrash>().Single(), items[0].Event);
    }

    [Fact]
    public void OldThingsAreLeftToTheDashboard()
    {
        var log = Log(Crash(1, "KeyError", "a", minutesAgo: 45), Req(2, 500, minutesAgo: 45));
        Assert.Empty(DebugHighlights.For(log, null, DebugAlertRules.Default, Now));
    }

    [Fact]
    public void ManyErrorsBecomeTwoAndACount()
    {
        var log = Log(Crash(1, "AError", "a", 3), Crash(2, "BError", "b", 2), Crash(3, "CError", "c", 1), Crash(4, "DError", "d"));
        var items = DebugHighlights.For(log, null, DebugAlertRules.Default, Now);
        Assert.Equal(3, items.Count);
        Assert.StartsWith("DError", items[0].Text, StringComparison.Ordinal);
        Assert.Equal("altri 2 errori diversi", items[2].Text);
    }

    [Fact]
    public void RedThingsWinWhenThereIsNoRoom()
    {
        var log = Log(Crash(1, "AError", "a"), Crash(2, "BError", "b"), Crash(3, "CError", "c"), Req(4, ms: 9000));
        var status = new PortalStatus(new PortalInfo("p"), [new PortalCheck("Coda", "80", CheckState.Warning), new PortalCheck("Pagamenti", "giù", CheckState.Failing)]);
        var items = DebugHighlights.For(log, status, DebugAlertRules.Default, Now);
        Assert.Equal(DebugHighlights.MaxItems, items.Count);
        Assert.Equal(("Pagamenti", "giù"), (items[^1].Text, items[^1].Detail)); // il controllo rotto resta, la lenta e quello da guardare escono
        Assert.DoesNotContain(items, i => i.Kind == HighlightKind.Slow);
    }

    [Fact]
    public void SummarisesTheLastFiveMinutes()
    {
        var log = Log(Req(1, ms: 100), Req(2, ms: 300), Crash(3, "KeyError", "x"));
        Assert.Equal("0.4/min · p95 300 ms · 1 errore", DebugHighlights.Summary(log, PortalReach.Online, null, false, Now));
        Assert.Equal("Non risponde", DebugHighlights.Summary(log, PortalReach.Offline, "Non risponde", false, Now));
        Assert.Equal("in pausa", DebugHighlights.Summary(log, PortalReach.Online, null, true, Now));
        Assert.Equal("nessuna richiesta negli ultimi 5 min", DebugHighlights.Summary(new DebugLog(), PortalReach.Online, null, false, Now));
    }

    [Fact]
    public void ABadPortalIsRed()
    {
        Assert.True(new DebugPortalView("p", "P", PortalReach.Offline, null, false, "", []).Bad);
        Assert.False(new DebugPortalView("p", "P", PortalReach.Online, null, false, "", [new DebugHighlight(HighlightKind.Slow, "x", null, null, null, false)]).Bad);
    }

    [Theory]
    [InlineData(20, "adesso")]
    [InlineData(300, "5 min fa")]
    [InlineData(7300, "2 h fa")]
    public void SaysHowLongAgo(int seconds, string expected) => Assert.Equal(expected, DebugHighlights.Ago(Now.AddSeconds(-seconds), Now));
}
