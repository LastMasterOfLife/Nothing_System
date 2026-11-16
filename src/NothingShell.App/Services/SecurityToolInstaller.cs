using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace NothingShell.App.Services;

/// <summary>
/// Installa gli scanner esterni della Modalità Sicurezza (docs/26-sicurezza.md) dalle fonti ufficiali:
/// nuclei dall'ultima release GitHub di ProjectDiscovery, scompattato in <see cref="SecurityScanService.ToolsFolder"/>
/// (niente amministratore, niente PATH); OWASP ZAP con winget (chiede l'elevazione), più un Java se manca.
/// </summary>
public static class SecurityToolInstaller
{
    private const string NucleiRelease = "https://api.github.com/repos/projectdiscovery/nuclei/releases/latest";
    private const string ZapPackage = "ZAP.ZAP";
    private const string JavaPackage = "EclipseAdoptium.Temurin.21.JRE";
    public const string ZapDownloadPage = "https://www.zaproxy.org/download/";

    /// <summary>Installa uno strumento; <paramref name="progress"/> riceve frasi brevi da mostrare. Torna null se è andata, altrimenti il motivo.</summary>
    public static Task<string?> InstallAsync(string id, IProgress<string> progress, CancellationToken token) => id switch
    {
        "nuclei" => InstallNucleiAsync(progress, token),
        "zap" => InstallZapAsync(progress, token),
        _ => Task.FromResult<string?>("strumento sconosciuto"),
    };

