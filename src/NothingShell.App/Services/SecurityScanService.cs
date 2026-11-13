using System.Diagnostics;
using System.IO;
using System.Text;
using NothingShell.Core.Network;
using NothingShell.Core.Security;
using NothingShell.Core.Storage;

namespace NothingShell.App.Services;

/// <summary>Cosa fare in una sessione: quali strumenti usare (oltre al recon, che c'è sempre), per quanto, dove scrivere il report.</summary>
public sealed record ScanOptions
{
    /// <summary>Gli id degli strumenti esterni da usare se installati (nuclei, zap, nikto, testssl). Il recon è sempre incluso.</summary>
    public IReadOnlyList<string> Tools { get; init; } = ["nuclei", "zap"];

    public bool IncludeSubdomains { get; init; }

    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Durata massima di un singolo strumento.</summary>
    public TimeSpan PerToolTimeout { get; init; } = TimeSpan.FromMinutes(8);

    public string OutputFolder { get; init; } = SecurityScanService.DefaultFolder;
}

/// <summary>
/// Orchestratore della Modalità Sicurezza (docs/26-sicurezza.md). Esegue, <b>solo contro l'host autorizzato</b> e in modo
/// non distruttivo, prima una ricognizione passiva (sempre, scritta nel Core), poi gli scanner esterni installati (nuclei,
/// OWASP ZAP, …) con flag prudenti; manda al notch un'istantanea dal vivo a ogni passo e, alla fine, scrive il report.
/// La logica, i modelli e i parser stanno nel Core puro e testato: qui c'è solo l'avvio dei processi e la rete.
/// </summary>
public sealed class SecurityScanService
{
    public static string DefaultFolder { get; } = Path.Combine(AtomicFile.DataFolder, "security");

    /// <summary>Dove la shell tiene gli scanner installati per conto suo (senza toccare il PATH): <c>~/.nothing-shell/tools</c>.</summary>
    public static string ToolsFolder { get; } = Path.Combine(AtomicFile.DataFolder, "tools");

    /// <summary>Gli strumenti che la modalità sa guidare, con il perché.</summary>
    public static IReadOnlyList<(string Id, string Exe, string Name, string Why)> Known { get; } =
    [
        ("nuclei", "nuclei", "nuclei", "Template di vulnerabilità note (CVE, misconfig), in sola rilevazione."),
        ("zap", "zap.bat", "OWASP ZAP", "Scansione baseline (passiva + attiva sicura) dell'app web, report JSON."),
        ("nikto", "nikto", "Nikto", "File e configurazioni rischiose note del server web."),
        ("testssl", "testssl.sh", "testssl.sh", "Protocolli e cifrari TLS deboli, vulnerabilità note del TLS."),
    ];

    /// <summary>Quali strumenti esterni sono installati su questo PC (il recon non ne ha bisogno).</summary>
    public static async Task<IReadOnlyList<ScanTool>> DetectToolsAsync(CancellationToken token)
    {
        var tools = new List<ScanTool>();
        foreach (var (id, exe, name, why) in Known)
        {
            var path = await LocateAsync(id, exe, token);
            tools.Add(new ScanTool(id, name, why, path is not null, path is not null ? await VersionAsync(id, path, token) : null, path));
        }
        return tools;
    }

