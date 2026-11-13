using NothingShell.Core.Security;

namespace NothingShell.Core.Tests;

public class SecurityScopeTests
{
    [Theory]
    [InlineData("miosito.it", "https", "miosito.it")]
    [InlineData("http://x.local:8080/a", "http", "x.local")]
    [InlineData("HTTPS://Esempio.IT/Path", "https", "esempio.it")]
    [InlineData("localhost:8000", "http", "localhost")] // dev server: http di default
    [InlineData("127.0.0.1:5000/app", "http", "127.0.0.1")]
    [InlineData("https://localhost:8443", "https", "localhost")] // schema esplicito rispettato
    public void ParsesWebUrls(string input, string expectedScheme, string expectedHost)
    {
        var target = ScopeGuard.Parse(input);
        Assert.NotNull(target);
        Assert.Equal(expectedHost, target!.Host);
        Assert.Equal(expectedScheme, target.Url.Scheme);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://x.local")]
    [InlineData("file:///c:/x")]
    [InlineData("javascript:alert(1)")]
    public void RejectsNonWeb(string input) => Assert.Null(ScopeGuard.Parse(input));

    [Fact]
    public void SameHostStaysInScopeAcrossPaths()
    {
        var target = ScopeGuard.Parse("https://miosito.it/app")!;
        Assert.True(ScopeGuard.IsInScope(target, "https://miosito.it/app/login"));
        Assert.True(ScopeGuard.IsInScope(target, "http://miosito.it/other")); // schema diverso, stesso host: dentro
        Assert.False(ScopeGuard.IsInScope(target, "https://altro.com/"));
        Assert.False(ScopeGuard.IsInScope(target, "https://evil.miosito.it.attacker.com/"));
    }

    [Fact]
    public void SubdomainsGatedByFlag()
    {
        var target = ScopeGuard.Parse("https://miosito.it")!;
        Assert.False(ScopeGuard.IsInScope(target, "https://api.miosito.it/"));
        Assert.True(ScopeGuard.IsInScope(target, "https://api.miosito.it/", includeSubdomains: true));
        Assert.False(ScopeGuard.IsInScope(target, "https://notmiosito.it/", includeSubdomains: true));
    }

    [Fact]
    public void RejectsNonWebCandidates()
    {
        var target = ScopeGuard.Parse("https://miosito.it")!;
        Assert.False(ScopeGuard.IsInScope(target, "ftp://miosito.it/"));
        Assert.False(ScopeGuard.IsInScope(target, "not a url"));
    }
}

public class SecuritySessionTests
{
    private static readonly IReadOnlyList<ScanStep> Plan = [ScanPlan.Recon, ScanPlan.Nuclei];
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BeginRefusedWithoutAuthorization()
    {
        var session = new SecuritySession();
        session.SetTarget(ScopeGuard.Parse("https://miosito.it")!);
        Assert.Equal(SessionStatus.NeedsAuthorization, session.Status);
        Assert.False(session.Begin(Plan, T0));
        Assert.Equal(SessionStatus.NeedsAuthorization, session.Status);
    }

    [Fact]
    public void AuthorizeRequiresMatchingHost()
    {
        var session = new SecuritySession();
        session.SetTarget(ScopeGuard.Parse("https://miosito.it")!);
        Assert.False(session.Authorize(new ScanAuthorization("altro.com", T0)));
        Assert.False(session.IsAuthorized);
        Assert.True(session.Authorize(new ScanAuthorization("MioSito.it", T0)));
        Assert.True(session.IsAuthorized);
    }

