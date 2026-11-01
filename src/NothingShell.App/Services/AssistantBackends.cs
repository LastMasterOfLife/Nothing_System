using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NothingShell.Core.Brain;

namespace NothingShell.App.Services;

/// <summary>
/// Esegue i comandi dell'assistente in PowerShell, senza profilo né finestra (docs/15-comandi-cervello.md). Quelli senza
/// conferma passano da <c>tools\NothingShell.Agent.ps1</c>, che ammette solo la lettura; sempre senza standard input,
/// così un comando che chiede qualcosa fallisce invece di restare appeso.
/// </summary>
public sealed class AssistantRunner : IAssistantRunner
{
    private static readonly string[] Quiet = ["-NoLogo", "-NoProfile", "-NonInteractive"];
    private static readonly TimeSpan ReadOnlyLimit = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ConfirmedLimit = TimeSpan.FromMinutes(2);

    private readonly string _script = Path.Combine(AppContext.BaseDirectory, "tools", "NothingShell.Agent.ps1");
    private readonly string _folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public async Task<AssistantRun> RunReadOnlyAsync(string command, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!File.Exists(_script))
            return new AssistantRun(AssistantRunStatus.Failed, string.Empty, $"manca {_script}");
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var (exit, output, errors) = await RunAsync(["-ExecutionPolicy", "RemoteSigned", "-File", _script, encoded], ReadOnlyLimit, token);
        if (exit is null)
            return new AssistantRun(AssistantRunStatus.Failed, string.Empty, errors.Length > 0 ? errors : "il comando è durato troppo");
        return ParseRunner(output) ?? new AssistantRun(AssistantRunStatus.Failed, string.Empty, errors.Length > 0 ? errors : "risposta illeggibile");
    }

    public async Task<AssistantRun> RunConfirmedAsync(string command, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(command);
        // Tutto come testo in un'unica uscita; un errore di PowerShell e un exit code dicono entrambi che non è riuscito.
        var script = $$"""
            [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
            $ProgressPreference = 'SilentlyContinue'
            $global:LASTEXITCODE = 0
            $failed = $false
            & {
            {{command}}
            } 2>&1 | ForEach-Object { if ($_ -is [System.Management.Automation.ErrorRecord]) { $failed = $true }; $_ } | Out-String -Width 160
            if ($LASTEXITCODE) { exit $LASTEXITCODE }
            if ($failed) { exit 1 }
            """;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var (exit, output, errors) = await RunAsync(["-EncodedCommand", encoded], ConfirmedLimit, token);
        if (exit is null)
            return new AssistantRun(AssistantRunStatus.Failed, output.TrimEnd(), errors.Length > 0 ? errors : "il comando è durato troppo: è stato chiuso");
        return exit == 0
            ? new AssistantRun(AssistantRunStatus.Ok, output.TrimEnd())
            : new AssistantRun(AssistantRunStatus.Failed, output.TrimEnd(), errors.Length > 0 ? errors.TrimEnd() : $"uscita con codice {exit}");
    }

    private static AssistantRun? ParseRunner(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (line is null)
            return null;
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            string Text(string name) => root.GetProperty(name).GetString() ?? string.Empty;
            var status = Text("status") switch
            {
                "ok" => AssistantRunStatus.Ok,
                "refused" => AssistantRunStatus.Refused,
                "rewrite" => AssistantRunStatus.Rewrite,
                _ => AssistantRunStatus.Failed,
            };
            return new AssistantRun(status, Text("output"), Text("error") is { Length: > 0 } error ? error : null, Text("reason"));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Il codice d'uscita è null se è durato troppo: si chiude tutto l'albero dei processi, anche se annullato.</summary>
    private async Task<(int? Exit, string Output, string Errors)> RunAsync(string[] arguments, TimeSpan limit, CancellationToken token)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            WorkingDirectory = _folder,
        };
        foreach (var argument in Quiet.Concat(arguments))
            start.ArgumentList.Add(argument);

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return (null, string.Empty, ex.Message);
        }
        if (process is null)
            return (null, string.Empty, "PowerShell non è partito");

        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(limit);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                return (process.ExitCode, await output, await errors);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // già finito
                }
                token.ThrowIfCancellationRequested();
                return (null, await output, string.Empty);
            }
        }
    }
}

/// <summary>Il modello incorporato che legge i risultati e risponde, in un processo a sé (docs/15-comandi-cervello.md).</summary>
public sealed class EmbeddedAssistantModel(BrainModel model) : IAssistantModel, IDisposable
{
    /// <summary>Basta anche per il primo turno, che carica il modello e legge tutto il prompt.</summary>
    private static readonly TimeSpan AnswerLimit = TimeSpan.FromMinutes(3);

    private readonly BrainWorkerClient _worker = new("--assistant");

    public string Name => model.Name;

    public void Dispose() => _worker.Dispose();

    public async Task<string?> AskAsync(string system, IReadOnlyList<AssistantTurn> turns, CancellationToken token)
    {
        var answer = await _worker.AskAsync(model.LocalPath, JsonSerializer.Serialize(new AssistantChat(system, turns)), AnswerLimit, token);
        token.ThrowIfCancellationRequested();
        return answer;
    }
}

/// <summary>
/// Il modello che legge i risultati e risponde con Ollama su questo PC (docs/15-comandi-cervello.md). Nulla esce dalla
/// macchina. Il modello piccolo incorporato non basta: nelle prove si inventava le risposte.
/// </summary>
public sealed class OllamaAssistantModel : IAssistantModel, IDisposable
{
    private static readonly Uri Server = new("http://127.0.0.1:11434/");

    /// <summary>Il primo caricamento di un modello da 5 GB può prendere un minuto.</summary>
    private static readonly TimeSpan AnswerLimit = TimeSpan.FromMinutes(3);

    private readonly HttpClient _http = new() { BaseAddress = Server, Timeout = Timeout.InfiniteTimeSpan };

    public OllamaAssistantModel(string name) => Name = name;

    public string Name { get; }

    public void Dispose() => _http.Dispose();

    /// <summary>I modelli installati, senza quelli solo per gli embedding; vuoto se Ollama non è acceso.</summary>
    public static async Task<IReadOnlyList<string>> InstalledAsync(CancellationToken token)
    {
        using var http = new HttpClient { BaseAddress = Server, Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            using var json = JsonDocument.Parse(await http.GetStringAsync(new Uri("api/tags", UriKind.Relative), token));
            return json.RootElement.GetProperty("models").EnumerateArray()
                .Select(m => m.GetProperty("name").GetString() ?? string.Empty)
                .Where(n => n.Length > 0 && !n.Contains("embed", StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException)
        {
            return [];
        }
    }

    public async Task<string?> AskAsync(string system, IReadOnlyList<AssistantTurn> turns, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(turns);
        object[] messages = [new { role = "system", content = system }, .. turns.Select(t => new { role = t.Role, content = t.Content })];
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(AnswerLimit);
        try
        {
            // La memoria torna a Windows poco dopo: un modello grande occupa diversi GB.
            using var response = await _http.PostAsJsonAsync(new Uri("api/chat", UriKind.Relative), new
            {
                model = Name,
                messages,
                stream = false,
                think = false,
                keep_alive = "3m",
                options = new { temperature = 0, num_predict = 500, num_ctx = 4096 },
            }, limit.Token);
            if (!response.IsSuccessStatusCode)
                return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(limit.Token));
            return json.RootElement.GetProperty("message").GetProperty("content").GetString();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
