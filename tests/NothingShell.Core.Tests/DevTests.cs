using NothingShell.Core.Dev;

namespace NothingShell.Core.Tests;

public class CodeLocationTests
{
    [Fact]
    public void MsBuildErrorWithProjectSuffix()
    {
        const string output = """
            Determining projects to restore...
            src\Program.cs(42,13): warning CS0168: variabile dichiarata [C:\app\app.csproj]
            src\Program.cs(57,9): error CS1002: ; expected [C:\app\app.csproj]
            """;
        var error = CodeLocations.FirstError(output);
        Assert.NotNull(error);
        Assert.Equal(@"src\Program.cs", error.File);
        Assert.Equal(57, error.Line);
        Assert.Equal(9, error.Column);
        Assert.Equal("CS1002: ; expected", error.Message);
        Assert.Equal("Program.cs:57", error.Label);
    }

    [Fact]
    public void TypeScriptAndGccAndRust()
    {
        var tsc = CodeLocations.FirstError("src/app.ts(3,7): error TS2322: Type 'string' is not assignable to type 'number'.");
        Assert.Equal(("src/app.ts", 3, 7), (tsc!.File, tsc.Line, tsc.Column));

        var gcc = CodeLocations.FirstError("main.c: In function 'main':\nmain.c:10:5: error: expected ';' before 'return'");
        Assert.Equal(("main.c", 10, 5), (gcc!.File, gcc.Line, gcc.Column));

        var rust = CodeLocations.FirstError("error[E0425]: cannot find value `x` in this scope\n --> src/main.rs:3:5\n  |");
        Assert.Equal(("src/main.rs", 3, 5), (rust!.File, rust.Line, rust.Column));
        Assert.StartsWith("E0425:", rust.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoErrorInCleanOutput()
    {
        Assert.Null(CodeLocations.FirstError("Build succeeded.\n    0 Warning(s)\n    0 Error(s)"));
        Assert.False(CodeLocations.Any("ciao, come va? alle 10:30 in sala"));
    }

    [Fact]
    public void StackFramesOfDotNetJsAndPython()
    {
        const string dotnet = """
            System.NullReferenceException: Object reference not set to an instance of an object.
               at App.Service.Run() in C:\app\Service.cs:line 42
               in App.Program.Main() in C:\app\Program.cs:riga 7
            """;
        var frames = CodeLocations.Frames(dotnet);
        Assert.Equal([(@"C:\app\Service.cs", 42), (@"C:\app\Program.cs", 7)], frames.Select(f => (f.File, f.Line)));

        var js = CodeLocations.Frames("TypeError: x is undefined\n    at run (C:\\app\\node_modules\\lib\\a.js:1:2)\n    at C:\\app\\index.js:10:15");
        Assert.Equal(2, js.Count);
        Assert.Equal((@"C:\app\index.js", 10, 15), (js[1].File, js[1].Line, js[1].Column));

        const string python = """
            Traceback (most recent call last):
              File "C:\app\main.py", line 12, in <module>
                run()
              File "C:\app\worker.py", line 3, in run
                1 / 0
            ZeroDivisionError: division by zero
            """;
        var py = CodeLocations.Frames(python);
        Assert.Equal(@"C:\app\worker.py", py[0].File); // il più interno per primo
        Assert.Equal("ZeroDivisionError: division by zero", py[0].Message);
    }

    [Fact]
    public void PickSkipsLibrariesAndMissingFiles()
    {
        var text = "Error\n    at f (C:\\app\\node_modules\\x\\a.js:1:1)\n    at g (C:\\app\\gone.js:2:2)\n    at h (C:\\app\\src\\main.js:3:3)";
        var exists = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\app\node_modules\x\a.js", @"C:\app\src\main.js" };
        var pick = CodeLocations.Pick(text, null, exists.Contains);
        Assert.Equal(@"C:\app\src\main.js", pick!.File);
    }

    [Fact]
    public void RelativePathsResolveAgainstTheCommandFolder()
    {
        var pick = CodeLocations.Pick(@"src\Program.cs(1,1): error CS1: x", @"C:\app", p => p == @"C:\app\src\Program.cs");
        Assert.Equal(@"C:\app\src\Program.cs", pick!.File);
    }
}

public class TaskTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 9, 0, 0, TimeSpan.FromHours(1));

