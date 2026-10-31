using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using NothingShell.Core.Brain;

namespace NothingShell.App.Services;

/// <summary>
/// Il cervello dei comandi (docs/15-comandi-cervello.md): prima le scorciatoie imparate, poi il modello locale. Risponde
/// sulla named pipe <c>NothingShell.Brain</c>, solo per l'utente corrente, al modulo PowerShell e al launcher.
/// </summary>
public sealed class BrainService : IDisposable
{
    private static readonly TimeSpan ColdLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WarmLimit = TimeSpan.FromSeconds(10);
    private const int ExampleCount = 5;

    private readonly BrainModel _model = BrainModel.Default;
    private readonly string _pipeName;
    private readonly BrainWorkerClient _worker = new();
    private CancellationTokenSource? _stop;
    private CancellationTokenSource? _install;
    private string? _installStatus;

    /// <param name="memoryPath">File delle scorciatoie; altro da quello vero solo per <c>--brain-serve</c>.</param>
    /// <param name="pipeName">Nome della pipe; altro da quello vero solo per <c>--brain-serve</c>, accanto alla shell aperta.</param>
    public BrainService(string? memoryPath = null, string pipeName = BrainProtocol.PipeName)
    {
        Memory = new ShortcutMemory(memoryPath ?? ShortcutMemory.DefaultPath);
        _pipeName = pipeName;
    }

    public ShortcutMemory Memory { get; }

    public BrainModel Model => _model;

    public bool ModelInstalled => File.Exists(_model.LocalPath);

    public bool Installing => _install is not null;

    /// <summary>Stato del modello per le impostazioni. Può cambiare da un thread qualsiasi.</summary>
    public string Status => _installStatus ??
        (ModelInstalled ? $"Modello {_model.Name} pronto{(_worker.IsLoaded ? ", caricato" : string.Empty)}" : $"Modello {_model.Name} non scaricato");

    /// <summary>Avanzamento del download o memoria cambiati. Può arrivare da un thread qualsiasi.</summary>
    public event Action? Changed;

    public void Start()
    {
        if (_stop is not null)
            return;
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _ = Task.Run(() => ListenAsync(token), token);
    }

    public void Dispose()
    {
        _stop?.Cancel();
        _stop?.Dispose();
        _stop = null;
        _install?.Cancel();
        _worker.Dispose();
    }

    /// <summary>Decide cosa fare; il comando è null se non c'è una decisione.</summary>
    public async Task<BrainReply> DecideAsync(string input, bool forced, string? cwd, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        var clock = Stopwatch.StartNew();
        if (input.Length > BrainProtocol.MaxInputLength)
            return new BrainReply(Note: "frase troppo lunga");
        if (Memory.Decide(input) is { } known)
            return BrainProtocol.Reply(known, clock.Elapsed.TotalMilliseconds);
        // Una parola sola è di solito un comando scritto male: il modello non vale l'attesa.
        if (!forced && IntentText.Tokens(input).Count < 2)
            return new BrainReply(Ms: clock.Elapsed.TotalMilliseconds);
        if (!ModelInstalled)
            return new BrainReply(Ms: clock.Elapsed.TotalMilliseconds, Note: "modello non scaricato");

        var prompt = BrainProtocol.ModelPrompt(input, Memory.Examples(input, ExampleCount), cwd);
        var limit = _worker.IsLoaded ? WarmLimit : ColdLimit;
        var answer = await _worker.AskAsync(_model.LocalPath, prompt, limit, token);
        if (answer is null)
            return new BrainReply(Ms: clock.Elapsed.TotalMilliseconds, Note: "il modello non ha risposto");
        if (BrainProtocol.CleanModelOutput(answer) is not { } command)
            return new BrainReply(Ms: clock.Elapsed.TotalMilliseconds);
        // Quello che propone il modello non parte mai da solo: lo decide chi preme Invio.
        var decision = new BrainDecision(command, DecisionSource.Model, CommandSafety.Classify(command), false, 0);
        return BrainProtocol.Reply(decision, clock.Elapsed.TotalMilliseconds);
    }

    public void Record(string input, string command, bool succeeded)
    {
        if (Memory.Record(input, command, succeeded, DateTimeOffset.Now))
            Changed?.Invoke();
    }