    [Fact]
    public void FullLifecycleTracksActionsAndFindings()
    {
        var session = new SecuritySession();
        session.SetTarget(ScopeGuard.Parse("https://miosito.it")!);
        session.Authorize(new ScanAuthorization("miosito.it", T0));
        Assert.True(session.Begin(Plan, T0));
        Assert.Equal(SessionStatus.Running, session.Status);

        session.StartStep("recon", TimeSpan.FromSeconds(1), "Leggo le intestazioni");
        session.AddFinding(new Finding(Severity.Medium, "Manca CSP", "https://miosito.it", "x", "y"));
        session.AddFinding(new Finding(Severity.High, "HTTP in chiaro", "http://miosito.it", "x", "y"));
        session.CompleteStep("recon", StepStatus.Done, TimeSpan.FromSeconds(3));

        session.StartStep("nuclei", TimeSpan.FromSeconds(3));
        session.CompleteStep("nuclei", StepStatus.Skipped, TimeSpan.FromSeconds(3), "non installato");

        session.Complete(T0.AddSeconds(5));

        var snap = session.Snapshot();
        Assert.Equal(SessionStatus.Completed, snap.Status);
        Assert.Equal(2, snap.Total);
        Assert.Equal(2, snap.Done);
        Assert.Equal(2, snap.Findings.Count);
        Assert.Equal(2, snap.Actions.First(a => a.StepId == "recon").Findings);
        Assert.Equal(1, snap.Count(Severity.High));
        Assert.Equal(Severity.High, snap.Peak);
        Assert.Equal(Severity.High, snap.Ordered[0].Severity); // ordinato per gravità
        Assert.Equal(TimeSpan.FromSeconds(5), snap.Duration);
    }

    [Fact]
    public void AbortMarksPendingAndRunningAsSkipped()
    {
        var session = new SecuritySession();
        session.SetTarget(ScopeGuard.Parse("https://miosito.it")!);
        session.Authorize(new ScanAuthorization("miosito.it", T0));
        session.Begin(Plan, T0);
        session.StartStep("recon", TimeSpan.FromSeconds(1));
        session.Abort(T0.AddSeconds(2));

        var snap = session.Snapshot();
        Assert.Equal(SessionStatus.Aborted, snap.Status);
        Assert.All(snap.Actions, a => Assert.Equal(StepStatus.Skipped, a.Status));
    }

    [Fact]
    public void SetTargetResetsAPreviousRun()
    {
        var session = new SecuritySession();
        session.SetTarget(ScopeGuard.Parse("https://uno.it")!);
        session.Authorize(new ScanAuthorization("uno.it", T0));
        session.Begin(Plan, T0);
        session.AddFinding(new Finding(Severity.Low, "x", "y", "z", "w"));
        session.SetTarget(ScopeGuard.Parse("https://due.it")!);

        var snap = session.Snapshot();
        Assert.Equal(SessionStatus.NeedsAuthorization, snap.Status);
        Assert.Empty(snap.Findings);
        Assert.Empty(snap.Actions);
        Assert.False(session.IsAuthorized);
    }
}

public class ReconChecksTests
{
    private static IReadOnlyList<Finding> Run(string scheme, string? tls, params (string, string)[] headers)
    {
        var dict = headers.ToDictionary(h => h.Item1, h => h.Item2, StringComparer.OrdinalIgnoreCase);
        return ReconChecks.FromResponse(new Uri($"{scheme}://miosito.it/"), 200, tls, dict);
    }

    [Fact]
    public void BareHttpsResponseFlagsMissingHeaders()
    {
        var findings = Run("https", "Tls13");
        Assert.Contains(findings, f => f.Title.Contains("HSTS"));
        Assert.Contains(findings, f => f.Title.Contains("Content-Security-Policy"));
        Assert.Contains(findings, f => f.Title.Contains("clickjacking"));
        Assert.Contains(findings, f => f.Title.Contains("nosniff"));
        Assert.DoesNotContain(findings, f => f.Title.Contains("HTTP (non cifrato)"));
    }

    [Fact]
    public void HardenedResponseIsMostlyClean()
    {
        var findings = Run("https", "Tls13",
            ("strict-transport-security", "max-age=31536000; includeSubDomains"),
            ("content-security-policy", "default-src 'self'; frame-ancestors 'none'"),
            ("x-content-type-options", "nosniff"),
            ("referrer-policy", "strict-origin-when-cross-origin"));
        Assert.DoesNotContain(findings, f => f.Severity >= Severity.Medium);
    }

