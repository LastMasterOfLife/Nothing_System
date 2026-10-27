using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using NothingShell.Core.Plugins;
using NothingShell.Core.Storage;

namespace NothingShell.App.Plugins;

/// <summary>
/// Carica i plugin da <c>~/.nothing-shell/plugins</c> e ne aggiorna i dati a intervalli
/// (docs/07-settings-plugins.md). Le sorgenti girano fuori dal thread UI e fuori dal processo
/// (comandi) o con timeout (http): un plugin in errore mostra il motivo e riprova al giro dopo.
/// </summary>
public sealed class PluginHost : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http = new() { Timeout = Timeout };
    private readonly List<DispatcherTimer> _timers = [];

    public PluginHost()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NothingShell/0.5");
    }

    public static string Folder { get; } = Path.Combine(AtomicFile.DataFolder, "plugins");

    public IReadOnlyList<PluginLoadResult> Results { get; private set; } = [];

    public IEnumerable<PluginManifest> Manifests => Results.Where(r => r.Manifest is not null).Select(r => r.Manifest!);

    public IReadOnlyList<PluginLoadResult> Load()
    {
        StopAll();
        Directory.CreateDirectory(Folder);
        var results = new List<PluginLoadResult>();
        foreach (var dir in Directory.EnumerateDirectories(Folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            var manifestPath = Path.Combine(dir, "plugin.json");
            results.Add(AtomicFile.TryReadAllText(manifestPath) is { } json
                ? PluginManifestParser.Parse(name, json)
                : new PluginLoadResult(name, null, "manca plugin.json"));
        }
        Results = results;
        return results;
    }

    /// <summary>Esegue subito la sorgente e poi a ogni intervallo; <paramref name="onData"/> arriva sul thread UI.</summary>
    public void Start(PluginManifest manifest, Action<WidgetData> onData)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(onData);
        var busy = false;
        async void Run()
        {
            if (busy)
                return; // il giro precedente è ancora in corso
            busy = true;
            try
            {
                onData(await FetchAsync(manifest));
            }
            finally
            {
                busy = false;
            }
        }

        var timer = new DispatcherTimer { Interval = manifest.Refresh };
        timer.Tick += (_, _) => Run();
        _timers.Add(timer);
        timer.Start();
        Run();
    }

    public void StopAll()
    {
        foreach (var t in _timers)
            t.Stop();
        _timers.Clear();
    }

    public void Dispose()
    {
        StopAll();
        _http.Dispose();
    }

    /// <summary>Un giro della sorgente; gli errori diventano un <see cref="WidgetData"/> con il motivo.</summary>
    public async Task<WidgetData> FetchAsync(PluginManifest manifest)
    {
        try
        {
            return manifest.SourceType == PluginSourceType.Http
                ? await FetchHttpAsync(manifest)
                : await RunCommandAsync(manifest);
        }
        catch (HttpRequestException ex)
        {
            return WidgetData.Error(manifest, $"rete: {ex.StatusCode?.ToString() ?? ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return WidgetData.Error(manifest, "tempo scaduto");
        }
        catch (JsonException)
        {
            return WidgetData.Error(manifest, "risposta non in JSON");
        }
        catch (Win32Exception)
        {
            return WidgetData.Error(manifest, "comando non trovato");
        }
    }

    private async Task<WidgetData> FetchHttpAsync(PluginManifest manifest)
    {
        using var response = await _http.GetAsync(new Uri(manifest.Target));
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        return WidgetData.FromHttp(manifest, document.RootElement);
    }

    private static async Task<WidgetData> RunCommandAsync(PluginManifest manifest)
    {
        var folder = Path.Combine(Folder, manifest.Id);
        var start = BuildStartInfo(manifest, folder);
        using var process = Process.Start(start) ?? throw new Win32Exception("processo non avviato");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return WidgetData.Error(manifest, "tempo scaduto");
        }

        var output = await stdout;
        if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
        {
            var reason = (await stderr).Split('\n', 2)[0].Trim();
            return WidgetData.Error(manifest, $"uscita {process.ExitCode}{(reason.Length > 0 ? $": {reason}" : string.Empty)}");
        }
        return WidgetData.FromCommandOutput(manifest, output);
    }

    /// <summary>Script .ps1 tramite PowerShell; file nella cartella del plugin; altrimenti comando dal PATH.</summary>
    private static ProcessStartInfo BuildStartInfo(PluginManifest manifest, string folder)
    {
        var local = Path.Combine(folder, manifest.Target);
        ProcessStartInfo start;
        if (manifest.Target.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            start = new ProcessStartInfo("powershell.exe");
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", local })
                start.ArgumentList.Add(a);
        }
        else
        {
            start = new ProcessStartInfo(File.Exists(local) ? local : manifest.Target);
        }
        foreach (var arg in manifest.Arguments)
            start.ArgumentList.Add(arg);

        start.WorkingDirectory = folder;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        return start;
    }
}