    [Fact]
    public void ParsesTheMessagesAndRejectsBadOnes()
    {
        var start = TaskProtocol.Parse("""{"event":"start","id":"a1","title":"dotnet build","cwd":"C:\\app","pid":42}""");
        Assert.Equal(new TaskMessage(TaskEvent.Start, "a1", "dotnet build", @"C:\app", 42), start);
        Assert.True(TaskProtocol.Parse("""{"Event":"DONE","title":"npm test","seconds":95.5,"exitCode":1,"quiet":true}""")!.Quiet);

        Assert.Null(TaskProtocol.Parse("non è json"));
        Assert.Null(TaskProtocol.Parse("""{"event":"esplodi"}"""));
        Assert.Null(TaskProtocol.Parse("""{"event":"start","id":"a1"}""")); // senza titolo
        Assert.Null(TaskProtocol.Parse("""{"event":"push"}""")); // senza cartella
        Assert.Null(TaskProtocol.Parse("[1,2]"));
    }

    [Fact]
    public void SerializeRoundTrips()
    {
        var message = new TaskMessage(TaskEvent.End, "a1", ExitCode: 3, Log: @"C:\t\task.log");
        Assert.Equal(message, TaskProtocol.Parse(TaskProtocol.Serialize(message)));
    }

    [Fact]
    public void StartThenEndGivesDuration()
    {
        var tracker = new TaskTracker();
        Assert.True(tracker.Start(new TaskMessage(TaskEvent.Start, "a1", "dotnet build"), T0));
        Assert.Single(tracker.Running);

        var finished = tracker.Finish(new TaskMessage(TaskEvent.End, "a1", ExitCode: 1), T0.AddSeconds(83));
        Assert.NotNull(finished);
        Assert.False(finished.Succeeded);
        Assert.Equal(TimeSpan.FromSeconds(83), finished.Duration);
        Assert.Empty(tracker.Running);
        Assert.Equal(finished, tracker.Recent[0]);
        Assert.Null(tracker.Finish(new TaskMessage(TaskEvent.End, "sconosciuto"), T0)); // end senza start
    }

    [Fact]
    public void DoneFromThePromptHook()
    {
        var tracker = new TaskTracker();
        var finished = tracker.Finish(new TaskMessage(TaskEvent.Done, Title: "npm test", Seconds: 40, Quiet: true), T0);
        Assert.Equal(TimeSpan.FromSeconds(40), finished!.Duration);
        Assert.True(finished.Succeeded);
        Assert.True(finished.Quiet);
    }

    [Fact]
    public void HistoryIsCappedAndNewestFirst()
    {
        var tracker = new TaskTracker();
        for (var i = 0; i < TaskTracker.HistoryCapacity + 3; i++)
            tracker.Finish(new TaskMessage(TaskEvent.Done, Title: $"c{i}"), T0.AddMinutes(i));
        Assert.Equal(TaskTracker.HistoryCapacity, tracker.Recent.Count);
        Assert.Equal($"c{TaskTracker.HistoryCapacity + 2}", tracker.Recent[0].Title);
    }

    [Fact]
    public void PruneDropsDeadTerminalsAndOldTasks()
    {
        var tracker = new TaskTracker();
        tracker.Start(new TaskMessage(TaskEvent.Start, "vivo", "a", Pid: 1), T0);
        tracker.Start(new TaskMessage(TaskEvent.Start, "morto", "b", Pid: 2), T0);
        Assert.True(tracker.Prune(T0.AddMinutes(1), t => t.Pid == 1));
        Assert.Equal("vivo", tracker.Running.Single().Id);
        Assert.True(tracker.Prune(T0 + TaskTracker.MaxRunning + TimeSpan.FromSeconds(1), _ => true));
        Assert.Empty(tracker.Running);
    }