    [Fact]
    public void PlainHttpIsHighSeverity()
    {
        var findings = Run("http", null);
        Assert.Contains(findings, f => f.Severity == Severity.High && f.Title.Contains("HTTP"));
    }

    [Fact]
    public void OldTlsIsHighSeverity()
    {
        Assert.Contains(Run("https", "Tls11"), f => f.Severity == Severity.High && f.Title.Contains("TLS"));
        Assert.DoesNotContain(Run("https", "Tls12"), f => f.Title.Contains("TLS obsoleta"));
        Assert.DoesNotContain(Run("https", "Tls13"), f => f.Title.Contains("TLS obsoleta"));
    }

    [Fact]
    public void CookieWithoutFlagsIsFlagged()
    {
        var findings = Run("https", "Tls13", ("set-cookie", "sid=abc; Path=/"));
        Assert.Contains(findings, f => f.Title.Contains("HttpOnly"));
        Assert.Contains(findings, f => f.Title.Contains("Secure"));
        Assert.Contains(findings, f => f.Title.Contains("SameSite"));
    }

    [Fact]
    public void ServerBannerWithVersionIsFlagged()
    {
        var findings = Run("https", "Tls13", ("server", "nginx/1.18.0"));
        Assert.Contains(findings, f => f.Title.Contains("Versione del software"));
        // senza numeri non si segnala
        Assert.DoesNotContain(Run("https", "Tls13", ("server", "cloudflare")), f => f.Title.Contains("Versione del software"));
    }
}

public class ScanParserTests
{
    [Fact]
    public void NucleiJsonlParsedWithSeverityAndLocation()
    {
        const string jsonl = """
            {"template-id":"tech-detect","info":{"name":"Nginx","severity":"info"},"matched-at":"https://miosito.it"}
            not-json-noise
            {"template-id":"CVE-2021-1234","info":{"name":"RCE grave","severity":"critical","description":"esecuzione remota","remediation":"aggiorna","reference":["https://x"]},"matched-at":"https://miosito.it/api"}
            """;
        var findings = NucleiParser.Parse(jsonl);
        Assert.Equal(2, findings.Count);
        var crit = findings.Single(f => f.Severity == Severity.Critical);
        Assert.Equal("RCE grave", crit.Title);
        Assert.Equal("https://miosito.it/api", crit.Where);
        Assert.Equal("aggiorna", crit.Remediation);
        Assert.Equal("https://x", crit.Reference);
        Assert.Equal("nuclei", crit.Source);
    }

    [Fact]
    public void NucleiEmptyOrBlankGivesNothing()
    {
        Assert.Empty(NucleiParser.Parse(null));
        Assert.Empty(NucleiParser.Parse("   \n  \n"));
    }

    [Fact]
    public void ZapJsonParsedWithRiskAndHtmlStripped()
    {
        const string json = """
            {"site":[{"@name":"https://miosito.it","alerts":[
              {"alert":"Cross Site Scripting (Reflected)","riskcode":"3","desc":"<p>XSS <b>riflessa</b></p>","solution":"<p>Fai l'escape dell'output</p>","cweid":"79","instances":[{"uri":"https://miosito.it/s?q=1","evidence":"<script>"}]},
              {"alert":"Cookie senza flag","riskcode":"1","desc":"<p>manca Secure</p>","solution":"<p>Aggiungi Secure</p>","cweid":"614","instances":[]}
            ]}]}
            """;
        var findings = ZapParser.Parse(json);
        Assert.Equal(2, findings.Count);
        var xss = findings.Single(f => f.Title.Contains("Cross Site"));
        Assert.Equal(Severity.High, xss.Severity);
        Assert.Equal("https://miosito.it/s?q=1", xss.Where);
        Assert.Equal("CWE-79", xss.Reference);
        Assert.DoesNotContain("<", xss.Remediation);
        Assert.Equal("zap", xss.Source);
        Assert.Contains(findings, f => f.Severity == Severity.Low);
    }

