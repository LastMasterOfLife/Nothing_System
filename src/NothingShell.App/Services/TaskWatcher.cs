using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows.Threading;
using NothingShell.Core.Agents;
using NothingShell.Core.Dev;

namespace NothingShell.App.Services;

/// <summary>
/// Comandi dal terminale (docs/12-sviluppo.md): la named pipe <c>NothingShell.Tasks</c>, solo per l'utente corrente,
/// riceve un messaggio JSON per connessione. Alla fine di un comando si cerca il primo errore nel suo log.
/// </summary>
public sealed class TaskWatcher : IDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly TaskTracker _tracker = new();
    private readonly DispatcherTimer _prune = new() { Interval = TimeSpan.FromSeconds(30) };
    private CancellationTokenSource? _stop;

    public TaskWatcher()
    {
        _prune.Tick += (_, _) =>
        {
            if (_tracker.Prune(DateTimeOffset.Now, t => t.Pid is not { } pid || DevTools.ProcessAlive(pid)))
                Publish();
        };
    }

    public IReadOnlyList<RunningTask> Running => _tracker.Running;

    public IReadOnlyList<FinishedTask> Recent => _tracker.Recent;

    /// <summary>Comandi in corso o ultimi finiti cambiati.</summary>
    public event Action? Changed;

    /// <summary>Un comando è finito; il primo errore del log è già cercato.</summary>
    public event Action<FinishedTask>? Finished;

    /// <summary><c>git push</c> riuscito: cartella del repository.</summary>
    public event Action<string>? Pushed;

    /// <summary>Totali di un agente qualsiasi (<c>{"event":"agent",…}</c>, docs/14-agenti-connettori.md).</summary>
    public event Action<AgentReport>? AgentReported;

    public void Start()
    {
        if (_stop is not null)
            return;
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _ = Task.Run(() => ListenAsync(token), token);
        _prune.Start();
    }

    public void Stop()
    {
        _stop?.Cancel();
        _stop?.Dispose();
        _stop = null;
        _prune.Stop();
    }

    public void Dispose() => Stop();

    /// <summary>Per <c>--task</c> e per le prove: manda un messaggio alla shell in esecuzione.</summary>
    public static bool Send(string json, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var pipe = new NamedPipeClientStream(".", TaskProtocol.PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect((int)timeout.TotalMilliseconds);
            var bytes = Encoding.UTF8.GetBytes(json.ReplaceLineEndings(" ") + "\n");
            pipe.Write(bytes);
            pipe.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false; // Nothing Shell non è aperta
        }
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(TaskProtocol.PipeName, PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token);
                if (await ReadLineAsync(server, token) is not { } line)
                    continue;
                if (AgentProtocol.Parse(line) is { } report)
                    await _dispatcher.InvokeAsync(() => AgentReported?.Invoke(report));
                else if (TaskProtocol.Parse(line) is { } message)
                    await _dispatcher.InvokeAsync(() => OnMessage(message));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // il client ha chiuso a metà, o la pipe è occupata da un'altra istanza: si riprova
                await Task.Delay(500, token).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ReadTimeout);
        var buffer = new byte[4096];
        using var line = new MemoryStream();
        try
        {
            while (line.Length < TaskProtocol.MaxMessageLength)
            {
                var read = await stream.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                    break;
                var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
                line.Write(buffer, 0, newline >= 0 ? newline : read);
                if (newline >= 0)
                    break;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return null; // client muto
        }
        return line.Length == 0 ? null : Encoding.UTF8.GetString(line.ToArray()).Trim('﻿', '\r', ' ');
    }

    private void OnMessage(TaskMessage message)
    {
        switch (message.Event)
        {
            case TaskEvent.Start:
                if (_tracker.Start(message, DateTimeOffset.Now))
                    Publish();
                break;
            case TaskEvent.Push:
                Pushed?.Invoke(message.Cwd!);
                break;
            default:
                if (_tracker.Finish(message, DateTimeOffset.Now) is { } finished)
                {
                    Publish();
                    _ = AnnounceAsync(finished);
                }
                break;
        }
    }

    private async Task AnnounceAsync(FinishedTask finished)
    {
        var announced = finished;
        if (!finished.Succeeded && finished.Log is { } log)
        {
            var error = await Task.Run(() => DevTools.ReadLogTail(log) is { } text ? CodeLocations.Pick(text, finished.Cwd, File.Exists) : null);
            if (error is not null)
            {
                announced = finished with { Error = error };
                _tracker.Replace(finished, announced);
                Publish();
            }
        }
        Finished?.Invoke(announced);
    }

    private void Publish() => Changed?.Invoke();
}