    public void ClearMemory()
    {
        Memory.Clear();
        Changed?.Invoke();
    }

    /// <summary>Scarica il modello riprendendo dal punto in cui si era fermato; null se riuscito, altrimenti il motivo.</summary>
    public async Task<string?> InstallModelAsync()
    {
        if (ModelInstalled)
            return null;
        if (_install is not null)
            return "download già in corso";
        _install = new CancellationTokenSource();
        var token = _install.Token;
        var part = _model.LocalPath + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(part)!);
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, _model.Url);
            var have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (have > 0)
                request.Headers.Range = new RangeHeaderValue(have, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var resumed = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var file = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                var last = Stopwatch.StartNew();
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), token);
                    if (last.ElapsedMilliseconds < 1000)
                        continue;
                    last.Restart();
                    Progress($"Scarico {_model.Name}: {file.Length / (1024 * 1024)} di {_model.Bytes / (1024 * 1024)} MB");
                }
            }

            Progress($"Controllo {_model.Name}…");
            if (!await HashMatchesAsync(part, _model.Sha256, token))
            {
                File.Delete(part);
                return "il file scaricato non corrisponde all'hash atteso";
            }
            File.Move(part, _model.LocalPath, overwrite: true);
            return null;
        }
        catch (OperationCanceledException)
        {
            return "download annullato: riprende da dove si è fermato";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            return $"download interrotto: {ex.Message}";
        }
        finally
        {
            _install?.Dispose();
            _install = null;
            Progress(null);
        }
    }

    public void CancelInstall() => _install?.Cancel();

    private void Progress(string? status)
    {
        _installStatus = status;
        Changed?.Invoke();
    }

    private static async Task<bool> HashMatchesAsync(string path, string expected, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var hash = await SHA256.HashDataAsync(file, token);
        return Convert.ToHexStringLower(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await server.WaitForConnectionAsync(token);
                }
                catch
                {
                    await server.DisposeAsync();
                    throw;
                }
                _ = Task.Run(() => ServeAsync(server, token), token); // il modello non blocca le scorciatoie degli altri terminali
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                await Task.Delay(500, token).ConfigureAwait(false);
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream server, CancellationToken token)
    {
        await using (server)
        {
            try
            {
                var utf8 = new UTF8Encoding(false);
                using var reader = new StreamReader(server, utf8, leaveOpen: true);
                await using var writer = new StreamWriter(server, utf8, leaveOpen: true) { AutoFlush = true };
                using var readLimit = CancellationTokenSource.CreateLinkedTokenSource(token);
                readLimit.CancelAfter(TimeSpan.FromSeconds(2));
                var line = await reader.ReadLineAsync(readLimit.Token);
                if (line is null || line.Length > BrainProtocol.MaxMessageLength || BrainProtocol.ParseRequest(line.Trim('﻿')) is not { } request)
                    return;
                await writer.WriteLineAsync(BrainProtocol.Write(await HandleAsync(request, token)));
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // il terminale si è chiuso mentre si decideva
            }
        }
    }

    private async Task<BrainReply> HandleAsync(BrainRequest request, CancellationToken token)
    {
        switch (request.Op)
        {
            case "decide" when request.Input is { Length: > 0 }:
                return await DecideAsync(request.Input, request.Forced, request.Cwd, token);
            case "outcome" when request.Input is { Length: > 0 } && request.Command is { Length: > 0 }:
                Record(request.Input, request.Command, request.Ok);
                return new BrainReply();
            case "remember" when request.Input is { Length: > 0 } && request.Command is { Length: > 0 }:
                var remembered = Memory.Remember(request.Input, request.Command, DateTimeOffset.Now);
                if (remembered)
                    Changed?.Invoke();
                return new BrainReply(Note: remembered ? null : "non si impara: comando pericoloso o con un segreto");
            case "forget" when request.Input is { Length: > 0 }:
                var forgotten = Memory.Forget(request.Input);
                if (forgotten)
                    Changed?.Invoke();
                return new BrainReply(Note: forgotten ? null : "nessuna scorciatoia per questa frase");
            default:
                return new BrainReply(Note: "richiesta non valida");
        }
    }
}