    [Fact]
    public void ZapInvalidJsonGivesNothing() => Assert.Empty(ZapParser.Parse("{ not valid"));
}

public class SecurityReportTests
{
    [Fact]
    public void ReportHasTargetActionsAndRemediation()
    {
        var session = new SecuritySession();
        session.SetTarget(ScopeGuard.Parse("https://miosito.it")!);
        session.Authorize(new ScanAuthorization("miosito.it", DateTimeOffset.UnixEpoch));
        session.Begin([ScanPlan.Recon], DateTimeOffset.UnixEpoch);
        session.StartStep("recon", TimeSpan.Zero);
        session.AddFinding(new Finding(Severity.High, "HTTP in chiaro", "http://miosito.it", "risposta 200", "usa HTTPS", "CWE-319"));
        session.CompleteStep("recon", StepStatus.Done, TimeSpan.FromSeconds(2));
        session.Complete(DateTimeOffset.UnixEpoch.AddSeconds(2));

        var md = SecurityReport.ToMarkdown(session.Snapshot());
        Assert.Contains("https://miosito.it", md);
        Assert.Contains("## Azione per azione", md);
        Assert.Contains("Ricognizione passiva", md);
        Assert.Contains("[ALTA] HTTP in chiaro", md);
        Assert.Contains("Come si risolve", md);
        Assert.Contains("usa HTTPS", md);
        Assert.Contains("non distruttiva", md);
    }

    [Fact]
    public void ReportWithoutFindingsSaysSo()
    {
        var snap = SecuritySnapshot.Empty with { Status = SessionStatus.Completed, Target = ScopeGuard.Parse("https://miosito.it") };
        var md = SecurityReport.ToMarkdown(snap);
        Assert.Contains("Nessuna debolezza trovata", md);
    }

    [Fact]
    public void HtmlReportHasTargetAndFindingAndEscapes()
    {
        var snap = SecuritySnapshot.Empty with
        {
            Status = SessionStatus.Completed,
            Target = ScopeGuard.Parse("https://miosito.it"),
            Findings = [new Finding(Severity.High, "XSS <script>", "https://miosito.it/s?q=1", "prova", "fai l'escape")],
        };
        var html = SecurityReport.ToHtml(snap);
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("miosito.it", html);
        Assert.Contains("fai l'escape", html);
        Assert.Contains("XSS &lt;script&gt;", html); // il testo è sfuggito, non iniettato
        Assert.DoesNotContain("<script>", html);
    }
}

public class SecurityReportNameTests
{
    [Fact]
    public void StemAndParseRoundTrip()
    {
        var at = new DateTimeOffset(2026, 11, 13, 22, 42, 53, TimeSpan.Zero).ToLocalTime();
        var stem = SecurityReportName.Stem("lastmasteroflife.github.io", at);
        var parsed = SecurityReportName.Parse(stem);
        Assert.NotNull(parsed);
        Assert.Equal("lastmasteroflife.github.io", parsed!.Value.Host);
        Assert.Equal(at.LocalDateTime, parsed.Value.At);
    }

    [Fact]
    public void ParseRejectsNonReportNames()
    {
        Assert.Null(SecurityReportName.Parse("random-file"));
        Assert.Null(SecurityReportName.Parse(""));
        Assert.Null(SecurityReportName.Parse("host-20261301-999999")); // data non valida
    }

    [Fact]
    public void SafeKeepsHostCharactersAndReplacesOthers()
    {
        Assert.Equal("a.b-c", SecurityReportName.Safe("a.b-c"));
        Assert.Equal("a_b_c", SecurityReportName.Safe("a/b:c"));
    }
}