    [Theory]
    [InlineData(12, "12 s")]
    [InlineData(83, "1:23")]
    [InlineData(3723, "1:02:03")]
    public void Durations(int seconds, string expected) =>
        Assert.Equal(expected, TaskTracker.FormatDuration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void ElapsedAndAgo()
    {
        Assert.Equal("0:07", TaskTracker.FormatElapsed(TimeSpan.FromSeconds(7.9)));
        Assert.Equal("adesso", TaskTracker.FormatAgo(T0, T0.AddSeconds(30)));
        Assert.Equal("5 min fa", TaskTracker.FormatAgo(T0, T0.AddMinutes(5)));
        Assert.Equal("2 h fa", TaskTracker.FormatAgo(T0, T0.AddHours(2)));
    }
}

public class CiRunsTests
{
    private static readonly DateTimeOffset Push = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private const string Json = """
        [
          {"databaseId": 3, "status": "in_progress", "conclusion": "", "name": "fix", "workflowName": "test", "url": "https://github.com/a/b/actions/runs/3", "createdAt": "2026-10-28T09:00:20Z"},
          {"databaseId": 2, "status": "completed", "conclusion": "success", "name": "fix", "workflowName": "build", "url": "https://github.com/a/b/actions/runs/2", "createdAt": "2026-10-28T09:00:15Z"},
          {"databaseId": 1, "status": "completed", "conclusion": "failure", "name": "vecchio", "workflowName": "build", "url": "u", "createdAt": "2026-10-27T18:00:00Z"}
        ]
        """;

    [Fact]
    public void OnlyRunsAfterThePushCount()
    {
        var runs = CiRuns.Since(CiRuns.Parse(Json), Push);
        Assert.Equal(["build", "test"], runs.Select(r => r.Name));
        Assert.Equal(CiState.Pending, CiRuns.State(runs));
    }

    [Fact]
    public void FailureWinsOnceAllAreCompleted()
    {
        var runs = new[]
        {
            new CiRun(1, "build", "completed", "success", "u", Push),
            new CiRun(2, "test", "completed", "failure", "u", Push),
            new CiRun(3, "lint", "completed", "skipped", "u", Push),
        };
        Assert.Equal(CiState.Failure, CiRuns.State(runs));
        Assert.Equal("test", CiRuns.Summary(runs));
        Assert.Equal(CiState.Success, CiRuns.State(runs.Where(r => r.Id != 2).ToArray()));
        Assert.Equal(CiState.Pending, CiRuns.State([]));
    }

    [Fact]
    public void BadJsonIsEmpty()
    {
        Assert.Empty(CiRuns.Parse("gh: command not found"));
        Assert.Empty(CiRuns.Parse("{}"));
    }

    [Fact]
    public void StatusNamesTheRepositoryFolder() =>
        Assert.Equal("nothing-shell", new CiStatus(@"C:\dev\nothing-shell\", "main", []).Repository);
}

public class ClipClassifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 9, 0, 0, TimeSpan.FromHours(1));

    private static ClipKind Classify(string text) => ClipClassifier.Classify(text, p => p == @"C:\dev\progetto");

    [Theory]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789", ClipKind.Secret)]
    [InlineData("export GITHUB_TOKEN=ghp_abcdefghijklmnopqrstuvwxyz0123456789", ClipKind.Secret)]
    [InlineData("AKIAIOSFODNN7EXAMPLE", ClipKind.Secret)]
    [InlineData("sk-ant-api03-abcdefghijklmnopqrstuvwxyz", ClipKind.Secret)]
    [InlineData("Server=db;Database=app;User Id=sa;Password=Segreta123;", ClipKind.Secret)]
    [InlineData("postgres://app:hunter22@localhost:5432/app", ClipKind.Secret)]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIE...", ClipKind.Secret)]
    [InlineData("{\"a\": [1, 2, {\"b\": null}]}", ClipKind.Json)]
    [InlineData("[1, 2, 3]", ClipKind.Json)]
    [InlineData("{ non json }", ClipKind.Text)]
    [InlineData("src\\Program.cs(57,9): error CS1002: ; expected", ClipKind.Code)]
    [InlineData("#FF3B30", ClipKind.Color)]
    [InlineData("#fff", ClipKind.Color)]
    [InlineData("rgba(255, 59, 48, 0.5)", ClipKind.Color)]
    [InlineData("1761638400", ClipKind.Timestamp)]
    [InlineData("1761638400000", ClipKind.Timestamp)]
    [InlineData("1234", ClipKind.Text)]
    [InlineData("https://github.com/LastMasterOfLife/Nothing_System", ClipKind.Url)]
    [InlineData("\"C:\\dev\\progetto\"", ClipKind.Path)]
    [InlineData("C:\\non\\esiste", ClipKind.Text)]
    [InlineData("ciao a tutti", ClipKind.Text)]
    public void Kinds(string text, ClipKind expected) => Assert.Equal(expected, Classify(text));