    // ── nuclei ───────────────────────────────────────────────────────────────────
    private static async Task<string?> InstallNucleiAsync(IProgress<string> progress, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NothingShell");
        try
        {
            progress.Report("cerco l'ultima versione…");
            using var meta = JsonDocument.Parse(await http.GetStringAsync(NucleiRelease, token));
            var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
            var suffix = $"_windows_{arch}.zip";
            string? url = null, name = null, digest = null, checksumsUrl = null;
            foreach (var asset in meta.RootElement.GetProperty("assets").EnumerateArray())
            {
                var n = asset.GetProperty("name").GetString() ?? string.Empty;
                if (n.StartsWith("nuclei_", StringComparison.OrdinalIgnoreCase) && n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    url = asset.GetProperty("browser_download_url").GetString();
                    name = n;
                    if (asset.TryGetProperty("digest", out var d) && d.GetString() is { } s && s.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        digest = s["sha256:".Length..];
                }
                else if (n.EndsWith("_checksums.txt", StringComparison.OrdinalIgnoreCase))
                    checksumsUrl = asset.GetProperty("browser_download_url").GetString();
            }
            if (url is null || name is null)
                return $"nessun pacchetto Windows {arch} nell'ultima release";
            if (checksumsUrl is null)
                return "la release non pubblica i checksum: non installo un file che non posso verificare";

            // L'impronta attesa dal file dei checksum firmato nella release (e, se GitHub la dà, anche dalla sua).
            var expected = (await http.GetStringAsync(checksumsUrl, token))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .FirstOrDefault(p => p.Length == 2 && p[1].TrimStart('*') == name)?[0];
            if (expected is null)
                return "il pacchetto non compare nel file dei checksum";
            if (digest is not null && !digest.Equals(expected, StringComparison.OrdinalIgnoreCase))
                return "checksum della release e di GitHub non coincidono";

            var temp = Path.Combine(Path.GetTempPath(), $"nothing-{Guid.NewGuid():N}-{name}");
            try
            {
                await DownloadAsync(http, url, temp, p => progress.Report($"scarico {p}%"), token);

                progress.Report("verifico l'impronta SHA-256…");
                string actual;
                await using (var file = File.OpenRead(temp))
                    actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(file, token));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return "il file scaricato non corrisponde al checksum ufficiale: scartato";

                progress.Report("controllo antivirus (Microsoft Defender)…");
                if (await DefenderScanAsync(temp, token) is { } threat)
                    return threat;

                progress.Report("scompatto…");
                var folder = Path.Combine(SecurityScanService.ToolsFolder, "nuclei");
                Directory.CreateDirectory(folder);
                ZipFile.ExtractToDirectory(temp, folder, overwriteFiles: true);
            }
            finally
            {
                TryDelete(temp);
            }

            // I template si scaricano al primo avvio: meglio farlo ora che a metà di una scansione.
            var exe = Path.Combine(SecurityScanService.ToolsFolder, "nuclei", "nuclei.exe");
            if (File.Exists(exe))
            {
                progress.Report("scarico i template…");
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(TimeSpan.FromMinutes(5));
                try
                {
                    await RunAsync(exe, ["-update-templates", "-silent"], limit.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // i template arriveranno al primo avvio
                }
            }
            return File.Exists(exe) ? null : "nuclei.exe non trovato nel pacchetto";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException
                                       or KeyNotFoundException or UnauthorizedAccessException or TaskCanceledException)
        {
            return token.IsCancellationRequested ? "annullato" : ex.Message;
        }
    }

    private static async Task DownloadAsync(HttpClient http, string url, string path, Action<int> onPercent, CancellationToken token)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(token);
        await using var target = File.Create(path);
        var buffer = new byte[81920];
        long done = 0;
        var last = -1;
        int read;
        while ((read = await source.ReadAsync(buffer, token)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), token);
            done += read;
            if (total is > 0)
            {
                var percent = (int)(done * 100 / total.Value);
                if (percent / 10 != last / 10)
                {
                    last = percent;
                    onPercent(percent);
                }
            }
        }
    }

    /// <summary>
    /// Scansione a richiesta di Microsoft Defender su un file. Null se pulito (o se Defender non c'è: la protezione in
    /// tempo reale resta comunque attiva), altrimenti il motivo per cui fermarsi.
    /// </summary>
    private static async Task<string?> DefenderScanAsync(string path, CancellationToken token)
    {
        var platform = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows Defender", "Platform");
        string? exe = null;
        try
        {
            if (Directory.Exists(platform))
                exe = Directory.EnumerateDirectories(platform).OrderDescending(StringComparer.OrdinalIgnoreCase)
                    .Select(d => Path.Combine(d, "MpCmdRun.exe")).FirstOrDefault(File.Exists);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // si prova il percorso classico
        }
        exe ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        if (!File.Exists(exe))
            return null;

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromMinutes(5));
        int code;
        try
        {
            code = await RunAsync(exe, ["-Scan", "-ScanType", "3", "-File", path, "-DisableRemediation"], limit.Token);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException && !token.IsCancellationRequested)
        {
            return null; // Defender non avviabile (es. sostituito da un altro antivirus)
        }
        // 0 = nessuna minaccia, 2 = minaccia trovata; gli altri codici sono errori di Defender, non del file.
        return code == 2 ? "Microsoft Defender ha segnalato una minaccia nel file scaricato: scartato" : null;
    }

    // ── OWASP ZAP ────────────────────────────────────────────────────────────────
    private static async Task<string?> InstallZapAsync(IProgress<string> progress, CancellationToken token)
    {
        if (!await RunsAsync("winget", ["--version"], token))
            return "winget non disponibile: scarica ZAP da " + ZapDownloadPage;

        try
        {
            // ZAP su Windows non porta con sé Java (serve la 17 o più nuova).
            if (!await RunsAsync("java", ["-version"], token, freshPath: true))
            {
                progress.Report("installo Java (Temurin 21)…");
                var java = await WingetInstallAsync(JavaPackage, token);
                if (java is not (0 or WingetAlreadyInstalled))
                    return $"installazione di Java non riuscita (winget {java})";
            }

            progress.Report("installo OWASP ZAP… (conferma la richiesta di Windows)");
            var zap = await WingetInstallAsync(ZapPackage, token);
            if (zap is not (0 or WingetAlreadyInstalled))
                return $"installazione di ZAP non riuscita (winget {zap})";
            return SecurityScanService.QuickPresent("zap") ? null : "ZAP installato ma zap.bat non trovato nei percorsi noti";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or OperationCanceledException)
        {
            return token.IsCancellationRequested ? "annullato" : ex.Message;
        }
    }

    /// <summary>winget: APPINSTALLER_CLI_ERROR_PACKAGE_ALREADY_INSTALLED.</summary>
    private const int WingetAlreadyInstalled = unchecked((int)0x8A15002B);

    /// <remarks>
    /// winget controlla da sé lo SHA-256 dell'installer contro il manifest del repository ufficiale e rifiuta un file
    /// diverso (qui non si passa mai <c>--ignore-security-hash</c>); Defender in tempo reale analizza l'installer.
    /// </remarks>
    private static Task<int> WingetInstallAsync(string package, CancellationToken token) =>
        RunAsync("winget",
        [
            "install", "--id", package, "--exact", "--source", "winget", "--silent",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity",
        ], token);

    // ── Processi ─────────────────────────────────────────────────────────────────
    private static async Task<bool> RunsAsync(string exe, IReadOnlyList<string> args, CancellationToken token, bool freshPath = false)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(15));
            return await RunAsync(exe, args, limit.Token, freshPath) == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException && !token.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task<int> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken token, bool freshPath = false)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
            start.ArgumentList.Add(a);
        if (freshPath)
            start.Environment["PATH"] = FreshPath();

        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
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

    /// <summary>
    /// Il PATH com'è adesso nel registro (macchina + utente): un programma appena installato con winget non compare nel
    /// PATH ereditato dalla shell finché non si riavvia, questo invece sì.
    /// </summary>
    public static string FreshPath()
    {
        var machine = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? string.Empty;
        var user = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
        var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var parts = (machine + ";" + user + ";" + current)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Environment.ExpandEnvironmentVariables)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return string.Join(';', parts);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // file temporaneo: ci pensa Windows
        }
    }
}