    /// <summary>
    /// Esegue la sessione. <paramref name="session"/> deve avere un bersaglio già autorizzato: senza, fallisce e basta.
    /// <paramref name="onProgress"/> riceve un'istantanea a ogni cambiamento (per il notch).
    /// </summary>
    public async Task<SecuritySnapshot> RunAsync(SecuritySession session, ScanOptions options, Action<SecuritySnapshot> onProgress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(onProgress);

        var started = DateTimeOffset.Now;
        if (session.Target is not { } target || !session.IsAuthorized)
        {
            session.Fail("bersaglio non autorizzato: la sessione non può partire", started);
            onProgress(session.Snapshot());
            return session.Snapshot();
        }

        var tools = await DetectToolsAsync(token);
        var plan = BuildPlan(options, tools);
        session.Begin(plan, started);
        onProgress(session.Snapshot());

        var clock = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(options.MaxDuration);
        var cancel = budget.Token;

        try
        {
            foreach (var step in plan)
            {
                if (cancel.IsCancellationRequested)
                    break;
                session.StartStep(step.Id, clock.Elapsed, Activity(step));
                onProgress(session.Snapshot());
                try
                {
                    var (status, detail) = step.Tool switch
                    {
                        "recon" => await ReconAsync(session, target, options, clock, onProgress, cancel),
                        _ => await ExternalAsync(session, target, step, tools, options, clock, onProgress, cancel),
                    };
                    session.CompleteStep(step.Id, status, clock.Elapsed, detail);
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    session.CompleteStep(step.Id, StepStatus.Skipped, clock.Elapsed, "tempo massimo raggiunto");
                }
                catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or TimeoutException
                                               or System.ComponentModel.Win32Exception or InvalidOperationException or System.Net.Http.HttpRequestException)
                {
                    session.CompleteStep(step.Id, StepStatus.Error, clock.Elapsed, Short(ex.Message));
                }
                onProgress(session.Snapshot());
            }

            if (token.IsCancellationRequested)
                session.Abort(DateTimeOffset.Now);
            else
                session.Complete(DateTimeOffset.Now);
        }
        catch (OperationCanceledException)
        {
            session.Abort(DateTimeOffset.Now);
        }