    [Fact]
    public void JwtIsDecodedWithExpiry()
    {
        // {"alg":"HS256","typ":"JWT"} . {"sub":"42","exp":1761645600}
        const string jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiI0MiIsImV4cCI6MTc2MTY0NTYwMH0.c2lnbmF0dXJh";
        Assert.Equal(ClipKind.Jwt, Classify(jwt));
        var token = ClipClassifier.Jwt(jwt)!;
        Assert.Contains("\"sub\": \"42\"", token.Payload, StringComparison.Ordinal);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1761645600), token.Expires);
        Assert.Equal("scaduto 365 giorni fa", ClipClassifier.Expiry(token, DateTimeOffset.FromUnixTimeSeconds(1761645600).AddDays(365)));
        Assert.Equal("scade tra 2 h", ClipClassifier.Expiry(token, DateTimeOffset.FromUnixTimeSeconds(1761645600 - 7200)));
        Assert.True(ClipClassifier.IsSensitive(ClipKind.Jwt));
    }

    [Fact]
    public void SecretsAreMasked()
    {
        Assert.Equal("CHIAVE GITHUB", ClipClassifier.SecretName("ghp_abcdefghijklmnopqrstuvwxyz0123456789"));
        Assert.Equal("ghp_••••6789", ClipClassifier.Mask("ghp_abcdefghijklmnopqrstuvwxyz0123456789"));
        Assert.Equal("Server=db; Password=••••;", ClipClassifier.Mask("Server=db; Password=Segreta123;"));
        Assert.DoesNotContain("hunter22", ClipClassifier.Mask("postgres://app:hunter22@localhost/app"), StringComparison.Ordinal);
    }

    [Fact]
    public void JsonFormatKeepsAccentsAndDuplicateKeys()
    {
        Assert.Equal("{\n  \"città\": \"Roma\",\n  \"a\": 1,\n  \"a\": 2\n}", ClipClassifier.Format("{\"città\":\"Roma\",\"a\":1,\"a\":2}").ReplaceLineEndings("\n"));
        Assert.Equal("[1,{\"b\":true}]", ClipClassifier.Minify("[ 1,\n { \"b\": true } ]"));
    }

    [Fact]
    public void ColorsConvert()
    {
        Assert.Equal("rgb(255, 59, 48)", ClipClassifier.Color("#FF3B30")!.Value.Rgb);
        Assert.Equal("#FFFFFF", ClipClassifier.Color("#fff")!.Value.Hex);
        Assert.Equal("#FF3B3080", ClipClassifier.Color("rgba(255, 59, 48, 0.5)")!.Value.Hex);
        Assert.Null(ClipClassifier.Color("rgb(300, 0, 0)"));
    }

    [Fact]
    public void HintsOnlyForUsefulKinds()
    {
        Assert.Equal("JSON - 1 KB", ClipClassifier.Hint(ClipKind.Json, "[1]", Now));
        Assert.Equal("ERRORE - Program.cs:57", ClipClassifier.Hint(ClipKind.Code, @"src\Program.cs(57,9): error CS1002: x", Now));
        Assert.Equal("#FF3B30", ClipClassifier.Hint(ClipKind.Color, "#ff3b30", Now));
        Assert.Equal("28/10/2025 09:00:00", ClipClassifier.Hint(ClipKind.Timestamp, "1761638400", Now));
        Assert.Null(ClipClassifier.Hint(ClipKind.Url, "https://x.it", Now));
    }

    [Fact]
    public void HistoryNeverKeepsSecretsAndCollapsesRepeats()
    {
        var history = new ClipboardHistory();
        history.Add("ciao", ClipKind.Text, Now);
        history.Add("ciao", ClipKind.Text, Now.AddSeconds(1));
        var secret = history.Add("ghp_abcdefghijklmnopqrstuvwxyz0123456789", ClipKind.Secret, Now.AddSeconds(2));
        Assert.Null(secret.Text);
        Assert.Equal(2, history.Entries.Count);
        Assert.Equal(ClipKind.Secret, history.Entries[0].Kind);
        for (var i = 0; i < ClipboardHistory.Capacity + 5; i++)
            history.Add($"t{i}", ClipKind.Text, Now);
        Assert.Equal(ClipboardHistory.Capacity, history.Entries.Count);
    }
}

