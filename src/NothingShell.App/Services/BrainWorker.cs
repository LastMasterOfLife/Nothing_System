using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using NothingShell.Core.Brain;

namespace NothingShell.App.Services;

/// <summary>
/// Il modello vive in un processo a sé (<c>NothingShell.exe --brain &lt;file&gt;</c>): un errore nel codice nativo non può
/// chiudere la shell lasciando la taskbar nascosta, e chiudendo il processo la memoria torna tutta a Windows.
/// Dialogo su stdin e stdout: una riga JSON per richiesta, una per risposta. Fine di stdin, fine del processo.
/// </summary>
public static class BrainWorker
{
    private const int ContextSize = 1024;
    private const int MaxAnswerTokens = 48;

    /// <summary>
    /// Il caricatore di LLamaSharp 0.30 non trova da solo la variante per questa CPU: si caricano prima le DLL giuste,
    /// e le sue DllImport trovano il modulo già in memoria dal nome.
    /// </summary>
    private static void LoadNativeLibraries()
    {
        var native = Path.Combine(AppContext.BaseDirectory, "runtimes");
        native = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? Path.Combine(native, "win-arm64", "native")
            : Path.Combine(native, "win-x64", "native",
                System.Runtime.Intrinsics.X86.Avx2.IsSupported ? "avx2" : System.Runtime.Intrinsics.X86.Avx.IsSupported ? "avx" : "noavx");
        foreach (var library in new[] { "ggml-base", "ggml-cpu", "ggml", "llama", "mtmd" })
            System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(native, library + ".dll"));
    }

    public static async Task RunAsync(string modelPath)
    {
        LoadNativeLibraries();
        NativeLibraryConfig.All.WithLogCallback((_, _) => { });
        var parameters = new ModelParams(modelPath)
        {
            ContextSize = ContextSize,
            GpuLayerCount = 0,
            Threads = Math.Max(2, Environment.ProcessorCount / 2),
            BatchSize = 512,
        };
        using var weights = await LLamaWeights.LoadFromFileAsync(parameters);
        using var context = weights.CreateContext(parameters);
        var utf8 = new UTF8Encoding(false);
        using var input = new StreamReader(Console.OpenStandardInput(), utf8);
        await using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };

        while (await input.ReadLineAsync() is { } line)
        {
            var prompt = JsonSerializer.Deserialize<string>(line) ?? string.Empty;
            context.NativeHandle.MemoryClear();
            var executor = new InteractiveExecutor(context);
            var answer = new StringBuilder();
            var inference = new InferenceParams
            {
                MaxTokens = MaxAnswerTokens,
                AntiPrompts = ["\n", "<|im_end|>"],
                SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0f },
            };
            await foreach (var piece in executor.InferAsync(prompt, inference))
                answer.Append(piece);
            await output.WriteLineAsync(JsonSerializer.Serialize(answer.ToString().Replace("<|im_end|>", string.Empty, StringComparison.Ordinal)));
        }
    }
}

/// <summary>Il processo del modello visto dalla shell: si avvia alla prima domanda, si chiude dopo un po' che non serve.</summary>
public sealed class BrainWorkerClient : IDisposable
{
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _idle;
    private Process? _process;

    public BrainWorkerClient() => _idle = new Timer(_ => Stop());

    public bool IsLoaded => _process is { HasExited: false };

    /// <summary>Risposta del modello, o null se entro <paramref name="timeout"/> non arriva: il processo viene chiuso.</summary>
    public async Task<string?> AskAsync(string modelPath, string prompt, TimeSpan timeout, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(timeout);
            var process = _process is { HasExited: false } ? _process : (_process = Launch(modelPath));
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(prompt));
            await process.StandardInput.FlushAsync(limit.Token);
            var line = await process.StandardOutput.ReadLineAsync(limit.Token);
            _idle.Change(IdleLimit, Timeout.InfiniteTimeSpan);
            return line is null ? null : JsonSerializer.Deserialize<string>(line);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Kill();
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _idle.Dispose();
        Kill();
        _gate.Dispose();
    }

    private void Stop()
    {
        if (!_gate.Wait(0))
            return; // sta rispondendo: il timer ripartirà alla fine
        try
        {
            Kill();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Kill()
    {
        try
        {
            _process?.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // già chiuso
        }
        _process?.Dispose();
        _process = null;
    }

    private static Process Launch(string modelPath)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("percorso dell'eseguibile sconosciuto"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        start.ArgumentList.Add("--brain");
        start.ArgumentList.Add(modelPath);
        return Process.Start(start) ?? throw new InvalidOperationException("processo del modello non avviato");
    }
}