        var snapshot = session.Snapshot();
        TryWriteReport(snapshot, tools, options, target);
        onProgress(snapshot);
        return snapshot;
    }

    /// <summary>Il percorso del report appena scritto, o null se non è stato possibile scriverlo.</summary>
    public string? LastReportPath { get; private set; }

    private static List<ScanStep> BuildPlan(ScanOptions options, IReadOnlyList<ScanTool> tools)
    {
        var steps = new List<ScanStep> { ScanPlan.Recon };
        foreach (var step in ScanPlan.All)
        {
            if (step.Tool == "recon" || !options.Tools.Contains(step.Tool))
                continue;
            if (tools.FirstOrDefault(t => t.Id == step.Tool)?.Installed == true)
                steps.Add(step);
        }
        return steps;
    }

    private static string Activity(ScanStep step) => step.Tool switch
    {
        "recon" => "Ricognizione: leggo intestazioni, TLS e cookie",
        "nuclei" => "nuclei: cerco vulnerabilità note",
        "zap" => "OWASP ZAP: scansione baseline",
        "nikto" => "Nikto: controllo il server web",
        "testssl" => "testssl.sh: analizzo il TLS",
        _ => step.Label,
    };

    // ── Ricognizione passiva (sempre, nessuno strumento esterno) ────────────────────
    private static async Task<(StepStatus, string?)> ReconAsync(SecuritySession session, SecurityTarget target, ScanOptions options,
        Stopwatch clock, Action<SecuritySnapshot> onProgress, CancellationToken token)
    {
        HttpHop hop;
        try
        {
            hop = await HttpProbe.SendAsync(target.Url, token, maxBody: 64 * 1024);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or TimeoutException or InvalidOperationException or InvalidDataException)
        {
            session.Fail($"bersaglio irraggiungibile: {Short(ex.Message)}", DateTimeOffset.Now);
            onProgress(session.Snapshot());
            throw new OperationCanceledException(); // ferma il piano: senza bersaglio non ha senso continuare
        }

        foreach (var finding in ReconChecks.FromResponse(hop.Url, hop.Status, hop.TlsProtocol, hop.Headers))
        {
            session.AddFinding(finding);
            session.NoteActivity($"Ricognizione: {finding.Title}");
            onProgress(session.Snapshot());
        }

        // Se il bersaglio è HTTPS, prova anche l'HTTP in chiaro sullo stesso host (in scope): servito senza redirect = debolezza.
        if (target.Url.Scheme == "https")
        {
            var httpUrl = new UriBuilder(target.Url) { Scheme = "http", Port = -1 }.Uri;
            if (ScopeGuard.IsInScope(target, httpUrl.AbsoluteUri))
            {
                try
                {
                    var clear = await HttpProbe.SendAsync(httpUrl, token, maxBody: 4 * 1024);
                    if (clear.Status is >= 200 and < 300)
                    {
                        session.AddFinding(new Finding(Severity.High, "Contenuto servito in HTTP (non cifrato)", httpUrl.AbsoluteUri,
                            $"Risposta {clear.Status} su http:// senza redirect a HTTPS.",
                            "Reindirizza tutto l'HTTP all'HTTPS (301) e attiva HSTS.", "CWE-319"));
                        onProgress(session.Snapshot());
                    }
                }
                catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or TimeoutException or InvalidOperationException or InvalidDataException)
                {
                    // HTTP non raggiungibile: va bene così, nessuna debolezza
                }
            }
        }

        var n = session.Snapshot().Actions.FirstOrDefault(a => a.StepId == "recon")?.Findings ?? 0;
        return (StepStatus.Done, n == 0 ? "nessuna debolezza dalle intestazioni" : null);
    }

    // ── Strumenti esterni ───────────────────────────────────────────────────────────
    private static async Task<(StepStatus, string?)> ExternalAsync(SecuritySession session, SecurityTarget target, ScanStep step,
        IReadOnlyList<ScanTool> tools, ScanOptions options, Stopwatch clock, Action<SecuritySnapshot> onProgress, CancellationToken token)
    {
        if (tools.FirstOrDefault(t => t.Id == step.Tool) is not { Installed: true, Path: { } exe })
            return (StepStatus.Skipped, "non installato");

        using var perTool = CancellationTokenSource.CreateLinkedTokenSource(token);
        perTool.CancelAfter(options.PerToolTimeout);
        var url = target.Display;
        var output = new StringBuilder();

        switch (step.Tool)
        {
            case "nuclei":
            {
                // -jsonl su stdout; esclusi i tag distruttivi; limiti di ritmo e tempo. Niente fuzzing aggressivo.
                var args = new[]
                {
                    "-target", url, "-jsonl", "-silent", "-no-color", "-disable-update-check",
                    "-exclude-tags", "dos,intrusive,fuzz", "-rate-limit", "25", "-timeout", "10", "-retries", "1",
                };
                await RunProcessAsync(exe, args, line =>
                {
                    output.AppendLine(line);
                    var preview = Short(line, 70);
                    session.NoteActivity($"nuclei: {preview}");
                    onProgress(session.Snapshot());
                }, perTool.Token);
                foreach (var f in NucleiParser.Parse(output.ToString()).Where(f => ScopeGuard.IsInScope(target, f.Where, options.IncludeSubdomains) || f.Where.Length == 0))
                    session.AddFinding(f);
                break;
            }

            case "zap":
            {
                var json = Path.Combine(options.OutputFolder, $"zap-{Stamp()}.json");
                Directory.CreateDirectory(options.OutputFolder);
                // Scansione rapida baseline: passiva + attiva sicura, report JSON standard (site[].alerts[]).
                var args = new[] { "-cmd", "-quickurl", url, "-quickout", json, "-quickprogress" };
                await RunProcessAsync(exe, args, line =>
                {
                    session.NoteActivity($"ZAP: {Short(line, 70)}");
                    onProgress(session.Snapshot());
                }, perTool.Token);
                if (File.Exists(json))
                    foreach (var f in ZapParser.Parse(await File.ReadAllTextAsync(json, perTool.Token)))
                        session.AddFinding(f);
                break;
            }

            default:
            {
                // nikto, testssl: non hanno (ancora) un parser dedicato. Li eseguiamo in modo prudente, salviamo l'uscita grezza
                // accanto al report e la citiamo nell'azione; non inventiamo debolezze da testo non strutturato.
                var raw = Path.Combine(options.OutputFolder, $"{step.Tool}-{Stamp()}.txt");
                Directory.CreateDirectory(options.OutputFolder);
                var args = step.Tool == "nikto"
                    ? new[] { "-host", url, "-nointeractive", "-Tuning", "123bde", "-maxtime", ((int)options.PerToolTimeout.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture) }
                    : new[] { "--quiet", "--color", "0", url };
                await RunProcessAsync(exe, args, line =>
                {
                    output.AppendLine(line);
                    session.NoteActivity($"{step.Tool}: {Short(line, 70)}");
                    onProgress(session.Snapshot());
                }, perTool.Token);
                await File.WriteAllTextAsync(raw, output.ToString(), token);
                return (StepStatus.Done, $"uscita salvata in {Path.GetFileName(raw)}");
            }
        }

        var found = session.Snapshot().Actions.FirstOrDefault(a => a.StepId == step.Id)?.Findings ?? 0;
        return (StepStatus.Done, found == 0 ? "nessuna debolezza" : $"{found} trovate");
    }

    private void TryWriteReport(SecuritySnapshot snapshot, IReadOnlyList<ScanTool> tools, ScanOptions options, SecurityTarget target)
    {
        try
        {
            Directory.CreateDirectory(options.OutputFolder);
            var stem = SecurityReportName.Stem(target.Host, snapshot.StartedAt ?? DateTimeOffset.Now);
            // HTML (si apre nel browser, sempre associato) + Markdown (per chi lo preferisce e per la CLI).
            var html = Path.Combine(options.OutputFolder, stem + ".html");
            File.WriteAllText(html, SecurityReport.ToHtml(snapshot, tools));
            File.WriteAllText(Path.Combine(options.OutputFolder, stem + ".md"), SecurityReport.ToMarkdown(snapshot, tools));
            LastReportPath = html;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastReportPath = null;
        }
    }

    // ── Processi ─────────────────────────────────────────────────────────────────────
    private static async Task<int> RunProcessAsync(string exe, IReadOnlyList<string> args, Action<string> onLine, CancellationToken token)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args)
            start.ArgumentList.Add(a);

        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data is { } t && t.Length > 0) onLine(t); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is { } t && t.Length > 0) onLine(t); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            throw;
        }
        return process.ExitCode;
    }

    /// <summary>Dove sta uno strumento: prima i percorsi noti e la cartella degli strumenti della shell, poi il PATH.</summary>
    private static async Task<string?> LocateAsync(string id, string exe, CancellationToken token)
    {
        foreach (var candidate in Candidates(id))
            if (File.Exists(candidate))
                return candidate;
        var folder = Path.Combine(ToolsFolder, id == "zap" ? "ZAP" : id);
        if (Directory.Exists(folder))
        {
            try
            {
                if (Directory.EnumerateFiles(folder, Path.GetFileName(exe), SearchOption.AllDirectories).FirstOrDefault() is { } hit)
                    return hit;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // cartella illeggibile: si prova il PATH
            }
        }
        return await WhichAsync(exe, token);
    }

    /// <summary>Controllo sincrono e a buon mercato (solo file, niente processi né PATH) se uno strumento è a portata di mano.</summary>
    public static bool QuickPresent(string id)
    {
        foreach (var candidate in Candidates(id))
            if (File.Exists(candidate))
                return true;
        var folder = Path.Combine(ToolsFolder, id == "zap" ? "ZAP" : id);
        var name = id == "nuclei" ? "nuclei.exe" : "zap.bat";
        try
        {
            return Directory.Exists(folder) && Directory.EnumerateFiles(folder, name, SearchOption.AllDirectories).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> Candidates(string id) => id switch
    {
        "nuclei" => [Path.Combine(ToolsFolder, "nuclei", "nuclei.exe")],
        "zap" =>
        [
            @"C:\Program Files\ZAP\Zed Attack Proxy\zap.bat",
            @"C:\Program Files (x86)\ZAP\Zed Attack Proxy\zap.bat",
        ],
        _ => [],
    };

    private static async Task<string?> WhichAsync(string exe, CancellationToken token)
    {
        try
        {
            var start = new ProcessStartInfo("where.exe", exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(start);
            if (process is null)
                return null;
            var stdout = await process.StandardOutput.ReadToEndAsync(token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
            var first = stdout.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            return process.ExitCode == 0 && first is { Length: > 0 } ? first : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or IOException)
        {
            return null;
        }
    }

    private static async Task<string?> VersionAsync(string id, string exe, CancellationToken token)
    {
        if (id == "zap")
            return null; // zap.bat -version avvia la JVM: troppo caro solo per la versione
        var flag = id switch { "testssl" => "--version", _ => "-version" };
        try
        {
            var captured = new StringBuilder();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await RunProcessAsync(exe, [flag], l => { if (captured.Length < 400) captured.Append(l).Append(' '); }, timeout.Token);
            // via i codici colore ANSI (nuclei li usa) e, se c'è, tieni solo il numero di versione
            var text = System.Text.RegularExpressions.Regex.Replace(captured.ToString(), "\u001b\\[[0-9;]*m", string.Empty).Trim();
            var version = System.Text.RegularExpressions.Regex.Match(text, @"v?\d+\.\d+(\.\d+)?");
            return version.Success ? version.Value : text.Length == 0 ? null : Short(text, 60);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

    private static string Short(string? text, int max = 120)
    {
        var s = (text ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}