public class DevServerTests
{
    private static PortOwner? Owner(int pid) => pid switch
    {
        4 => new PortOwner("System", true),
        100 => new PortOwner("svchost", true),
        200 => new PortOwner("node", false),
        300 => new PortOwner("postgres", true),
        400 => new PortOwner("Spotify", false),
        500 => new PortOwner("MyApi", false),
        600 => new PortOwner("com.docker.backend", false),
        700 => new PortOwner("FortiSSLVPNdaemon", true),
        _ => null,
    };

    [Fact]
    public void KeepsDevServersOncePerPortAndProcess()
    {
        PortListener[] listeners =
        [
            new(445, 4, false), new(135, 100, false), new(49664, 100, true),
            new(5173, 200, false), new(5173, 200, true), new(54000, 200, false),
            new(5432, 300, false), new(57621, 400, false), new(5000, 500, false), new(58000, 500, false),
            new(8080, 600, false), new(9999, 999, false), new(8053, 700, false),
        ];
        var servers = DevServers.Select(listeners, Owner);
        Assert.Equal([5000, 5173, 5432, 8080, 54000], servers.Select(s => s.Port));

        var postgres = servers.Single(s => s.Port == 5432);
        Assert.Equal("Postgres", postgres.Service);
        Assert.False(postgres.CanOpen);
        Assert.False(postgres.CanKill); // servizio di Windows
        Assert.False(servers.Single(s => s.Port == 8080).CanKill); // Docker
        Assert.True(servers.Single(s => s.Port == 5173).CanKill);
        Assert.Equal("porta effimera", DevServers.Exclusion(new(58000, 500, false), Owner(500)));
        Assert.Equal("app in background", DevServers.Exclusion(new(57621, 400, false), Owner(400)));
        Assert.Equal("servizio in background", DevServers.Exclusion(new(8053, 700, false), Owner(700)));
    }

    [Fact]
    public void AddedComparesPortAndProcess()
    {
        var before = new[] { new DevServer(5173, 200, "node", null, false) };
        var after = new[] { new DevServer(5173, 200, "node", null, false), new DevServer(3000, 201, "node", null, false) };
        Assert.Equal(3000, DevServers.Added(before, after).Single().Port);
        Assert.True(DevServers.Same(before, [new DevServer(5173, 200, "node", null, false)]));
    }
}

public class EditorCommandTests
{
    private static readonly CodeLocation Location = new(@"C:\app\Program.cs", 42, 0, null);

    [Fact]
    public void FillsThePlaceholders()
    {
        var (program, arguments) = EditorCommand.Build("code " + EditorCommand.GotoArguments, Location);
        Assert.Equal("code", program);
        Assert.Equal("--goto \"C:\\app\\Program.cs:42:1\"", arguments);
    }

    [Fact]
    public void QuotedProgram()
    {
        var (program, arguments) = EditorCommand.Build("\"C:\\Program Files\\Notepad++\\notepad++.exe\" -n{line} \"{file}\"", Location);
        Assert.Equal(@"C:\Program Files\Notepad++\notepad++.exe", program);
        Assert.Equal("-n42 \"C:\\app\\Program.cs\"", arguments);
    }
}
